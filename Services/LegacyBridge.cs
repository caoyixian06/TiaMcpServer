using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Download;
using Siemens.Engineering.SW;

namespace TiaMcpServer;

/// <summary>
/// 阶段1裁剪桥接：从已归档文件中搬入、但保留域仍在调用的成员。
/// 全部为 PortalService 的 partial 成员，逐字保留原实现（含 private 可跨 partial 访问）。
/// </summary>
public partial class PortalService
{
    /// <summary>
    /// 增强版下载到设备，支持完整/差异/停止后下载模式。
    /// downloadMode: "Complete" 完整 / "Differences" 差异 / "StopFirst" 停止后下载。
    /// 根据 downloadMode + includeHardware + includeSoftware 组合 DownloadOptions 枚举。
    /// 参考 DownloadService.DownloadToFolder 与 MoreServices.DownloadToDevice 的反射调用模式。
    /// </summary>
    public string DownloadToDeviceEnhanced(string plcName, string downloadMode, bool includeHardware, bool includeSoftware)
    {
        SafeOnlineExecutor.DemandAuthorized("DownloadToDeviceEnhanced");
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlc(plcName);
                var dp = plc.GetService<DownloadProvider>();
                if (dp == null)
                    return Err($"PLC '{plc.Name}' 不支持 DownloadProvider 服务");

                var config = dp.GetType().GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dp);
                if (config == null)
                    return Err("DownloadProvider.Configuration 为空，未配置连接");

                // 解析下载模式
                string mode = (downloadMode ?? "Complete").Trim();
                bool isDifferences = mode.Equals("Differences", StringComparison.OrdinalIgnoreCase)
                                     || mode.Equals("differences", StringComparison.OrdinalIgnoreCase);
                bool isStopFirst = mode.Equals("StopFirst", StringComparison.OrdinalIgnoreCase)
                                   || mode.Equals("stop_first", StringComparison.OrdinalIgnoreCase)
                                   || mode.Equals("StopFirst".ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                // 组合 DownloadOptions 枚举（反射以避免版本差异）
                // DownloadOptions.None=0, Hardware=1, Software=2, SoftwareOnlyChanges=4
                long optionsValue = 0;
                if (includeHardware) optionsValue |= 1;   // Hardware
                if (includeSoftware)
                {
                    if (isDifferences) optionsValue |= 4;  // SoftwareOnlyChanges
                    else optionsValue |= 2;                // Software
                }
                if (optionsValue == 0)
                {
                    // 默认完整下载（硬件+软件）
                    optionsValue = 1 | 2;
                    includeHardware = true;
                    includeSoftware = true;
                }

                var downloadOptionsType = typeof(DownloadOptions);
                object downloadOptions;
                try
                {
                    downloadOptions = Enum.ToObject(downloadOptionsType, optionsValue);
                }
                catch
                {
                    return Err($"无法构造 DownloadOptions (value={optionsValue})");
                }

                // 若为 StopFirst 模式：先尝试反射调用 Configuration 上的 Stop / StopPlc 方法
                var stopActions = new List<string>();
                if (isStopFirst)
                {
                    var stopMethod = config.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.Equals("Stop", StringComparison.OrdinalIgnoreCase)
                                             || m.Name.Equals("StopPlc", StringComparison.OrdinalIgnoreCase));
                    if (stopMethod != null)
                    {
                        try
                        {
                            var ps = stopMethod.GetParameters();
                            var args = ps.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                            stopMethod.Invoke(config, args);
                            stopActions.Add($"已调用 {stopMethod.Name}()");
                        }
                        catch (Exception ex)
                        {
                            stopActions.Add($"调用 {stopMethod.Name} 失败: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                    else
                    {
                        stopActions.Add("未找到 Stop/StopPlc 方法，跳过停止步骤直接下载");
                    }
                }

                // 反射调用 Download(IConfiguration, Delegate, Delegate, DownloadOptions) — 4 参数重载
                var downloadMethod = typeof(DownloadProvider).GetMethods()
                    .FirstOrDefault(m => m.Name == "Download" && m.GetParameters().Length == 4
                        && m.GetParameters()[0].ParameterType.Name == "IConfiguration");

                if (downloadMethod == null)
                    return Err("未找到 DownloadProvider.Download(IConfiguration,...) 4 参数重载");

                object? result;
                try
                {
                    result = downloadMethod.Invoke(dp, new object?[] { config, null, null, downloadOptions });
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"下载调用失败: {ex.InnerException?.Message ?? ex.Message}",
                        plcName = plc.Name,
                        downloadMode = mode,
                        downloadOptions = downloadOptions.ToString(),
                        stopActions
                    }, Formatting.Indented);
                }

                if (result == null)
                    return Err("下载返回 null 结果");

                int errCount = 0;
                int warnCount = 0;
                string state = "";
                try { errCount = Convert.ToInt32(result.GetType().GetProperty("ErrorCount")?.GetValue(result) ?? 0); } catch { }
                try { warnCount = Convert.ToInt32(result.GetType().GetProperty("WarningCount")?.GetValue(result) ?? 0); } catch { }
                try { state = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? ""; } catch { }

                return JsonConvert.SerializeObject(new
                {
                    success = errCount == 0,
                    plcName = plc.Name,
                    downloadMode = mode,
                    includeHardware,
                    includeSoftware,
                    downloadOptions = downloadOptions.ToString(),
                    stopActions,
                    result = new
                    {
                        state,
                        errorCount = errCount,
                        warningCount = warnCount,
                        rawType = result.GetType().Name
                    }
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>枚举提供者所有公共方法的签名，用于 apiExplored 诊断输出。</summary>
    private static List<string> ExploreProviderMethods(Type providerType)
    {
        var methods = new List<string>();
        try
        {
            foreach (var m in providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => !m.IsSpecialName))
            {
                var ps = m.GetParameters();
                methods.Add($"{m.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
            }
        }
        catch { }
        return methods;
    }

    /// <summary>
    /// 在线读取 PLC 变量值。
    /// V19 OnlineProvider 仅暴露 GoOnline/GoOffline/Configuration/State，未直接支持变量读写。
    /// 本方法采用反射探测：优先 OnlineProvider，回退 DownloadProvider；查找 Read*/Variable*/Tag* 方法；
    /// 失败时返回 apiExplored（所有可用方法签名）供诊断，便于后续扩展。
    /// </summary>
    public string ReadOnlineVariables(string plcName, List<string> variableNames)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();
                if (variableNames == null || variableNames.Count == 0)
                    return Err("variableNames 不能为空");

                var plc = ResolvePlc(plcName);
                // 优先 OnlineProvider；不存在时回退 DownloadProvider（标注实际类型，避免混用）
                var diag = new List<string>();
                var (provider, providerTypeName) = ResolveOnlineProvider(plc, diag);
                if (provider == null)
                {
                    var (dlProvider, dlTypeName) = ResolveDownloadProvider(plc, diag);
                    provider = dlProvider;
                    providerTypeName = dlTypeName;
                }
                if (provider == null)
                {
                    var (psProvider, psTypeName) = ResolvePlcSimProvider(plc, diag);
                    provider = psProvider;
                    providerTypeName = psTypeName;
                }
                if (provider == null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"PLC '{plc.Name}' 未提供 OnlineProvider 或 DownloadProvider 服务",
                        plcName = plc.Name,
                        plcType = plc.GetType().FullName,
                        tiaVersion = typeof(TiaPortal).Assembly.GetName().Version?.ToString(),
                        diag
                    }, Formatting.Indented);

                var providerType = provider.GetType();
                var apiExplored = ExploreProviderMethods(providerType);

                bool isOnline = IsProviderOnline(provider, providerType);
                if (!isOnline)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"PLC '{plc.Name}' 未在线，请先调用 go_online",
                        plcName = plc.Name,
                        providerType = providerTypeName,
                        isOnline,
                        apiExplored
                    }, Formatting.Indented);

                // 反射探测读变量方法：ReadVariables / ReadVariable / ReadTag / ReadValue 等
                var readMethods = providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName
                                && m.Name.IndexOf("Read", StringComparison.OrdinalIgnoreCase) >= 0
                                && (m.Name.IndexOf("Variable", StringComparison.OrdinalIgnoreCase) >= 0
                                    || m.Name.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0
                                    || m.Name.IndexOf("Value", StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                var results = new List<object>();
                Exception? lastErr = null;
                bool anySuccess = false;

                foreach (var name in variableNames)
                {
                    object? value = null;
                    string? error = null;
                    string invokedMethod = "";

                    foreach (var m in readMethods)
                    {
                        try
                        {
                            var ps = m.GetParameters();
                            // 尝试单参数 string 调用
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                            {
                                value = m.Invoke(provider, new object[] { name });
                                invokedMethod = m.Name;
                                error = null;
                                break;
                            }
                            // 尝试 IEnumerable<string> 单参数调用
                            if (ps.Length == 1 && ps[0].ParameterType != typeof(string)
                                && typeof(IEnumerable).IsAssignableFrom(ps[0].ParameterType))
                            {
                                value = m.Invoke(provider, new object[] { new[] { name } });
                                invokedMethod = m.Name;
                                error = null;
                                break;
                            }
                        }
                        catch (Exception ex) { lastErr = ex.InnerException ?? ex; }
                    }

                    if (readMethods.Count == 0)
                        error = "未找到 ReadVariables 方法";
                    else if (invokedMethod == "" && error == null)
                        error = lastErr?.Message ?? "所有 Read 方法签名不匹配";

                    results.Add(new
                    {
                        name,
                        value = value?.ToString() ?? "",
                        type = value?.GetType().Name ?? "",
                        method = invokedMethod,
                        error
                    });
                    if (error == null && invokedMethod != "") anySuccess = true;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = anySuccess,
                    plcName = plc.Name,
                    providerType = providerTypeName,
                    isOnline,
                    results,
                    readMethodsTried = readMethods.Select(m => m.Name).ToList(),
                    apiExplored,
                    hint = readMethods.Count == 0
                        ? "OnlineProvider 未暴露 ReadVariables 方法。V19 OnlineProvider 仅支持 GoOnline/GoOffline/Configuration/State，不直接支持变量读写。建议通过 TagTable 或 Watch 表配合 HMI/SCADA 读写变量。"
                        : null
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 在线写入单个 PLC 变量值。
    /// 与 ReadOnlineVariables 同理，反射探测 Write*/Variable*/Tag* 方法；
    /// 失败时返回 apiExplored 供诊断。
    /// </summary>
    public string WriteOnlineVariable(string plcName, string variableName, string value)
    {
        SafeOnlineExecutor.DemandAuthorized("WriteOnlineVariable");
        lock (_lock)
        {
            try
            {
                RequireProject();
                if (string.IsNullOrEmpty(variableName))
                    return Err("variableName 不能为空");

                var plc = ResolvePlc(plcName);
                // 优先 OnlineProvider；不存在时回退 DownloadProvider（标注实际类型，避免混用）
                var diag = new List<string>();
                var (provider, providerTypeName) = ResolveOnlineProvider(plc, diag);
                if (provider == null)
                {
                    var (dlProvider, dlTypeName) = ResolveDownloadProvider(plc, diag);
                    provider = dlProvider;
                    providerTypeName = dlTypeName;
                }
                if (provider == null)
                {
                    var (psProvider, psTypeName) = ResolvePlcSimProvider(plc, diag);
                    provider = psProvider;
                    providerTypeName = psTypeName;
                }
                if (provider == null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"PLC '{plc.Name}' 未提供 OnlineProvider 或 DownloadProvider 服务",
                        plcName = plc.Name,
                        plcType = plc.GetType().FullName,
                        tiaVersion = typeof(TiaPortal).Assembly.GetName().Version?.ToString(),
                        diag
                    }, Formatting.Indented);

                var providerType = provider.GetType();
                var apiExplored = ExploreProviderMethods(providerType);

                bool isOnline = IsProviderOnline(provider, providerType);
                if (!isOnline)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"PLC '{plc.Name}' 未在线，请先调用 go_online",
                        plcName = plc.Name,
                        providerType = providerTypeName,
                        isOnline,
                        apiExplored
                    }, Formatting.Indented);

                // 反射探测写变量方法：WriteVariables / WriteVariable / WriteTag / WriteValue 等
                var writeMethods = providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName
                                && m.Name.IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0
                                && (m.Name.IndexOf("Variable", StringComparison.OrdinalIgnoreCase) >= 0
                                    || m.Name.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0
                                    || m.Name.IndexOf("Value", StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                Exception? lastErr = null;
                string invokedMethod = "";
                bool success = false;

                foreach (var m in writeMethods)
                {
                    try
                    {
                        var ps = m.GetParameters();
                        // 尝试 (string name, string value) 调用
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                        {
                            m.Invoke(provider, new object[] { variableName, value });
                            invokedMethod = m.Name;
                            success = true;
                            break;
                        }
                        // 尝试 (string name, object value) 调用
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                        {
                            m.Invoke(provider, new object[] { variableName, (object)value });
                            invokedMethod = m.Name;
                            success = true;
                            break;
                        }
                    }
                    catch (Exception ex) { lastErr = ex.InnerException ?? ex; }
                }

                return JsonConvert.SerializeObject(new
                {
                    success,
                    plcName = plc.Name,
                    variableName,
                    value,
                    providerType = providerTypeName,
                    isOnline,
                    invokedMethod,
                    error = success ? null : (writeMethods.Count == 0
                        ? "未找到 WriteVariables 方法"
                        : (lastErr?.Message ?? "所有 Write 方法签名不匹配")),
                    writeMethodsTried = writeMethods.Select(m => m.Name).ToList(),
                    apiExplored,
                    hint = writeMethods.Count == 0
                        ? "OnlineProvider 未暴露 WriteVariables 方法。V19 OnlineProvider 不直接支持变量读写。"
                        : null
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>按订单号（如 6ES7214-1AG40-0XB0）查找 TypeIdentifier。</summary>
    public string? FindTypeIdentifierByOrder(string orderNumber)
    {
        if (_tiaPortal == null || string.IsNullOrEmpty(orderNumber))
            return null;

        // ★ 优先从已验证的类型识别符库查找（避免猜错）
        var libraryHit = TypeIdentifierLibrary.FindByOrderNumber(orderNumber);
        if (!string.IsNullOrEmpty(libraryHit))
            return libraryHit;

        // 库中未找到，走 HardwareCatalog 实时搜索
        var catalog = GetHardwareCatalog();
        if (catalog == null) return null;

        // 标准化订单号：去空格、去前缀
        var normalized = orderNumber.Replace(" ", "").Trim();
        if (normalized.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Substring("OrderNumber:".Length);
        var slashIdx = normalized.IndexOf('/');
        if (slashIdx > 0)
            normalized = normalized.Substring(0, slashIdx);

        var all = FindCatalogEntries(catalog, "").ToList();

        // 1. 精确匹配 TypeIdentifier
        var exact = all.FirstOrDefault(e =>
        {
            try { return string.Equals(GetCatalogProp(e, "TypeIdentifier")?.Replace(" ", ""), normalized, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        if (exact != null) return GetCatalogProp(exact, "TypeIdentifier");

        // 2. 精确匹配 ArticleNumber
        var byArt = all.FirstOrDefault(e =>
        {
            try { return string.Equals(GetCatalogProp(e, "ArticleNumber")?.Replace(" ", ""), normalized, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        if (byArt != null) return GetCatalogProp(byArt, "TypeIdentifier");

        // 3. 包含匹配（TypeIdentifier 或 ArticleNumber 包含订单号）
        var contains = all.FirstOrDefault(e =>
        {
            try
            {
                var tid = GetCatalogProp(e, "TypeIdentifier")?.Replace(" ", "") ?? "";
                var art = GetCatalogProp(e, "ArticleNumber")?.Replace(" ", "") ?? "";
                return tid.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0
                    || art.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        });
        return contains != null ? GetCatalogProp(contains, "TypeIdentifier") : null;
    }

        private List<InstructionTemplate> GetInstructionLibrary()
        {
            var lib = new List<InstructionTemplate>();

            // ══════════ 触点类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "Contact",
                Category = "触点",
                Description = "常开触点，串联逻辑。最基础的触点指令。",
                Verified = true,
                JsonTemplate = @"{""contact"":{""contact"":""变量名""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "operand", Type = "输入", Description = "触点关联的变量（Bool）" },
                    new PinInfo { Name = "in", Type = "逻辑流", Description = "上游逻辑流输入" },
                    new PinInfo { Name = "out", Type = "逻辑流", Description = "逻辑流输出" }
                },
                Example = @"{""contact"":{""contact"":""输入组.I_启动""}}",
                Notes = "变量必须是 Bool 类型。多个触点串联形成 AND 逻辑。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "NegatedContact",
                Category = "触点",
                Description = "常闭触点（Negated Contact）。当变量为 FALSE 时导通。",
                Verified = true,
                JsonTemplate = @"{""contact"":{""contact"":""变量名"",""type"":""negated""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "operand", Type = "输入", Description = "触点关联的变量（Bool），取反" },
                    new PinInfo { Name = "in", Type = "逻辑流", Description = "上游逻辑流输入" },
                    new PinInfo { Name = "out", Type = "逻辑流", Description = "逻辑流输出" }
                },
                Example = @"{""contact"":{""contact"":""输入组.I_急停"",""type"":""negated""}}",
                Notes = "type:\"negated\" 表示常闭触点。等价于 LAD 中的常闭触点符号。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "PContact",
                Category = "触点",
                Description = "上升沿触点（Positive Contact）。检测信号的上升沿（0→1）。",
                Verified = true,
                JsonTemplate = @"{""contact"":{""contact"":""信号名"",""type"":""pcontact"",""bit"":""p状态.p[索引]""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "电源轨连接（Powerrail）" },
                    new PinInfo { Name = "bit", Type = "输入", Description = "边沿存储位，通常是 DB.p数组[索引]" },
                    new PinInfo { Name = "operand", Type = "输入", Description = "信号源（Bool）" },
                    new PinInfo { Name = "out", Type = "逻辑流", Description = "上升沿脉冲输出（一个扫描周期）" }
                },
                Example = @"{""contact"":{""contact"":""DB210_Input.I_急停"",""type"":""pcontact"",""bit"":""p状态.p[3]""}}",
                Notes = "需配套 DB：p状态(Array of Bool) + 边沿检测 DB。每个信号占用 p数组的一个索引位。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "NContact",
                Category = "触点",
                Description = "下降沿触点（Negative Contact）。检测信号的下降沿（1→0）。",
                Verified = true,
                JsonTemplate = @"{""contact"":{""contact"":""信号名"",""type"":""ncontact"",""bit"":""N状态.n[索引]""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "电源轨连接（Powerrail）" },
                    new PinInfo { Name = "bit", Type = "输入", Description = "边沿存储位，通常是 DB.n数组[索引]" },
                    new PinInfo { Name = "operand", Type = "输入", Description = "信号源（Bool）" },
                    new PinInfo { Name = "out", Type = "逻辑流", Description = "下降沿脉冲输出（一个扫描周期）" }
                },
                Example = @"{""contact"":{""contact"":""DB210_Input.I_急停"",""type"":""ncontact"",""bit"":""N状态.n[3]""}}",
                Notes = "需配套 DB：N状态(Array of Bool) + 边沿检测 DB。"
            });

            // ══════════ 线圈类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "Coil",
                Category = "线圈",
                Description = "输出线圈。将逻辑流结果写入变量。",
                Verified = true,
                JsonTemplate = @"{""coil"":{""coil"":""变量名""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "in", Type = "逻辑流", Description = "逻辑流输入" },
                    new PinInfo { Name = "operand", Type = "输出", Description = "输出变量（Bool）" }
                },
                Example = @"{""coil"":{""coil"":""输出组.Q_电机运行""}}",
                Notes = "变量必须是 Bool 类型。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "SetCoil",
                Category = "线圈",
                Description = "置位线圈（S）。逻辑流为 TRUE 时置位变量，变量保持 TRUE 直到被复位。",
                Verified = true,
                JsonTemplate = @"{""coil"":{""coil"":""变量名"",""type"":""set""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "in", Type = "逻辑流", Description = "逻辑流输入" },
                    new PinInfo { Name = "operand", Type = "输出", Description = "被置位的变量（Bool）" }
                },
                Example = @"{""coil"":{""coil"":""M_故障标志"",""type"":""set""}}",
                Notes = "禁止用 {\"coil\":{\"coil\":\"SetCoil\"}} —— SetCoil 会被解释为变量名！必须用 type:\"set\"。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "ResetCoil",
                Category = "线圈",
                Description = "复位线圈（R）。逻辑流为 TRUE 时复位变量。",
                Verified = true,
                JsonTemplate = @"{""coil"":{""coil"":""变量名"",""type"":""reset""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "in", Type = "逻辑流", Description = "逻辑流输入" },
                    new PinInfo { Name = "operand", Type = "输出", Description = "被复位的变量（Bool）" }
                },
                Example = @"{""coil"":{""coil"":""M_故障标志"",""type"":""reset""}}",
                Notes = "禁止用 {\"coil\":{\"coil\":\"ResetCoil\"}}。必须用 type:\"reset\"。"
            });

            // ══════════ 逻辑门 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "O",
                Category = "逻辑门",
                Description = "O 门（紧凑 OR）。多个输入并联后给一个线圈。比 branch 格式简洁。",
                Verified = true,
                JsonTemplate = @"{""or"":{""inputs"":[""触点1"",""触点2""],""output"":""输出变量""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "in1..inN", Type = "逻辑流", Description = "N 个并联输入" },
                    new PinInfo { Name = "out", Type = "逻辑流", Description = "OR 结果输出" }
                },
                Example = @"{""or"":{""inputs"":[""DB1.M_Fault1"",""DB1.M_Fault2""],""output"":""DB1.M_FaultSummary""}}",
                Notes = "Card 值根据 inputs 数量自动计算。等价于两个触点并联后给一个线圈。"
            });

            // ══════════ 定时器 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "TON",
                Category = "定时器",
                Description = "通电延时定时器（Timer On-Delay）。IN 为 TRUE 后，延时 PT 时间后 Q 变 TRUE。",
                Verified = true,
                JsonTemplate = @"{""sysCall"":{""inst"":""TON"",""instance"":""Ton_1"",""pins"":{""IN"":""触发变量"",""PT"":""T#3S"",""Q"":""输出变量""},""datatype"":""Time""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "IN", Type = "逻辑流", Description = "触发输入（通过触点连接，不能直接连变量）" },
                    new PinInfo { Name = "PT", Type = "输入", Description = "预设时间（T#开头的 Time 常量，如 T#3S）" },
                    new PinInfo { Name = "Q", Type = "输出", Description = "延时到达后的输出（Bool）" },
                    new PinInfo { Name = "ET", Type = "输出", Description = "已延时时间（通常悬空 OpenCon）" }
                },
                Example = @"{""sysCall"":{""inst"":""TON"",""instance"":""Ton_防抖"",""pins"":{""IN"":""M_触发"",""PT"":""T#5S"",""Q"":""M_延时到达""},""datatype"":""Time""}}",
                Notes = "FC 无 Static 区：必须用 FB 或全局实例（instanceScope:\"global\"）。FB 内用 instance 声明在 Static 区。触发引脚 IN 是逻辑流，需通过触点连接。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "TON_Global",
                Category = "定时器",
                Description = "TON 全局实例（FC 中可用）。使用独立全局 DB 存储，不需要 FB Static 区。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""TON"",""instance"":""T_防抖延时"",""instanceScope"":""global"",""pins"":{""IN"":""触发变量"",""PT"":""T#3S""},""datatype"":""Time""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "IN", Type = "逻辑流", Description = "触发输入" },
                    new PinInfo { Name = "PT", Type = "输入", Description = "预设时间" },
                    new PinInfo { Name = "Q", Type = "输出", Description = "延时到达输出（可选）" },
                    new PinInfo { Name = "ET", Type = "输出", Description = "已延时时间（自动悬空）" }
                },
                Example = @"{""box"":{""box"":""TON"",""instance"":""T_防抖延时"",""instanceScope"":""global"",""pins"":{""IN"":""M_触发"",""PT"":""T#20MS""},""datatype"":""Time""}}",
                Notes = "必须提前在博途中创建同名全局 DB（类型: IEC_TIMER）。不指定 instanceScope 时默认 LocalVariable (FB Static)。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "TOF",
                Category = "定时器",
                Description = "断电延时定时器（Timer Off-Delay）。IN 为 FALSE 后，延时 PT 时间后 Q 变 FALSE。",
                Verified = true,
                JsonTemplate = @"{""sysCall"":{""inst"":""TOF"",""instance"":""Tof_1"",""pins"":{""IN"":""触发变量"",""PT"":""T#3S"",""Q"":""输出变量""},""datatype"":""Time""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "IN", Type = "逻辑流", Description = "触发输入" },
                    new PinInfo { Name = "PT", Type = "输入", Description = "预设时间" },
                    new PinInfo { Name = "Q", Type = "输出", Description = "延时到达后输出" },
                    new PinInfo { Name = "ET", Type = "输出", Description = "已延时时间" }
                },
                Example = @"{""sysCall"":{""inst"":""TOF"",""instance"":""Tof_1"",""pins"":{""IN"":""M_触发"",""PT"":""T#3S""},""datatype"":""Time""}}",
                Notes = "用法同 TON，但语义相反。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "TP",
                Category = "定时器",
                Description = "脉冲定时器（Pulse Timer）。IN 上升沿触发，Q 输出 PT 时长的脉冲。",
                Verified = true,
                JsonTemplate = @"{""sysCall"":{""inst"":""TP"",""instance"":""Tp_1"",""pins"":{""IN"":""触发变量"",""PT"":""T#2S""},""datatype"":""Time""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "IN", Type = "逻辑流", Description = "触发输入（上升沿启动）" },
                    new PinInfo { Name = "PT", Type = "输入", Description = "脉冲宽度" },
                    new PinInfo { Name = "Q", Type = "输出", Description = "脉冲输出" },
                    new PinInfo { Name = "ET", Type = "输出", Description = "已脉冲时间" }
                },
                Example = @"{""sysCall"":{""inst"":""TP"",""instance"":""Tp_1"",""pins"":{""IN"":""M_触发"",""PT"":""T#2S""},""datatype"":""Time""}}",
                Notes = "一旦启动，即使 IN 变 FALSE，Q 也会持续 PT 时长。"
            });

            // ══════════ 比较类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "GT",
                Category = "比较",
                Description = "大于比较（>）。in1 > in2 时输出 TRUE。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""GT"",""pins"":{""in1"":""变量1"",""in2"":""变量2""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能（来自上游 out）" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "比较数1（Real/Int/DInt）" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "比较数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "比较结果（Bool）" }
                },
                Example = @"{""box"":{""box"":""GT"",""pins"":{""in1"":""液位"",""in2"":""80.0""},""datatype"":""Real""}}",
                Notes = "比较盒的 out 是逻辑流输出（Bool），连下游 in 或 O 门。in1/in2 来自 IdentCon。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "LT",
                Category = "比较",
                Description = "小于比较（<）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""LT"",""pins"":{""in1"":""变量1"",""in2"":""变量2""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "比较数1" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "比较数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "结果（Bool）" }
                },
                Example = @"{""box"":{""box"":""LT"",""pins"":{""in1"":""液位"",""in2"":""20.0""},""datatype"":""Real""}}",
                Notes = "同 GT，只是比较方向相反。还有 GE(>=)/LE(<=)/EQ(=)/NE(<>) 用法相同。"
            });

            // ══════════ 数学类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "ADD",
                Category = "数学",
                Description = "加法（in1 + in2 = out）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""ADD"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "加数1" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "加数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "和（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""ADD"",""pins"":{""in1"":""计数"",""in2"":""1"",""out"":""计数""},""datatype"":""Int""}}",
                Notes = "out 引脚必须连接变量（不能是常量）。还有 SUB/MUL/DIV/MOD 用法相同。datatype 决定运算类型。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "MUL",
                Category = "数学",
                Description = "乘法（in1 × in2 = out）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""MUL"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "乘数1" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "乘数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "积（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""MUL"",""pins"":{""in1"":""流量"",""in2"":""0.5"",""out"":""缩放流量""},""datatype"":""Real""}}",
                Notes = "out 必须连接变量。"
            });

            // ══════════ 转换/移动类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "MOVE",
                Category = "转换",
                Description = "数据移动（in → out）。将输入值赋给输出变量。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""MOVE"",""pins"":{""in"":""源变量"",""out"":""目标变量""},""datatype"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "in", Type = "输入", Description = "源数据（变量或常量）" },
                    new PinInfo { Name = "out1", Type = "输出", Description = "目标变量（注意是 out1 不是 out！）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""MOVE"",""pins"":{""in"":""0"",""out"":""计数器""},""datatype"":""Int""}}",
                Notes = "MOVE 的输出引脚名是 out1（不是 out）！但 JSON 中用 \"out\" 会自动映射。需 DisabledENO=true。"
            });

            // ══════════ 缩放类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "NORM_X",
                Category = "缩放",
                Description = "标准化（Normalize）。将模拟量输入（0-27648）标准化为 0.0-1.0 的实数。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""NORM_X"",""pins"":{""min"":""0"",""value"":""AI_Signal"",""max"":""27648"",""out"":""归一化结果""},""datatype"":""Int"",""destType"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "min", Type = "输入", Description = "最小值（常量，Int）" },
                    new PinInfo { Name = "value", Type = "输入", Description = "输入值（AI 信号）" },
                    new PinInfo { Name = "max", Type = "输入", Description = "最大值（常量，Int）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "标准化结果（Real 0.0-1.0）" }
                },
                Example = @"{""box"":{""box"":""NORM_X"",""pins"":{""min"":""0"",""value"":""AI_液位"",""max"":""27648"",""out"":""归一化液位""},""datatype"":""Int"",""destType"":""Real""}}",
                Notes = "必须指定 destType:\"Real\"。min/max 用 Int 常量。常配合 SCALE_X 使用。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "SCALE_X",
                Category = "缩放",
                Description = "缩放（Scale）。将 0.0-1.0 的实数缩放到工程范围（如 0.0-100.0）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""SCALE_X"",""pins"":{""min"":""0.0"",""value"":""归一化值"",""max"":""100.0"",""out"":""工程值""},""datatype"":""Real"",""destType"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "min", Type = "输入", Description = "工程最小值（Real 常量）" },
                    new PinInfo { Name = "value", Type = "输入", Description = "归一化输入（0.0-1.0）" },
                    new PinInfo { Name = "max", Type = "输入", Description = "工程最大值（Real 常量）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "工程值输出（Real）" }
                },
                Example = @"{""box"":{""box"":""SCALE_X"",""pins"":{""min"":""0.0"",""value"":""归一化液位"",""max"":""100.0"",""out"":""工程液位""},""datatype"":""Real"",""destType"":""Real""}}",
                Notes = "必须指定 destType。min/max 用 Real 常量。通常先 NORM_X 再 SCALE_X 完成模拟量转换。"
            });

            // ══════════ 其他 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "SEL",
                Category = "选择",
                Description = "选择器（Select）。G=0 选 IN0，G=1 选 IN1。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""SEL"",""pins"":{""G"":""选择开关"",""IN0"":""值0"",""IN1"":""值1"",""OUT"":""输出变量""},""datatype"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "G", Type = "输入", Description = "选择开关（Bool）" },
                    new PinInfo { Name = "IN0", Type = "输入", Description = "G=0 时输出" },
                    new PinInfo { Name = "IN1", Type = "输入", Description = "G=1 时输出" },
                    new PinInfo { Name = "OUT", Type = "输出", Description = "选择结果" }
                },
                Example = @"{""box"":{""box"":""SEL"",""pins"":{""G"":""M_自动模式"",""IN0"":""手动速度"",""IN1"":""自动速度"",""OUT"":""实际速度""},""datatype"":""Real""}}",
                Notes = "Version=1.4。G 是 Bool，IN0/IN1/OUT 类型必须一致。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "LIMIT",
                Category = "限幅",
                Description = "限幅器（Limit）。将 IN 限制在 [MN, MX] 范围内。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""LIMIT"",""pins"":{""MN"":""最小值"",""IN"":""输入值"",""MX"":""最大值"",""OUT"":""输出变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "MN", Type = "输入", Description = "最小值（注意是 MN 不是 MIN！）" },
                    new PinInfo { Name = "IN", Type = "输入", Description = "输入值" },
                    new PinInfo { Name = "MX", Type = "输入", Description = "最大值（注意是 MX 不是 MAX！）" },
                    new PinInfo { Name = "OUT", Type = "输出", Description = "限幅后输出" }
                },
                Example = @"{""box"":{""box"":""LIMIT"",""pins"":{""MN"":""0.0"",""IN"":""计算速度"",""MX"":""100.0"",""OUT"":""实际速度""},""datatype"":""Real""}}",
                Notes = "引脚名是 MN/MX（不是 MIN/MAX）！写错会导致 import 报错。"
            });

            // ══════════ 算术类（补充） ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "SUB",
                Category = "算术",
                Description = "减法（in1 - in2 = out）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""SUB"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "被减数" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "减数" },
                    new PinInfo { Name = "out", Type = "输出", Description = "差（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""SUB"",""pins"":{""in1"":""设定值"",""in2"":""实际值"",""out"":""偏差""},""datatype"":""Real""}}",
                Notes = "out 引脚必须连接变量（不能是常量）。SrcType 由 datatype 自动推断。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "DIV",
                Category = "算术",
                Description = "除法（in1 / in2 = out）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""DIV"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "被除数" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "除数（不能为 0）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "商（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""DIV"",""pins"":{""in1"":""总流量"",""in2"":""2"",""out"":""半流量""},""datatype"":""Real""}}",
                Notes = "out 必须连接变量。除数为 0 时 ENO=FALSE。显式 SrcType=datatype。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "MOD",
                Category = "算术",
                Description = "取模（in1 mod in2 = out），求余数。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""MOD"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "被除数" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "除数" },
                    new PinInfo { Name = "out", Type = "输出", Description = "余数（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""MOD"",""pins"":{""in1"":""计数"",""in2"":""10"",""out"":""个位""},""datatype"":""Int""}}",
                Notes = "out 必须连接变量。常用于循环计数、奇偶判断。显式 SrcType=datatype。"
            });

            // ══════════ 数学函数 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "ABS",
                Category = "数学函数",
                Description = "绝对值（|in| = out）。支持多种数据类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""ABS"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "输入值" },
                    new PinInfo { Name = "out", Type = "输出", Description = "绝对值（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""ABS"",""pins"":{""in"":""偏差"",""out"":""绝对偏差""},""datatype"":""Real""}}",
                Notes = "ABS 支持多种类型（Int/DInt/Real），SrcType 由 datatype 决定。其他数学函数强制 Real。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "SQRT",
                Category = "数学函数",
                Description = "平方根（√in = out）。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""SQRT"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "输入值（Real，>=0）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "平方根（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""SQRT"",""pins"":{""in"":""平方值"",""out"":""均方根""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real（FlgNetBuilder 自动设置）。输入为负时 ENO=FALSE。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "SQR",
                Category = "数学函数",
                Description = "平方（in² = out）。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""SQR"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "输入值（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "平方（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""SQR"",""pins"":{""in"":""半径"",""out"":""半径平方""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。比 in*in 更精确。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "LN",
                Category = "数学函数",
                Description = "自然对数（ln in = out）。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""LN"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "输入值（Real，>0）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "自然对数（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""LN"",""pins"":{""in"":""指数值"",""out"":""对数结果""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。输入 <=0 时 ENO=FALSE。TIA Portal 无常用对数 LOG，需用 LN(x)/LN(10) 转换。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "EXP",
                Category = "数学函数",
                Description = "指数（e^in = out）。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""EXP"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "指数（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "e^in（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""EXP"",""pins"":{""in"":""幂次"",""out"":""指数结果""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "SIN",
                Category = "数学函数",
                Description = "正弦函数（sin(in) = out），输入为弧度。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""SIN"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "弧度值（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "正弦值（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""SIN"",""pins"":{""in"":""角度弧度"",""out"":""正弦值""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。输入是弧度，角度需先除以 180 再乘 π。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "COS",
                Category = "数学函数",
                Description = "余弦函数（cos(in) = out），输入为弧度。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""COS"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "弧度值（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "余弦值（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""COS"",""pins"":{""in"":""角度弧度"",""out"":""余弦值""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。输入是弧度。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "TAN",
                Category = "数学函数",
                Description = "正切函数（tan(in) = out），输入为弧度。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""TAN"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "弧度值（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "正切值（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""TAN"",""pins"":{""in"":""角度弧度"",""out"":""正切值""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。输入是弧度。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "ASIN",
                Category = "数学函数",
                Description = "反正弦（arcsin(in) = out），输出弧度。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""ASIN"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "输入值（Real，[-1,1]）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "反正弦弧度（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""ASIN"",""pins"":{""in"":""正弦值"",""out"":""弧度""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。输入超出 [-1,1] 时 ENO=FALSE。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "ACOS",
                Category = "数学函数",
                Description = "反余弦（arccos(in) = out），输出弧度。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""ACOS"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "输入值（Real，[-1,1]）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "反余弦弧度（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""ACOS"",""pins"":{""in"":""余弦值"",""out"":""弧度""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。输入超出 [-1,1] 时 ENO=FALSE。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "ATAN",
                Category = "数学函数",
                Description = "反正切（arctan(in) = out），输出弧度。强制 Real 类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""ATAN"",""pins"":{""in"":""变量"",""out"":""结果变量""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "输入值（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "反正切弧度（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""ATAN"",""pins"":{""in"":""斜率"",""out"":""弧度""},""datatype"":""Real""}}",
                Notes = "强制 SrcType=Real。结果范围 (-π/2, π/2)。"
            });

            // ══════════ 比较类（补充） ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "EQ",
                Category = "比较",
                Description = "等于比较（=）。in1 == in2 时输出 TRUE。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""EQ"",""pins"":{""in1"":""变量1"",""in2"":""变量2""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能（来自上游 out）" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "比较数1（Real/Int/DInt）" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "比较数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "比较结果（Bool）" }
                },
                Example = @"{""box"":{""box"":""EQ"",""pins"":{""in1"":""实际值"",""in2"":""设定值""},""datatype"":""Real""}}",
                Notes = "比较盒用 out（Bool 逻辑流），不用 eno。pre 连电源轨或上游逻辑。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "NE",
                Category = "比较",
                Description = "不等于比较（<>）。in1 != in2 时输出 TRUE。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""NE"",""pins"":{""in1"":""变量1"",""in2"":""变量2""},""datatype"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "比较数1" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "比较数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "结果（Bool）" }
                },
                Example = @"{""box"":{""box"":""NE"",""pins"":{""in1"":""状态码"",""in2"":""0""},""datatype"":""Int""}}",
                Notes = "比较盒用 out，不用 eno。pre 连电源轨或上游逻辑。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "GE",
                Category = "比较",
                Description = "大于等于比较（>=）。in1 >= in2 时输出 TRUE。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""GE"",""pins"":{""in1"":""变量1"",""in2"":""变量2""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "比较数1" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "比较数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "结果（Bool）" }
                },
                Example = @"{""box"":{""box"":""GE"",""pins"":{""in1"":""液位"",""in2"":""80.0""},""datatype"":""Real""}}",
                Notes = "比较盒用 out，不用 eno。常用于上限报警。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "LE",
                Category = "比较",
                Description = "小于等于比较（<=）。in1 <= in2 时输出 TRUE。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""LE"",""pins"":{""in1"":""变量1"",""in2"":""变量2""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "比较数1" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "比较数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "结果（Bool）" }
                },
                Example = @"{""box"":{""box"":""LE"",""pins"":{""in1"":""液位"",""in2"":""20.0""},""datatype"":""Real""}}",
                Notes = "比较盒用 out，不用 eno。常用于下限报警。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "INRANGE",
                Category = "比较",
                Description = "范围内判断（min <= in <= max 时输出 TRUE）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""INRANGE"",""pins"":{""min"":""最小值"",""in"":""输入值"",""max"":""最大值""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能（连电源轨或上游 out）" },
                    new PinInfo { Name = "min", Type = "输入", Description = "范围下限" },
                    new PinInfo { Name = "in", Type = "输入", Description = "被判断的值" },
                    new PinInfo { Name = "max", Type = "输入", Description = "范围上限" },
                    new PinInfo { Name = "out", Type = "输出", Description = "在范围内时为 TRUE（Bool）" }
                },
                Example = @"{""box"":{""box"":""INRANGE"",""pins"":{""min"":""20.0"",""in"":""液位"",""max"":""80.0""},""datatype"":""Real""}}",
                Notes = "范围判断指令，引脚为 pre/in/min/max/out（无 en/eno）。pre 连电源轨。与 OUTRANGE 语义相反。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "OUTRANGE",
                Category = "比较",
                Description = "范围外判断（in < min 或 in > max 时输出 TRUE）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""OUTRANGE"",""pins"":{""min"":""最小值"",""in"":""输入值"",""max"":""最大值""},""datatype"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "pre", Type = "逻辑流", Description = "使能（连电源轨或上游 out）" },
                    new PinInfo { Name = "min", Type = "输入", Description = "范围下限" },
                    new PinInfo { Name = "in", Type = "输入", Description = "被判断的值" },
                    new PinInfo { Name = "max", Type = "输入", Description = "范围上限" },
                    new PinInfo { Name = "out", Type = "输出", Description = "在范围外时为 TRUE（Bool）" }
                },
                Example = @"{""box"":{""box"":""OUTRANGE"",""pins"":{""min"":""20.0"",""in"":""液位"",""max"":""80.0""},""datatype"":""Real""}}",
                Notes = "范围判断指令，引脚为 pre/in/min/max/out（无 en/eno）。与 INRANGE 语义相反，常用于越限报警。"
            });

            // ══════════ 逻辑类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "NOT",
                Category = "逻辑",
                Description = "逻辑非（取反）。in 为 TRUE 时 out 为 FALSE，反之亦然。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""NOT"",""pins"":{""in"":""输入变量""}}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "in", Type = "逻辑流", Description = "输入（连上游 out 或电源轨）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "取反结果（Bool 逻辑流）" }
                },
                Example = @"{""box"":{""box"":""NOT"",""pins"":{""in"":""M_自动模式""}}}",
                Notes = "NOT 是特殊指令：无 en/eno，无 TemplateValue。触发引脚为 in，输出引脚为 out（Bool 逻辑流）。也可用 negateOutput:true 在其他 box 输出端插入 NOT。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "AND",
                Category = "逻辑",
                Description = "字逻辑与（in1 & in2 = out）。按位与运算。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""AND"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Word""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "操作数1（Word/DWord）" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "操作数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "按位与结果（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""AND"",""pins"":{""in1"":""状态字"",""in2"":""16#000F"",""out"":""低4位""},""datatype"":""Word""}}",
                Notes = "字逻辑指令（非 Bool 逻辑流）。Card=2，SrcType=datatype。常用于位掩码提取。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "OR",
                Category = "逻辑",
                Description = "字逻辑或（in1 | in2 = out）。按位或运算。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""OR"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Word""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "操作数1（Word/DWord）" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "操作数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "按位或结果（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""OR"",""pins"":{""in1"":""状态字"",""in2"":""16#0001"",""out"":""置位结果""},""datatype"":""Word""}}",
                Notes = "字逻辑指令（非 Bool 逻辑流）。Card=2，SrcType=datatype。常用于位掩码置位。注意：Bool 触点并联请用 O 门或 branch，不是此 OR 指令。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "XOR",
                Category = "逻辑",
                Description = "字逻辑异或（in1 ^ in2 = out）。按位异或运算。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""XOR"",""pins"":{""in1"":""变量1"",""in2"":""变量2"",""out"":""结果变量""},""datatype"":""Word""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in1", Type = "输入", Description = "操作数1（Word/DWord）" },
                    new PinInfo { Name = "in2", Type = "输入", Description = "操作数2" },
                    new PinInfo { Name = "out", Type = "输出", Description = "按位异或结果（必须连接变量）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出" }
                },
                Example = @"{""box"":{""box"":""XOR"",""pins"":{""in1"":""状态字"",""in2"":""16#00FF"",""out"":""翻转结果""},""datatype"":""Word""}}",
                Notes = "字逻辑指令（非 Bool 逻辑流）。Card=2，SrcType=datatype。常用于位翻转。"
            });

            // ══════════ 转换类 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "CONVERT",
                Category = "转换",
                Description = "类型转换（in → out）。将输入转换为 destType 指定的类型。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""CONVERT"",""pins"":{""in"":""源变量"",""out"":""目标变量""},""datatype"":""Int"",""destType"":""Real""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "源数据（datatype 类型）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "转换结果（destType 类型）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出（DisabledENO=true）" }
                },
                Example = @"{""box"":{""box"":""CONVERT"",""pins"":{""in"":""AI_原始值"",""out"":""实数值""},""datatype"":""Int"",""destType"":""Real""}}",
                Notes = "需要 DisabledENO=true（FlgNetBuilder 自动设置）。SrcType=datatype，DestType=destType。如 Int→Real、Real→Int 等。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "ROUND",
                Category = "转换",
                Description = "四舍五入取整（Real → Int/DInt）。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""ROUND"",""pins"":{""in"":""实数变量"",""out"":""整数变量""},""datatype"":""Real"",""destType"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "实数输入（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "四舍五入后的整数" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出（DisabledENO=true）" }
                },
                Example = @"{""box"":{""box"":""ROUND"",""pins"":{""in"":""计算值"",""out"":""整数值""},""datatype"":""Real"",""destType"":""Int""}}",
                Notes = "需要 DisabledENO=true。SrcType=Real，DestType=Int/DInt。小数部分 >=0.5 进位。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "TRUNC",
                Category = "转换",
                Description = "截断取整（Real → Int/DInt）。直接丢弃小数部分。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""TRUNC"",""pins"":{""in"":""实数变量"",""out"":""整数变量""},""datatype"":""Real"",""destType"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "实数输入（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "截断后的整数（向 0 取整）" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出（DisabledENO=true）" }
                },
                Example = @"{""box"":{""box"":""TRUNC"",""pins"":{""in"":""计算值"",""out"":""整数值""},""datatype"":""Real"",""destType"":""Int""}}",
                Notes = "需要 DisabledENO=true。向零方向截断（如 3.7→3, -3.7→-3）。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "CEIL",
                Category = "转换",
                Description = "向上取整（Real → Int/DInt）。取大于等于输入的最小整数。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""CEIL"",""pins"":{""in"":""实数变量"",""out"":""整数变量""},""datatype"":""Real"",""destType"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "实数输入（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "向上取整结果" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出（DisabledENO=true）" }
                },
                Example = @"{""box"":{""box"":""CEIL"",""pins"":{""in"":""计算值"",""out"":""整数值""},""datatype"":""Real"",""destType"":""Int""}}",
                Notes = "需要 DisabledENO=true。如 3.1→4, -3.9→-3。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "FLOOR",
                Category = "转换",
                Description = "向下取整（Real → Int/DInt）。取小于等于输入的最大整数。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""FLOOR"",""pins"":{""in"":""实数变量"",""out"":""整数变量""},""datatype"":""Real"",""destType"":""Int""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "en", Type = "逻辑流", Description = "使能输入" },
                    new PinInfo { Name = "in", Type = "输入", Description = "实数输入（Real）" },
                    new PinInfo { Name = "out", Type = "输出", Description = "向下取整结果" },
                    new PinInfo { Name = "eno", Type = "输出", Description = "使能输出（DisabledENO=true）" }
                },
                Example = @"{""box"":{""box"":""FLOOR"",""pins"":{""in"":""计算值"",""out"":""整数值""},""datatype"":""Real"",""destType"":""Int""}}",
                Notes = "需要 DisabledENO=true。如 3.9→3, -3.1→-4。"
            });

            // ══════════ 定时器（补充） ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "TONR",
                Category = "定时器",
                Description = "保持型通电延时定时器（Timer On-Delay Retentive）。累计延时，需 R 复位。",
                Verified = true,
                JsonTemplate = @"{""sysCall"":{""inst"":""TONR"",""instance"":""Tonr_1"",""pins"":{""IN"":""触发变量"",""PT"":""T#5S"",""Q"":""输出变量""},""datatype"":""Time""}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "IN", Type = "逻辑流", Description = "触发输入（通过触点连接，不能直接连变量）" },
                    new PinInfo { Name = "PT", Type = "输入", Description = "预设时间（T#开头的 Time 常量）" },
                    new PinInfo { Name = "Q", Type = "输出", Description = "累计达 PT 后输出 TRUE" },
                    new PinInfo { Name = "ET", Type = "输出", Description = "已累计时间（自动悬空 OpenCon）" },
                    new PinInfo { Name = "R", Type = "逻辑流", Description = "复位输入（未提供时自动 OpenCon 悬空，不复位）" }
                },
                Example = @"{""sysCall"":{""inst"":""TONR"",""instance"":""Tonr_累计"",""pins"":{""IN"":""M_运行中"",""PT"":""T#10S"",""Q"":""M_累计到达"",""R"":""M_复位""},""datatype"":""Time""}}",
                Notes = "与 TON 不同：IN 变 FALSE 时 ET 保持，不归零。需 R 引脚复位（未提供则自动悬空）。需在 FB Static 区声明 instance:TONR_TIME。FC 中需用全局实例。time_type 自动设为 Time。"
            });

            // ══════════ 触发器 ══════════
            lib.Add(new InstructionTemplate
            {
                Name = "SR",
                Category = "触发器",
                Description = "SR 触发器（置位优先，Set-Dominant）。s 与 r1 同时为 TRUE 时，结果为 TRUE。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""SR"",""instance"":""Sr_1"",""pins"":{""s"":""置位信号"",""r1"":""复位信号"",""operand"":""状态变量""}}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "s", Type = "逻辑流", Description = "置位输入（触发引脚，连上游 out）" },
                    new PinInfo { Name = "r1", Type = "逻辑流", Description = "复位输入" },
                    new PinInfo { Name = "operand", Type = "输出", Description = "状态变量（Bool，被置位/复位）" },
                    new PinInfo { Name = "q", Type = "输出", Description = "状态输出（Bool 逻辑流，自动悬空或连下游）" }
                },
                Example = @"{""box"":{""box"":""SR"",""instance"":""Sr_自锁"",""pins"":{""s"":""M_启动"",""r1"":""M_停止"",""operand"":""M_运行""}}}",
                Notes = "特殊逻辑元素：无 en/eno，无 TemplateValue。触发引脚为 s（连逻辑流）。置位优先：s=r1=TRUE 时 operand=TRUE。需在 Static 区声明 instance（SR 类型）。q 引脚自动处理悬空。"
            });

            lib.Add(new InstructionTemplate
            {
                Name = "RS",
                Category = "触发器",
                Description = "RS 触发器（复位优先，Reset-Dominant）。r 与 s1 同时为 TRUE 时，结果为 FALSE。",
                Verified = true,
                JsonTemplate = @"{""box"":{""box"":""RS"",""instance"":""Rs_1"",""pins"":{""r"":""复位信号"",""s1"":""置位信号"",""operand"":""状态变量""}}}",
                Pins = new List<PinInfo>
                {
                    new PinInfo { Name = "r", Type = "逻辑流", Description = "复位输入（触发引脚，连上游 out）" },
                    new PinInfo { Name = "s1", Type = "逻辑流", Description = "置位输入" },
                    new PinInfo { Name = "operand", Type = "输出", Description = "状态变量（Bool，被置位/复位）" },
                    new PinInfo { Name = "q", Type = "输出", Description = "状态输出（Bool 逻辑流，自动悬空或连下游）" }
                },
                Example = @"{""box"":{""box"":""RS"",""instance"":""Rs_故障"",""pins"":{""r"":""M_复位"",""s1"":""M_报警"",""operand"":""M_故障标志""}}}",
                Notes = "特殊逻辑元素：无 en/eno，无 TemplateValue。触发引脚为 r（连逻辑流）。复位优先：r=s1=TRUE 时 operand=FALSE（适合故障锁存）。需在 Static 区声明 instance（RS 类型）。q 引脚自动处理悬空。"
            });

            return lib;
        }

        /// <summary>从已加载程序集中解析 Siemens.Engineering 类型（全名）。</summary>
        private static Type? ResolveFeatureType(string typeFullName)
        {
            try
            {
                var t = Type.GetType(typeFullName + ", Siemens.Engineering");
                if (t != null) return t;
            }
            catch { }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(typeFullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>解析 PLC（plcName 为空时取第一个 PLC），未找到返回 null。</summary>
        private PlcSoftware? ResolvePlc(string? plcName)
        {
            RequireProject();
            if (string.IsNullOrEmpty(plcName))
                return GetPlcSoftwareList().FirstOrDefault();
            return FindPlcByName(plcName);
        }

        /// <summary>反射调用 source.GetService&lt;T&gt;()，容错返回 null。</summary>
        private static object? TryGetService(object source, Type serviceType)
        {
            try
            {
                var getServiceImpl = source.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);
                if (getServiceImpl != null)
                {
                    var generic = getServiceImpl.MakeGenericMethod(serviceType);
                    return generic.Invoke(source, null);
                }
            }
            catch { }
            return null;
        }

        private static bool IsLadConstant(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || v.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return true;
            if (Regex.IsMatch(v, @"^[+-]?(\d+(\.\d+)?|\.\d+)$")) return true;
            // 仅开放 Builder 已实现并经过样例核对的 IEC Time 形式。
            return Regex.IsMatch(v, @"^(T|TIME)#", RegexOptions.IgnoreCase);
        }

        private bool TryGetExistingHmiCanvasSize(HmiTarget hmi, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                // ★修复★ 空画面 HMI（新建设备）改为导出画面模板探测真实画布：
                // 新 HMI 通常自带默认模板，模板 XML 的 AttributeList 含设备真实
                // Width/Height。此前空画面直接返回 false，回退链会采信 IR 请求的
                // 1280x800 之类的尺寸，导入时被 TIA 以 "The screen size does not
                // match the device" 拒绝。
                string? temp = null;
                var screen = GetAllScreens(hmi.ScreenFolder).FirstOrDefault();
                if (screen != null)
                {
                    temp = Path.Combine(Path.GetTempPath(), $"tia_hmi_canvas_{Guid.NewGuid():N}.xml");
                    screen.Export(new FileInfo(temp), ExportOptions.WithDefaults);
                }
                else
                {
                    var tplFolder = hmi.ScreenTemplateFolder;
                    var scrProp = tplFolder.GetType().GetProperty("Screens");
                    if (scrProp?.GetValue(tplFolder) is not System.Collections.IEnumerable scrList) return false;
                    object? firstTemplate = null;
                    foreach (var s in scrList) { firstTemplate = s; break; }
                    if (firstTemplate == null) return false;
                    var exportMethod = firstTemplate.GetType().GetMethod("Export",
                        new[] { typeof(FileInfo), typeof(ExportOptions) });
                    if (exportMethod == null) return false;
                    temp = Path.Combine(Path.GetTempPath(), $"tia_hmi_canvas_{Guid.NewGuid():N}.xml");
                    exportMethod.Invoke(firstTemplate, new object[] { new FileInfo(temp), ExportOptions.WithDefaults });
                }

                try
                {
                    var doc = XDocument.Load(temp);
                    // 画面根元素是 Hmi.Screen.Screen，模板是 ScreenTemplate——统一按
                    // "同时包含 Width 与 Height 子元素的 AttributeList"探测。
                    var attrs = doc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "AttributeList"
                                             && e.Elements().Any(x => x.Name.LocalName == "Width")
                                             && e.Elements().Any(x => x.Name.LocalName == "Height"));
                    if (attrs == null) return false;
                    var wText = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Width")?.Value;
                    var hText = attrs.Elements().FirstOrDefault(e => e.Name.LocalName == "Height")?.Value;
                    return int.TryParse(wText, NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
                           && int.TryParse(hText, NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
                           && width > 0 && height > 0;
                }
                finally
                {
                    TryDelete3(temp);
                }
            }
            catch
            {
                width = 0;
                height = 0;
                return false;
            }
        }

        private static bool TryReadSuccess(string json, out bool success, out JObject? payload)
        {
            success = false;
            payload = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                payload = JObject.Parse(json);
                var token = payload["success"];
                if (token == null || token.Type != JTokenType.Boolean) return false;
                success = token.Value<bool>();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ValidateBlockInstanceCompatibility(LadNetworkDef def, string blockType, out string error)
        {
            var localError = "";
            var restrictedBlock = blockType.Equals("OB", StringComparison.OrdinalIgnoreCase)
                                  || blockType.Equals("FC", StringComparison.OrdinalIgnoreCase);

            bool Check(string instruction, string? instance, string? instanceScope, string path)
            {
                if (!IsBackgroundInstruction(instruction)) return true;
                var global = string.Equals(instanceScope, "global", StringComparison.OrdinalIgnoreCase);
                if (restrictedBlock && !global)
                {
                    localError = $"{path}: {blockType} 不支持 Static 多重实例。请提供 instanceScope='global' 和已存在的全局实例 DB，或把逻辑放入 FB。";
                    return false;
                }
                if (global && string.IsNullOrWhiteSpace(instance))
                {
                    localError = $"{path}: 全局实例必须显式提供 instance 名称；服务器不会猜测或自动创建实例 DB。";
                    return false;
                }
                return true;
            }

            if (def.sysCall != null && !Check(def.sysCall.inst, def.sysCall.instance, def.sysCall.instanceScope, "sysCall"))
            {
                error = localError;
                return false;
            }

            bool Walk(IEnumerable<RungElement>? elements, string path)
            {
                if (elements == null) return true;
                var index = 0;
                foreach (var element in elements)
                {
                    if (element.box != null && !Check(element.box.box, element.box.instance, element.box.instanceScope, $"{path}[{index}].box"))
                        return false;
                    if (element.call != null && !string.Equals(element.call.blockType, "FC", StringComparison.OrdinalIgnoreCase))
                    {
                        var global = string.Equals(element.call.instanceScope, "global", StringComparison.OrdinalIgnoreCase);
                        if (restrictedBlock && !global)
                        {
                            localError = $"{path}[{index}].call: {blockType} 中调用 FB 必须使用 instanceScope='global' 和已存在的实例 DB，不能声明 Static 多重实例。";
                            return false;
                        }
                        if (string.IsNullOrWhiteSpace(element.call.instance))
                        {
                            localError = $"{path}[{index}].call: FB 调用必须显式提供 instance 名称。";
                            return false;
                        }
                    }
                    if (element.branch?.branch != null)
                    {
                        for (var b = 0; b < element.branch.branch.Count; b++)
                            if (!Walk(element.branch.branch[b], $"{path}[{index}].branch[{b}]")) return false;
                    }
                    index++;
                }
                return true;
            }

            var ok = Walk(def.rung, "rung");
            error = localError;
            return ok;
        }

        public string ValidateHmiScreenSpecJson(string specJson, int? screenWidth = null, int? screenHeight = null)
        {
            try
            {
                var spec = JObject.Parse(specJson);
                var width = screenWidth ?? spec["width"]?.Value<int?>() ?? 800;
                var height = screenHeight ?? spec["height"]?.Value<int?>() ?? 480;
                if (width <= 0 || height <= 0) return Err($"画布尺寸无效: {width}x{height}");

                var warnings = new List<string>();
                var errors = new List<string>();
                var normalized = new List<JObject>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var groupItems = new List<JObject>();
                ValidateHmiLayerDefinitions(spec, errors);
                var items = spec["items"] as JArray ?? new JArray();

                foreach (var token in items)
                {
                    if (token is not JObject source)
                    {
                        errors.Add("items 中存在非对象项");
                        continue;
                    }
                    var item = (JObject)source.DeepClone();
                    var name = item["name"]?.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) errors.Add("控件缺少 name");
                    else if (!names.Add(name)) errors.Add($"控件名称重复: {name}");

                    var canonicalType = HmiControlCatalog.Canonicalize(item["type"]?.ToString());
                    if (string.IsNullOrEmpty(canonicalType))
                    {
                        errors.Add($"控件 '{name}' 类型 '{item["type"]}' 不受支持。支持: {string.Join(", ", HmiControlCatalog.CanonicalTypes)}");
                        continue;
                    }
                    item["type"] = canonicalType;
                    var hasExplicitLayer = item["layer"] != null;
                    var layer = item["layer"]?.Value<int?>() ?? 0;
                    if (layer < 0 || layer > 31)
                    {
                        errors.Add($"控件 '{name}' 的 layer={layer} 超出 0..31");
                        continue;
                    }

                    if (HmiControlCatalog.IsGroup(canonicalType))
                    {
                        // 未显式指定 Group.layer 时，第二阶段从首个成员推断；不能提前强制为 0。
                        if (hasExplicitLayer) item["layer"] = layer;
                        else item.Remove("layer");
                        if (item["members"] is not JArray members || members.Count == 0)
                            errors.Add($"Group '{name}' 必须提供非空 members[]");
                        groupItems.Add(item);
                        normalized.Add(item);
                        continue;
                    }
                    item["layer"] = layer;

                    if (!TryNormalizeHmiItemLayout(item, width, height, warnings, out var layoutError))
                    {
                        errors.Add(layoutError);
                        continue;
                    }
                    if (!TryResolveHmiItemOverlap(item, normalized.Where(x => !HmiControlCatalog.IsGroup(HmiControlCatalog.Canonicalize(x["type"]?.ToString()))).ToList(), width, height, warnings, out var overlapError))
                    {
                        errors.Add(overlapError);
                        continue;
                    }

                    ValidateHmiItemSemantics(item, canonicalType, errors);
                    normalized.Add(item);
                }

                // 重复名称已经记录为校验错误；这里不能再用 ToDictionary 抛异常并吞掉完整错误列表。
                var byName = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var normalizedItem in normalized)
                {
                    var normalizedName = normalizedItem["name"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(normalizedName) && !byName.ContainsKey(normalizedName))
                        byName[normalizedName] = normalizedItem;
                }
                var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groupItems)
                {
                    var groupName = group["name"]?.ToString() ?? "";
                    var groupLayer = group["layer"]?.Value<int?>();
                    foreach (var memberToken in group["members"] as JArray ?? new JArray())
                    {
                        var memberName = memberToken?.ToString() ?? "";
                        if (!byName.TryGetValue(memberName, out var member))
                        {
                            errors.Add($"Group '{groupName}' 引用了不存在的成员 '{memberName}'");
                            continue;
                        }
                        if (HmiControlCatalog.IsGroup(HmiControlCatalog.Canonicalize(member["type"]?.ToString())))
                            errors.Add($"Group '{groupName}' 当前不支持嵌套 Group 成员 '{memberName}'");
                        if (!consumed.Add(memberName))
                            errors.Add($"控件 '{memberName}' 被多个 Group 重复引用");
                        var memberLayer = member["layer"]?.Value<int?>() ?? 0;
                        if (groupLayer.HasValue && groupLayer.Value != memberLayer)
                            errors.Add($"Group '{groupName}' 与成员 '{memberName}' 不在同一 layer");
                        groupLayer ??= memberLayer;
                    }
                    group["layer"] = groupLayer ?? 0;
                    ValidateHmiAnimations(group, errors);
                }

                var tagDependencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var pictureDependencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var textListDependencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var screenDependencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in normalized)
                    CollectHmiDependencies(item, tagDependencies, pictureDependencies, textListDependencies, screenDependencies);

                return JsonConvert.SerializeObject(new
                {
                    success = errors.Count == 0,
                    valid = errors.Count == 0,
                    schemaVersion = "hmi-screen-spec/1.2",
                    screenWidth = width,
                    screenHeight = height,
                    itemCount = normalized.Count,
                    layerCount = normalized.Select(x => x["layer"]?.Value<int?>() ?? 0).Distinct().Count(),
                    errors,
                    warnings,
                    dependencies = new
                    {
                        tags = tagDependencies,
                        pictures = pictureDependencies,
                        textLists = textListDependencies,
                        screens = screenDependencies
                    },
                    normalizedItems = normalized
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err("HMI 规格校验失败: " + ex.Message); }
        }

        private static bool ValidateLadNetworkDefinition(
            LadNetworkDef def,
            out List<string> errors,
            out List<string> warnings)
        {
            errors = new List<string>();
            warnings = new List<string>();

            if (def.networks != null)
            {
                if (def.networks.Count == 0)
                    errors.Add("networks 为空");
                for (var i = 0; i < def.networks.Count; i++)
                {
                    ValidateLadNetworkDefinition(def.networks[i], out var childErrors, out var childWarnings);
                    errors.AddRange(childErrors.Select(x => $"network[{i}]: {x}"));
                    warnings.AddRange(childWarnings.Select(x => $"network[{i}]: {x}"));
                }
                return errors.Count == 0;
            }

            var representations = 0;
            if (def.rung != null) representations++;
            if (def.sysCall != null) representations++;
            if (def.contacts != null || def.coils != null || def.calls != null || def.branches != null || def.tail != null) representations++;
            if (representations == 0) errors.Add("网络没有任何逻辑内容");
            if (representations > 1) errors.Add("同一网络不能同时混用 rung、sysCall 和旧 contacts/coils 表示法");

            var declarations = new Dictionary<string, LadVariableDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in def.variables ?? Enumerable.Empty<LadVariableDef>())
            {
                if (!IsValidInterfaceName(variable.name))
                {
                    errors.Add($"variables 中存在非法变量名 '{variable.name}'；仅允许 Unicode 字母、数字和下划线，且不能以数字开头");
                    continue;
                }
                if (!IsValidSectionName(variable.section))
                    errors.Add($"变量 '{variable.name}' 的 section '{variable.section}' 无效");
                if (!IsSafeDatatype(variable.datatype))
                    errors.Add($"变量 '{variable.name}' 的 datatype '{variable.datatype}' 无效或包含危险字符");
                if (declarations.TryGetValue(variable.name, out var prior))
                {
                    if (!string.Equals(prior.datatype, variable.datatype, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(prior.section, variable.section, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"变量 '{variable.name}' 被重复声明且类型/区域冲突");
                    else
                        errors.Add($"变量 '{variable.name}' 被重复声明");
                }
                else
                {
                    declarations[variable.name] = variable;
                }
            }

            var writers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (def.rung != null)
                ValidateRungPath(def.rung, "rung", errors, warnings, writers, usedVariables);

            if (def.sysCall != null)
            {
                ValidatePins(def.sysCall.inst, def.sysCall.pins, "sysCall", errors);
                if (def.sysCall.pins != null)
                {
                    foreach (var value in def.sysCall.pins.Values.Where(v => !string.IsNullOrWhiteSpace(v) && !IsLadConstant(v)))
                        usedVariables.Add(value);
                }
            }

            if (def.coils != null)
            {
                foreach (var coil in def.coils)
                {
                    if (string.IsNullOrWhiteSpace(coil.variable)) errors.Add("旧格式 coils 中存在线圈变量为空");
                    else if (writers.ContainsKey(coil.variable)) errors.Add($"变量 '{coil.variable}' 被重复写入");
                    else writers[coil.variable] = coil.name ?? "Coil";
                }
            }

            if (def.calls != null)
            {
                foreach (var call in def.calls)
                {
                    if (string.IsNullOrWhiteSpace(call.blockName)) errors.Add("旧格式 calls 中存在空 blockName");
                    var isFc = string.Equals(call.blockType, "FC", StringComparison.OrdinalIgnoreCase);
                    if (!isFc)
                    {
                        if (string.IsNullOrWhiteSpace(call.instanceName))
                            errors.Add($"FB 调用 '{call.blockName}' 必须显式提供 instanceName");
                        if (!string.Equals(call.instanceScope, "local", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(call.instanceScope, "global", StringComparison.OrdinalIgnoreCase))
                            errors.Add($"FB 调用 '{call.blockName}' 必须显式提供 instanceScope=local/global");
                    }
                    foreach (var pin in call.pins ?? Enumerable.Empty<LadPinDef>())
                    {
                        if (string.IsNullOrWhiteSpace(pin.name) || string.IsNullOrWhiteSpace(pin.variable))
                            errors.Add($"调用 '{call.blockName}' 存在空引脚名称或变量");
                        if (string.IsNullOrWhiteSpace(pin.datatype) || !IsSafeDatatype(pin.datatype))
                            errors.Add($"调用 '{call.blockName}' 的引脚 '{pin.name}' 必须提供合法 datatype");
                        if (string.Equals(pin.direction, "inout", StringComparison.OrdinalIgnoreCase))
                            errors.Add($"调用 '{call.blockName}' 的 InOut 引脚 '{pin.name}' 尚未通过 TIA 回归，当前版本拒绝生成");
                        else if (!string.Equals(pin.direction, "in", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(pin.direction, "out", StringComparison.OrdinalIgnoreCase))
                            errors.Add($"调用 '{call.blockName}' 的引脚 '{pin.name}' direction 只能是 in/out");
                        if (!string.IsNullOrWhiteSpace(pin.variable) && IsLadConstant(pin.variable))
                            errors.Add($"调用 '{call.blockName}' 的旧 calls 表示法暂不支持字面量参数 '{pin.variable}'");
                        else if (!string.IsNullOrWhiteSpace(pin.variable))
                            usedVariables.Add(pin.variable);
                    }
                }
            }

            foreach (var used in usedVariables)
            {
                if (!declarations.ContainsKey(used) && !IsLadConstant(used))
                    warnings.Add($"变量 '{used}' 未在当前网络 variables 中声明；请确认它是 PLC 全局变量或块接口变量");
            }

            return errors.Count == 0;
        }

        public string ValidateLadNetworkJson(string networkJson)
        {
            try
            {
                var def = JsonConvert.DeserializeObject<LadNetworkDef>(networkJson)
                          ?? throw new Exception("无法解析 networkJson");
                var networks = new List<LadNetworkDef> { def };
                var valid = PrepareLadProgram(networks, null, null, out var errors, out var warnings);
                return JsonConvert.SerializeObject(new
                {
                    success = valid,
                    valid,
                    validationErrors = errors,
                    validationWarnings = warnings,
                    normalizedNetwork = networks[0],
                    note = "validate 与 apply 共用 PrepareLadProgram；项目写入时还会增加目标块类型和既有实例冲突检查"
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err("LAD 校验失败: " + ex.Message); }
        }

        public string ValidateLadProgramJson(string networksJson)
        {
            try
            {
                var networks = JsonConvert.DeserializeObject<List<LadNetworkDef>>(networksJson)
                               ?? throw new Exception("无法解析 networksJson");
                var valid = PrepareLadProgram(networks, null, null, out var errors, out var warnings);
                var writers = networks.SelectMany(CollectLadWriters).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
                return JsonConvert.SerializeObject(new
                {
                    success = valid,
                    valid,
                    networkCount = networks.Count,
                    errors,
                    warnings,
                    normalizedNetworks = networks,
                    outputWriters = writers,
                    testPlan = BuildDefaultPlcTestPlan(writers)
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err("LAD 程序校验失败: " + ex.Message); }
        }

        internal class InstructionTemplate
        {
            public string Name { get; set; } = "";
            public string Category { get; set; } = "";
            public string Description { get; set; } = "";
            public bool Verified { get; set; }
            public string JsonTemplate { get; set; } = "";
            public List<PinInfo> Pins { get; set; } = new List<PinInfo>();
            public string Example { get; set; } = "";
            public string Notes { get; set; } = "";
        }

        internal class PinInfo
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public string Description { get; set; } = "";
        }

        private static bool IsBackgroundInstruction(string? instruction)
        {
            return LadInstructionCatalog.IsBackground(instruction);
        }

        private static void ValidateHmiLayerDefinitions(JObject spec, ICollection<string> errors)
        {
            var indices = new HashSet<int>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in (spec["layers"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var index = layer["index"]?.Value<int?>();
                if (!index.HasValue || index.Value < 0 || index.Value > 31)
                {
                    errors.Add("screen.layers 中每一层都必须提供 0..31 的 index");
                    continue;
                }
                if (!indices.Add(index.Value)) errors.Add($"screen.layers 存在重复 index={index.Value}");
                var name = layer["name"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(name) && !names.Add(name)) errors.Add($"screen.layers 存在重复名称 '{name}'");
            }
        }

        /// <summary>
        /// 对单个 HMI 控件做标准化。默认自动扩大文本容器；空间不足时自动缩小字号；
        /// 最后将控件平移/裁剪到画面边界内。
        /// </summary>
        private static bool TryNormalizeHmiItemLayout(
            JObject item,
            int screenWidth,
            int screenHeight,
            IList<string> warnings,
            out string error)
        {
            error = "";
            var rawType = (item["type"]?.Value<string>() ?? "").Trim();
            var type = HmiControlCatalog.Canonicalize(rawType);
            if (string.IsNullOrEmpty(type))
            {
                error = string.IsNullOrWhiteSpace(rawType) ? "控件缺少 type" : $"不支持的控件类型: {rawType}";
                return false;
            }
            item["type"] = type;
            if (HmiControlCatalog.IsGroup(type)) return true;

            const int margin = 4;
            var left = GetInt(item, "left", margin);
            var top = GetInt(item, "top", margin);
            var width = GetInt(item, "width", type is "button" or "navigation" ? 100 : 100);
            var height = GetInt(item, "height", type is "button" or "navigation" ? 44 : type == "line" ? 0 : 30);

            if (type == "indicator")
            {
                var radius = Math.Max(4, GetInt(item, "radius", 20));
                width = radius * 2;
                height = radius * 2;
            }

            if (screenWidth <= margin * 2 || screenHeight <= margin * 2)
            {
                error = $"画面尺寸无效: {screenWidth}x{screenHeight}";
                return false;
            }

            var name = item["name"]?.Value<string>() ?? type;
            if (type == "line")
            {
                var startLeft = GetInt(item, "startLeft", left);
                var startTop = GetInt(item, "startTop", top);
                var endLeft = GetInt(item, "endLeft", left + Math.Max(1, width));
                var endTop = GetInt(item, "endTop", top + height);
                if (startLeft == endLeft && startTop == endTop)
                {
                    error = $"线控件 '{name}' 的起点和终点不能相同";
                    return false;
                }
                left = Math.Min(startLeft, endLeft);
                top = Math.Min(startTop, endTop);
                width = Math.Max(1, Math.Abs(endLeft - startLeft));
                height = Math.Max(1, Math.Abs(endTop - startTop));
                var normalizedStartLeft = Math.Max(margin, Math.Min(startLeft, screenWidth - margin));
                var normalizedStartTop = Math.Max(margin, Math.Min(startTop, screenHeight - margin));
                var normalizedEndLeft = Math.Max(margin, Math.Min(endLeft, screenWidth - margin));
                var normalizedEndTop = Math.Max(margin, Math.Min(endTop, screenHeight - margin));
                item["startLeft"] = normalizedStartLeft;
                item["startTop"] = normalizedStartTop;
                item["endLeft"] = normalizedEndLeft;
                item["endTop"] = normalizedEndTop;
                left = Math.Min(normalizedStartLeft, normalizedEndLeft);
                top = Math.Min(normalizedStartTop, normalizedEndTop);
                width = Math.Max(1, Math.Abs(normalizedEndLeft - normalizedStartLeft));
                height = Math.Max(1, Math.Abs(normalizedEndTop - normalizedStartTop));
            }
            else if (width <= 0 || height <= 0)
            {
                error = $"控件 '{name}' 的 width/height 必须大于 0";
                return false;
            }

            var isTextual = HmiControlCatalog.IsTextual(type);
            if (isTextual)
            {
                var defaultFont = (type == "iofield" || type == "symboliciofield") ? 24 : 14;
                var requestedFont = ParseFontSize(item, defaultFont);
                var recommendedMax = (type == "iofield" || type == "symboliciofield")
                    ? (screenHeight <= 480 ? 32 : 40)
                    : (type == "button" || type == "navigation")
                        ? (screenHeight <= 480 ? 22 : 28)
                        : (screenHeight <= 480 ? 24 : 32);
                if (requestedFont > recommendedMax)
                {
                    warnings.Add($"控件 '{name}' 请求字号 {requestedFont} 过大，按目标画面限制为 {recommendedMax}");
                    requestedFont = recommendedMax;
                }
                var text = item["text"]?.Value<string>() ?? "";
                if (type == "iofield" || type == "symboliciofield")
                {
                    var fieldLength = Math.Max(1, GetInt(item, "fieldLength", 3));
                    text = new string('8', fieldLength);
                }

                var padding = (type == "button" || type == "navigation") ? 18 : 10;
                var requiredWidth = (int)Math.Ceiling(WeightedTextLength(text) * requestedFont * 1.34 + padding);
                var requiredHeight = (int)Math.Ceiling(requestedFont * 1.55 + 6);
                var availableWidth = Math.Max(8, screenWidth - margin - Math.Max(margin, left));
                var availableHeight = Math.Max(8, screenHeight - margin - Math.Max(margin, top));

                if (item["autoFit"]?.Value<bool?>() != false)
                {
                    if (width < requiredWidth && requiredWidth <= availableWidth)
                    {
                        warnings.Add($"控件 '{name}' 宽度由 {width} 自动扩大为 {requiredWidth}，避免文本截断");
                        width = requiredWidth;
                    }
                    if (height < requiredHeight && requiredHeight <= availableHeight)
                    {
                        warnings.Add($"控件 '{name}' 高度由 {height} 自动扩大为 {requiredHeight}，避免文本截断");
                        height = requiredHeight;
                    }
                }

                var fitted = FitFontSize(text, requestedFont, width, height, padding);
                if (fitted != requestedFont)
                    warnings.Add($"控件 '{name}' 字号由 {requestedFont} 自动调整为 {fitted}");
                item["fontSize"] = fitted.ToString(CultureInfo.InvariantCulture);
            }

            var maxWidth = screenWidth - margin * 2;
            var maxHeight = screenHeight - margin * 2;
            if (width > maxWidth)
            {
                warnings.Add($"控件 '{name}' 宽度 {width} 超出画面，裁剪为 {maxWidth}");
                width = maxWidth;
            }
            if (height > maxHeight)
            {
                warnings.Add($"控件 '{name}' 高度 {height} 超出画面，裁剪为 {maxHeight}");
                height = maxHeight;
            }

            var normalizedLeft = Math.Max(margin, Math.Min(left, screenWidth - margin - width));
            var normalizedTop = Math.Max(margin, Math.Min(top, screenHeight - margin - height));
            if (normalizedLeft != left)
                warnings.Add($"控件 '{name}' Left 由 {left} 调整为 {normalizedLeft}，防止越界");
            if (normalizedTop != top)
                warnings.Add($"控件 '{name}' Top 由 {top} 调整为 {normalizedTop}，防止越界");

            item["left"] = normalizedLeft;
            item["top"] = normalizedTop;
            item["width"] = width;
            item["height"] = height;
            if (type == "indicator")
                item["radius"] = Math.Max(4, Math.Min(width, height) / 2);

            return true;
        }

        /// <summary>
        /// 将非背景控件自动移动到最近的空闲网格位置，避免 AI 给出重叠布局。
        /// 显式 allowOverlap=true 时保留原位置。
        /// </summary>
        private static bool TryResolveHmiItemOverlap(
            JObject item,
            IList<JObject> placedItems,
            int screenWidth,
            int screenHeight,
            IList<string> warnings,
            out string error)
        {
            error = "";
            if (item["allowOverlap"]?.Value<bool?>() == true || IsHmiBackgroundItem(item)) return true;
            if (!placedItems.Any(p => HmiItemsOverlapSignificantly(item, p))) return true;

            const int margin = 4;
            const int grid = 8;
            var originalLeft = GetInt(item, "left", margin);
            var originalTop = GetInt(item, "top", margin);
            var width = GetInt(item, "width", 1);
            var height = GetInt(item, "height", 1);
            var maxLeft = screenWidth - margin - width;
            var maxTop = screenHeight - margin - height;
            if (maxLeft < margin || maxTop < margin)
            {
                error = $"控件 '{item["name"]}' 尺寸超过可用画布，无法消除重叠";
                return false;
            }

            var positions = new List<(int X, int Y, int Distance)>();
            for (var y = margin; y <= maxTop; y += grid)
            {
                for (var x = margin; x <= maxLeft; x += grid)
                {
                    positions.Add((x, y, Math.Abs(x - originalLeft) + Math.Abs(y - originalTop)));
                }
            }
            foreach (var pos in positions.OrderBy(p => p.Distance).ThenBy(p => p.Y).ThenBy(p => p.X))
            {
                item["left"] = pos.X;
                item["top"] = pos.Y;
                if (!placedItems.Any(p => HmiItemsOverlapSignificantly(item, p)))
                {
                    warnings.Add($"控件 '{item["name"]}' 与已有控件重叠，位置由 ({originalLeft},{originalTop}) 自动调整为 ({pos.X},{pos.Y})");
                    return true;
                }
            }

            item["left"] = originalLeft;
            item["top"] = originalTop;
            error = $"控件 '{item["name"]}' 与已有控件显著重叠，且画面中没有足够空闲区域。可调整尺寸/位置，或显式设置 allowOverlap=true";
            return false;
        }

        private static void ValidateHmiItemSemantics(JObject item, string canonicalType, ICollection<string> errors)
        {
            var name = item["name"]?.ToString() ?? canonicalType;
            if (HmiControlCatalog.RequiresTag(canonicalType) && string.IsNullOrWhiteSpace(item["tag"]?.ToString()))
                errors.Add($"控件 '{name}' ({canonicalType}) 必须提供 tag");
            if (HmiControlCatalog.RequiresTextList(canonicalType) && string.IsNullOrWhiteSpace(item["textList"]?.ToString()))
                errors.Add($"控件 '{name}' (symboliciofield) 必须提供 textList");
            if (HmiControlCatalog.RequiresPicture(canonicalType) && string.IsNullOrWhiteSpace(item["picture"]?.ToString()))
                errors.Add($"控件 '{name}' (graphicview) 必须提供 picture");

            if (canonicalType is "button" or "navigation")
            {
                var requestedBehavior = canonicalType == "navigation"
                    ? "activateScreen"
                    : item["eventType"]?.ToString() ?? item["behavior"]?.ToString() ?? "invertBit";
                string behavior;
                try
                {
                    behavior = HmiActionCatalog.CanonicalBehavior(requestedBehavior);
                    item["eventType"] = behavior;
                }
                catch (Exception ex)
                {
                    errors.Add($"按钮 '{name}' 行为无效: {ex.Message}");
                    behavior = "none";
                }
                if (behavior.Equals("multiAction", StringComparison.OrdinalIgnoreCase))
                {
                    var actions = item["actions"] as JArray;
                    if (actions == null || actions.Count == 0) errors.Add($"按钮 '{name}' 的 multiAction 缺少 actions[]");
                    else foreach (var action in actions.OfType<JObject>())
                    {
                        try { HmiActionCatalog.Validate(HmiActionCatalog.FromJson(action)); }
                        catch (Exception ex) { errors.Add($"按钮 '{name}' action 无效: {ex.Message}"); }
                    }
                }
                else if (behavior.Equals("activateScreen", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(item["targetScreen"]?.ToString())
                        && string.IsNullOrWhiteSpace(item["eventTarget"]?.ToString()))
                        errors.Add($"导航按钮 '{name}' 必须提供 targetScreen/eventTarget");
                }
                else if (!behavior.Equals("none", StringComparison.OrdinalIgnoreCase)
                         && string.IsNullOrWhiteSpace(item["tag"]?.ToString()))
                    errors.Add($"按钮 '{name}' 行为 '{behavior}' 必须提供 tag");
            }

            if (canonicalType is "iofield" or "symboliciofield")
            {
                var mode = item["mode"]?.ToString() ?? "Output";
                if (!mode.Equals("Output", StringComparison.OrdinalIgnoreCase)
                    && !mode.Equals("InOutput", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"控件 '{name}' 的 mode='{mode}' 不受当前样本契约支持；仅支持 Output/InOutput");
                else item["mode"] = mode.Equals("InOutput", StringComparison.OrdinalIgnoreCase) ? "InOutput" : "Output";
            }
            if (canonicalType == "graphicview")
            {
                var autoSizing = item["autoSizing"]?.ToString() ?? "StretchPicture";
                if (!autoSizing.Equals("StretchPicture", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"GraphicView '{name}' 的 autoSizing='{autoSizing}' 尚未通过样本回归；当前仅支持 StretchPicture");
                else item["autoSizing"] = "StretchPicture";
            }

            if (canonicalType == "line")
            {
                var lineWidth = item["lineWidth"]?.Value<int?>() ?? 1;
                if (lineWidth < 1 || lineWidth > 20) errors.Add($"线控件 '{name}' lineWidth 必须在 1..20");
            }
            ValidateHmiColors(item, name, errors);
            ValidateHmiAnimations(item, errors);
        }

        private static void ValidateHmiAnimations(JObject item, ICollection<string> errors)
        {
            var name = item["name"]?.ToString() ?? "";
            foreach (var animation in ParseHmiAnimations(item))
            {
                var type = (animation.Type ?? "").Trim().ToLowerInvariant();
                if (type is not ("visibility" or "rangevisibility" or "singlebitvisibility" or "bitvisibility" or "enabling" or "objectenabling"))
                    errors.Add($"控件 '{name}' 动画类型 '{animation.Type}' 不受支持");
                if (string.IsNullOrWhiteSpace(animation.Tag)) errors.Add($"控件 '{name}' 动画缺少 tag");
                if (animation.RangeStart > animation.RangeEnd) errors.Add($"控件 '{name}' 动画 rangeStart 不能大于 rangeEnd");
                if ((type == "singlebitvisibility" || type == "bitvisibility")
                    && (animation.BitPosition < 0 || animation.BitPosition > 31))
                    errors.Add($"控件 '{name}' 动画 bitPosition 必须在 0..31");
            }
        }

        private static void CollectHmiDependencies(
            JObject item, ISet<string> tags, ISet<string> pictures, ISet<string> textLists, ISet<string> screens)
        {
            void Add(ISet<string> set, string? value) { if (!string.IsNullOrWhiteSpace(value)) set.Add(value!); }
            Add(tags, item["tag"]?.ToString());
            Add(pictures, item["picture"]?.ToString());
            Add(textLists, item["textList"]?.ToString());
            var canonicalType = HmiControlCatalog.Canonicalize(item["type"]?.ToString());
            string behavior = "none";
            if (canonicalType == "navigation") behavior = "activateScreen";
            else if (canonicalType == "button")
            {
                try { behavior = HmiActionCatalog.CanonicalBehavior(item["eventType"]?.ToString() ?? item["behavior"]?.ToString() ?? "invertBit"); }
                catch { }
            }
            if (behavior == "activateScreen")
            {
                Add(screens, item["targetScreen"]?.ToString());
                Add(screens, item["eventTarget"]?.ToString());
            }
            else if (canonicalType == "button")
            {
                Add(tags, item["eventTarget"]?.ToString());
            }
            foreach (var animation in ParseHmiAnimations(item)) Add(tags, animation.Tag);
            foreach (var action in (item["actions"] as JArray ?? new JArray()).OfType<JObject>())
            {
                try
                {
                    var actionSpec = HmiActionCatalog.FromJson(action);
                    var function = HmiActionCatalog.CanonicalFunction(actionSpec.Function);
                    foreach (var parameter in HmiActionCatalog.ResolveParameters(actionSpec))
                    {
                        if (function == "ActivateScreen" && parameter.Name.Equals("Screen name", StringComparison.OrdinalIgnoreCase)) Add(screens, parameter.Value);
                        else if (parameter.IsLink) Add(tags, parameter.Value);
                    }
                }
                catch
                {
                    // 具体签名错误已由 ValidateHmiItemSemantics 记录；依赖收集必须保持失败关闭但不能吞掉完整校验结果。
                }
            }
        }

        private static object[] BuildDefaultPlcTestPlan(IEnumerable<string> outputs)
        {
            var outputList = outputs.Take(20).ToArray();
            return new object[]
            {
                new { scenario = "正常启动", expectation = "启动条件满足后仅预期输出动作", outputs = outputList },
                new { scenario = "停止", expectation = "停止命令优先于启动，运动输出全部断开" },
                new { scenario = "急停", expectation = "急停立即切断危险输出且不得自动重启" },
                new { scenario = "互斥命令", expectation = "正反转、升降或开关门同时请求时不得同时输出" },
                new { scenario = "故障复位", expectation = "仅在故障条件消失后允许复位，复位不得直接启动设备" },
                new { scenario = "断电恢复", expectation = "恢复供电后保持安全停止状态，除非需求明确且风险评估允许" }
            };
        }

        private static IEnumerable<string> CollectLadWriters(LadNetworkDef def)
        {
            if (def.networks != null)
            {
                foreach (var child in def.networks)
                    foreach (var writer in CollectLadWriters(child)) yield return writer;
            }
            if (def.rung != null)
                foreach (var writer in CollectRungWriters(def.rung)) yield return writer;
            if (def.coils != null)
                foreach (var coil in def.coils)
                    if (!string.IsNullOrWhiteSpace(coil.variable)) yield return coil.variable;
        }

    /// <summary>反射调用 catalog.Find(key) 并枚举结果。</summary>
    private static System.Collections.Generic.List<object?> FindCatalogEntries(object? catalog, string key)
    {
        var list = new System.Collections.Generic.List<object?>();
        try
        {
            if (catalog == null) return list;
            var result = catalog.GetType().GetMethod("Find")?.Invoke(catalog, new object[] { key });
            if (result is IEnumerable en)
                foreach (var item in en) list.Add(item);
        }
        catch { }
        return list;
    }

        private static int FitFontSize(string text, int requested, int width, int height, int horizontalPadding)
        {
            requested = Math.Max(8, Math.Min(requested, 72));
            var byHeight = Math.Max(8, (int)Math.Floor((height - 6) * 0.72));
            var weighted = WeightedTextLength(text);
            var byWidth = Math.Max(8, (int)Math.Floor(Math.Max(1, width - horizontalPadding) * 0.75 / weighted));
            return Math.Max(8, Math.Min(requested, Math.Min(byHeight, byWidth)));
        }

    /// <summary>反射读取 CatalogEntry 属性。</summary>
    private static string? GetCatalogProp(object? entry, string prop)
    {
        try
        {
            return entry?.GetType().GetProperty(prop)?.GetValue(entry)?.ToString();
        }
        catch { return null; }
    }

    /// <summary>反射获取 IEngineering.HardwareCatalog。</summary>
    private object? GetHardwareCatalog()
    {
        try
        {
            return _tiaPortal?.GetType().GetProperty("HardwareCatalog")?.GetValue(_tiaPortal);
        }
        catch { return null; }
    }

        private static int GetInt(JObject item, string name, int fallback)
        {
            var token = item[name];
            if (token == null) return fallback;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private static bool HmiItemsOverlapSignificantly(JObject a, JObject b)
        {
            if ((a["layer"]?.Value<int?>() ?? 0) != (b["layer"]?.Value<int?>() ?? 0)) return false;
            if (IsHmiBackgroundItem(a) || IsHmiBackgroundItem(b)) return false;
            var ax = GetInt(a, "left", 0); var ay = GetInt(a, "top", 0);
            var aw = GetInt(a, "width", 1); var ah = GetInt(a, "height", 1);
            var bx = GetInt(b, "left", 0); var by = GetInt(b, "top", 0);
            var bw = GetInt(b, "width", 1); var bh = GetInt(b, "height", 1);
            var iw = Math.Max(0, Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx));
            var ih = Math.Max(0, Math.Min(ay + ah, by + bh) - Math.Max(ay, by));
            if (iw == 0 || ih == 0) return false;
            var intersection = iw * ih;
            var smaller = Math.Max(1, Math.Min(aw * ah, bw * bh));
            return intersection / (double)smaller > 0.15;
        }

        private static bool IsHmiBackgroundItem(JObject item)
        {
            var type = HmiControlCatalog.Canonicalize(item["type"]?.Value<string>());
            return HmiControlCatalog.IsDecorative(type) || HmiControlCatalog.IsGroup(type);
        }

    /// <summary>
    /// 反射检查提供者的在线状态：优先 provider.Configuration.Online / IsOnline，
    /// 回退 provider.Online / IsOnline / State。
    /// </summary>
    private static bool IsProviderOnline(object provider, Type providerType)
    {
        try
        {
            // 先尝试 provider.Configuration.Online / IsOnline
            var configProp = providerType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance);
            if (configProp != null)
            {
                var config = configProp.GetValue(provider);
                if (config != null)
                {
                    var onlineProp = config.GetType().GetProperty("Online", BindingFlags.Public | BindingFlags.Instance)
                                     ?? config.GetType().GetProperty("IsOnline", BindingFlags.Public | BindingFlags.Instance);
                    if (onlineProp != null)
                    {
                        var v = onlineProp.GetValue(config);
                        if (v is bool b) return b;
                        return "Online".Equals(v?.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            // 回退 provider.Online / IsOnline
            var provOnlineProp = providerType.GetProperty("Online", BindingFlags.Public | BindingFlags.Instance)
                                 ?? providerType.GetProperty("IsOnline", BindingFlags.Public | BindingFlags.Instance);
            if (provOnlineProp != null)
            {
                var v = provOnlineProp.GetValue(provider);
                if (v is bool b) return b;
                return "Online".Equals(v?.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            // 最后看 State 属性是否含 Online
            var stateProp = providerType.GetProperty("State", BindingFlags.Public | BindingFlags.Instance);
            if (stateProp != null)
            {
                var s = stateProp.GetValue(provider)?.ToString() ?? "";
                return s.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { }
        // PLCSIM 仿真服务存在即视为在线（V19 PLCSIM 无 Online 属性语义，仿真运行即为在线）
        if (providerType.FullName?.IndexOf("PlcSim", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

        private static bool IsSafeDatatype(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length > 256) return false;
            // 支持基本类型、Array/Struct/UDT 常见语法；拒绝会改变 XML 树或包含控制字符的内容。
            return value.IndexOfAny(new[] { '<', '>', '&', '\r', '\n', '\t' }) < 0;
        }

        private static bool IsValidInterfaceName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
            // 允许 Unicode 标识符（含中文），禁止空白、路径/XML 控制字符和数字开头。
            return Regex.IsMatch(value, @"^[\p{L}_][\p{L}\p{N}_]*$");
        }

        private static bool IsValidSectionName(string? value)
        {
            var section = string.IsNullOrWhiteSpace(value) ? "Temp" : value!.Trim();
            return new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant", "Return" }
                .Any(x => x.Equals(section, StringComparison.OrdinalIgnoreCase));
        }

        private static int ParseFontSize(JObject item, int fallback)
        {
            var token = item["fontSize"];
            if (token == null) return fallback;
            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        private static void ValidateHmiColors(JObject item, string name, ICollection<string> errors)
        {
            foreach (var property in item.Properties().Where(p => p.Name.EndsWith("Color", StringComparison.OrdinalIgnoreCase)
                                                                   || p.Name.Equals("color", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = property.Value.ToString().Split(',').Select(x => x.Trim()).ToArray();
                if (parts.Length != 3 || parts.Any(x => !int.TryParse(x, out var value) || value < 0 || value > 255))
                    errors.Add($"控件 '{name}' 的颜色 {property.Name}='{property.Value}' 无效，应为 0..255 的 R,G,B");
            }
        }

        private static void ValidatePins(
            string instruction,
            IDictionary<string, string>? pins,
            string path,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                errors.Add($"{path}: 指令名为空");
                return;
            }
            if (pins == null)
            {
                errors.Add($"{path}: 指令 {instruction} 缺少 pins");
                return;
            }
            foreach (var pin in LadInstructionCatalog.GetRequiredPins(instruction))
            {
                var match = pins.FirstOrDefault(kv => kv.Key.Equals(pin, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(match.Key) || string.IsNullOrWhiteSpace(match.Value))
                    errors.Add($"{path}: 指令 {instruction} 缺少必需引脚 {pin}");
            }

            var logicalTrigger = LadInstructionCatalog.GetTriggerPin(instruction);
            if (!string.IsNullOrEmpty(logicalTrigger))
            {
                var trigger = pins.FirstOrDefault(kv => kv.Key.Equals(logicalTrigger, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(trigger.Key) && IsLadConstant(trigger.Value)
                    && !trigger.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                    && !trigger.Value.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{path}: 逻辑触发引脚 {logicalTrigger} 只接受 Bool 变量或 TRUE/FALSE，不能使用 '{trigger.Value}'");
            }
        }

        private static bool IsSupportedRungContactType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type)) return true;
            return string.Equals(type, "negated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "NegatedContact", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "NotContact", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "pcontact", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "PContact", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "ncontact", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "NContact", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateRungPath(
            IList<RungElement> path,
            string pathName,
            ICollection<string> errors,
            ICollection<string> warnings,
            IDictionary<string, string> writers,
            ISet<string> usedVariables)
        {
            if (path.Count == 0)
            {
                errors.Add($"{pathName}: 路径为空");
                return;
            }

            var hasConditionBefore = false;
            for (var i = 0; i < path.Count; i++)
            {
                var e = path[i];
                var count = (e.contact != null ? 1 : 0) + (e.coil != null ? 1 : 0)
                            + (e.box != null ? 1 : 0) + (e.call != null ? 1 : 0)
                            + (e.branch != null ? 1 : 0) + (e.or != null ? 1 : 0);
                if (count != 1)
                {
                    errors.Add($"{pathName}[{i}]: 每个 rung 元素必须且只能设置 contact/coil/box/call/branch/or 中的一项");
                    continue;
                }

                if (e.contact != null)
                {
                    if (string.IsNullOrWhiteSpace(e.contact.contact))
                        errors.Add($"{pathName}[{i}]: 触点变量名为空");
                    else
                        usedVariables.Add(e.contact.contact);

                    if (!IsSupportedRungContactType(e.contact.type))
                        errors.Add($"{pathName}[{i}]: 触点 type '{e.contact.type}' 无效；允许为空、negated/NegatedContact/NotContact、pcontact/PContact 或 ncontact/NContact");

                    hasConditionBefore = true;
                }
                else if (e.coil != null)
                {
                    if (string.IsNullOrWhiteSpace(e.coil.coil))
                        errors.Add($"{pathName}[{i}]: 线圈变量名为空");
                    else
                    {
                        var mode = (e.coil.type ?? "coil").Trim().ToLowerInvariant();
                        var key = e.coil.coil;
                        if (writers.TryGetValue(key, out var prior) && !prior.Equals(mode, StringComparison.OrdinalIgnoreCase))
                            errors.Add($"{pathName}[{i}]: 变量 '{key}' 在同一网络中被不同线圈类型重复写入（{prior}/{mode}）");
                        else if (writers.ContainsKey(key))
                            errors.Add($"{pathName}[{i}]: 变量 '{key}' 在同一网络中被重复写入");
                        else
                            writers[key] = mode;
                        usedVariables.Add(key);
                    }
                    if (i != path.Count - 1)
                        errors.Add($"{pathName}[{i}]: 线圈后仍有元素，线圈必须位于当前串联路径末端");
                    if (!hasConditionBefore)
                        warnings.Add($"{pathName}[{i}]: 线圈前没有触点或条件，输出可能无条件置位");
                }
                else if (e.box != null)
                {
                    ValidatePins(e.box.box, e.box.pins, $"{pathName}[{i}]", errors);
                    if (e.box.pins != null)
                    {
                        foreach (var value in e.box.pins.Values.Where(v => !string.IsNullOrWhiteSpace(v) && !IsLadConstant(v)))
                            usedVariables.Add(value);
                    }
                    hasConditionBefore = true;
                }
                else if (e.call != null)
                {
                    if (string.IsNullOrWhiteSpace(e.call.call))
                        errors.Add($"{pathName}[{i}]: 调用块名为空");
                    var callBlockType = string.IsNullOrWhiteSpace(e.call.blockType) ? "FB" : e.call.blockType!.Trim();
                    var isFcCall = callBlockType.Equals("FC", StringComparison.OrdinalIgnoreCase);
                    if (!isFcCall)
                    {
                        if (string.IsNullOrWhiteSpace(e.call.instance))
                            errors.Add($"{pathName}[{i}]: FB 调用必须显式提供 instance");
                        if (!string.Equals(e.call.instanceScope, "local", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(e.call.instanceScope, "global", StringComparison.OrdinalIgnoreCase))
                            errors.Add($"{pathName}[{i}]: FB 调用必须显式提供 instanceScope=local/global");
                    }
                    else if (!string.IsNullOrWhiteSpace(e.call.instance))
                        errors.Add($"{pathName}[{i}]: FC 调用不能提供 instance");
                    if (e.call.pins != null)
                    {
                        foreach (var pin in e.call.pins)
                        {
                            if (string.IsNullOrWhiteSpace(pin.name) || string.IsNullOrWhiteSpace(pin.variable))
                                errors.Add($"{pathName}[{i}]: 调用引脚名称或变量为空");
                            if (string.IsNullOrWhiteSpace(pin.datatype) || !IsSafeDatatype(pin.datatype))
                                errors.Add($"{pathName}[{i}]: 调用引脚 '{pin.name}' 必须提供合法 datatype");
                            if (string.Equals(pin.direction, "inout", StringComparison.OrdinalIgnoreCase))
                                errors.Add($"{pathName}[{i}]: InOut 引脚 '{pin.name}' 尚未通过 TIA 双向连线回归，当前版本拒绝生成");
                            if (!string.IsNullOrWhiteSpace(pin.variable) && IsLadConstant(pin.variable))
                                errors.Add($"{pathName}[{i}]: 调用引脚 '{pin.name}' 暂不支持字面量绑定，请使用显式中间变量");
                            else if (!string.IsNullOrWhiteSpace(pin.variable))
                                usedVariables.Add(pin.variable);
                        }
                    }
                    hasConditionBefore = true;
                }
                else if (e.branch != null)
                {
                    if (e.branch.branch == null || e.branch.branch.Count < 2)
                        errors.Add($"{pathName}[{i}]: 并联分支至少需要两条路径");
                    else
                    {
                        for (var b = 0; b < e.branch.branch.Count; b++)
                            ValidateRungPath(e.branch.branch[b], $"{pathName}[{i}].branch[{b}]", errors, warnings, writers, usedVariables);
                    }
                    hasConditionBefore = true;
                }
                else if (e.or != null)
                {
                    if (e.or.inputs == null || e.or.inputs.Count < 2 || string.IsNullOrWhiteSpace(e.or.output))
                        errors.Add($"{pathName}[{i}]: OR 元素至少需要两个 inputs 和一个 output");
                    else
                    {
                        foreach (var input in e.or.inputs.Where(v => !IsLadConstant(v))) usedVariables.Add(input);
                        usedVariables.Add(e.or.output!);
                    }
                    hasConditionBefore = true;
                }
            }
        }

        private static double WeightedTextLength(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1.0;
            double total = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch)) total += 0.35;
                else if (ch >= 0x2E80) total += 1.0;
                else if (char.IsUpper(ch) || char.IsDigit(ch)) total += 0.68;
                else total += 0.56;
            }
            return Math.Max(1.0, total);
        }

        private static IEnumerable<string> CollectRungWriters(IEnumerable<RungElement> rung)
        {
            foreach (var element in rung)
            {
                var coilName = element.coil?.coil;
                if (!string.IsNullOrWhiteSpace(coilName)) yield return coilName!;
                if (element.branch?.branch != null)
                    foreach (var branch in element.branch.branch)
                        foreach (var writer in CollectRungWriters(branch)) yield return writer;
            }
        }
}
