using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// PLCSIM 仿真 API 集成模块。
    /// PLCSIM API 是独立的 DLL（不在 Siemens.Engineering.dll 中），位于
    /// S7-PLCSIM V17\Bin\Siemens.Simatic.PlcSim.VplcApi.dll，
    /// 支持 Vplc1200（S7-1200 仿真）和 Vplc1500（S7-1500 仿真）。
    /// PLCSIM Advanced 位于 D:\portal\PLCSIMADV\bin\Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll。
    /// 由于 API 签名在不同版本可能变化，全部采用反射探测入口点，记录 triedPaths 便于诊断。
    /// 所有公共方法返回 JObject，由 Tools 层调用 ToString(Formatting.None) 转为 JSON 字符串。
    /// </summary>
    public partial class PortalService
    {
        // ═════════════════════════════════════════════════════════════════════════════
        // PLCSIM API 状态与常量
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>PLCSIM 虚拟 PLC 实例缓存（instanceName -> Vplc 实例对象）。</summary>
        private static readonly ConcurrentDictionary<string, object> _plcsimInstances = new();

        /// <summary>保护静态 _plcsimInstances 的静态锁（实例级 _lock 无法跨实例防竞态）。</summary>
        private static readonly object _plcsimStaticLock = new object();

        /// <summary>PLCSIM API 程序集缓存（避免重复加载，线程安全）。</summary>
        private static Assembly? _plcsimAsm;
        private static Assembly? _plcsimAdvAsm;
        private static readonly object _plcsimAsmLock = new();

        /// <summary>PLCSIM VplcApi DLL 候选路径（按优先级排序，D:\portal\ 硬编码路径降级到末尾）。</summary>
        private static readonly string[] _plcsimDllPaths = new[]
        {
            @"C:\Program Files\Siemens\Automation\S7-PLCSIM V17\Bin\Siemens.Simatic.PlcSim.VplcApi.dll",
            @"C:\Program Files\Siemens\Automation\PLCSIM_V19\resources\bin\Siemens.Simatic.PlcSim.VplcApi.dll",
            @"C:\Program Files\Siemens\Automation\Portal V19\data\plcsim\Siemens.Simatic.PlcSim.VplcApi.dll",
            @"C:\Program Files\Siemens\Automation\Portal V19\PLCSIM\Siemens.Simatic.PlcSim.VplcApi.dll",
            @"C:\Program Files (x86)\Siemens\Automation\Portal V19\data\plcsim\Siemens.Simatic.PlcSim.VplcApi.dll",
            @"D:\portal\PLCSIM_V19\resources\bin\Siemens.Simatic.PlcSim.VplcApi.dll"
        };

        /// <summary>PLCSIM Advanced ConfigureAdapters DLL 候选路径（D:\portal\ 降级到末尾）。</summary>
        private static readonly string[] _plcsimAdvDllPaths = new[]
        {
            @"C:\Program Files\Siemens\Automation\PLCSIM_V19\resources\bin\wwwroot\assets\lib\advAdapter\Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll",
            @"C:\Program Files (x86)\Common Files\Siemens\PLCSIMADV\Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll",
            @"C:\Program Files\Siemens\Automation\Portal V19\data\plcsim\Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll",
            @"C:\Program Files (x86)\Siemens\Automation\Portal V19\data\plcsim\Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll",
            @"D:\portal\PLCSIMADV\bin\Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll"
        };

        // PLCSIM API 类型全名（基于 PLCSIM V19 命名约定）
        private const string VplcApiTypeFullName = "Siemens.Simatic.PlcSim.VplcApi";
        private const string VplcFactoryTypeFullName = "Siemens.Simatic.PlcSim.VplcFactory";
        private const string VplcTypeFullName = "Siemens.Simatic.PlcSim.Vplc";
        private const string Vplc1200TypeFullName = "Siemens.Simatic.PlcSim.Vplc1200";
        private const string Vplc1500TypeFullName = "Siemens.Simatic.PlcSim.Vplc1500";
        private const string VplcStateTypeFullName = "Siemens.Simatic.PlcSim.VplcState";

        // PLCSIM Advanced API 类型全名
        private const string ConfigureAdaptersTypeFullName = "Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters";
        private const string PlcSimAdvancedNamespace = "Siemens.Simatic.PlcSim.Advanced";

        // ═════════════════════════════════════════════════════════════════════════════
        // PLCSIM API 加载与类型解析（辅助方法）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 加载 PLCSIM API 程序集（缓存，避免重复加载）。
        /// 依次尝试候选 DLL 路径，第一个加载成功的路径会被缓存。
        /// </summary>
        /// <param name="triedPaths">填入所有尝试过的路径与异常</param>
        /// <returns>加载成功的程序集；失败返回 null</returns>
        private Assembly? LoadPlcSimApi(List<string> triedPaths)
        {
            lock (_plcsimAsmLock)
            {
                if (_plcsimAsm != null) return _plcsimAsm;

                // 优先使用环境变量 PLCSIM_DLL_PATH 指定的 DLL
                var candidates = BuildPlcSimDllCandidates(_plcsimDllPaths, "PLCSIM_DLL_PATH");
                foreach (var path in candidates)
                {
                    triedPaths.Add($"检查DLL: {path}");
                    if (File.Exists(path))
                    {
                        try
                        {
                            var asm = Assembly.LoadFrom(path);
                            _plcsimAsm = asm;
                            triedPaths.Add($"加载成功: {path}");
                            return asm;
                        }
                        catch (Exception ex)
                        {
                            triedPaths.Add($"加载失败: {ex.Message}");
                        }
                    }
                    else
                    {
                        triedPaths.Add("文件不存在");
                    }
                }
                return null;
            }
        }

        /// <summary>加载 PLCSIM Advanced API 程序集（缓存）。</summary>
        private Assembly? LoadPlcSimAdvancedApi(List<string> triedPaths)
        {
            lock (_plcsimAsmLock)
            {
                if (_plcsimAdvAsm != null) return _plcsimAdvAsm;

                // 优先使用环境变量 PLCSIM_ADV_DLL_PATH 指定的 DLL
                var candidates = BuildPlcSimDllCandidates(_plcsimAdvDllPaths, "PLCSIM_ADV_DLL_PATH");
                foreach (var path in candidates)
                {
                    triedPaths.Add($"检查DLL: {path}");
                    if (File.Exists(path))
                    {
                        try
                        {
                            var asm = Assembly.LoadFrom(path);
                            _plcsimAdvAsm = asm;
                            triedPaths.Add($"加载成功: {path}");
                            return asm;
                        }
                        catch (Exception ex)
                        {
                            triedPaths.Add($"加载失败: {ex.Message}");
                        }
                    }
                    else
                    {
                        triedPaths.Add("文件不存在");
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// 构造 DLL 候选路径列表：若环境变量 envVar 指向有效文件，则放到首位。
        /// </summary>
        private static IEnumerable<string> BuildPlcSimDllCandidates(string[] defaultPaths, string envVar)
        {
            var envPath = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            {
                yield return envPath;
            }
            var tiaRoot = Environment.GetEnvironmentVariable("TIA_PORTAL_DIR");
            if (!string.IsNullOrWhiteSpace(tiaRoot))
            {
                var fileName = envVar == "PLCSIM_ADV_DLL_PATH"
                    ? "Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll"
                    : "Siemens.Simatic.PlcSim.VplcApi.dll";
                foreach (var relative in new[] { Path.Combine("data", "plcsim", fileName), Path.Combine("PLCSIM", fileName), Path.Combine("Bin", fileName) })
                {
                    var dynamicPath = Path.Combine(tiaRoot, relative);
                    if (!string.Equals(envPath, dynamicPath, StringComparison.OrdinalIgnoreCase)) yield return dynamicPath;
                }
            }
            foreach (var p in defaultPaths)
            {
                if (envPath == null || !string.Equals(envPath, p, StringComparison.OrdinalIgnoreCase))
                    yield return p;
            }
        }

        /// <summary>从 PLCSIM API 程序集解析类型（先在目标程序集查找，再回退到所有已加载程序集）。</summary>
        private static Type? ResolvePlcSimType(Assembly? asm, string typeFullName)
        {
            if (asm != null)
            {
                try { var t = asm.GetType(typeFullName); if (t != null) return t; } catch { }
            }
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = a.GetType(typeFullName);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>从缓存获取 PLCSIM 实例（找不到返回 null）。</summary>
        private static object? GetPlcSimInstance(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName)) return null;
            lock (_plcsimStaticLock)
            {
                return _plcsimInstances.TryGetValue(instanceName, out var inst) ? inst : null;
            }
        }

        /// <summary>获取缓存实例数（静态锁保护）。</summary>
        private static int GetCachedInstanceCount()
        {
            lock (_plcsimStaticLock) { return _plcsimInstances.Count; }
        }

        /// <summary>获取缓存实例快照（静态锁保护，避免枚举期间并发修改）。</summary>
        private static List<KeyValuePair<string, object>> GetCachedInstanceSnapshot()
        {
            lock (_plcsimStaticLock) { return _plcsimInstances.ToList(); }
        }

        /// <summary>反射收集对象所有公开属性名（用于诊断 API 可用性）。</summary>
        private static List<string> PlcSimListPropertyNames(object obj)
        {
            var names = new List<string>();
            try
            {
                foreach (var p in obj.GetType().GetProperties())
                    names.Add(p.Name);
            }
            catch { }
            return names;
        }

        /// <summary>反射收集对象所有公开方法名（排除特殊方法，用于诊断 API 可用性）。</summary>
        private static List<string> PlcSimListMethodNames(object obj)
        {
            var names = new List<string>();
            try
            {
                foreach (var m in obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (!m.IsSpecialName) names.Add(m.Name);
            }
            catch { }
            return names;
        }

        /// <summary>反射收集对象所有公开静态方法名（用于诊断 API 可用性）。</summary>
        private static List<string> PlcSimListStaticMethodNames(Type type)
        {
            var names = new List<string>();
            try
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (!m.IsSpecialName) names.Add(m.Name);
            }
            catch { }
            return names;
        }

        /// <summary>反射收集 Vplc 实例的关键属性，构造 JObject（Name/State/Type 等）。</summary>
        private static JObject ReflectPlcSimInstance(object inst)
        {
            var obj = new JObject();
            try
            {
                obj["name"] = SafeReflectGet(inst, "Name") ?? "";
                obj["type"] = inst.GetType().Name;
                obj["fullName"] = inst.GetType().FullName ?? "";
                obj["state"] = SafeReflectGet(inst, "State") ?? "";
                obj["operatingState"] = SafeReflectGet(inst, "OperatingState") ?? "";
                obj["isPowered"] = SafeReflectGet(inst, "IsPowered") ?? "";
            }
            catch { }
            return obj;
        }

        /// <summary>
        /// 在 target 上按方法名 + 参数个数查找并调用方法（容错）。
        /// 支持 instance 和 static（target=null + type 传入）两种模式。
        /// </summary>
        private static (bool ok, object? result, string attempt) PlcSimInvokeMethod(
            object? target, Type? type, string methodName, object[] args, bool isStatic = false)
        {
            var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            try
            {
                MethodInfo? method = null;
                var searchType = type ?? target?.GetType();
                if (searchType == null) return (false, null, $"{methodName} => 无类型信息");
                // 精确参数类型匹配
                var argTypes = args.Select(a => a?.GetType() ?? typeof(object)).ToArray();
                try { method = searchType.GetMethod(methodName, flags, null, argTypes, null); } catch { }
                // 回退：按参数个数匹配
                if (method == null)
                {
                    try
                    {
                        method = searchType.GetMethods(flags)
                            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
                    }
                    catch { }
                }
                if (method == null) return (false, null, $"{methodName}({args.Length} args) => 无匹配方法");
                var result = method.Invoke(target, args);
                return (true, result, $"{methodName}({args.Length} args) => 成功");
            }
            catch (TargetInvocationException tie)
            {
                return (false, null, $"{methodName} => 异常: " + (tie.InnerException?.Message ?? tie.Message));
            }
            catch (Exception ex)
            {
                return (false, null, $"{methodName} => 异常: " + ex.Message);
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 一、PLCSIM API 可用性与实例管理
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查 PLCSIM API 是否可用。
        /// 依次探测候选 DLL 路径，加载成功后返回程序集版本、关键类型（VplcApi/VplcFactory/Vplc1200/Vplc1500/VplcState）
        /// 及 VplcState 枚举值列表。失败时返回 triedPaths 便于诊断。
        /// </summary>
        public JObject PlcSimCheckAvailable()
        {
            lock (_lock)
            {
                try
                {
                    var triedPaths = new List<string>();
                    var asm = LoadPlcSimApi(triedPaths);
                    if (asm == null)
                        return MakeApiExplored("PLCSIM API", triedPaths);

                    var apiType = ResolvePlcSimType(asm, VplcApiTypeFullName);
                    var factoryType = ResolvePlcSimType(asm, VplcFactoryTypeFullName);
                    var vplcType = ResolvePlcSimType(asm, VplcTypeFullName);
                    var vplc1200Type = ResolvePlcSimType(asm, Vplc1200TypeFullName);
                    var vplc1500Type = ResolvePlcSimType(asm, Vplc1500TypeFullName);
                    var stateType = ResolvePlcSimType(asm, VplcStateTypeFullName);

                    var types = new List<string>();
                    try
                    {
                        foreach (var t in asm.GetTypes().Take(30))
                            types.Add(t.FullName ?? t.Name);
                    }
                    catch { }

                    var stateValues = new List<string>();
                    if (stateType != null && stateType.IsEnum)
                    {
                        try { stateValues = Enum.GetNames(stateType).ToList(); } catch { }
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["message"] = "PLCSIM API 可用",
                        ["dllPath"] = asm.Location,
                        ["version"] = asm.GetName().Version?.ToString() ?? "",
                        ["apiType"] = apiType?.FullName ?? "",
                        ["factoryType"] = factoryType?.FullName ?? "",
                        ["vplcType"] = vplcType?.FullName ?? "",
                        ["vplc1200Type"] = vplc1200Type?.FullName ?? "",
                        ["vplc1500Type"] = vplc1500Type?.FullName ?? "",
                        ["stateType"] = stateType?.FullName ?? "",
                        ["stateValues"] = new JArray(stateValues),
                        ["types"] = new JArray(types),
                        ["cachedInstances"] = GetCachedInstanceCount()
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// ★V17经典版探测★ 反射 S7-PLCSIM 经典 API（SimulationController / ISimulationRuntimeFactory 等）：
        /// dump 相关类型的方法签名、静态入口，并尝试获取控制器单例。
        /// </summary>
        public JObject PlcSimProbeClassicApi()
        {
            lock (_lock)
            {
                try
                {
                    var triedPaths = new List<string>();
                    var asm = LoadPlcSimApi(triedPaths);
                    if (asm == null)
                        return MakeApiExplored("PLCSIM API", triedPaths);

                    var relevantTypes = new List<object>();
                    var types = asm.GetTypes();
                    foreach (var t in types)
                    {
                        var n = t.FullName ?? t.Name;
                        if (n.IndexOf("Simulation", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("Vplc", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("PlcSim", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var methods = new List<string>();
                            try
                            {
                                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                {
                                    if (m.IsSpecialName) continue;
                                    methods.Add((m.IsStatic ? "static " : "") + m.Name + "(" +
                                        string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " +
                                        (m.ReturnType.Name));
                                }
                            }
                            catch { }
                            var props = new List<string>();
                            try
                            {
                                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                    props.Add(p.Name + " : " + p.PropertyType.Name + (p.GetMethod?.IsStatic == true ? " [static]" : ""));
                            }
                            catch { }
                            relevantTypes.Add(new
                            {
                                typeName = t.FullName,
                                isInterface = t.IsInterface,
                                isEnum = t.IsEnum,
                                methods = methods.Take(60).ToList(),
                                properties = props.Take(40).ToList()
                            });
                        }
                    }

                    // 尝试获取控制器单例
                    string? singletonType = null;
                    string? singletonVia = null;
                    var singletonMethods = new List<string>();
                    var singletonProps = new List<string>();
                    object? controller = null;

                    // 0) 全程序集扫描：返回 SimulationController/SimulationRuntime 的静态属性/方法
                    foreach (var t in types)
                    {
                        if (t.IsEnum) continue;
                        try
                        {
                            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                            {
                                var pt = p.PropertyType;
                                if ((pt.FullName ?? "").IndexOf("SimulationController", StringComparison.OrdinalIgnoreCase) >= 0
                                    || (pt.FullName ?? "").IndexOf("SimulationRuntime", StringComparison.OrdinalIgnoreCase) >= 0
                                    || (pt.FullName ?? "").IndexOf("RuntimeService", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    try
                                    {
                                        controller = p.GetValue(null);
                                        if (controller != null)
                                        {
                                            singletonVia = "static prop " + t.Name + "." + p.Name + " -> " + pt.Name;
                                            singletonType = controller.GetType().FullName;
                                            break;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                        if (controller != null) break;
                    }

                    // 1) SimulationController 类型: Instance / GetInstance 等
                    if (controller == null)
                    {
                        foreach (var t in types)
                        {
                            if (t.IsInterface || t.IsEnum) continue;
                            if ((t.FullName ?? "").IndexOf("SimulationController", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            try
                            {
                                var prop = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                                if (prop != null)
                                {
                                    controller = prop.GetValue(null);
                                    singletonVia = "static Instance property";
                                    singletonType = t.FullName;
                                    break;
                                }
                            }
                            catch { }
                            if (controller == null)
                            {
                                try
                                {
                                    var m = t.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                                    if (m != null)
                                    {
                                        controller = m.Invoke(null, null);
                                        singletonVia = "static GetInstance()";
                                        singletonType = t.FullName;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    if (controller != null)
                    {
                        try
                        {
                            foreach (var m in controller.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (m.IsSpecialName) continue;
                                singletonMethods.Add(m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name);
                            }
                        }
                        catch { }
                        try
                        {
                            foreach (var p in controller.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                singletonProps.Add(p.Name + " : " + p.PropertyType.Name);
                        }
                        catch { }
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["dllPath"] = asm.Location,
                        ["version"] = asm.GetName().Version?.ToString() ?? "",
                        ["relevantTypes"] = JArray.FromObject(relevantTypes),
                        ["controllerSingleton"] = singletonType == null ? null : JObject.FromObject(new
                        {
                            type = singletonType,
                            via = singletonVia,
                            methods = singletonMethods.Take(60).ToList(),
                            properties = singletonProps.Take(40).ToList()
                        })
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 列出所有 PLCSIM 虚拟 PLC 实例。
        /// 反射尝试多种入口：VplcApi.Instances 静态属性 / VplcApi.GetInstances() / VplcFactory.GetInstances() / VplcFactory.Instances。
        /// 同时合并本地缓存中的实例引用，返回每个实例的 Name/State/Type。
        /// </summary>
        public JObject PlcSimListInstances()
        {
            lock (_lock)
            {
                try
                {
                    var triedPaths = new List<string>();
                    var asm = LoadPlcSimApi(triedPaths);
                    if (asm == null)
                        return MakeApiExplored("PLCSIM API", triedPaths);

                    var instances = new JArray();
                    var entryPoints = new List<string>();
                    var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    void AddInstance(object item)
                    {
                        try
                        {
                            var name = SafeReflectGet(item, "Name") ?? "";
                            if (!string.IsNullOrEmpty(name) && seenNames.Contains(name)) return;
                            if (!string.IsNullOrEmpty(name)) seenNames.Add(name);
                            instances.Add(ReflectPlcSimInstance(item));
                        }
                        catch { }
                    }

                    // 方式1: VplcApi.Instances 静态属性 / GetInstances() 方法
                    var apiType = ResolvePlcSimType(asm, VplcApiTypeFullName);
                    if (apiType != null)
                    {
                        try
                        {
                            entryPoints.Add("VplcApi.Instances (static prop)");
                            var instProp = apiType.GetProperty("Instances", BindingFlags.Public | BindingFlags.Static);
                            if (instProp != null)
                            {
                                var coll = instProp.GetValue(null);
                                if (coll is IEnumerable enumerable)
                                    foreach (var item in enumerable) AddInstance(item);
                            }
                        }
                        catch (Exception ex) { entryPoints[entryPoints.Count - 1] += " => " + ex.Message; }

                        if (instances.Count == 0)
                        {
                            try
                            {
                                entryPoints.Add("VplcApi.GetInstances()");
                                var method = apiType.GetMethod("GetInstances", BindingFlags.Public | BindingFlags.Static);
                                if (method != null)
                                {
                                    var coll = method.Invoke(null, null);
                                    if (coll is IEnumerable enumerable)
                                        foreach (var item in enumerable) AddInstance(item);
                                }
                            }
                            catch (Exception ex) { entryPoints[entryPoints.Count - 1] += " => " + ex.Message; }
                        }
                    }

                    // 方式2: VplcFactory.GetInstances() / Instances
                    if (instances.Count == 0)
                    {
                        var factoryType = ResolvePlcSimType(asm, VplcFactoryTypeFullName);
                        if (factoryType != null)
                        {
                            try
                            {
                                entryPoints.Add("VplcFactory.GetInstances()");
                                var method = factoryType.GetMethod("GetInstances", BindingFlags.Public | BindingFlags.Static);
                                if (method != null)
                                {
                                    var coll = method.Invoke(null, null);
                                    if (coll is IEnumerable enumerable)
                                        foreach (var item in enumerable) AddInstance(item);
                                }
                            }
                            catch (Exception ex) { entryPoints[entryPoints.Count - 1] += " => " + ex.Message; }

                            try
                            {
                                entryPoints.Add("VplcFactory.Instances (static prop)");
                                var instProp = factoryType.GetProperty("Instances", BindingFlags.Public | BindingFlags.Static);
                                if (instProp != null)
                                {
                                    var coll = instProp.GetValue(null);
                                    if (coll is IEnumerable enumerable)
                                        foreach (var item in enumerable) AddInstance(item);
                                }
                            }
                            catch (Exception ex) { entryPoints[entryPoints.Count - 1] += " => " + ex.Message; }
                        }
                    }

                    // 方式3: 合并本地缓存中的实例（API 未返回时兜底）
                    foreach (var kvp in GetCachedInstanceSnapshot())
                    {
                        if (!seenNames.Contains(kvp.Key))
                        {
                            seenNames.Add(kvp.Key);
                            instances.Add(ReflectPlcSimInstance(kvp.Value));
                        }
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["count"] = instances.Count,
                        ["instances"] = instances,
                        ["entryPoints"] = new JArray(entryPoints),
                        ["cachedCount"] = GetCachedInstanceCount()
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 创建 PLCSIM 实例。
        /// cpuType: "1200" 或 "1500"（也接受 S7-1200/S7-1500）。
        /// 反射尝试 VplcFactory.CreateVplc1200(string) / CreateVplc1500(string) / Create(string, cpuType) / 构造函数。
        /// 创建成功后缓存实例引用。
        /// </summary>
        public JObject PlcSimCreateInstance(string cpuType, string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");
                    var cpu = (cpuType ?? "").Trim().ToUpperInvariant().Replace("S7-", "").Replace("S7", "");
                    if (cpu != "1200" && cpu != "1500")
                        return MakeError("cpuType 必须为 1200 或 1500（也接受 S7-1200/S7-1500）");

                    if (GetPlcSimInstance(instanceName) != null)
                        return MakeError($"已存在同名 PLCSIM 实例: {instanceName}（请先删除或换名）");

                    var triedPaths = new List<string>();
                    var asm = LoadPlcSimApi(triedPaths);
                    if (asm == null)
                        return MakeApiExplored("PLCSIM API", triedPaths);

                    var factoryType = ResolvePlcSimType(asm, VplcFactoryTypeFullName);
                    var vplc1200Type = ResolvePlcSimType(asm, Vplc1200TypeFullName);
                    var vplc1500Type = ResolvePlcSimType(asm, Vplc1500TypeFullName);
                    var targetVplcType = cpu == "1200" ? vplc1200Type : vplc1500Type;

                    var attempts = new List<string>();
                    object? instance = null;

                    // 方式1: VplcFactory.CreateVplc1200(string) / CreateVplc1500(string)
                    if (factoryType != null)
                    {
                        var methodName = "CreateVplc" + cpu;
                        var (ok, res, att) = PlcSimInvokeMethod(null, factoryType, methodName, new object[] { instanceName }, isStatic: true);
                        attempts.Add(att);
                        if (ok) instance = res;

                        // 方式1b: CreateVplc(string, CpuType) / Create(string, CpuType)
                        if (instance == null)
                        {
                            // 尝试查找 CpuType 枚举
                            Type? cpuEnumType = null;
                            try
                            {
                                cpuEnumType = asm.GetTypes().FirstOrDefault(t => t.IsEnum && t.Name.Equals("CpuType", StringComparison.OrdinalIgnoreCase));
                            }
                            catch { }
                            if (cpuEnumType != null)
                            {
                                var cpuVal = Enum.GetNames(cpuEnumType).FirstOrDefault(n => n.IndexOf(cpu, StringComparison.OrdinalIgnoreCase) >= 0);
                                if (cpuVal != null)
                                {
                                    var enumValue = Enum.Parse(cpuEnumType, cpuVal);
                                    var (ok2, res2, att2) = PlcSimInvokeMethod(null, factoryType, "CreateVplc", new object[] { instanceName, enumValue }, isStatic: true);
                                    attempts.Add(att2);
                                    if (ok2) instance = res2;

                                    if (instance == null)
                                    {
                                        var (ok3, res3, att3) = PlcSimInvokeMethod(null, factoryType, "Create", new object[] { instanceName, enumValue }, isStatic: true);
                                        attempts.Add(att3);
                                        if (ok3) instance = res3;
                                    }
                                }
                            }
                        }
                    }

                    // 方式2: new Vplc1200(string) / new Vplc1500(string) 构造函数
                    if (instance == null && targetVplcType != null)
                    {
                        try
                        {
                            attempts.Add($"new {targetVplcType.Name}(string)");
                            var ctor = targetVplcType.GetConstructor(new[] { typeof(string) });
                            if (ctor != null)
                                instance = ctor.Invoke(new object[] { instanceName });
                            else
                                attempts[attempts.Count - 1] += " => 无 string 构造函数";
                        }
                        catch (Exception ex) { attempts[attempts.Count - 1] += " => 异常: " + ex.Message; }
                    }

                    if (instance == null)
                    {
                        return new JObject
                        {
                            ["success"] = false,
                            ["error"] = "所有创建方式均失败（PLCSIM API 签名可能不匹配）",
                            ["apiExplored"] = true,
                            ["feature"] = "VplcFactory.Create",
                            ["attempts"] = new JArray(attempts),
                            ["triedPaths"] = new JArray(triedPaths),
                            ["factoryStaticMethods"] = factoryType != null ? new JArray(PlcSimListStaticMethodNames(factoryType)) : new JArray()
                        };
                    }

                    // 缓存实例（静态锁保护 check-then-act，防止并发重复创建导致实例泄漏）
                    lock (_plcsimStaticLock)
                    {
                        if (_plcsimInstances.ContainsKey(instanceName))
                        {
                            // 并发期间另一线程已创建同名实例，清理当前实例
                            try { PlcSimInvokeMethod(instance, null, "Delete", Array.Empty<object>()); } catch { }
                            return MakeError($"并发期间已创建同名 PLCSIM 实例: {instanceName}（请先删除或换名）");
                        }
                        _plcsimInstances[instanceName] = instance;
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["message"] = $"已创建 PLCSIM 实例: {instanceName} (CPU {cpu})",
                        ["instanceName"] = instanceName,
                        ["cpuType"] = cpu,
                        ["instanceType"] = instance.GetType().FullName,
                        ["state"] = SafeReflectGet(instance, "State") ?? "",
                        ["attempts"] = new JArray(attempts)
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 删除 PLCSIM 实例。
        /// 反射尝试 instance.Delete() / instance.Dispose() / VplcFactory.Delete(instance)。
        /// 成功后从缓存移除。
        /// </summary>
        public JObject PlcSimDeleteInstance(string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");

                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}（请先 create_instance 或 list_instances）");

                    var attempts = new List<string>();
                    bool ok = false;

                    // 方式1: instance.Delete() / Dispose() / Remove()
                    foreach (var methodName in new[] { "Delete", "Dispose", "Remove", "Close" })
                    {
                        var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, Array.Empty<object>());
                        attempts.Add(att);
                        if (mOk) { ok = true; break; }
                    }

                    // 方式2: VplcFactory.Delete(instance) / Remove(instance)
                    if (!ok)
                    {
                        var triedPaths = new List<string>();
                        var asm = LoadPlcSimApi(triedPaths);
                        var factoryType = ResolvePlcSimType(asm, VplcFactoryTypeFullName);
                        if (factoryType != null)
                        {
                            foreach (var methodName in new[] { "Delete", "Remove", "Destroy" })
                            {
                                var (mOk, _, att) = PlcSimInvokeMethod(null, factoryType, methodName, new object[] { instance }, isStatic: true);
                                attempts.Add(att);
                                if (mOk) { ok = true; break; }
                            }
                        }
                    }

                    // 无论 API 调用是否成功，都从缓存移除（静态锁保护）
                    lock (_plcsimStaticLock)
                    {
                        _plcsimInstances.TryRemove(instanceName, out _);
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["message"] = ok ? $"已删除 PLCSIM 实例: {instanceName}" : $"已从缓存移除实例: {instanceName}（API 调用未成功，可能需手动关闭 PLCSIM）",
                        ["instanceName"] = instanceName,
                        ["apiCalled"] = ok,
                        ["attempts"] = new JArray(attempts)
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 二、实例运行控制（启停/电源/状态）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 启动 PLCSIM 实例（切换到 Run 模式）。
        /// 反射尝试 instance.Run() / Start() / SetState(Run) / PowerOn()+Run()。
        /// 若实例未上电，先尝试 PowerOn。
        /// </summary>
        public JObject PlcSimStartInstance(string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    var attempts = new List<string>();
                    bool ok = false;

                    // 先尝试上电（若未上电 Run 会失败）
                    var (pOk, _, pAtt) = PlcSimInvokeMethod(instance, null, "PowerOn", Array.Empty<object>());
                    attempts.Add(pAtt);

                    // 方式1: Run() / Start()
                    foreach (var methodName in new[] { "Run", "Start", "SwitchToRun" })
                    {
                        var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, Array.Empty<object>());
                        attempts.Add(att);
                        if (mOk) { ok = true; break; }
                    }

                    // 方式2: SetState(VplcState.Run)
                    if (!ok)
                    {
                        var triedPaths = new List<string>();
                        var asm = LoadPlcSimApi(triedPaths);
                        var stateType = ResolvePlcSimType(asm, VplcStateTypeFullName);
                        if (stateType != null && stateType.IsEnum)
                        {
                            var runName = Enum.GetNames(stateType).FirstOrDefault(n => n.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (runName != null)
                            {
                                var runVal = Enum.Parse(stateType, runName);
                                var (mOk, _, att) = PlcSimInvokeMethod(instance, null, "SetState", new object[] { runVal });
                                attempts.Add(att);
                                if (mOk) ok = true;
                            }
                        }
                    }

                    return new JObject
                    {
                        ["success"] = ok,
                        ["message"] = ok ? $"已启动 PLCSIM 实例: {instanceName}" : "所有启动方式均失败",
                        ["instanceName"] = instanceName,
                        ["state"] = SafeReflectGet(instance, "State") ?? "",
                        ["attempts"] = new JArray(attempts)
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 停止 PLCSIM 实例（切换到 Stop 模式）。
        /// 反射尝试 instance.Stop() / Pause() / SetState(Stop)。
        /// </summary>
        public JObject PlcSimStopInstance(string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    var attempts = new List<string>();
                    bool ok = false;

                    // 方式1: Stop() / Pause()
                    foreach (var methodName in new[] { "Stop", "Pause", "SwitchToStop" })
                    {
                        var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, Array.Empty<object>());
                        attempts.Add(att);
                        if (mOk) { ok = true; break; }
                    }

                    // 方式2: SetState(VplcState.Stop)
                    if (!ok)
                    {
                        var triedPaths = new List<string>();
                        var asm = LoadPlcSimApi(triedPaths);
                        var stateType = ResolvePlcSimType(asm, VplcStateTypeFullName);
                        if (stateType != null && stateType.IsEnum)
                        {
                            var stopName = Enum.GetNames(stateType).FirstOrDefault(n => n.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (stopName != null)
                            {
                                var stopVal = Enum.Parse(stateType, stopName);
                                var (mOk, _, att) = PlcSimInvokeMethod(instance, null, "SetState", new object[] { stopVal });
                                attempts.Add(att);
                                if (mOk) ok = true;
                            }
                        }
                    }

                    return new JObject
                    {
                        ["success"] = ok,
                        ["message"] = ok ? $"已停止 PLCSIM 实例: {instanceName}" : "所有停止方式均失败",
                        ["instanceName"] = instanceName,
                        ["state"] = SafeReflectGet(instance, "State") ?? "",
                        ["attempts"] = new JArray(attempts)
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 获取 PLCSIM 实例当前状态（Run/Stop/PowerOff）。
        /// 反射读取 instance.State / OperatingState 属性，返回状态字符串与所有可读属性。
        /// </summary>
        public JObject PlcSimGetInstanceState(string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    var allProps = new JObject();
                    try
                    {
                        foreach (var p in instance.GetType().GetProperties())
                        {
                            try { allProps[p.Name] = p.GetValue(instance)?.ToString() ?? ""; } catch { }
                        }
                    }
                    catch { }

                    return new JObject
                    {
                        ["success"] = true,
                        ["instanceName"] = instanceName,
                        ["state"] = SafeReflectGet(instance, "State") ?? "",
                        ["operatingState"] = SafeReflectGet(instance, "OperatingState") ?? "",
                        ["isPowered"] = SafeReflectGet(instance, "IsPowered") ?? "",
                        ["instanceType"] = instance.GetType().FullName,
                        ["allProperties"] = allProps
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 设置 PLCSIM 实例电源（on/off）。
        /// on=true 反射调用 PowerOn()；on=false 反射调用 PowerOff()。
        /// </summary>
        public JObject PlcSimSetInstancePower(string instanceName, bool on)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    var methodName = on ? "PowerOn" : "PowerOff";
                    var attempts = new List<string>();
                    bool ok = false;

                    var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, Array.Empty<object>());
                    attempts.Add(att);
                    if (mOk) ok = true;

                    // 回退：SetPower(bool) / SetPowerState(bool)
                    if (!ok)
                    {
                        var (mOk2, _, att2) = PlcSimInvokeMethod(instance, null, "SetPower", new object[] { on });
                        attempts.Add(att2);
                        if (mOk2) ok = true;
                    }

                    return new JObject
                    {
                        ["success"] = ok,
                        ["message"] = ok ? $"已{(on ? "上电" : "断电")} PLCSIM 实例: {instanceName}" : $"{methodName} 调用失败",
                        ["instanceName"] = instanceName,
                        ["powerOn"] = on,
                        ["isPowered"] = SafeReflectGet(instance, "IsPowered") ?? "",
                        ["state"] = SafeReflectGet(instance, "State") ?? "",
                        ["attempts"] = new JArray(attempts)
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 三、与 TIA Portal 项目的连接 / 下载 / 上传
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 连接 TIA Portal 中的 PLC 到 PLCSIM 实例。
        /// 需要 plcName 对应的 PLC 设备存在。反射探测 PLCSIM 实例上的 ConnectToPlc / Connect 方法，
        /// 以及 PLC 设备上的 PLCSIM 相关服务。返回所有尝试路径。
        /// </summary>
        public JObject PlcSimConnectToPlcSim(string plcName, string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(plcName)) return MakeError("plcName 不能为空");
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");

                    var plc = ResolvePlc(plcName);
                    if (plc == null)
                        return MakeError("未找到 PLC" + (string.IsNullOrEmpty(plcName) ? "" : $": {plcName}"));

                    var instance = GetPlcSimInstance(instanceName);
                    var triedPaths = new List<string>();
                    if (instance == null)
                    {
                        triedPaths.Add($"缓存中未找到实例: {instanceName}（尝试从 PLCSIM API 查找）");
                        var asm = LoadPlcSimApi(triedPaths);
                        // 尝试从 API 查找实例（略，保持简单：提示用户先 create_instance）
                        return new JObject
                        {
                            ["success"] = false,
                            ["error"] = $"未找到缓存的 PLCSIM 实例: {instanceName}（请先 plcsim_create_instance）",
                            ["apiExplored"] = true,
                            ["triedPaths"] = new JArray(triedPaths)
                        };
                    }

                    var attempts = new List<string>();
                    bool ok = false;

                    // 方式1: instance.ConnectToPlc(plc) / Connect(plc)
                    foreach (var methodName in new[] { "ConnectToPlc", "Connect", "AttachToPlc" })
                    {
                        var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, new object[] { plc });
                        attempts.Add(att);
                        if (mOk) { ok = true; break; }
                    }

                    // 方式2: instance.ConnectToPlc(plc, args...) 多参数
                    if (!ok)
                    {
                        foreach (var methodName in new[] { "ConnectToPlc", "Connect" })
                        {
                            var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, new object[] { plc, instanceName });
                            attempts.Add(att);
                            if (mOk) { ok = true; break; }
                        }
                    }

                    // 方式3: PLC 侧的 PLCSIM 服务（反射探测）
                    if (!ok)
                    {
                        attempts.Add("plc.GetService<PlcSimProvider>() 反射探测");
                        try
                        {
                            // 在已加载程序集中查找 PLCSIM 相关服务类型
                            Type? plcsimSvcType = null;
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try
                                {
                                    plcsimSvcType = asm.GetTypes().FirstOrDefault(t =>
                                        t.IsInterface && t.Name.IndexOf("PlcSim", StringComparison.OrdinalIgnoreCase) >= 0
                                        && t.Name.IndexOf("Provider", StringComparison.OrdinalIgnoreCase) >= 0);
                                    if (plcsimSvcType != null) break;
                                }
                                catch { }
                            }
                            if (plcsimSvcType != null)
                            {
                                attempts.Add($"找到 PLCSIM 服务类型: {plcsimSvcType.FullName}");
                                var svc = TryGetService(plc, plcsimSvcType);
                                if (svc != null)
                                {
                                    attempts.Add($"plc.GetService<{plcsimSvcType.Name}>() => 成功");
                                    foreach (var methodName in new[] { "Connect", "ConnectToPlcSim", "Attach" })
                                    {
                                        var (mOk, _, att) = PlcSimInvokeMethod(svc, null, methodName, new object[] { instance });
                                        attempts.Add(att);
                                        if (mOk) { ok = true; break; }
                                    }
                                }
                                else
                                {
                                    attempts.Add($"plc.GetService<{plcsimSvcType.Name}>() => 返回 null");
                                }
                            }
                            else
                            {
                                attempts.Add("未找到 PLCSIM Provider 服务类型");
                            }
                        }
                        catch (Exception ex) { attempts.Add("PLC 侧 PLCSIM 服务探测异常: " + ex.Message); }
                    }

                    return new JObject
                    {
                        ["success"] = ok,
                        ["message"] = ok ? $"已连接 PLC [{plcName}] 到 PLCSIM 实例 [{instanceName}]" : "所有连接方式均失败",
                        ["plcName"] = plcName,
                        ["instanceName"] = instanceName,
                        ["instanceState"] = SafeReflectGet(instance, "State") ?? "",
                        ["attempts"] = new JArray(attempts),
                        ["instanceMethods"] = new JArray(PlcSimListMethodNames(instance))
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 断开 TIA Portal 与 PLCSIM 的连接。
        /// 反射探测 PLCSIM 实例上的 Disconnect / Detach 方法，以及缓存实例的连接状态。
        /// </summary>
        public JObject PlcSimDisconnectFromPlcSim()
        {
            lock (_lock)
            {
                try
                {
                    var attempts = new List<string>();
                    bool ok = false;
                    string disconnectedInstance = "";

                    // 遍历所有缓存实例，尝试断开（静态锁保护快照）
                    var snapshot = GetCachedInstanceSnapshot();
                    foreach (var kvp in snapshot)
                    {
                        foreach (var methodName in new[] { "Disconnect", "DisconnectFromPlc", "Detach", "DisconnectFromPlcSim" })
                        {
                            var (mOk, _, att) = PlcSimInvokeMethod(kvp.Value, null, methodName, Array.Empty<object>());
                            attempts.Add($"[{kvp.Key}] {att}");
                            if (mOk) { ok = true; disconnectedInstance = kvp.Key; break; }
                        }
                        if (ok) break;
                    }

                    bool isEmpty;
                    lock (_plcsimStaticLock) { isEmpty = _plcsimInstances.IsEmpty; }
                    if (!ok && isEmpty)
                        attempts.Add("无缓存的 PLCSIM 实例（无需断开）");

                    return new JObject
                    {
                        ["success"] = ok || isEmpty,
                        ["message"] = ok ? $"已断开 PLCSIM 实例 [{disconnectedInstance}] 连接" : "所有断开方式均失败",
                        ["disconnectedInstance"] = disconnectedInstance,
                        ["attempts"] = new JArray(attempts)
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 下载 TIA Portal 项目到 PLCSIM 实例。
        /// 需要 plcName 对应的 PLC 设备存在，且 PLCSIM 实例已创建并上电。
        /// 反射探测 PLC 的下载配置服务，尝试配置目标为 PLCSIM 实例后下载。
        /// 返回所有尝试路径。实际下载可能需结合 download_to_device 工具。
        /// </summary>
        public JObject PlcSimDownloadToInstance(string plcName, string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(plcName)) return MakeError("plcName 不能为空");
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");

                    var plc = ResolvePlc(plcName);
                    if (plc == null)
                        return MakeError("未找到 PLC" + (string.IsNullOrEmpty(plcName) ? "" : $": {plcName}"));

                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}（请先 plcsim_create_instance）");

                    var attempts = new List<string>();
                    bool ok = false;

                    // 方式1: instance.Download(plc) / DownloadToPlc(plc)
                    foreach (var methodName in new[] { "Download", "DownloadToPlc", "DownloadBlocks" })
                    {
                        var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, new object[] { plc });
                        attempts.Add(att);
                        if (mOk) { ok = true; break; }
                    }

                    // 方式2: PLC 侧下载配置（反射探测 DownloadConfigurationProvider）
                    if (!ok)
                    {
                        attempts.Add("plc 侧下载配置反射探测");
                        try
                        {
                            // 查找下载配置相关服务
                            Type? dlCfgType = null;
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try
                                {
                                    dlCfgType = asm.GetTypes().FirstOrDefault(t =>
                                        t.IsInterface && t.Name.IndexOf("DownloadConfig", StringComparison.OrdinalIgnoreCase) >= 0);
                                    if (dlCfgType != null) break;
                                }
                                catch { }
                            }
                            if (dlCfgType != null)
                            {
                                attempts.Add($"找到下载配置类型: {dlCfgType.FullName}");
                                var svc = TryGetService(plc, dlCfgType);
                                attempts.Add(svc != null ? $"plc.GetService<{dlCfgType.Name}>() => 成功" : $"plc.GetService<{dlCfgType.Name}>() => null");
                            }
                            else
                            {
                                attempts.Add("未找到下载配置类型（建议使用 download_to_device 工具配合 PLCSIM 网络适配器）");
                            }
                        }
                        catch (Exception ex) { attempts.Add("下载配置探测异常: " + ex.Message); }
                    }

                    return new JObject
                    {
                        ["success"] = ok,
                        ["message"] = ok ? $"已下载 PLC [{plcName}] 到 PLCSIM 实例 [{instanceName}]" : "直接下载未成功，建议先 plcsim_connect_to_plcsim 后使用 download_to_device 工具",
                        ["plcName"] = plcName,
                        ["instanceName"] = instanceName,
                        ["instanceState"] = SafeReflectGet(instance, "State") ?? "",
                        ["attempts"] = new JArray(attempts),
                        ["instanceMethods"] = new JArray(PlcSimListMethodNames(instance))
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 从 PLCSIM 实例上传到 TIA Portal 项目。
        /// 需要 plcName 对应的 PLC 设备存在，且 PLCSIM 实例已创建并上电。
        /// 反射探测 PLCSIM 实例上的 Upload / ReadBlocks 方法。
        /// 返回所有尝试路径。
        /// </summary>
        public JObject PlcSimUploadFromInstance(string plcName, string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(plcName)) return MakeError("plcName 不能为空");
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");

                    var plc = ResolvePlc(plcName);
                    if (plc == null)
                        return MakeError("未找到 PLC" + (string.IsNullOrEmpty(plcName) ? "" : $": {plcName}"));

                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}（请先 plcsim_create_instance）");

                    var attempts = new List<string>();
                    bool ok = false;

                    // 方式1: instance.Upload(plc) / ReadFromPlc(plc) / UploadBlocks(plc)
                    foreach (var methodName in new[] { "Upload", "UploadFromPlc", "ReadBlocks", "ReadFromPlc" })
                    {
                        var (mOk, _, att) = PlcSimInvokeMethod(instance, null, methodName, new object[] { plc });
                        attempts.Add(att);
                        if (mOk) { ok = true; break; }
                    }

                    return new JObject
                    {
                        ["success"] = ok,
                        ["message"] = ok ? $"已从 PLCSIM 实例 [{instanceName}] 上传到 PLC [{plcName}]" : "所有上传方式均失败",
                        ["plcName"] = plcName,
                        ["instanceName"] = instanceName,
                        ["instanceState"] = SafeReflectGet(instance, "State") ?? "",
                        ["attempts"] = new JArray(attempts),
                        ["instanceMethods"] = new JArray(PlcSimListMethodNames(instance))
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 获取 PLCSIM 实例详细信息。
        /// 反射收集实例所有可读属性、方法名，便于 AI 理解 API 结构。
        /// </summary>
        public JObject PlcSimGetInstanceInfo(string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(instanceName)) return MakeError("instanceName 不能为空");
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null)
                        return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    var allProps = new JObject();
                    try
                    {
                        foreach (var p in instance.GetType().GetProperties())
                        {
                            try { allProps[p.Name] = p.GetValue(instance)?.ToString() ?? ""; } catch { }
                        }
                    }
                    catch { }

                    return new JObject
                    {
                        ["success"] = true,
                        ["instanceName"] = instanceName,
                        ["name"] = SafeReflectGet(instance, "Name") ?? "",
                        ["state"] = SafeReflectGet(instance, "State") ?? "",
                        ["operatingState"] = SafeReflectGet(instance, "OperatingState") ?? "",
                        ["isPowered"] = SafeReflectGet(instance, "IsPowered") ?? "",
                        ["instanceType"] = instance.GetType().FullName,
                        ["allProperties"] = allProps,
                        ["methods"] = new JArray(PlcSimListMethodNames(instance)),
                        ["propertyNames"] = new JArray(PlcSimListPropertyNames(instance))
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 四、PLCSIM Advanced 适配器管理
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 列出 PLCSIM Advanced 适配器。
        /// 加载 ConfigureAdapters DLL，反射调用 ConfigureAdapters.GetAdapters() / ListAdapters() 等方法。
        /// 失败时返回 triedPaths 便于诊断。
        /// </summary>
        public JObject PlcSimAdvancedListAdapters()
        {
            lock (_lock)
            {
                try
                {
                    var triedPaths = new List<string>();
                    var asm = LoadPlcSimAdvancedApi(triedPaths);
                    if (asm == null)
                        return MakeApiExplored("PLCSIM Advanced API", triedPaths);

                    var cfgType = ResolvePlcSimType(asm, ConfigureAdaptersTypeFullName);
                    if (cfgType == null)
                    {
                        // 模糊查找 ConfigureAdapters 类型
                        try
                        {
                            cfgType = asm.GetTypes().FirstOrDefault(t => t.Name.IndexOf("ConfigureAdapters", StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        catch { }
                    }
                    if (cfgType == null)
                    {
                        var allTypes = new List<string>();
                        try { foreach (var t in asm.GetTypes()) allTypes.Add(t.FullName ?? t.Name); } catch { }
                        return new JObject
                        {
                            ["success"] = false,
                            ["error"] = "未找到 ConfigureAdapters 类型",
                            ["apiExplored"] = true,
                            ["triedPaths"] = new JArray(triedPaths),
                            ["assemblyTypes"] = new JArray(allTypes)
                        };
                    }

                    var attempts = new List<string>();
                    JArray adapters = new JArray();

                    // 尝试多种列出方法
                    foreach (var methodName in new[] { "GetAdapters", "ListAdapters", "GetAllAdapters", "Adapters" })
                    {
                        // 静态方法
                        var (mOk, res, att) = PlcSimInvokeMethod(null, cfgType, methodName, Array.Empty<object>(), isStatic: true);
                        attempts.Add("static " + att);
                        if (mOk && res is IEnumerable enumerable)
                        {
                            foreach (var item in enumerable)
                            {
                                var obj = new JObject();
                                try
                                {
                                    foreach (var p in item.GetType().GetProperties())
                                    {
                                        try { obj[p.Name] = p.GetValue(item)?.ToString() ?? ""; } catch { }
                                    }
                                }
                                catch { }
                                adapters.Add(obj);
                            }
                            break;
                        }

                        // 静态属性
                        if (adapters.Count == 0)
                        {
                            try
                            {
                                attempts.Add($"static prop {methodName}");
                                var prop = cfgType.GetProperty(methodName, BindingFlags.Public | BindingFlags.Static);
                                if (prop != null)
                                {
                                    var coll = prop.GetValue(null);
                                    if (coll is IEnumerable en)
                                        foreach (var item in en)
                                        {
                                            var obj = new JObject();
                                            try { foreach (var p in item.GetType().GetProperties()) try { obj[p.Name] = p.GetValue(item)?.ToString() ?? ""; } catch { } } catch { }
                                            adapters.Add(obj);
                                        }
                                }
                            }
                            catch (Exception ex) { attempts[attempts.Count - 1] += " => " + ex.Message; }
                        }
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["count"] = adapters.Count,
                        ["adapters"] = adapters,
                        ["configType"] = cfgType.FullName,
                        ["attempts"] = new JArray(attempts),
                        ["staticMethods"] = new JArray(PlcSimListStaticMethodNames(cfgType))
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 配置 PLCSIM Advanced 适配器。
        /// 加载 ConfigureAdapters DLL，反射调用 ConfigureAdapters.Configure / CreateAdapter / AddAdapter 方法。
        /// adapterName 适配器名称，ipAddress 可选 IP，subnetMask 可选子网掩码。
        /// 返回所有尝试路径。
        /// </summary>
        public JObject PlcSimAdvancedConfigureAdapter(string adapterName, string? ipAddress = null, string? subnetMask = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(adapterName)) return MakeError("adapterName 不能为空");

                    var triedPaths = new List<string>();
                    var asm = LoadPlcSimAdvancedApi(triedPaths);
                    if (asm == null)
                        return MakeApiExplored("PLCSIM Advanced API", triedPaths);

                    var cfgType = ResolvePlcSimType(asm, ConfigureAdaptersTypeFullName);
                    if (cfgType == null)
                    {
                        try { cfgType = asm.GetTypes().FirstOrDefault(t => t.Name.IndexOf("ConfigureAdapters", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
                    }
                    if (cfgType == null)
                        return MakeError("未找到 ConfigureAdapters 类型");

                    var attempts = new List<string>();
                    bool ok = false;
                    object? result = null;

                    // 方式1: ConfigureAdapters.Configure(adapterName) / Configure(adapterName, ip, mask)
                    if (!string.IsNullOrEmpty(ipAddress) && !string.IsNullOrEmpty(subnetMask))
                    {
                        var (mOk, res, att) = PlcSimInvokeMethod(null, cfgType, "Configure", new object[] { adapterName, ipAddress, subnetMask }, isStatic: true);
                        attempts.Add(att);
                        if (mOk) { ok = true; result = res; }
                    }

                    if (!ok)
                    {
                        var (mOk, res, att) = PlcSimInvokeMethod(null, cfgType, "Configure", new object[] { adapterName }, isStatic: true);
                        attempts.Add(att);
                        if (mOk) { ok = true; result = res; }
                    }

                    // 方式2: CreateAdapter / AddAdapter
                    if (!ok)
                    {
                        foreach (var methodName in new[] { "CreateAdapter", "AddAdapter", "Create" })
                        {
                            var (mOk, res, att) = PlcSimInvokeMethod(null, cfgType, methodName, new object[] { adapterName }, isStatic: true);
                            attempts.Add(att);
                            if (mOk) { ok = true; result = res; break; }
                        }
                    }

                    return new JObject
                    {
                        ["success"] = ok,
                        ["message"] = ok ? $"已配置 PLCSIM Advanced 适配器: {adapterName}" : "所有配置方式均失败",
                        ["adapterName"] = adapterName,
                        ["ipAddress"] = ipAddress ?? "",
                        ["subnetMask"] = subnetMask ?? "",
                        ["result"] = result?.ToString() ?? "",
                        ["configType"] = cfgType.FullName,
                        ["attempts"] = new JArray(attempts),
                        ["staticMethods"] = new JArray(PlcSimListStaticMethodNames(cfgType))
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // 五、PLCSIM IO 闭环（SetInput/SetTagValue/ReadOutput 反射驱动）
        // 用于仿真的自动激励注入与输出观测。
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>反射方法签名转字符串（用于诊断输出）。</summary>
        private static string PlcSimSig(MethodBase m)
        {
            try
            {
                var ps = m is MethodInfo mi ? mi.GetParameters() : new ParameterInfo[0];
                return m.Name + "(" + string.Join(",", ps.Select(p => p.ParameterType.Name)) + ") -> " +
                       (m is MethodInfo mm ? mm.ReturnType.Name : "");
            }
            catch { return m.Name; }
        }

        /// <summary>
        /// 获取实例上的运行时接口（SimulationRuntime/SimulationController）。
        /// 依次尝试实例属性名、实例无参方法、程序集内 SimulationController 静态入口。
        /// </summary>
        private static object? PlcSimAcquireRuntime(object instance, JObject log)
        {
            var t = instance.GetType();
            foreach (var pname in new[] { "SimulationRuntime", "Runtime", "RuntimeService", "Controller", "SimulationController" })
            {
                var p = t.GetProperty(pname, BindingFlags.Public | BindingFlags.Instance);
                if (p == null) continue;
                try
                {
                    var v = p.GetValue(instance);
                    log[pname] = v == null ? "null" : v.GetType().FullName;
                    if (v != null) return v;
                }
                catch (Exception ex) { log[pname] = "ERR:" + ex.Message; }
            }
            foreach (var mname in new[] { "GetSimulationRuntime", "GetRuntime", "GetController" })
            {
                var m = t.GetMethod(mname, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m == null) continue;
                try
                {
                    var v = m.Invoke(instance, null);
                    log[mname + "()"] = v == null ? "null" : v.GetType().FullName;
                    if (v != null) return v;
                }
                catch (Exception ex) { log[mname + "()"] = "ERR:" + ex.Message; }
            }
            try
            {
                var asm = instance.GetType().Assembly;
                foreach (var tn in new[] { "Siemens.Simatic.PlcSim.VplcApi.SimulationController", "Siemens.Simatic.PlcSim.VplcApi.SimulationRuntimeController" })
                {
                    var st = asm.GetType(tn);
                    if (st == null) continue;
                    foreach (var sp in st.GetProperties(BindingFlags.Public | BindingFlags.Static))
                    {
                        try
                        {
                            var v = sp.GetValue(null);
                            log[tn + "." + sp.Name] = v == null ? "null" : v.GetType().FullName;
                            if (v != null) return v;
                        }
                        catch (Exception ex) { log[tn + "." + sp.Name] = "ERR:" + ex.Message; }
                    }
                    foreach (var sm in st.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetParameters().Length == 0))
                    {
                        try
                        {
                            var v = sm.Invoke(null, null);
                            log[tn + "." + sm.Name + "()"] = v == null ? "null" : v.GetType().FullName;
                            if (v != null) return v;
                        }
                        catch (Exception ex) { log[tn + "." + sm.Name + "()"] = "ERR:" + ex.Message; }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 探测 PLCSIM IO API 结构：实例类型成员、运行时接口获取路径、
        /// ProcessValue/ProcessValueAddress 类型成员与 Types 命名空间全部枚举值。
        /// </summary>
        public JObject PlcSimIoProbe(string instanceName)
        {
            lock (_lock)
            {
                try
                {
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null) return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    var result = new JObject { ["success"] = true, ["instanceName"] = instanceName };
                    var instType = instance.GetType();
                    result["instanceType"] = instType.FullName;
                    var instProps = new JObject();
                    foreach (var p in instType.GetProperties())
                    {
                        try
                        {
                            var v = p.GetValue(instance);
                            instProps[p.Name] = v == null ? "null"
                                : (v is string || v.GetType().IsPrimitive || v is Enum ? v.ToString() : v.GetType().FullName);
                        }
                        catch (Exception ex) { instProps[p.Name] = "ERR:" + ex.Message; }
                    }
                    result["instanceProperties"] = instProps;
                    result["instanceMethods"] = new JArray(instType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => !m.IsSpecialName).Select(PlcSimSig));

                    var acqLog = new JObject();
                    var runtime = PlcSimAcquireRuntime(instance, acqLog);
                    result["runtimeAcquisition"] = acqLog;
                    if (runtime != null)
                    {
                        result["runtimeType"] = runtime.GetType().FullName;
                        result["runtimeMethods"] = new JArray(runtime.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName).Select(PlcSimSig));
                    }

                    var typeInfo = new JArray();
                    var asm = LoadPlcSimApi(new List<string>());
                    if (asm != null)
                    {
                        foreach (var tn in new[]
                        {
                            "Siemens.Simatic.PlcSim.VplcApi.Types.ProcessValue",
                            "Siemens.Simatic.PlcSim.VplcApi.Types.ProcessValueAddress",
                            "Siemens.Simatic.PlcSim.VplcApi.ProcessValue",
                            "Siemens.Simatic.PlcSim.VplcApi.ProcessValueAddress",
                            "Siemens.Simatic.PlcSim.VplcApi.SimulationController"
                        })
                        {
                            Type? t = null;
                            try { t = asm.GetType(tn); } catch { }
                            if (t == null)
                            {
                                try { t = asm.GetTypes().FirstOrDefault(x => x.FullName == tn || x.Name == tn.Split('.').Last()); } catch { }
                            }
                            if (t == null) continue;
                            var tj = new JObject { ["typeName"] = t.FullName, ["isEnum"] = t.IsEnum };
                            if (t.IsEnum)
                            {
                                tj["values"] = new JArray(Enum.GetNames(t).Select(n => n + "=" + Convert.ToInt64(Enum.Parse(t, n))));
                            }
                            else
                            {
                                tj["constructors"] = new JArray(t.GetConstructors().Select(PlcSimSig));
                                tj["properties"] = new JArray(t.GetProperties().Select(p => p.Name + " : " + p.PropertyType.Name + (p.CanWrite ? " (rw)" : " (ro)")));
                                tj["fields"] = new JArray(t.GetFields().Select(f => f.Name + " : " + f.FieldType.Name));
                                tj["staticMethods"] = new JArray(t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => !m.IsSpecialName).Select(PlcSimSig));
                                tj["staticProperties"] = new JArray(t.GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => p.Name + " : " + p.PropertyType.Name));
                            }
                            typeInfo.Add(tj);
                        }
                        try
                        {
                            foreach (var t in asm.GetTypes().Where(t => t.IsEnum && (t.Namespace ?? "").Contains("Types")))
                            {
                                typeInfo.Add(new JObject
                                {
                                    ["typeName"] = t.FullName,
                                    ["isEnum"] = true,
                                    ["values"] = new JArray(Enum.GetNames(t).Select(n => n + "=" + Convert.ToInt64(Enum.Parse(t, n))))
                                });
                            }
                        }
                        catch { }
                    }
                    result["relevantTypes"] = typeInfo;
                    return result;
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>按类型名在 PLCSIM 程序集内解析类型（Types 子命名空间优先）。</summary>
        private static Type? PlcSimResolveValueType(Assembly asm, string shortName)
        {
            foreach (var full in new[]
            {
                "Siemens.Simatic.PlcSim.VplcApi.Types." + shortName,
                "Siemens.Simatic.PlcSim.VplcApi." + shortName
            })
            {
                try { var t = asm.GetType(full); if (t != null) return t; } catch { }
            }
            try { return asm.GetTypes().FirstOrDefault(x => x.Name == shortName); } catch { return null; }
        }

        /// <summary>给目标对象按候选属性/字段名设置值（自动类型转换），返回尝试日志。</summary>
        private static string PlcSimSetMember(object target, string[] candidateNames, object value)
        {
            var t = target.GetType();
            foreach (var name in candidateNames)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite)
                {
                    try
                    {
                        object? converted = PlcSimConvert(value, p.PropertyType);
                        if (converted != null)
                        {
                            p.SetValue(target, converted);
                            return $"prop {name}={converted}";
                        }
                    }
                    catch (Exception ex) { return $"prop {name} 异常: {ex.Message}"; }
                }
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null)
                {
                    try
                    {
                        object? converted = PlcSimConvert(value, f.FieldType);
                        if (converted != null)
                        {
                            f.SetValue(target, converted);
                            return $"field {name}={converted}";
                        }
                    }
                    catch (Exception ex) { return $"field {name} 异常: {ex.Message}"; }
                }
            }
            return "无可写成员";
        }

        /// <summary>把 JToken/原始值转换为目标类型（bool/数值/枚举/字符串）。</summary>
        private static object? PlcSimConvert(object? value, Type targetType)
        {
            if (targetType == typeof(string)) return value?.ToString();
            if (targetType.IsEnum)
            {
                var s = value?.ToString() ?? "";
                var names = Enum.GetNames(targetType);
                var hit = names.FirstOrDefault(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                {
                    // 允许按数字值解析
                    try { return Enum.ToObject(targetType, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)); }
                    catch { return null; }
                }
                return Enum.Parse(targetType, hit);
            }
            var nt = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nt == typeof(bool)) return value is bool bv ? bv : (value?.ToString() == "1" || string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
            if (nt == typeof(byte)) return Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(short)) return Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(ushort)) return Convert.ToUInt16(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(int)) return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(uint)) return Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(long)) return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(ulong)) return Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(float)) return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(double)) return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            try { return Convert.ChangeType(value, nt, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        /// <summary>
        /// 构造 ProcessValue 对象（Types.ProcessValue / VplcApi.ProcessValue）。
        /// area: I/Q/M/T/C/DB；byte/bit 为地址；value 为值。
        /// </summary>
        private (object? pv, List<string> log) PlcSimBuildProcessValue(string area, int byteOffset, int? bit, object? value)
        {
            var log = new List<string>();
            var asm = LoadPlcSimApi(log);
            if (asm == null) return (null, log);
            var t = PlcSimResolveValueType(asm, "ProcessValue");
            if (t == null) { log.Add("未找到 ProcessValue 类型"); return (null, log); }
            object pv;
            try { pv = Activator.CreateInstance(t)!; } catch (Exception ex) { log.Add("CreateInstance 异常: " + ex.Message); return (null, log); }

            // Area 枚举：候选名映射（大小写/别名）
            var areaProp = t.GetProperty("Area") ?? t.GetProperty("MemoryArea") ?? t.GetProperty("DataType") ?? t.GetProperty("Type");
            if (areaProp != null)
            {
                var et = areaProp.PropertyType;
                if (et.IsEnum)
                {
                    string[] cands = area.ToUpperInvariant() switch
                    {
                        "I" => new[] { "Input", "Inputs", "I", "In", "InputImage", "PE", "PeripheralInput" },
                        "Q" => new[] { "Output", "Outputs", "O", "Out", "OutputImage", "PA", "PeripheralOutput" },
                        "M" => new[] { "Memory", "Memories", "M", "Flag", "Flags", "Merker", "MemoryBits" },
                        "T" => new[] { "Timer", "Timers", "T" },
                        "C" => new[] { "Counter", "Counters", "C" },
                        "DB" => new[] { "DataBlock", "DataBlocks", "DB" },
                        _ => new[] { area }
                    };
                    string? hit = null;
                    var names = Enum.GetNames(et);
                    foreach (var c in cands)
                    {
                        hit = names.FirstOrDefault(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase));
                        if (hit != null) break;
                    }
                    if (hit == null) hit = names.FirstOrDefault(n => n.IndexOf(cands[0], StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hit != null)
                    {
                        areaProp.SetValue(pv, Enum.Parse(et, hit));
                        log.Add($"Area={hit}");
                    }
                    else log.Add($"Area 枚举未匹配({string.Join("/", cands)}), 可用值: {string.Join(",", names)}");
                }
                else log.Add($"Area 属性非枚举({et.Name}), 尝试直接赋值");
                if (areaProp.PropertyType == typeof(string)) { areaProp.SetValue(pv, area); log.Add($"Area(string)={area}"); }
            }
            else log.Add("无 Area 属性");

            log.Add("Byte: " + PlcSimSetMember(pv, new[] { "Byte", "ByteNumber", "ByteOffset", "Offset", "Address" }, byteOffset));
            if (bit.HasValue) log.Add("Bit: " + PlcSimSetMember(pv, new[] { "Bit", "BitNumber", "BitOffset" }, bit.Value));
            log.Add("Value: " + PlcSimSetMember(pv, new[] { "Value", "Data", "ProcessValue" }, value ?? false));
            return (pv, log);
        }

        /// <summary>
        /// 构造 ProcessValueAddress 对象（用于 SetInputBit）。
        /// </summary>
        private (object? pva, List<string> log) PlcSimBuildProcessValueAddress(int byteOffset, int bit)
        {
            var log = new List<string>();
            var asm = LoadPlcSimApi(log);
            if (asm == null) return (null, log);
            var t = PlcSimResolveValueType(asm, "ProcessValueAddress");
            if (t == null) { log.Add("未找到 ProcessValueAddress 类型"); return (null, log); }
            object pva;
            try { pva = Activator.CreateInstance(t)!; } catch (Exception ex) { log.Add("CreateInstance 异常: " + ex.Message); return (null, log); }
            log.Add("Byte: " + PlcSimSetMember(pva, new[] { "Byte", "ByteNumber", "ByteOffset", "Offset", "Address" }, byteOffset));
            log.Add("Bit: " + PlcSimSetMember(pva, new[] { "Bit", "BitNumber", "BitOffset" }, bit));
            return (pva, log);
        }

        /// <summary>
        /// 写 PLCSIM 过程映像值（激励注入）。
        /// writesJson: [{"area":"I"|"M"|"Q","byte":0,"bit":0,"value":true|123}]
        /// I 区走 SetInput/SetInputBit，其余走 SetTagValue。
        /// </summary>
        public JObject PlcSimWriteValues(string instanceName, string writesJson)
        {
            lock (_lock)
            {
                try
                {
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null) return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    JArray writes;
                    try { writes = JArray.Parse(writesJson); }
                    catch { return MakeError("writesJson 必须是 JSON 数组"); }

                    var acqLog = new JObject();
                    var runtime = PlcSimAcquireRuntime(instance, acqLog);
                    var targets = new List<object> { instance };
                    if (runtime != null) targets.Insert(0, runtime);

                    var results = new JArray();
                    foreach (var w in writes)
                    {
                        var area = (w["area"]?.ToString() ?? "M").ToUpperInvariant();
                        var byteOffset = w["byte"]?.ToObject<int>() ?? 0;
                        var bit = w["bit"]?.ToObject<int>();
                        var value = w["value"] ?? JValue.CreateNull();
                        var entry = new JObject
                        {
                            ["area"] = area,
                            ["byte"] = byteOffset,
                            ["bit"] = bit,
                            ["value"] = value.ToString()
                        };
                        var attempts = new List<string>();
                        bool ok = false;

                        if (area == "I" && bit.HasValue)
                        {
                            // 位输入: SetInputBit(ProcessValueAddress, byte)
                            var (pva, pvaLog) = PlcSimBuildProcessValueAddress(byteOffset, bit.Value);
                            attempts.AddRange(pvaLog);
                            if (pva != null)
                            {
                                var bitVal = Convert.ToByte(value.ToObject<bool>() ? 1 : 0);
                                foreach (var target in targets)
                                {
                                    var (mOk, _, att) = PlcSimInvokeMethod(target, null, "SetInputBit", new object[] { pva, bitVal });
                                    attempts.Add(target.GetType().Name + ": " + att);
                                    if (mOk) { ok = true; break; }
                                }
                            }
                        }

                        if (!ok)
                        {
                            var (pv, pvLog) = PlcSimBuildProcessValue(area, byteOffset, bit, value.ToObject<object>());
                            attempts.AddRange(pvLog);
                            if (pv != null)
                            {
                                var methodName = area == "I" ? "SetInput" : "SetTagValue";
                                foreach (var target in targets)
                                {
                                    var (mOk, _, att) = PlcSimInvokeMethod(target, null, methodName, new[] { pv });
                                    attempts.Add(target.GetType().Name + ": " + att);
                                    if (mOk) { ok = true; break; }
                                }
                                if (!ok && area == "I")
                                {
                                    foreach (var target in targets)
                                    {
                                        var (mOk, _, att) = PlcSimInvokeMethod(target, null, "SetInput", new[] { pv });
                                        attempts.Add(target.GetType().Name + " SetInput: " + att);
                                        if (mOk) { ok = true; break; }
                                    }
                                }
                            }
                        }

                        entry["success"] = ok;
                        entry["attempts"] = new JArray(attempts);
                        results.Add(entry);
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["instanceName"] = instanceName,
                        ["runtimeFound"] = runtime != null,
                        ["runtimeAcquisition"] = acqLog,
                        ["results"] = results
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }

        /// <summary>
        /// 读 PLCSIM 输出映像（ReadOutput 字节 + 位图）。
        /// 尝试多种参数组合（首参=起始字节/区域选择），返回各次尝试的十六进制与位分解。
        /// </summary>
        public JObject PlcSimReadValues(string instanceName, int byteOffset, int count)
        {
            lock (_lock)
            {
                try
                {
                    var instance = GetPlcSimInstance(instanceName);
                    if (instance == null) return MakeError($"未找到缓存的 PLCSIM 实例: {instanceName}");

                    var acqLog = new JObject();
                    var runtime = PlcSimAcquireRuntime(instance, acqLog);
                    var targets = new List<object> { instance };
                    if (runtime != null) targets.Insert(0, runtime);

                    var results = new JArray();
                    var combos = new[] { (0UL, (ulong)Math.Max(1, count)), ((ulong)Math.Max(0, byteOffset), (ulong)Math.Max(1, count)) };
                    foreach (var target in targets)
                    {
                        foreach (var (a, b) in combos)
                        {
                            var (ok, res, att) = PlcSimInvokeMethod(target, null, "ReadOutput", new object[] { a, b });
                            if (!ok) { results.Add(new JObject { ["target"] = target.GetType().Name, ["a"] = a, ["b"] = b, ["ok"] = false, ["attempt"] = att }); continue; }
                            var bytes = res as byte[];
                            var hex = bytes != null ? BitConverter.ToString(bytes) : (res?.ToString() ?? "null");
                            var jo = new JObject { ["target"] = target.GetType().Name, ["a"] = a, ["b"] = b, ["ok"] = true, ["hex"] = hex };
                            if (bytes != null)
                            {
                                var bits = new JArray();
                                for (int i = 0; i < bytes.Length; i++)
                                    for (int bit = 0; bit < 8; bit++)
                                        if ((bytes[i] & (1 << bit)) != 0) bits.Add($"Q{(i + (int)a) * 8 + bit}(B{i}.{bit})");
                                jo["setBits"] = bits;
                            }
                            results.Add(jo);
                            break; // 同 target 一次成功即停
                        }
                    }

                    return new JObject
                    {
                        ["success"] = true,
                        ["instanceName"] = instanceName,
                        ["runtimeFound"] = runtime != null,
                        ["runtimeAcquisition"] = acqLog,
                        ["results"] = results
                    };
                }
                catch (Exception ex) { return MakeException(ex); }
            }
        }
    }
}
