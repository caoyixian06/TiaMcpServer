using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;

namespace TiaMcpServer;

public partial class PortalService
{
    public string DownloadToFolder(string folderPath, string? plcName = null)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);
                var downloadProvider = plc.GetService<DownloadProvider>();
                if (downloadProvider == null)
                    return Err("PLC 不支持下载服务");

                var dir = new DirectoryInfo(folderPath);
                Directory.CreateDirectory(folderPath);
                // V19: Download(DirectoryInfo, DownloadOptions) 返回 CompilerResult；
                // V17: 无该重载（下载走 Download(IConfiguration,...)），反射尝试并给出说明。
                var dpType = downloadProvider.GetType();
                var m = dpType.GetMethod("Download", new[] { typeof(DirectoryInfo), typeof(DownloadOptions) });
                if (m == null)
                    return Err("当前 TIA 版本（V17）不支持 DownloadProvider.Download(DirectoryInfo, DownloadOptions) 下载到文件夹方式，" +
                        "请改用 download_to_device 工具（通过连接配置下载）");

                var result = m.Invoke(downloadProvider, new object[] { dir, DownloadOptions.None });
                var rt = result?.GetType();
                int errorCount = rt != null ? Convert.ToInt32(rt.GetProperty("ErrorCount")?.GetValue(result) ?? 0) : 1;
                var state = rt?.GetProperty("State")?.GetValue(result)?.ToString() ?? "";

                return JsonConvert.SerializeObject(new
                {
                    success = (errorCount == 0),
                    state,
                    errorCount,
                    warningCount = rt != null ? Convert.ToInt32(rt.GetProperty("WarningCount")?.GetValue(result) ?? 0) : 0,
                }, Formatting.Indented);
            }
            catch (TargetInvocationException tie) { return Err(tie.InnerException?.Message ?? tie.Message); }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // 下载/上传/CPU 控制 完整版 API
    // 采用反射调用 DownloadProvider/UploadProvider/OnlineProvider，兼容 V19。
    // 参考现有 DownloadService.DownloadToFolder 与 DiagnosticsService.DownloadToDeviceEnhanced 风格。
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 下载前检查。通过反射获取 DownloadProvider 的 Check 方法。
    /// 返回 success/checkResult/changesCount。
    /// </summary>
    public string DownloadCheck(string plcName)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);
                var dp = plc.GetService<DownloadProvider>();
                if (dp == null)
                    return Err($"PLC '{plc.Name}' 不支持 DownloadProvider 服务");

                var dpType = dp.GetType();

                // 反射查找 Check 方法（可能签名为 Check(IConfiguration) / Check(IConfiguration, Delegate, Delegate)）
                var checkMethod = dpType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Equals("Check", StringComparison.OrdinalIgnoreCase)
                                         && !m.IsSpecialName);
                if (checkMethod == null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "DownloadProvider 未暴露 Check 方法",
                        plcName = plc.Name,
                        apiExplored = ExploreProviderMethods(dpType)
                    }, Formatting.Indented);

                // 取 Configuration
                var config = dpType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dp);
                if (config == null)
                    return Err("DownloadProvider.Configuration 为空，未配置连接");

                object? result = null;
                var ps = checkMethod.GetParameters();
                try
                {
                    // 构造参数：第一参数为 IConfiguration，后续 Delegate 默认 null
                    var args = new object?[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i == 0)
                            args[i] = config;
                        else if (ps[i].ParameterType.IsSubclassOf(typeof(Delegate)) || ps[i].ParameterType == typeof(Delegate))
                            args[i] = null;
                        else if (ps[i].HasDefaultValue)
                            args[i] = ps[i].DefaultValue;
                        else
                            args[i] = null;
                    }
                    result = checkMethod.Invoke(dp, args);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Check 调用失败: {ex.InnerException?.Message ?? ex.Message}",
                        plcName = plc.Name,
                        invokedMethod = checkMethod.Name
                    }, Formatting.Indented);
                }

                // 序列化检查结果（反射读取 State/ChangesCount 等属性）
                var (checkResult, changesCount) = DescribeCheckResult(result);
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    plcName = plc.Name,
                    checkResult,
                    changesCount,
                    invokedMethod = checkMethod.Name,
                    rawType = result?.GetType().Name ?? ""
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 完整参数版下载到设备。
    /// downloadMode: Complete/Differences/StopFirst。
    /// 通过反射构建 DownloadConfiguration 并调用 Download 方法。
    /// 返回 success/downloadMode/result。
    /// </summary>
    public string DownloadToDeviceFull(string plcName, string downloadMode,
        bool stopModules, bool startAfterDownload, bool confirm, string? pcInterfaceName)
    {
        SafeOnlineExecutor.DemandAuthorized("DownloadToDeviceFull");
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);
                var dp = plc.GetService<DownloadProvider>();
                if (dp == null)
                    return Err($"PLC '{plc.Name}' 不支持 DownloadProvider 服务");

                var config = dp.GetType().GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dp);
                if (config == null)
                    return Err("DownloadProvider.Configuration 为空，未配置连接");

                string mode = (downloadMode ?? "Complete").Trim();
                bool isDifferences = mode.Equals("Differences", StringComparison.OrdinalIgnoreCase);
                bool isStopFirst = mode.Equals("StopFirst", StringComparison.OrdinalIgnoreCase)
                                   || mode.Equals("stop_first", StringComparison.OrdinalIgnoreCase);

                // 若指定了 pcInterfaceName，反射尝试切换 PG/PC 接口
                var interfaceActions = new List<string>();
                if (!string.IsNullOrEmpty(pcInterfaceName))
                {
                    var ifAction = TrySelectPcInterface(config, pcInterfaceName!);
                    interfaceActions.Add(ifAction);
                }

                // 组合 DownloadOptions 枚举（反射以避免版本差异）
                // DownloadOptions.None=0, Hardware=1, Software=2, SoftwareOnlyChanges=4
                long optionsValue = 1 | 2;  // 默认完整下载（硬件+软件）
                if (isDifferences) optionsValue = 1 | 4;  // 硬件 + 软件差异

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

                // 若为 StopFirst 或 stopModules=true：优先通过 OnlineProvider.Configuration 停止 CPU（与 CpuStop 对齐）
                var stopActions = new List<string>();
                if (isStopFirst || stopModules)
                {
                    var (onlineProvider, onlineTypeName) = ResolveOnlineProvider(plc);
                    var stopConfig = onlineProvider != null
                        ? onlineProvider.GetType().GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(onlineProvider)
                        : null;
                    // OnlineProvider 不可用时回退到 DownloadProvider.Configuration
                    stopConfig ??= config;
                    var stopAction = TryInvokeConfigurationMethod(stopConfig, new[] { "Stop", "StopPlc", "StopCpu" });
                    stopActions.Add(stopAction);
                }

                // 反射调用 Download(IConfiguration, Delegate, Delegate, DownloadOptions) — 4 参数重载
                var downloadMethod = typeof(DownloadProvider).GetMethods()
                    .FirstOrDefault(m => m.Name == "Download"
                        && m.GetParameters().Length == 4
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
                        stopActions,
                        interfaceActions
                    }, Formatting.Indented);
                }

                if (result == null)
                    return Err("下载返回 null 结果");

                int errCount = 0;
                int warnCount = 0;
                string state = "";
                try { errCount = (int)(result.GetType().GetProperty("ErrorCount")?.GetValue(result) ?? 0); } catch { }
                try { warnCount = (int)(result.GetType().GetProperty("WarningCount")?.GetValue(result) ?? 0); } catch { }
                try { state = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? ""; } catch { }

                // 下载成功后若 startAfterDownload=true，反射调用 Configuration.Start/StartPlc
                var startActions = new List<string>();
                if (startAfterDownload && errCount == 0)
                {
                    var startAction = TryInvokeConfigurationMethod(config, new[] { "Start", "StartPlc", "StartCpu" });
                    startActions.Add(startAction);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = errCount == 0,
                    plcName = plc.Name,
                    downloadMode = mode,
                    downloadOptions = downloadOptions.ToString(),
                    stopActions,
                    startActions,
                    interfaceActions,
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

    /// <summary>
    /// 完整上传站点。
    /// modeName: Consistent/All/SoftwareOnly。
    /// 通过反射获取 UploadProvider 并构建 UploadConfiguration。
    /// 返回 success/uploadedItems。
    /// </summary>
    public string UploadStation(string plcName, string modeName,
        string? pcInterfaceName, int addressIndex,
        string? readPassword, string? writePassword, bool confirm)
    {
        SafeOnlineExecutor.DemandAuthorized("UploadStation");
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);

                // 反射获取 UploadProvider（StationUploadProvider 优先）
                string[] candidateTypeNames =
                {
                    "Siemens.Engineering.Upload.StationUploadProvider",
                    "Siemens.Engineering.Upload.UploadProvider"
                };

                object? uploadProvider = null;
                string? providerTypeName = null;
                foreach (var tn in candidateTypeNames)
                {
                    try
                    {
                        var candidateType = typeof(TiaPortal).Assembly.GetType(tn);
                        if (candidateType == null) continue;

                        var getServiceMethod = plc.GetType().GetMethods(
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod)
                            ?? typeof(IEngineeringServiceProvider)
                                .GetMethods()
                                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);
                        if (getServiceMethod == null) continue;

                        var generic = getServiceMethod.MakeGenericMethod(candidateType);
                        uploadProvider = generic.Invoke(plc, null);
                        providerTypeName = tn;
                        if (uploadProvider != null) break;
                    }
                    catch { }
                }

                if (uploadProvider == null)
                    return Err($"PLC '{plc.Name}' 未提供上传服务（StationUploadProvider/UploadProvider 均不可用）");

                var providerType = uploadProvider.GetType();

                // 取 Configuration（UploadProvider 通常也有 Configuration 属性）
                var config = providerType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(uploadProvider);
                if (config == null)
                    return Err("UploadProvider.Configuration 为空，未配置连接");

                // 若指定了 pcInterfaceName，反射尝试切换 PG/PC 接口
                var interfaceActions = new List<string>();
                if (!string.IsNullOrEmpty(pcInterfaceName))
                {
                    var ifAction = TrySelectPcInterface(config, pcInterfaceName!);
                    interfaceActions.Add(ifAction);
                }

                // 取 ConfigurationAddress（按 addressIndex 选择）
                object? address = TryGetConfigurationAddress(config, addressIndex);
                if (address == null)
                    return Err($"未找到上传目标地址（addressIndex={addressIndex}）。请检查 PLC 在线连接配置。");

                // 设置密码（如有）
                var passwordActions = new List<string>();
                if (!string.IsNullOrEmpty(readPassword) || !string.IsNullOrEmpty(writePassword))
                {
                    var pwdAction = TrySetUploadPassword(address, readPassword, writePassword);
                    passwordActions.Add(pwdAction);
                }

                // 反射查找 Upload 方法
                var uploadMethods = providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName
                                && m.Name.IndexOf("Upload", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (uploadMethods.Count == 0)
                    return Err($"UploadProvider {providerType.Name} 未暴露 Upload 方法。可用方法: {string.Join("; ", ExploreProviderMethods(providerType))}");

                // 依次尝试调用 Upload 方法
                Exception? lastErr = null;
                var triedMethods = new List<string>();
                foreach (var m in uploadMethods)
                {
                    triedMethods.Add(m.Name);
                    try
                    {
                        var ps = m.GetParameters();
                        var args = new object?[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                        {
                            var pType = ps[i].ParameterType;
                            if (typeof(Delegate).IsAssignableFrom(pType))
                                args[i] = null;
                            else if (pType.Name == "ConfigurationAddress")
                                args[i] = address;
                            else if (pType == typeof(string) && ps[i].Name?.IndexOf("mode", StringComparison.OrdinalIgnoreCase) >= 0)
                                args[i] = modeName;
                            else if (pType.IsEnum && ps[i].Name?.IndexOf("mode", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try { args[i] = Enum.Parse(pType, modeName, true); }
                                catch { args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : Enum.ToObject(pType, 0); }
                            }
                            else if (ps[i].HasDefaultValue)
                                args[i] = ps[i].DefaultValue;
                            else
                                args[i] = null;
                        }

                        var result = m.Invoke(uploadProvider, args);
                        var uploadedItems = SerializeUploadStationResult(result);

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            plcName = plc.Name,
                            modeName,
                            addressIndex,
                            providerType = providerType.Name,
                            providerTypeName,
                            invokedMethod = m.Name,
                            uploadedItems,
                            interfaceActions,
                            passwordActions
                        }, Formatting.Indented);
                    }
                    catch (Exception ex)
                    {
                        lastErr = ex.InnerException ?? ex;
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = $"所有 Upload 方法调用失败: {lastErr?.Message}",
                    plcName = plc.Name,
                    providerType = providerType.Name,
                    triedMethods,
                    interfaceActions,
                    passwordActions
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 上传前检查。通过反射获取 UploadProvider 的 Check 方法。
    /// 返回 success/checkResult。
    /// </summary>
    public string UploadCheck(string plcName)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);

                // 反射获取 UploadProvider
                string[] candidateTypeNames =
                {
                    "Siemens.Engineering.Upload.StationUploadProvider",
                    "Siemens.Engineering.Upload.UploadProvider"
                };

                object? uploadProvider = null;
                string? providerTypeName = null;
                foreach (var tn in candidateTypeNames)
                {
                    try
                    {
                        var candidateType = typeof(TiaPortal).Assembly.GetType(tn);
                        if (candidateType == null) continue;

                        var getServiceMethod = plc.GetType().GetMethods(
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod)
                            ?? typeof(IEngineeringServiceProvider)
                                .GetMethods()
                                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);
                        if (getServiceMethod == null) continue;

                        var generic = getServiceMethod.MakeGenericMethod(candidateType);
                        uploadProvider = generic.Invoke(plc, null);
                        providerTypeName = tn;
                        if (uploadProvider != null) break;
                    }
                    catch { }
                }

                if (uploadProvider == null)
                    return Err($"PLC '{plc.Name}' 未提供上传服务");

                var providerType = uploadProvider.GetType();

                // 反射查找 Check 方法
                var checkMethod = providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.Equals("Check", StringComparison.OrdinalIgnoreCase)
                                         && !m.IsSpecialName);

                if (checkMethod == null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "UploadProvider 未暴露 Check 方法",
                        plcName = plc.Name,
                        providerType = providerType.Name,
                        apiExplored = ExploreProviderMethods(providerType)
                    }, Formatting.Indented);

                // 取 Configuration
                var config = providerType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(uploadProvider);
                if (config == null)
                    return Err("UploadProvider.Configuration 为空");

                object? result = null;
                var ps = checkMethod.GetParameters();
                try
                {
                    var args = new object?[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pType = ps[i].ParameterType;
                        if (pType.Name == "IConfiguration")
                            args[i] = config;
                        else if (pType.Name == "ConfigurationAddress")
                            args[i] = TryGetConfigurationAddress(config, 0);
                        else if (typeof(Delegate).IsAssignableFrom(pType))
                            args[i] = null;
                        else if (ps[i].HasDefaultValue)
                            args[i] = ps[i].DefaultValue;
                        else
                            args[i] = null;
                    }
                    result = checkMethod.Invoke(uploadProvider, args);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Check 调用失败: {ex.InnerException?.Message ?? ex.Message}",
                        plcName = plc.Name,
                        invokedMethod = checkMethod.Name
                    }, Formatting.Indented);
                }

                var (checkResult, _) = DescribeCheckResult(result);
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    plcName = plc.Name,
                    checkResult,
                    invokedMethod = checkMethod.Name,
                    rawType = result?.GetType().Name ?? ""
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 停止 CPU。通过 OnlineProvider 反射调用 Configuration.Stop。
    /// 返回 success/plcName。
    /// </summary>
    public string CpuStop(string plcName)
    {
        SafeOnlineExecutor.DemandAuthorized("CpuStop");
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);

                // 优先尝试 OnlineProvider，回退 DownloadProvider
                var (provider, providerTypeName) = ResolveOnlineProvider(plc);
                if (provider == null)
                    return Err($"PLC '{plc.Name}' 未提供 OnlineProvider 或 DownloadProvider 服务");

                var providerType = provider.GetType();
                var config = providerType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(provider);
                if (config == null)
                    return Err($"{providerTypeName}.Configuration 为空");

                // 反射调用 Configuration.Stop / StopPlc / StopCpu
                var stopMethod = config.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => (m.Name.Equals("Stop", StringComparison.OrdinalIgnoreCase)
                                          || m.Name.Equals("StopPlc", StringComparison.OrdinalIgnoreCase)
                                          || m.Name.Equals("StopCpu", StringComparison.OrdinalIgnoreCase))
                                         && !m.IsSpecialName);

                if (stopMethod == null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"{providerTypeName}.Configuration 未暴露 Stop/StopPlc/StopCpu 方法",
                        plcName = plc.Name,
                        providerType = providerTypeName,
                        apiExplored = ExploreProviderMethods(config.GetType())
                    }, Formatting.Indented);

                try
                {
                    var ps = stopMethod.GetParameters();
                    var args = ps.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                    stopMethod.Invoke(config, args);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        providerType = providerTypeName,
                        action = "stop",
                        invokedMethod = stopMethod.Name
                    }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Stop 调用失败: {ex.InnerException?.Message ?? ex.Message}",
                        plcName = plc.Name,
                        invokedMethod = stopMethod.Name
                    }, Formatting.Indented);
                }
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 启动 CPU。通过 OnlineProvider 反射调用 Configuration.Start。
    /// 返回 success/plcName。
    /// </summary>
    public string CpuStart(string plcName)
    {
        SafeOnlineExecutor.DemandAuthorized("CpuStart");
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);

                var (provider, providerTypeName) = ResolveOnlineProvider(plc);
                if (provider == null)
                    return Err($"PLC '{plc.Name}' 未提供 OnlineProvider 或 DownloadProvider 服务");

                var providerType = provider.GetType();
                var config = providerType.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(provider);
                if (config == null)
                    return Err($"{providerTypeName}.Configuration 为空");

                // 反射调用 Configuration.Start / StartPlc / StartCpu
                var startMethod = config.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => (m.Name.Equals("Start", StringComparison.OrdinalIgnoreCase)
                                          || m.Name.Equals("StartPlc", StringComparison.OrdinalIgnoreCase)
                                          || m.Name.Equals("StartCpu", StringComparison.OrdinalIgnoreCase))
                                         && !m.IsSpecialName);

                if (startMethod == null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"{providerTypeName}.Configuration 未暴露 Start/StartPlc/StartCpu 方法",
                        plcName = plc.Name,
                        providerType = providerTypeName,
                        apiExplored = ExploreProviderMethods(config.GetType())
                    }, Formatting.Indented);

                try
                {
                    var ps = startMethod.GetParameters();
                    var args = ps.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                    startMethod.Invoke(config, args);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        providerType = providerTypeName,
                        action = "start",
                        invokedMethod = startMethod.Name
                    }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"Start 调用失败: {ex.InnerException?.Message ?? ex.Message}",
                        plcName = plc.Name,
                        invokedMethod = startMethod.Name
                    }, Formatting.Indented);
                }
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 扫描网络设备。通过反射获取 PG/PC 接口的扫描功能。
    /// 返回 foundDevices 列表（IP/DeviceName/DeviceType）。
    /// </summary>
    public string ScanDevices(string pcInterfaceName, string? plcName = null)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);
                var dp = plc.GetService<DownloadProvider>();
                if (dp == null)
                    return Err("PLC 不支持下载服务");

                var config = dp.GetType().GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dp);
                if (config == null)
                    return Err("DownloadProvider.Configuration 为空，未配置连接");

                // 反射取 Modes → PcInterfaces
                var modesProp = config.GetType().GetProperty("Modes", BindingFlags.Public | BindingFlags.Instance);
                var modes = modesProp?.GetValue(config) as IEnumerable;
                if (modes == null)
                    return Err("Configuration.Modes 为空");

                object? firstMode = null;
                foreach (var m in modes) { firstMode = m; break; }
                if (firstMode == null)
                    return Err("未找到连接模式（Configuration.Modes 为空）");

                // PcInterfaces
                var pcIfProp = firstMode.GetType().GetProperty("PcInterfaces", BindingFlags.Public | BindingFlags.Instance);
                var pcIfs = pcIfProp?.GetValue(firstMode) as IEnumerable;
                if (pcIfs == null)
                    return Err("Configuration.Modes[0].PcInterfaces 为空");

                // 若指定了 pcInterfaceName，按名称筛选；否则取第一个
                object? selectedIf = null;
                foreach (var iface in pcIfs)
                {
                    if (string.IsNullOrEmpty(pcInterfaceName))
                    {
                        selectedIf = iface;
                        break;
                    }
                    var nameProp = iface.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    var ifName = nameProp?.GetValue(iface)?.ToString() ?? "";
                    if (ifName.IndexOf(pcInterfaceName!, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        selectedIf = iface;
                        break;
                    }
                }
                if (selectedIf == null)
                    return Err($"未找到 PG/PC 接口: {pcInterfaceName}");

                // 反射调用 GetAccessibleDevices()
                var getDevicesMethod = selectedIf.GetType().GetMethod("GetAccessibleDevices", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getDevicesMethod == null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "PG/PC 接口未暴露 GetAccessibleDevices 方法",
                        apiExplored = ExploreProviderMethods(selectedIf.GetType())
                    }, Formatting.Indented);

                object? devicesObj;
                try
                {
                    devicesObj = getDevicesMethod.Invoke(selectedIf, null);
                }
                catch (Exception ex)
                {
                    return Err($"扫描失败: {ex.InnerException?.Message ?? ex.Message}");
                }

                var foundDevices = new List<object>();
                if (devicesObj is IEnumerable devEnum)
                {
                    foreach (var d in devEnum)
                    {
                        var t = d.GetType();
                        string ip = SafeGetProp(d, "Address") ?? SafeGetProp(d, "IPAddress") ?? SafeGetProp(d, "Ip") ?? "";
                        string deviceName = SafeGetProp(d, "Name") ?? "";
                        string deviceType = SafeGetProp(d, "DeviceSeries") ?? SafeGetProp(d, "DeviceType") ?? SafeGetProp(d, "TypeName") ?? "";
                        string macAddress = SafeGetProp(d, "MACAddress") ?? "";
                        foundDevices.Add(new
                        {
                            ip,
                            deviceName,
                            deviceType,
                            macAddress
                        });
                    }
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = foundDevices.Count,
                    foundDevices,
                    pcInterface = SafeGetProp(selectedIf, "Name") ?? ""
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 配置在线连接。设置 PG/PC 接口和目标设备 IP。
    /// 返回 success/plcName/pcInterface/deviceIp。
    /// </summary>
    public string ConfigureConnection(string plcName, string pcInterfaceName, string deviceIp)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();
                var plc = ResolvePlcForDownload(plcName);
                var dp = plc.GetService<DownloadProvider>();
                if (dp == null)
                    return Err($"PLC '{plc.Name}' 不支持 DownloadProvider 服务");

                var config = dp.GetType().GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dp);
                if (config == null)
                    return Err("DownloadProvider.Configuration 为空，未配置连接");

                var actions = new List<string>();

                // 切换 PG/PC 接口
                if (!string.IsNullOrEmpty(pcInterfaceName))
                {
                    var ifAction = TrySelectPcInterface(config, pcInterfaceName);
                    actions.Add(ifAction);
                }

                // 设置目标设备 IP：尝试在 ConfigurationAddress 上设置 Address/IpAddress 属性
                if (!string.IsNullOrEmpty(deviceIp))
                {
                    var addrAction = TrySetDeviceIp(config, deviceIp);
                    actions.Add(addrAction);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    plcName = plc.Name,
                    pcInterface = pcInterfaceName ?? "",
                    deviceIp = deviceIp ?? "",
                    actions
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    /// <summary>
    /// 项目另存为。通过 Project.SaveAs() 方法。
    /// projectPath 可以是目录或对应版本的 .apXX 文件路径。
    /// 返回 success/newPath。
    /// </summary>
    public string SaveAsProject(string projectPath)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();

                // 解析路径：若是目录，使用 SaveAs(DirectoryInfo, string name) 重载；
                // 若是 .apXX/.zapXX 文件，使用 SaveAs(FileInfo) 重载。
                var ext = Path.GetExtension(projectPath);
                string newPath;
                if (EnvironmentDiscoveryService.IsTiaProjectFile(projectPath))
                {
                    var fi = new FileInfo(projectPath);
                    var parentDir = fi.DirectoryName;
                    if (string.IsNullOrEmpty(parentDir))
                        return Err($"无效路径: {projectPath}");
                    Directory.CreateDirectory(parentDir);

                    // 反射调用 SaveAs(FileInfo)
                    var saveAsMethod = _project!.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "SaveAs"
                                             && m.GetParameters().Length == 1
                                             && m.GetParameters()[0].ParameterType == typeof(FileInfo));
                    if (saveAsMethod == null)
                        return Err("Project 未暴露 SaveAs(FileInfo) 方法");

                    saveAsMethod.Invoke(_project, new object[] { fi });
                    newPath = fi.FullName;
                }
                else
                {
                    // 视为目录，使用 SaveAs(DirectoryInfo, string name) 重载
                    var di = new DirectoryInfo(projectPath);
                    string projectName = di.Name;
                    Directory.CreateDirectory(projectPath);

                    var saveAsMethod = _project!.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "SaveAs"
                                             && m.GetParameters().Length == 2
                                             && m.GetParameters()[0].ParameterType == typeof(DirectoryInfo)
                                             && m.GetParameters()[1].ParameterType == typeof(string));
                    if (saveAsMethod == null)
                        return Err("Project 未暴露 SaveAs(DirectoryInfo, string) 方法");

                    saveAsMethod.Invoke(_project, new object[] { di, projectName });
                    newPath = di.FullName;
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    newPath,
                    message = $"项目已另存为: {newPath}"
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // 辅助方法
    // ResolveOnlineProvider / ExploreProviderMethods / ResolvePlc 复用
    // DiagnosticsService.cs 中已定义的同名方法（partial class 共享）。
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>解析 PLC：null/空时返回默认 PLC，未找到时抛异常（与 DiagnosticsService.ResolvePlc 同义）。</summary>
    private PlcSoftware ResolvePlcForDownload(string? plcName)
    {
        if (string.IsNullOrEmpty(plcName)) return GetPlcSoftware();
        return FindPlcByName(plcName) ?? throw new InvalidOperationException($"未找到 PLC: {plcName}");
    }

    /// <summary>反射尝试在 Configuration 上切换 PG/PC 接口。</summary>
    private static string TrySelectPcInterface(object config, string pcInterfaceName)
    {
        try
        {
            // config.Modes → Mode.PcInterfaces → SetInterface / Select
            var modesProp = config.GetType().GetProperty("Modes", BindingFlags.Public | BindingFlags.Instance);
            var modes = modesProp?.GetValue(config) as IEnumerable;
            if (modes == null) return $"切换 PG/PC 接口失败: Configuration.Modes 为空";

            object? firstMode = null;
            foreach (var m in modes) { firstMode = m; break; }
            if (firstMode == null) return $"切换 PG/PC 接口失败: Modes 为空";

            var pcIfProp = firstMode.GetType().GetProperty("PcInterfaces", BindingFlags.Public | BindingFlags.Instance);
            var pcIfs = pcIfProp?.GetValue(firstMode) as IEnumerable;
            if (pcIfs == null) return $"切换 PG/PC 接口失败: PcInterfaces 为空";

            // 反射查找 Select / SetInterface / UseInterface 方法
            foreach (var iface in pcIfs)
            {
                var nameProp = iface.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                var ifName = nameProp?.GetValue(iface)?.ToString() ?? "";
                if (ifName.IndexOf(pcInterfaceName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // 尝试调用 Select 方法
                    var selectMethod = firstMode.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.Equals("Select", StringComparison.OrdinalIgnoreCase)
                                             || m.Name.Equals("SetInterface", StringComparison.OrdinalIgnoreCase)
                                             || m.Name.Equals("UseInterface", StringComparison.OrdinalIgnoreCase));
                    if (selectMethod != null)
                    {
                        var ps = selectMethod.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(iface))
                        {
                            selectMethod.Invoke(firstMode, new object[] { iface });
                            return $"已切换 PG/PC 接口: {ifName}（{selectMethod.Name}）";
                        }
                    }
                    return $"已定位 PG/PC 接口: {ifName}（未找到 Select 方法，仅返回信息）";
                }
            }
            return $"未找到匹配的 PG/PC 接口: {pcInterfaceName}";
        }
        catch (Exception ex)
        {
            return $"切换 PG/PC 接口异常: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>反射尝试在 Configuration 上设置目标设备 IP。</summary>
    private static string TrySetDeviceIp(object config, string deviceIp)
    {
        try
        {
            // 取 Modes[0].Addresses[0]
            var address = TryGetConfigurationAddress(config, 0);
            if (address == null) return $"设置设备 IP 失败: 未找到 ConfigurationAddress";

            var addrType = address.GetType();

            // 尝试设置 IpAddress / Address / IP 属性
            string[] propCandidates = { "IpAddress", "IPAddress", "Address", "IP", "Ip" };
            foreach (var propName in propCandidates)
            {
                var prop = addrType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite && (prop.PropertyType == typeof(string) || prop.PropertyType == typeof(object)))
                {
                    prop.SetValue(address, deviceIp);
                    return $"已通过 {propName} 设置设备 IP: {deviceIp}";
                }
            }

            // 尝试调用 SetAddress(string) / SetIp(string) 方法
            string[] methodCandidates = { "SetAddress", "SetIp", "SetIPAddress", "Configure" };
            foreach (var methodName in methodCandidates)
            {
                var method = addrType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (method != null)
                {
                    method.Invoke(address, new object[] { deviceIp });
                    return $"已通过 {methodName}() 设置设备 IP: {deviceIp}";
                }
            }

            return $"设置设备 IP 失败: ConfigurationAddress 未暴露可写 IP 属性或 SetIp 方法。可用成员: {string.Join(", ", ExploreProviderMethods(addrType))}";
        }
        catch (Exception ex)
        {
            return $"设置设备 IP 异常: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>反射获取 ConfigurationAddress（按 index 选择）。</summary>
    private static object? TryGetConfigurationAddress(object config, int addressIndex)
    {
        try
        {
            var modesProp = config.GetType().GetProperty("Modes", BindingFlags.Public | BindingFlags.Instance);
            var modes = modesProp?.GetValue(config) as IEnumerable;
            if (modes == null) return null;

            object? firstMode = null;
            foreach (var m in modes) { firstMode = m; break; }
            if (firstMode == null) return null;

            // Mode.Addresses
            var addrProp = firstMode.GetType().GetProperty("Addresses", BindingFlags.Public | BindingFlags.Instance);
            var addresses = addrProp?.GetValue(firstMode) as IEnumerable;
            if (addresses == null) return null;

            int idx = 0;
            foreach (var a in addresses)
            {
                if (idx == addressIndex) return a;
                idx++;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>反射尝试在 ConfigurationAddress 上设置读/写密码。</summary>
    private static string TrySetUploadPassword(object address, string? readPassword, string? writePassword)
    {
        try
        {
            var addrType = address.GetType();
            var actions = new List<string>();

            if (!string.IsNullOrEmpty(readPassword))
            {
                var prop = addrType.GetProperty("ReadPassword", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    var pwd = ToSecureString(readPassword!);
                    prop.SetValue(address, pwd);
                    actions.Add("已设置 ReadPassword");
                }
                else
                {
                    var method = addrType.GetMethod("SetReadPassword", BindingFlags.Public | BindingFlags.Instance);
                    if (method != null)
                    {
                        method.Invoke(address, new object[] { ToSecureString(readPassword!) });
                        actions.Add("已调用 SetReadPassword()");
                    }
                }
            }

            if (!string.IsNullOrEmpty(writePassword))
            {
                var prop = addrType.GetProperty("WritePassword", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    var pwd = ToSecureString(writePassword!);
                    prop.SetValue(address, pwd);
                    actions.Add("已设置 WritePassword");
                }
                else
                {
                    var method = addrType.GetMethod("SetWritePassword", BindingFlags.Public | BindingFlags.Instance);
                    if (method != null)
                    {
                        method.Invoke(address, new object[] { ToSecureString(writePassword!) });
                        actions.Add("已调用 SetWritePassword()");
                    }
                }
            }

            return actions.Count > 0 ? string.Join("; ", actions) : "未找到可用的密码设置属性/方法";
        }
        catch (Exception ex)
        {
            return $"设置密码异常: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>反射调用 Configuration 上的指定方法（按候选名列表）。</summary>
    private static string TryInvokeConfigurationMethod(object config, string[] candidateNames)
    {
        try
        {
            var method = config.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => !m.IsSpecialName
                                     && candidateNames.Any(n => n.Equals(m.Name, StringComparison.OrdinalIgnoreCase)));
            if (method == null)
                return $"未找到 {string.Join("/", candidateNames)} 方法，跳过";

            var ps = method.GetParameters();
            var args = ps.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            method.Invoke(config, args);
            return $"已调用 {method.Name}()";
        }
        catch (Exception ex)
        {
            return $"调用失败: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>序列化 Check 结果（反射读取 State/ChangesCount 等属性）。</summary>
    private static (string checkResult, int changesCount) DescribeCheckResult(object? result)
    {
        if (result == null) return ("null", 0);
        try
        {
            var t = result.GetType();
            var state = t.GetProperty("State")?.GetValue(result)?.ToString() ?? "";
            var changesCount = 0;
            var changesProp = t.GetProperty("ChangesCount") ?? t.GetProperty("ChangeCount") ?? t.GetProperty("Count");
            if (changesProp != null)
            {
                var v = changesProp.GetValue(result);
                if (v != null) int.TryParse(v.ToString(), out changesCount);
            }

            // 拼接 State + ChangesCount 作为 checkResult 字符串
            var checkResult = string.IsNullOrEmpty(state) ? "Unknown" : state;
            return (checkResult, changesCount);
        }
        catch
        {
            return (result.GetType().Name, 0);
        }
    }

    /// <summary>序列化上传站点结果。</summary>
    private static List<object> SerializeUploadStationResult(object? result)
    {
        var list = new List<object>();
        if (result == null) return list;
        try
        {
            var t = result.GetType();
            var state = t.GetProperty("State")?.GetValue(result)?.ToString();
            var errCount = t.GetProperty("ErrorCount")?.GetValue(result);
            var warnCount = t.GetProperty("WarningCount")?.GetValue(result);
            list.Add(new
            {
                state = state ?? "",
                errorCount = errCount?.ToString() ?? "0",
                warningCount = warnCount?.ToString() ?? "0",
                rawType = t.Name
            });
        }
        catch { }
        return list;
    }

    /// <summary>反射读取对象属性值（容错）。</summary>
    private static string? SafeGetProp(object obj, string propName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(obj)?.ToString();
        }
        catch { return null; }
    }

    // ToSecureString 已由 AdvancedFeaturesService.cs 定义（partial class 共享）
}
