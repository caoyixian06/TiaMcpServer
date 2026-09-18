using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;

namespace TiaMcpServer;

/// <summary>
/// PortalService 各 partial 文件共用的反射辅助方法。
/// 阶段1裁剪说明：这些方法原先散落在已归档的 UmacService/PlcAlarmService/AdvancedFeaturesService 中，
/// 因保留域（PlcSimService/PlcSoftwareExtendedService 等）仍在使用而集中到本文件。
/// </summary>
public partial class PortalService
{
    // ── 归档域的生命周期清理桩（原实现在 Archive\ 中，其静态缓存已随域一起归档）──

    /// <summary>安全域缓存清理（安全域已归档，保留调用点兼容）。</summary>
    private static void ClearSecurityCache() { }

    /// <summary>事务注册表清理（事务域已归档，保留调用点兼容）。</summary>
    private static void ClearAllTransactions() { }

    /// <summary>在线会话注册表清理（在线配置域已归档，保留调用点兼容）。</summary>
    private static void ClearOnlineSessions() { }

    /// <summary>下载配置注册表清理（精细下载配置域已归档，保留调用点兼容）。</summary>
    private static void ClearDownloadConfigs() { }

    // ── 通用反射辅助 ────────────────────────────────────────────────────────────

    /// <summary>反射读取对象属性值（容错，返回 ToString 或 null）。</summary>
    internal static string? SafeReflectGet(object? obj, string propName)
    {
        if (obj == null) return null;
        try
        {
            var prop = obj.GetType().GetProperty(propName);
            var val = prop?.GetValue(obj);
            return val?.ToString() ?? "";
        }
        catch { return null; }
    }

    /// <summary>反射获取对象上的集合属性（返回 IEnumerable 或 null）。</summary>
    internal static IEnumerable? TryGetCollectionProperty(object obj, string propertyName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(obj) is IEnumerable enumerable && !(enumerable is string))
                return enumerable;
        }
        catch { }
        return null;
    }

    /// <summary>统计集合属性元素个数，失败返回 -1。</summary>
    internal static int TryCountCollectionProperty(object obj, string propertyName)
    {
        try
        {
            var coll = TryGetCollectionProperty(obj, propertyName);
            if (coll == null) return -1;
            int n = 0;
            foreach (var _ in coll) n++;
            return n;
        }
        catch { return -1; }
    }

    /// <summary>
    /// 反射调用对象的 Export(FileInfo, ExportOptions) 或 Export(FileInfo) 方法。
    /// 成功返回 true；失败返回 false 并通过 errorMessage 输出原因。
    /// </summary>
    internal static bool InvokeExport(object obj, string filePath, out string errorMessage)
    {
        errorMessage = "";
        try
        {
            EnsureDir(filePath);
            var fi = new FileInfo(filePath);
            var objType = obj.GetType();

            // 尝试 Export(FileInfo, ExportOptions)
            var exportMethod = objType.GetMethod("Export", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(FileInfo), typeof(ExportOptions) }, null);
            if (exportMethod != null)
            {
                exportMethod.Invoke(obj, new object[] { fi, ExportOptions.WithDefaults });
                return true;
            }

            // 尝试 Export(FileInfo)
            exportMethod = objType.GetMethod("Export", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(FileInfo) }, null);
            if (exportMethod != null)
            {
                exportMethod.Invoke(obj, new object[] { fi });
                return true;
            }

            errorMessage = $"类型 '{objType.Name}' 未暴露 Export(FileInfo, ExportOptions) 方法";
            return false;
        }
        catch (TargetInvocationException tie)
        {
            errorMessage = tie.InnerException?.Message ?? tie.Message;
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    // ── 反射容错探测（原 UmacService 家族，PlcSoftwareExtendedService 仍在使用）────

    /// <summary>反射查找方法（按名称和参数个数，类型兼容匹配；用于不确定 API 签名时的容错探测）。</summary>
    internal static MethodInfo? UmacFindMethod(Type type, string methodName, object[] args)
    {
        try
        {
            var methods = type.GetMethods().Where(m => m.Name == methodName).ToList();
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (args[i] == null)
                    {
                        if (ps[i].ParameterType.IsValueType &&
                            Nullable.GetUnderlyingType(ps[i].ParameterType) == null)
                        { ok = false; break; }
                        continue;
                    }
                    var argType = args[i]!.GetType();
                    if (!ps[i].ParameterType.IsAssignableFrom(argType)) { ok = false; break; }
                }
                if (ok) return m;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 在 target 上依次尝试多个方法调用（按方法名 + 参数列表），返回第一个成功的结果。
    /// 用于 API 在不同 TIA 版本签名不确定时的容错探测，attempts 记录所有尝试。
    /// </summary>
    internal static (bool ok, object? result, List<string> attempts) UmacTryInvoke(
        object target, params (string name, object[] args)[] candidates)
    {
        var attempts = new List<string>();
        foreach (var (name, args) in candidates)
        {
            var sig = name + "(" + string.Join(", ",
                args.Select(a => a?.GetType().Name ?? "null")) + ")";
            try
            {
                attempts.Add(sig);
                var m = UmacFindMethod(target.GetType(), name, args);
                if (m == null) { attempts[attempts.Count - 1] += " => 无匹配重载"; continue; }
                var result = m.Invoke(target, args);
                return (ok: true, result, attempts);
            }
            catch (TargetInvocationException tie)
            {
                attempts[attempts.Count - 1] += " => 异常: " + (tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception ex) { attempts[attempts.Count - 1] += " => 异常: " + ex.Message; }
        }
        return (false, null, attempts);
    }

    /// <summary>
    /// 反射设置对象属性（大小写不敏感，类型不匹配时尝试 Convert.ChangeType）。
    /// 返回是否成功及诊断信息（attempts）。
    /// </summary>
    internal static (bool ok, List<string> attempts) UmacTrySetProperty(object target, string propName, object value)
    {
        var attempts = new List<string>();
        try
        {
            attempts.Add($"set {propName}={value}");
            var p = target.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null && p.CanWrite)
            {
                try { p.SetValue(target, value); return (true, attempts); }
                catch (ArgumentException)
                {
                    // 类型不匹配，尝试 Convert.ChangeType（应对 Int/UInt/Byte 等差异）
                    try
                    {
                        var converted = Convert.ChangeType(value, p.PropertyType);
                        p.SetValue(target, converted);
                        return (true, attempts);
                    }
                    catch (Exception cex) { attempts[attempts.Count - 1] += " => ChangeType 失败: " + cex.Message; }
                }
                catch (Exception ex) { attempts[attempts.Count - 1] += " => SetValue 异常: " + ex.Message; }
            }
            else
            {
                attempts[attempts.Count - 1] += " => 属性不可写或不存在";
            }
        }
        catch (Exception ex) { attempts[attempts.Count - 1] += " => 异常: " + ex.Message; }
        return (false, attempts);
    }

    /// <summary>
    /// 反射查找枚举类型并解析值。
    /// 先按短名在所有已加载程序集中模糊查找枚举类型，
    /// 再按值名（大小写不敏感）解析。失败时返回可用值列表便于诊断。
    /// </summary>
    internal static (object? value, Type? enumType, string error, List<string> availableValues) UmacParseEnum(
        string enumTypeName, string valueName)
    {
        Type? enumType = null;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.IsEnum && t.Name.Equals(enumTypeName, StringComparison.OrdinalIgnoreCase))
                        { enumType = t; break; }
                    }
                    if (enumType != null) break;
                }
                catch { }
            }
        }
        catch { }
        if (enumType == null)
        {
            // 退而求其次：按全名查找
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(enumTypeName);
                    if (t != null && t.IsEnum) { enumType = t; break; }
                }
            }
            catch { }
        }
        if (enumType == null)
            return (null, null, $"未找到枚举类型: {enumTypeName}", new List<string>());

        var names = Enum.GetNames(enumType).ToList();
        var match = names.FirstOrDefault(n => n.Equals(valueName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return (null, enumType, $"枚举 {enumType.FullName} 中无值 [{valueName}]", names);
        return (Enum.Parse(enumType, match), enumType, "", names);
    }

    // ── JSON 结果构造（原 AdvancedFeaturesService 头部，全项目共用）──────────────

    /// <summary>构造错误返回（success=false）。</summary>
    internal static JObject MakeError(string msg) => new JObject
    {
        ["success"] = false,
        ["error"] = msg
    };

    /// <summary>构造成功返回（success=true + message）。</summary>
    internal static JObject MakeSuccess(string msg) => new JObject
    {
        ["success"] = true,
        ["message"] = msg
    };

    /// <summary>构造异常返回（解包 TargetInvocationException）。</summary>
    internal static JObject MakeException(Exception ex)
    {
        var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
        return new JObject
        {
            ["success"] = false,
            ["error"] = inner.Message,
            ["exceptionType"] = inner.GetType().Name
        };
    }

    /// <summary>构造 API 探测失败返回（含 triedPaths 便于诊断）。</summary>
    internal static JObject MakeApiExplored(string featureName, List<string> triedPaths) => new JObject
    {
        ["success"] = false,
        ["error"] = $"未找到 {featureName} 服务入口（API 不可用或当前 TIA 版本不支持）",
        ["apiExplored"] = true,
        ["feature"] = featureName,
        ["triedPaths"] = new JArray(triedPaths)
    };

    /// <summary>将明文密码转为只读 SecureString。</summary>
    internal static SecureString ToSecureString(string? password)
    {
        var ss = new SecureString();
        if (!string.IsNullOrEmpty(password))
            foreach (char c in password) ss.AppendChar(c);
        ss.MakeReadOnly();
        return ss;
    }

    // ── 在线/下载 Provider 解析（原 DiagnosticsService，DownloadService 等仍在使用）──

    /// <summary>反射获取 OnlineProvider（仅在线服务提供者）。</summary>
    internal (object? provider, string typeName) ResolveOnlineProvider(PlcSoftware plc, List<string>? diag = null)
        => ResolveProviderByType(plc, "Siemens.Engineering.Online.OnlineProvider", diag);

    /// <summary>反射获取 DownloadProvider（仅下载服务提供者）。</summary>
    internal (object? provider, string typeName) ResolveDownloadProvider(PlcSoftware plc, List<string>? diag = null)
        => ResolveProviderByType(plc, "Siemens.Engineering.Download.DownloadProvider", diag);

    /// <summary>
    /// V19 新增的 PLCSIM 仿真服务（Siemens.Engineering.Simatic.PlcSim.PlcSimProvider）。
    /// PLCSIM 在线时 OnlineProvider/DownloadProvider 可能为 null，但 PlcSimProvider 应可用。
    /// </summary>
    internal (object? provider, string typeName) ResolvePlcSimProvider(PlcSoftware plc, List<string>? diag = null)
    {
        foreach (var tn in new[]
                 {
                     "Siemens.Engineering.Simatic.PlcSim.PlcSimProvider",
                     "Siemens.Engineering.Simatic.PlcSim.PlcSimSimulationProvider"
                 })
        {
            var r = ResolveProviderByType(plc, tn, diag);
            if (r.provider != null)
            {
                diag?.Add($"解析到 PLCSIM 服务: {tn} => {r.provider.GetType().FullName}");
                return r;
            }
        }
        return (null, "");
    }

    /// <summary>
    /// 反射按完整类型名解析 plc.GetService&lt;T&gt;()，返回 (provider, 类型短名)。
    /// diag 非空时收集每一步的失败原因（用于 PLCSIM 在线但 Provider 为 null 的场景排查）。
    /// </summary>
    internal (object? provider, string typeName) ResolveProviderByType(PlcSoftware plc, string typeFullName, List<string>? diag = null)
    {
        try
        {
            var providerType = typeof(TiaPortal).Assembly.GetType(typeFullName);
            if (providerType == null)
            {
                diag?.Add($"未找到类型: {typeFullName}");
                return (null, "");
            }
            var getServiceMethod = plc.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);
            if (getServiceMethod == null)
            {
                diag?.Add("未找到 GetService 泛型方法");
                return (null, "");
            }
            var generic = getServiceMethod.MakeGenericMethod(providerType);
            var provider = generic.Invoke(plc, null);
            if (provider != null) return (provider, providerType.Name);
            diag?.Add($"GetService<{providerType.Name}>() 返回 null");
            return (null, "");
        }
        catch (Exception ex)
        {
            diag?.Add($"解析 {typeFullName} 异常: {ex.Message}");
            return (null, "");
        }
    }
}
