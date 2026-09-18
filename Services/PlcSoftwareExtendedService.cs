using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcpServer
{
    /// <summary>
    /// PLC 软件扩展模块（SW.Alarm 完整 / SW.Blocks.Interface / SW.ExternalSources 完整 /
    /// SW.OpcUa 完整 / SW.Supervision 完整）。
    /// 全部采用反射探测入口点，应对不同 TIA 版本下 Siemens.Engineering.SW.* 命名空间 API 可访问性差异。
    /// 复用项目内已有辅助方法（ResolveFeatureType / TryGetService / InvokeGetService /
    /// GetProperty / SetProperty / EnsureDir / TryGetCollectionProperty / InvokeExport 等），
    /// 不重复定义 ToSecureString / ResolvePlc / FindEngineeringObjectByPath。
    /// 所有公共方法返回 JSON 字符串（与 UmacService 风格一致）。
    /// </summary>
    public partial class PortalService
    {
        // ═════════════════════════════════════════════════════════════════════════════
        // 通用辅助方法（模块专用）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 反射探测 PLC 的 PlcAlarmTextProvider 入口（多入口点尝试）。
        /// 依次尝试：
        ///   1) plc.PlcAlarmTextProvider / AlarmTextProvider / PlcAlarmTextlistGroup 等属性
        ///   2) plc.GetService&lt;PlcAlarmTextProvider&gt;() / GetService&lt;PlcAlarmTextListProvider&gt;()
        /// </summary>
        private object? GetPlcAlarmTextProviderFull(PlcSoftware plc, out string entryPoint, out List<string> triedPaths)
        {
            entryPoint = "";
            triedPaths = new List<string>();

            // 1) 属性探测
            var propCandidates = new[]
            {
                "PlcAlarmTextProvider", "AlarmTextProvider",
                "PlcAlarmTextlistGroup", "AlarmTextlistGroup"
            };
            foreach (var pn in propCandidates)
            {
                try
                {
                    triedPaths.Add($"plc.{pn}");
                    var prop = typeof(PlcSoftware).GetProperty(pn, BindingFlags.Public | BindingFlags.Instance);
                    if (prop?.GetValue(plc) is { } value)
                    {
                        entryPoint = $"plc.{pn}";
                        return value;
                    }
                }
                catch (Exception ex) { triedPaths[triedPaths.Count - 1] += " => 异常: " + ex.Message; }
            }

            // 2) GetService<T>() 探测
            var typeCandidates = new[]
            {
                "Siemens.Engineering.SW.Alarm.PlcAlarmTextProvider",
                "Siemens.Engineering.SW.Alarm.PlcAlarmTextListProvider"
            };
            foreach (var tn in typeCandidates)
            {
                try
                {
                    var shortName = tn.Split('.').Last();
                    triedPaths.Add($"plc.GetService<{shortName}>()");
                    var svcType = ResolveFeatureType(tn);
                    if (svcType != null)
                    {
                        var svc = TryGetService(plc, svcType);
                        if (svc != null)
                        {
                            entryPoint = $"plc.GetService<{svcType.Name}>()";
                            return svc;
                        }
                    }
                }
                catch (Exception ex) { triedPaths[triedPaths.Count - 1] += " => 异常: " + ex.Message; }
            }

            return null;
        }

        /// <summary>在集合上反射查找 Find(string) 方法并调用；失败时遍历集合按 Name 匹配。</summary>
        private static object? FindInComposition(object? composition, string name)
        {
            if (composition == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var findMethod = composition.GetType().GetMethod("Find", new[] { typeof(string) });
                if (findMethod != null)
                    return findMethod.Invoke(composition, new object[] { name });
            }
            catch { }
            try
            {
                foreach (var item in (IEnumerable)composition)
                {
                    var n = SafeReflectGet(item, "Name");
                    if (n != null && n.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return item;
                }
            }
            catch { }
            return null;
        }

        /// <summary>反射调用 composition.Delete(item) / item.Delete() / composition.Remove(item)。</summary>
        private static (bool ok, List<string> attempts) DeleteFromComposition(object composition, object item)
        {
            var attempts = new List<string>();
            var r1 = UmacTryInvoke(composition,
                ("Delete", new object[] { item }),
                ("Remove", new object[] { item }));
            attempts.AddRange(r1.attempts);
            if (r1.ok) return (true, attempts);

            var r2 = UmacTryInvoke(item,
                ("Delete", new object[] { }),
                ("Remove", new object[] { }));
            attempts.AddRange(r2.attempts);
            if (r2.ok) return (true, attempts);
            return (false, attempts);
        }

        /// <summary>反射调用 Import(FileInfo, ImportOptions) / Import(FileInfo)，返回是否成功。</summary>
        private static (bool ok, string error, List<string> attempts) InvokeImport(object obj, string filePath)
        {
            var attempts = new List<string>();
            try
            {
                EnsureDir(filePath);
                var fi = new FileInfo(filePath);
                var objType = obj.GetType();

                // Import(FileInfo, ImportOptions)
                var importMethod = objType.GetMethod("Import", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(FileInfo), typeof(ImportOptions) }, null);
                if (importMethod != null)
                {
                    attempts.Add("Import(FileInfo, ImportOptions)");
                    importMethod.Invoke(obj, new object[] { fi, ImportOptions.Override });
                    return (true, "", attempts);
                }

                // Import(FileInfo)
                importMethod = objType.GetMethod("Import", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(FileInfo) }, null);
                if (importMethod != null)
                {
                    attempts.Add("Import(FileInfo)");
                    importMethod.Invoke(obj, new object[] { fi });
                    return (true, "", attempts);
                }

                return (false, $"类型 '{objType.Name}' 未暴露 Import(FileInfo[, ImportOptions]) 方法", attempts);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                return (false, inner.Message, attempts);
            }
        }

        /// <summary>枚举对象所有公开属性名（用于 API 探索诊断）。</summary>
        private static List<string> ListPublicPropertyNames(object? obj)
        {
            var names = new List<string>();
            if (obj == null) return names;
            try
            {
                foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    names.Add(p.Name);
            }
            catch { }
            return names;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 一、SW.Alarm 完整 API（Siemens.Engineering.SW.Alarm）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>1.1 列出 PLC 报警文本表（完整版，使用 PlcAlarmTextProvider 官方 API）。</summary>
        public string ListPlcAlarmTextlistsFull(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var triedPaths);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口（属性/GetService 均失败）",
                            apiExplored = true,
                            triedPaths
                        });

                    var textlists = new List<object>();
                    var coll = TryGetCollectionProperty(provider, "AlarmTextLists")
                               ?? TryGetCollectionProperty(provider, "Textlists");
                    if (coll != null)
                    {
                        foreach (var list in coll)
                        {
                            textlists.Add(new
                            {
                                name = SafeReflectGet(list, "Name") ?? "?",
                                typeName = list.GetType().Name,
                                entryCount = TryCountCollectionProperty(list, "TextListEntries")
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        entryPoint,
                        count = textlists.Count,
                        textlists
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.2 创建用户报警文本表（反射调用 CreateUserAlarmTextList(name)）。</summary>
        public string CreatePlcAlarmUserTextlist(string plcName, string textlistName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(textlistName)) return Err("textlistName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var triedPaths);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true,
                            triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "AlarmTextLists")
                               ?? TryGetCollectionProperty(provider, "Textlists");
                    if (coll == null)
                        return Err("PlcAlarmTextProvider 未暴露 AlarmTextLists/Textlists 集合属性");

                    var collType = coll.GetType();
                    var createMethod = collType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.Equals("CreateUserAlarmTextList", StringComparison.OrdinalIgnoreCase)
                                          && m.GetParameters().Length == 1
                                          && m.GetParameters()[0].ParameterType == typeof(string));
                    if (createMethod == null)
                    {
                        var apiExplored = collType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName && m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                            .ToList();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "AlarmTextLists 集合未暴露 CreateUserAlarmTextList(string) 方法",
                            apiExplored
                        }, Formatting.Indented);
                    }

                    object? created;
                    try { created = createMethod.Invoke(coll, new object[] { textlistName }); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                        return Err($"CreateUserAlarmTextList 调用失败: {inner.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建用户报警文本表: {textlistName}",
                        plcName = plc.Name,
                        textlistName = SafeReflectGet(created, "Name") ?? textlistName,
                        typeName = created?.GetType().Name ?? "",
                        entryPoint
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.3 创建系统报警文本表（反射调用 CreateSystemAlarmTextList(name, type)）。</summary>
        public string CreatePlcAlarmSystemTextlist(string plcName, string textlistName, string? systemType = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(textlistName)) return Err("textlistName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var triedPaths);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true,
                            triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "AlarmTextLists")
                               ?? TryGetCollectionProperty(provider, "Textlists");
                    if (coll == null)
                        return Err("PlcAlarmTextProvider 未暴露 AlarmTextLists/Textlists 集合属性");

                    var collType = coll.GetType();

                    // 优先尝试 CreateSystemAlarmTextList(string, SystemAlarmTextListType)
                    if (!string.IsNullOrEmpty(systemType))
                    {
                        var (enumVal, enumType, enumErr, enumValues) = UmacParseEnum("SystemAlarmTextListType", systemType);
                        if (enumVal == null)
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = enumErr,
                                enumType = enumType?.FullName,
                                availableSystemTypes = enumValues
                            });

                        var createMethod2 = collType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name.Equals("CreateSystemAlarmTextList", StringComparison.OrdinalIgnoreCase)
                                              && m.GetParameters().Length == 2
                                              && m.GetParameters()[0].ParameterType == typeof(string)
                                              && m.GetParameters()[1].ParameterType == enumType);
                        if (createMethod2 != null)
                        {
                            object? created;
                            try { created = createMethod2.Invoke(coll, new object[] { textlistName, enumVal }); }
                            catch (Exception ex)
                            {
                                var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                                return Err($"CreateSystemAlarmTextList 调用失败: {inner.Message}");
                            }
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = $"已创建系统报警文本表: {textlistName}",
                                plcName = plc.Name,
                                textlistName = SafeReflectGet(created, "Name") ?? textlistName,
                                systemType = enumVal.ToString(),
                                typeName = created?.GetType().Name ?? "",
                                entryPoint
                            }, Formatting.Indented);
                        }
                    }

                    // 回退：CreateSystemAlarmTextList(string) 单参数重载
                    var createMethod1 = collType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name.Equals("CreateSystemAlarmTextList", StringComparison.OrdinalIgnoreCase)
                                          && m.GetParameters().Length == 1
                                          && m.GetParameters()[0].ParameterType == typeof(string));
                    if (createMethod1 == null)
                    {
                        var apiExplored = collType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName && m.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                            .ToList();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "AlarmTextLists 集合未暴露 CreateSystemAlarmTextList(string[, type]) 方法",
                            apiExplored
                        }, Formatting.Indented);
                    }

                    object? created1;
                    try { created1 = createMethod1.Invoke(coll, new object[] { textlistName }); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                        return Err($"CreateSystemAlarmTextList 调用失败: {inner.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建系统报警文本表: {textlistName}",
                        plcName = plc.Name,
                        textlistName = SafeReflectGet(created1, "Name") ?? textlistName,
                        typeName = created1?.GetType().Name ?? "",
                        entryPoint
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.4 删除报警文本表（反射查找后调用 composition.Delete）。</summary>
        public string DeletePlcAlarmTextlist(string plcName, string textlistName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(textlistName)) return Err("textlistName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var _);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true
                        });

                    var coll = TryGetCollectionProperty(provider, "AlarmTextLists")
                               ?? TryGetCollectionProperty(provider, "Textlists");
                    if (coll == null)
                        return Err("PlcAlarmTextProvider 未暴露 AlarmTextLists/Textlists 集合属性");

                    var target = FindInComposition(coll, textlistName);
                    if (target == null) return Err($"未找到报警文本表: {textlistName}");

                    var (ok, attempts) = DeleteFromComposition(coll, target);
                    return JsonConvert.SerializeObject(new
                    {
                        success = ok,
                        message = ok ? $"已删除报警文本表: {textlistName}" : "所有删除方式均失败",
                        plcName = plc.Name,
                        textlistName,
                        entryPoint,
                        attempts
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.5 导出报警文本到 XLSX（反射调用 provider.ExportTexts/Export(FileInfo[, ExportOptions])）。</summary>
        public string ExportPlcAlarmTextsXlsx(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var _);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true
                        });

                    EnsureDir(filePath);
                    var fi = new FileInfo(filePath);
                    var providerType = provider.GetType();
                    var attempts = new List<string>();

                    // 尝试 ExportTexts(FileInfo, ExportOptions)
                    var exportTextsMethod = providerType.GetMethod("ExportTexts", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(FileInfo), typeof(ExportOptions) }, null);
                    if (exportTextsMethod != null)
                    {
                        attempts.Add("ExportTexts(FileInfo, ExportOptions)");
                        try
                        {
                            exportTextsMethod.Invoke(provider, new object[] { fi, ExportOptions.WithDefaults });
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = "报警文本已导出到 XLSX",
                                plcName = plc.Name, filePath, entryPoint, attempts
                            });
                        }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                            attempts[attempts.Count - 1] += " => 异常: " + inner.Message;
                        }
                    }

                    // 尝试 ExportTexts(FileInfo)
                    exportTextsMethod = providerType.GetMethod("ExportTexts", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(FileInfo) }, null);
                    if (exportTextsMethod != null)
                    {
                        attempts.Add("ExportTexts(FileInfo)");
                        try
                        {
                            exportTextsMethod.Invoke(provider, new object[] { fi });
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = "报警文本已导出到 XLSX",
                                plcName = plc.Name, filePath, entryPoint, attempts
                            });
                        }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                            attempts[attempts.Count - 1] += " => 异常: " + inner.Message;
                        }
                    }

                    // 回退：通用 Export(FileInfo, ExportOptions) / Export(FileInfo)
                    if (InvokeExport(provider, filePath, out var exportError))
                    {
                        attempts.Add("Export(FileInfo[, ExportOptions]) => 成功");
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = "报警文本已导出",
                            plcName = plc.Name, filePath, entryPoint, attempts
                        });
                    }
                    attempts.Add("Export(FileInfo[, ExportOptions]) => " + exportError);

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "PlcAlarmTextProvider 未暴露 ExportTexts/Export(FileInfo) 方法",
                        plcName = plc.Name, filePath, entryPoint, attempts,
                        providerProperties = ListPublicPropertyNames(provider)
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.6 从 XLSX 导入报警文本（反射调用 provider.ImportTexts/Import(FileInfo[, ImportOptions])）。</summary>
        public string ImportPlcAlarmTextsXlsx(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var _);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true
                        });

                    var providerType = provider.GetType();
                    var attempts = new List<string>();
                    var fi = new FileInfo(filePath);

                    // 尝试 ImportTexts(FileInfo, ImportOptions)
                    var importTextsMethod = providerType.GetMethod("ImportTexts", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(FileInfo), typeof(ImportOptions) }, null);
                    if (importTextsMethod != null)
                    {
                        attempts.Add("ImportTexts(FileInfo, ImportOptions)");
                        try
                        {
                            importTextsMethod.Invoke(provider, new object[] { fi, ImportOptions.Override });
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = "报警文本已从 XLSX 导入",
                                plcName = plc.Name, filePath, entryPoint, attempts
                            });
                        }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                            attempts[attempts.Count - 1] += " => 异常: " + inner.Message;
                        }
                    }

                    // 尝试 ImportTexts(FileInfo)
                    importTextsMethod = providerType.GetMethod("ImportTexts", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(FileInfo) }, null);
                    if (importTextsMethod != null)
                    {
                        attempts.Add("ImportTexts(FileInfo)");
                        try
                        {
                            importTextsMethod.Invoke(provider, new object[] { fi });
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = "报警文本已从 XLSX 导入",
                                plcName = plc.Name, filePath, entryPoint, attempts
                            });
                        }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                            attempts[attempts.Count - 1] += " => 异常: " + inner.Message;
                        }
                    }

                    // 回退：通用 Import(FileInfo, ImportOptions) / Import(FileInfo)
                    var (ok, err, importAttempts) = InvokeImport(provider, filePath);
                    attempts.AddRange(importAttempts);
                    if (ok)
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = "报警文本已从 XLSX 导入",
                            plcName = plc.Name, filePath, entryPoint, attempts
                        });

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "PlcAlarmTextProvider 未暴露 ImportTexts/Import(FileInfo) 方法。" + err,
                        plcName = plc.Name, filePath, entryPoint, attempts,
                        providerProperties = ListPublicPropertyNames(provider)
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.7 获取报警类数据（反射获取 AlarmClasses 集合并按名称查找，返回全部可读属性）。</summary>
        public string GetPlcAlarmClassData(string plcName, string? alarmClassName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var _);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true
                        });

                    var coll = TryGetCollectionProperty(provider, "AlarmClasses");
                    if (coll == null)
                        return Err("PlcAlarmTextProvider 未暴露 AlarmClasses 集合属性");

                    var classes = new List<object>();
                    var allProps = new List<string>();
                    foreach (var c in coll)
                    {
                        try
                        {
                            var name = SafeReflectGet(c, "Name") ?? "?";
                            // 指定名称时只返回匹配项
                            if (!string.IsNullOrEmpty(alarmClassName)
                                && !name.Equals(alarmClassName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var details = new Dictionary<string, string?>();
                            try
                            {
                                foreach (var p in c.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    try { details[p.Name] = p.GetValue(c)?.ToString(); } catch { }
                                    if (!allProps.Contains(p.Name)) allProps.Add(p.Name);
                                }
                            }
                            catch { }

                            classes.Add(new
                            {
                                name,
                                typeName = c.GetType().Name,
                                properties = details
                            });
                        }
                        catch { }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        entryPoint,
                        count = classes.Count,
                        alarmClasses = classes,
                        allPropertyNames = allProps
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.8 导出报警类（完整版，反射调用 AlarmClasses 集合的 Export）。</summary>
        public string ExportPlcAlarmClassesFull(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var _);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true
                        });

                    var coll = TryGetCollectionProperty(provider, "AlarmClasses");
                    if (coll == null)
                        return Err("PlcAlarmTextProvider 未暴露 AlarmClasses 集合属性");

                    if (!InvokeExport(coll, filePath, out var exportError))
                        return Err(exportError);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "报警类已导出",
                        plcName = plc.Name, filePath, entryPoint
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.9 导入报警类（完整版，反射调用 AlarmClasses 集合的 Import）。</summary>
        public string ImportPlcAlarmClassesFull(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");
                    var plc = GetPlcSoftwareFor(plcName);

                    var provider = GetPlcAlarmTextProviderFull(plc, out var entryPoint, out var _);
                    if (provider == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcAlarmTextProvider 入口",
                            apiExplored = true
                        });

                    var coll = TryGetCollectionProperty(provider, "AlarmClasses");
                    if (coll == null)
                        return Err("PlcAlarmTextProvider 未暴露 AlarmClasses 集合属性");

                    var (ok, err, attempts) = InvokeImport(coll, filePath);
                    if (!ok)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "AlarmClasses 集合导入失败: " + err,
                            plcName = plc.Name, filePath, entryPoint, attempts
                        }, Formatting.Indented);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "报警类已导入",
                        plcName = plc.Name, filePath, entryPoint, attempts
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 二、SW.Blocks.Interface 强类型接口（Siemens.Engineering.SW.Blocks.Interface）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>反射获取块的 PlcBlockInterface（强类型接口）。</summary>
        private object? GetPlcBlockInterfaceTyped(PlcBlock block, out string entryPoint, out List<string> triedPaths)
        {
            entryPoint = "";
            triedPaths = new List<string>();

            // 1) block.Interface 属性
            try
            {
                triedPaths.Add("block.Interface");
                var prop = typeof(PlcBlock).GetProperty("Interface", BindingFlags.Public | BindingFlags.Instance);
                if (prop?.GetValue(block) is { } value)
                {
                    entryPoint = "block.Interface";
                    return value;
                }
            }
            catch (Exception ex) { triedPaths[triedPaths.Count - 1] += " => 异常: " + ex.Message; }

            // 2) block.GetService<PlcBlockInterface>()
            try
            {
                triedPaths.Add("block.GetService<PlcBlockInterface>()");
                var ifaceType = ResolveFeatureType("Siemens.Engineering.SW.Blocks.Interface.PlcBlockInterface");
                if (ifaceType != null)
                {
                    var svc = TryGetService(block, ifaceType);
                    if (svc != null)
                    {
                        entryPoint = "block.GetService<PlcBlockInterface>()";
                        return svc;
                    }
                }
            }
            catch (Exception ex) { triedPaths[triedPaths.Count - 1] += " => 异常: " + ex.Message; }

            return null;
        }

        /// <summary>枚举接口的所有 Section（Input/Output/InOut/Static/Temp/Constant/Return）及其成员集合。</summary>
        private static List<(string sectionName, object? members)> EnumerateInterfaceSections(object iface)
        {
            var result = new List<(string, object?)>();
            var sectionNames = new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant", "Return" };
            foreach (var sn in sectionNames)
            {
                try
                {
                    var prop = iface.GetType().GetProperty(sn, BindingFlags.Public | BindingFlags.Instance);
                    if (prop?.GetValue(iface) is { } members)
                        result.Add((sn, members));
                }
                catch { }
            }
            return result;
        }

        /// <summary>反射读取成员的关键属性（Name/DataType/Comment/StartValue/Remanence/Accessibility）。</summary>
        private static object ReadMemberInfo(object member)
        {
            var details = new Dictionary<string, string?>();
            try
            {
                foreach (var p in member.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try { details[p.Name] = p.GetValue(member)?.ToString(); } catch { }
                }
            }
            catch { }
            return new
            {
                name = SafeReflectGet(member, "Name") ?? "?",
                dataType = SafeReflectGet(member, "DataType"),
                comment = SafeReflectGet(member, "Comment"),
                typeName = member.GetType().Name,
                properties = details
            };
        }

        /// <summary>2.1 获取块接口（强类型版本，通过 PlcBlock.Interface / GetService&lt;PlcBlockInterface&gt;）。</summary>
        public string GetBlockInterfaceTyped(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var iface = GetPlcBlockInterfaceTyped(block, out var entryPoint, out var triedPaths);
                    if (iface == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcBlockInterface 入口（block.Interface / GetService 均失败）",
                            blockName, apiExplored = true, triedPaths
                        });

                    var sections = new List<object>();
                    foreach (var (secName, members) in EnumerateInterfaceSections(iface))
                    {
                        var memberList = new List<object>();
                        if (members is IEnumerable enumMembers)
                        {
                            foreach (var m in enumMembers)
                            {
                                try { memberList.Add(ReadMemberInfo(m)); } catch { }
                            }
                        }
                        sections.Add(new { section = secName, memberCount = memberList.Count, members = memberList });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName = block.Name,
                        blockType = block.GetType().Name,
                        number = block.Number,
                        language = block.ProgrammingLanguage.ToString(),
                        entryPoint,
                        sections
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>2.2 列出接口成员（按 Section 分组返回成员名称与类型）。</summary>
        public string ListBlockInterfaceMembers(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var iface = GetPlcBlockInterfaceTyped(block, out var entryPoint, out var _);
                    if (iface == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcBlockInterface 入口",
                            blockName, apiExplored = true
                        });

                    var sections = new List<object>();
                    foreach (var (secName, members) in EnumerateInterfaceSections(iface))
                    {
                        var memberList = new List<object>();
                        if (members is IEnumerable enumMembers)
                        {
                            foreach (var m in enumMembers)
                            {
                                try
                                {
                                    memberList.Add(new
                                    {
                                        name = SafeReflectGet(m, "Name") ?? "?",
                                        dataType = SafeReflectGet(m, "DataType") ?? "",
                                        typeName = m.GetType().Name
                                    });
                                }
                                catch { }
                            }
                        }
                        sections.Add(new { section = secName, memberCount = memberList.Count, members = memberList });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName = block.Name,
                        entryPoint,
                        sections
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>2.3 获取单个接口成员详情（按 section + memberName 定位）。</summary>
        public string GetBlockInterfaceMember(string blockName, string memberName, string? section = null, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(memberName)) return Err("memberName 不能为空");
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var iface = GetPlcBlockInterfaceTyped(block, out var entryPoint, out var _);
                    if (iface == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcBlockInterface 入口",
                            blockName, apiExplored = true
                        });

                    var sectionsToSearch = EnumerateInterfaceSections(iface);
                    if (!string.IsNullOrEmpty(section))
                        sectionsToSearch = sectionsToSearch.Where(s => s.sectionName.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();

                    foreach (var (secName, members) in sectionsToSearch)
                    {
                        if (!(members is IEnumerable enumMembers)) continue;
                        foreach (var m in enumMembers)
                        {
                            try
                            {
                                var n = SafeReflectGet(m, "Name");
                                if (n != null && n.Equals(memberName, StringComparison.OrdinalIgnoreCase))
                                    return JsonConvert.SerializeObject(new
                                    {
                                        success = true,
                                        blockName = block.Name,
                                        section = secName,
                                        member = ReadMemberInfo(m),
                                        entryPoint
                                    }, Formatting.Indented);
                            }
                            catch { }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        found = false,
                        message = $"未找到接口成员: section={section ?? "*"}, member={memberName}",
                        blockName = block.Name,
                        entryPoint
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>2.4 添加接口成员（反射调用 section.Create(name, dataType)）。</summary>
        public string AddBlockInterfaceMember(string blockName, string section, string memberName, string dataType, string? comment = null, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(section)) return Err("section 不能为空");
                    if (string.IsNullOrEmpty(memberName)) return Err("memberName 不能为空");
                    if (string.IsNullOrEmpty(dataType)) return Err("dataType 不能为空");
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var iface = GetPlcBlockInterfaceTyped(block, out var entryPoint, out var _);
                    if (iface == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcBlockInterface 入口",
                            blockName, apiExplored = true
                        });

                    var secProp = iface.GetType().GetProperty(section, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (secProp == null)
                        return Err($"接口未暴露 Section: {section}（可用: Input/Output/InOut/Static/Temp/Constant/Return）");

                    var members = secProp.GetValue(iface);
                    if (members == null) return Err($"Section [{section}] 集合为空");

                    var collType = members.GetType();
                    // 探测 Create(string, string) 或 Create(string, PlcDataType) 重载
                    var createMethod = collType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Create"
                                          && m.GetParameters().Length == 2
                                          && m.GetParameters()[0].ParameterType == typeof(string)
                                          && (m.GetParameters()[1].ParameterType == typeof(string)
                                              || m.GetParameters()[1].ParameterType.Name.Contains("PlcDataType")
                                              || m.GetParameters()[1].ParameterType.IsEnum
                                              || m.GetParameters()[1].ParameterType == typeof(object)));
                    if (createMethod == null)
                    {
                        var apiExplored = collType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName && m.Name == "Create")
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                            .ToList();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"Section [{section}] 集合未暴露 Create(string, dataType) 方法",
                            apiExplored
                        }, Formatting.Indented);
                    }

                    // 第二个参数类型转换：若为枚举或 PlcDataType，尝试解析
                    object dataTypeArg = dataType;
                    var p2Type = createMethod.GetParameters()[1].ParameterType;
                    if (p2Type.IsEnum)
                    {
                        var (enumVal, _, enumErr, enumValues) = UmacParseEnum(p2Type.Name, dataType);
                        if (enumVal == null)
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = enumErr,
                                enumType = p2Type.FullName,
                                availableValues = enumValues
                            });
                        dataTypeArg = enumVal;
                    }

                    object? created;
                    try { created = createMethod.Invoke(members, new object[] { memberName, dataTypeArg }); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                        return Err($"Create 调用失败: {inner.Message}");
                    }

                    // 补设 Comment
                    if (created != null && !string.IsNullOrEmpty(comment))
                    {
                        var (okC, _) = UmacTrySetProperty(created, "Comment", comment!);
                        _ = okC;
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已在 Section [{section}] 添加成员: {memberName}",
                        blockName = block.Name,
                        section,
                        memberName = SafeReflectGet(created, "Name") ?? memberName,
                        dataType,
                        comment = SafeReflectGet(created, "Comment") ?? comment,
                        entryPoint
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>2.5 删除接口成员（按 section + memberName 定位后调用 composition.Delete）。</summary>
        public string DeleteBlockInterfaceMember(string blockName, string memberName, string? section = null, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(memberName)) return Err("memberName 不能为空");
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var iface = GetPlcBlockInterfaceTyped(block, out var entryPoint, out var _);
                    if (iface == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 PlcBlockInterface 入口",
                            blockName, apiExplored = true
                        });

                    var sectionsToSearch = EnumerateInterfaceSections(iface);
                    if (!string.IsNullOrEmpty(section))
                        sectionsToSearch = sectionsToSearch.Where(s => s.sectionName.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();

                    foreach (var (secName, members) in sectionsToSearch)
                    {
                        if (!(members is IEnumerable enumMembers)) continue;
                        object? target = null;
                        foreach (var m in enumMembers)
                        {
                            try
                            {
                                var n = SafeReflectGet(m, "Name");
                                if (n != null && n.Equals(memberName, StringComparison.OrdinalIgnoreCase))
                                {
                                    target = m; break;
                                }
                            }
                            catch { }
                        }
                        if (target == null) continue;

                        var (ok, attempts) = DeleteFromComposition(members!, target);
                        return JsonConvert.SerializeObject(new
                        {
                            success = ok,
                            message = ok ? $"已删除接口成员: {memberName} (section={secName})" : "所有删除方式均失败",
                            blockName = block.Name,
                            section = secName,
                            memberName,
                            entryPoint,
                            attempts
                        });
                    }

                    return Err($"未找到接口成员: section={section ?? "*"}, member={memberName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 三、SW.ExternalSources 完整 API（Siemens.Engineering.SW.ExternalSources）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>反射获取 plc.ExternalSourceGroup（PlcExternalSourceGroup）。</summary>
        private static object? GetExternalSourceGroup(PlcSoftware plc)
        {
            try
            {
                var prop = typeof(PlcSoftware).GetProperty("ExternalSourceGroup", BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(plc);
            }
            catch { return null; }
        }

        /// <summary>3.1 列出外部源（ExternalSources 集合）。</summary>
        public string ListExternalSources(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var coll = TryGetCollectionProperty(group, "ExternalSources");
                    if (coll == null) return Err("ExternalSourceGroup 未暴露 ExternalSources 属性");

                    var sources = new List<object>();
                    foreach (var s in coll)
                    {
                        sources.Add(new
                        {
                            name = SafeReflectGet(s, "Name") ?? "?",
                            typeName = s.GetType().Name,
                            type = SafeReflectGet(s, "Type") ?? ""
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = sources.Count,
                        externalSources = sources
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.2 列出外部源分组（ExternalSourceGroup.Groups）。</summary>
        public string ListExternalSourceGroups(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var coll = TryGetCollectionProperty(group, "Groups");
                    if (coll == null) return Err("ExternalSourceGroup 未暴露 Groups 属性");

                    var groups = new List<object>();
                    foreach (var g in coll)
                    {
                        groups.Add(new
                        {
                            name = SafeReflectGet(g, "Name") ?? "?",
                            typeName = g.GetType().Name
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = groups.Count,
                        groups
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.3 创建外部源分组（反射调用 Groups.Create(name)）。</summary>
        public string CreateExternalSourceGroup(string plcName, string groupName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(groupName)) return Err("groupName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var coll = TryGetCollectionProperty(group, "Groups");
                    if (coll == null) return Err("ExternalSourceGroup 未暴露 Groups 属性");

                    var createMethod = coll.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (createMethod == null)
                        return Err("Groups 集合未暴露 Create(string) 方法");

                    object? created;
                    try { created = createMethod.Invoke(coll, new object[] { groupName }); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                        return Err($"Create 调用失败: {inner.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建外部源分组: {groupName}",
                        plcName = plc.Name,
                        groupName = SafeReflectGet(created, "Name") ?? groupName,
                        typeName = created?.GetType().Name ?? ""
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.4 删除外部源（按名称查找后调用 composition.Delete）。</summary>
        public string DeleteExternalSource(string plcName, string sourceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(sourceName)) return Err("sourceName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var coll = TryGetCollectionProperty(group, "ExternalSources");
                    if (coll == null) return Err("ExternalSourceGroup 未暴露 ExternalSources 属性");

                    var target = FindInComposition(coll, sourceName);
                    if (target == null) return Err($"未找到外部源: {sourceName}");

                    var (ok, attempts) = DeleteFromComposition(coll, target);
                    return JsonConvert.SerializeObject(new
                    {
                        success = ok,
                        message = ok ? $"已删除外部源: {sourceName}" : "所有删除方式均失败",
                        plcName = plc.Name,
                        sourceName,
                        attempts
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.5 从外部源生成块（反射调用 source.GenerateBlockFromSource / GenerateBlocksFromSource）。</summary>
        public string GenerateBlockFromExternalSource(string plcName, string sourceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(sourceName)) return Err("sourceName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var coll = TryGetCollectionProperty(group, "ExternalSources");
                    if (coll == null) return Err("ExternalSourceGroup 未暴露 ExternalSources 属性");

                    var target = FindInComposition(coll, sourceName);
                    if (target == null) return Err($"未找到外部源: {sourceName}");

                    var sourceType = target.GetType();
                    // 优先 GenerateBlockFromSource()（单块）
                    var generateMethod = sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GenerateBlockFromSource" && m.GetParameters().Length == 0);
                    bool single = true;
                    if (generateMethod == null)
                    {
                        // 回退：GenerateBlocksFromSource()（多块）
                        generateMethod = sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name == "GenerateBlocksFromSource" && m.GetParameters().Length == 0);
                        single = false;
                    }
                    if (generateMethod == null)
                    {
                        var apiExplored = sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName && m.Name.Contains("Generate"))
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                            .ToList();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "外部源未暴露 GenerateBlockFromSource/GenerateBlocksFromSource() 方法",
                            apiExplored
                        }, Formatting.Indented);
                    }

                    object? result;
                    try { result = generateMethod.Invoke(target, null); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                        return Err($"Generate 调用失败: {inner.Message}");
                    }

                    var generatedBlocks = new List<object>();
                    if (single && result != null)
                    {
                        generatedBlocks.Add(new
                        {
                            name = SafeReflectGet(result, "Name") ?? "",
                            type = result.GetType().Name
                        });
                    }
                    else if (result is IEnumerable enumResult)
                    {
                        foreach (var r in enumResult)
                        {
                            generatedBlocks.Add(new
                            {
                                name = SafeReflectGet(r, "Name") ?? "",
                                type = r?.GetType().Name ?? ""
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已从外部源 {sourceName} 生成 {generatedBlocks.Count} 个块",
                        plcName = plc.Name,
                        sourceName,
                        invokedMethod = generateMethod.Name,
                        generatedBlocks,
                        blockCount = generatedBlocks.Count
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.6 批量从外部源生成块（对多个 sourceName 依次调用生成方法）。</summary>
        public string GenerateBlocksFromExternalSource(string plcName, List<string> sourceNames)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (sourceNames == null || sourceNames.Count == 0) return Err("sourceNames 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var coll = TryGetCollectionProperty(group, "ExternalSources");
                    if (coll == null) return Err("ExternalSourceGroup 未暴露 ExternalSources 属性");

                    var allGenerated = new List<object>();
                    var notFound = new List<string>();
                    var perSource = new List<object>();

                    foreach (var sn in sourceNames)
                    {
                        var target = FindInComposition(coll, sn);
                        if (target == null) { notFound.Add(sn); continue; }

                        var sourceType = target.GetType();
                        var generateMethod = sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name == "GenerateBlocksFromSource" && m.GetParameters().Length == 0)
                            ?? sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(m => m.Name == "GenerateBlockFromSource" && m.GetParameters().Length == 0);

                        if (generateMethod == null)
                        {
                            perSource.Add(new { sourceName = sn, success = false, error = "未暴露 Generate 方法" });
                            continue;
                        }

                        object? result;
                        try { result = generateMethod.Invoke(target, null); }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                            perSource.Add(new { sourceName = sn, success = false, error = inner.Message });
                            continue;
                        }

                        var blocks = new List<object>();
                        if (result is IEnumerable enumResult)
                        {
                            foreach (var r in enumResult)
                                blocks.Add(new { name = SafeReflectGet(r, "Name") ?? "", type = r?.GetType().Name ?? "" });
                        }
                        else if (result != null)
                        {
                            blocks.Add(new { name = SafeReflectGet(result, "Name") ?? "", type = result.GetType().Name });
                        }
                        allGenerated.AddRange(blocks);
                        perSource.Add(new { sourceName = sn, success = true, blockCount = blocks.Count, blocks });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"批量生成完成：共 {allGenerated.Count} 块，{notFound.Count} 个源未找到",
                        plcName = plc.Name,
                        totalBlocks = allGenerated.Count,
                        perSource,
                        notFoundSources = notFound
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.7 获取生成源选项（反射读取 ExternalSourceGroup.GenerateSourceOptions）。</summary>
        public string GetGenerateSourceOptions(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var options = GetProperty(group, "GenerateSourceOptions");
                    if (options == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "ExternalSourceGroup 未暴露 GenerateSourceOptions 属性",
                            groupProperties = ListPublicPropertyNames(group)
                        }, Formatting.Indented);

                    var details = new Dictionary<string, string?>();
                    try
                    {
                        foreach (var p in options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { details[p.Name] = p.GetValue(options)?.ToString(); } catch { }
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        optionsTypeName = options.GetType().Name,
                        options = details
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.8 设置生成源选项（反射设置 GenerateSourceOptions 上的可写属性）。</summary>
        public string SetGenerateSourceOptions(string plcName, Dictionary<string, string> options)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (options == null || options.Count == 0) return Err("options 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = GetExternalSourceGroup(plc);
                    if (group == null) return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var optObj = GetProperty(group, "GenerateSourceOptions");
                    if (optObj == null)
                        return Err("ExternalSourceGroup 未暴露 GenerateSourceOptions 属性");

                    var attempts = new List<object>();
                    bool anySet = false;
                    foreach (var kv in options)
                    {
                        var (ok, att) = UmacTrySetProperty(optObj, kv.Key, kv.Value);
                        attempts.Add(new { key = kv.Key, value = kv.Value, ok, attempts = att });
                        if (ok) anySet = true;
                    }

                    // 回读最终值
                    var final = new Dictionary<string, string?>();
                    try
                    {
                        foreach (var p in optObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { final[p.Name] = p.GetValue(optObj)?.ToString(); } catch { }
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = anySet,
                        message = anySet ? "生成源选项已更新" : "所有设置均失败",
                        plcName = plc.Name,
                        finalOptions = final,
                        attempts
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 四、SW.OpcUa 完整 API（Siemens.Engineering.SW.OpcUa）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>反射获取 PLC 的 OpcUaProvider（复用 InvokeGetService）。</summary>
        private (object? provider, Type? providerType, List<string> triedPaths) GetOpcUaProvider(PlcSoftware plc)
        {
            return InvokeGetService(plc,
                new[]
                {
                    "Siemens.Engineering.SW.OpcUa.OpcUaProvider",
                    "Siemens.Engineering.OpcUa.OpcUaProvider"
                });
        }

        /// <summary>4.1 列出 OPC UA 服务器接口（完整版，包含 Enabled/Namespaces/ServerInterfaceGroups 等详情）。</summary>
        public string ListOpcUaServerInterfacesFull(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetOpcUaProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务（可能 PLC 不支持 OPC UA 或 TIA 版本不匹配）",
                            apiExplored = true, triedPaths
                        });

                    var siColl = providerType.GetProperty("ServerInterfaces")?.GetValue(provider) as IEnumerable;
                    var interfaces = new List<object>();
                    if (siColl != null)
                    {
                        foreach (var si in siColl)
                        {
                            var siType = si.GetType();
                            var nsList = new List<string>();
                            var nsProp = siType.GetProperty("Namespaces");
                            if (nsProp?.GetValue(si) is IEnumerable nsEnum)
                                foreach (var ns in nsEnum) nsList.Add(ns?.ToString() ?? "");

                            interfaces.Add(new
                            {
                                name = siType.GetProperty("Name")?.GetValue(si)?.ToString() ?? "",
                                enabled = siType.GetProperty("Enabled")?.GetValue(si) ?? false,
                                typeName = siType.Name,
                                namespaces = nsList
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = interfaces.Count,
                        serverInterfaces = interfaces
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.2 列出 OPC UA 通信组（OpcUaProvider.CommunicationGroups）。</summary>
        public string ListOpcUaCommunicationGroups(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetOpcUaProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "CommunicationGroups");
                    if (coll == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "OpcUaProvider 未暴露 CommunicationGroups 属性",
                            providerProperties = ListPublicPropertyNames(provider)
                        }, Formatting.Indented);

                    var groups = new List<object>();
                    foreach (var g in coll)
                    {
                        var details = new Dictionary<string, string?>();
                        try
                        {
                            foreach (var p in g.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                try { details[p.Name] = p.GetValue(g)?.ToString(); } catch { }
                            }
                        }
                        catch { }
                        groups.Add(new
                        {
                            name = SafeReflectGet(g, "Name") ?? "?",
                            typeName = g.GetType().Name,
                            properties = details
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = groups.Count,
                        communicationGroups = groups
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.3 创建 OPC UA 通信组（反射调用 CommunicationGroups.Create(name)）。</summary>
        public string CreateOpcUaCommunicationGroup(string plcName, string groupName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(groupName)) return Err("groupName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetOpcUaProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "CommunicationGroups");
                    if (coll == null) return Err("OpcUaProvider 未暴露 CommunicationGroups 属性");

                    var createMethod = coll.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (createMethod == null)
                        return Err("CommunicationGroups 集合未暴露 Create(string) 方法");

                    object? created;
                    try { created = createMethod.Invoke(coll, new object[] { groupName }); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                        return Err($"Create 调用失败: {inner.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建 OPC UA 通信组: {groupName}",
                        plcName = plc.Name,
                        groupName = SafeReflectGet(created, "Name") ?? groupName,
                        typeName = created?.GetType().Name ?? ""
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.4 列出服务器接口组（OpcUaProvider.ServerInterfaceGroups）。</summary>
        public string ListOpcUaServerInterfaceGroups(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetOpcUaProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "ServerInterfaceGroups");
                    if (coll == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "OpcUaProvider 未暴露 ServerInterfaceGroups 属性",
                            providerProperties = ListPublicPropertyNames(provider)
                        }, Formatting.Indented);

                    var groups = new List<object>();
                    foreach (var g in coll)
                    {
                        groups.Add(new
                        {
                            name = SafeReflectGet(g, "Name") ?? "?",
                            typeName = g.GetType().Name
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = groups.Count,
                        serverInterfaceGroups = groups
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.5 列出 SIMATIC 接口（OpcUaProvider.SimaticInterfaces）。</summary>
        public string ListOpcUaSimaticInterfaces(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetOpcUaProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "SimaticInterfaces");
                    if (coll == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "OpcUaProvider 未暴露 SimaticInterfaces 属性",
                            providerProperties = ListPublicPropertyNames(provider)
                        }, Formatting.Indented);

                    var ifaces = new List<object>();
                    foreach (var i in coll)
                    {
                        ifaces.Add(new
                        {
                            name = SafeReflectGet(i, "Name") ?? "?",
                            typeName = i.GetType().Name
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = ifaces.Count,
                        simaticInterfaces = ifaces
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.6 列出引用命名空间（OpcUaProvider.ReferenceNamespaces）。</summary>
        public string ListOpcUaReferenceNamespaces(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetOpcUaProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "ReferenceNamespaces");
                    if (coll == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "OpcUaProvider 未暴露 ReferenceNamespaces 属性",
                            providerProperties = ListPublicPropertyNames(provider)
                        }, Formatting.Indented);

                    var nsList = new List<object>();
                    foreach (var ns in coll)
                    {
                        var details = new Dictionary<string, string?>();
                        try
                        {
                            foreach (var p in ns.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                try { details[p.Name] = p.GetValue(ns)?.ToString(); } catch { }
                            }
                        }
                        catch { }
                        nsList.Add(new
                        {
                            name = SafeReflectGet(ns, "Name") ?? "",
                            typeName = ns.GetType().Name,
                            properties = details
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = nsList.Count,
                        referenceNamespaces = nsList
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.7 创建引用命名空间（反射调用 ReferenceNamespaces.Create(name, uri) / Create(uri)）。</summary>
        public string CreateOpcUaReferenceNamespace(string plcName, string namespaceName, string? uri = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(namespaceName)) return Err("namespaceName 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetOpcUaProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    var coll = TryGetCollectionProperty(provider, "ReferenceNamespaces");
                    if (coll == null) return Err("OpcUaProvider 未暴露 ReferenceNamespaces 属性");

                    var collType = coll.GetType();
                    object? created = null;

                    // 优先 Create(string, string)
                    if (!string.IsNullOrEmpty(uri))
                    {
                        var createMethod2 = collType.GetMethod("Create", new[] { typeof(string), typeof(string) });
                        if (createMethod2 != null)
                        {
                            try { created = createMethod2.Invoke(coll, new object[] { namespaceName, uri! }); }
                            catch (Exception ex)
                            {
                                var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                                return Err($"Create(string, string) 调用失败: {inner.Message}");
                            }
                        }
                    }

                    // 回退：Create(string)
                    if (created == null)
                    {
                        var createMethod1 = collType.GetMethod("Create", new[] { typeof(string) });
                        if (createMethod1 == null)
                            return Err("ReferenceNamespaces 集合未暴露 Create(string) 或 Create(string, string) 方法");
                        try { created = createMethod1.Invoke(coll, new object[] { namespaceName }); }
                        catch (Exception ex)
                        {
                            var inner = ex is TargetInvocationException tie ? (tie.InnerException ?? ex) : ex;
                            return Err($"Create(string) 调用失败: {inner.Message}");
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建引用命名空间: {namespaceName}",
                        plcName = plc.Name,
                        namespaceName = SafeReflectGet(created, "Name") ?? namespaceName,
                        uri = SafeReflectGet(created, "Uri") ?? uri,
                        typeName = created?.GetType().Name ?? ""
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 五、SW.Supervision 完整 API（Siemens.Engineering.SW.Supervision）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>反射获取 PLC 的 SupervisionSettingsProvider（plc.GetService&lt;SupervisionSettingsProvider&gt;()）。</summary>
        private (object? provider, Type? providerType, List<string> triedPaths) GetSupervisionSettingsProvider(PlcSoftware plc)
        {
            return InvokeGetService(plc,
                new[]
                {
                    "Siemens.Engineering.SW.Supervision.SupervisionSettingsProvider",
                    "Siemens.Engineering.Supervision.SupervisionSettingsProvider"
                });
        }

        /// <summary>5.1 获取监控设置（反射读取 provider.Settings 或 provider 上所有属性）。</summary>
        public string GetSupervisionSettings(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetSupervisionSettingsProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SupervisionSettingsProvider 服务（可能 PLC 不支持监控设置或 TIA 版本不匹配）",
                            apiExplored = true, triedPaths
                        });

                    // 优先读取 Settings 属性
                    var settings = GetProperty(provider, "Settings");
                    var settingsDetails = new Dictionary<string, string?>();
                    if (settings != null)
                    {
                        try
                        {
                            foreach (var p in settings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                try { settingsDetails[p.Name] = p.GetValue(settings)?.ToString(); } catch { }
                            }
                        }
                        catch { }
                    }

                    // 同时收集 provider 自身所有属性
                    var providerDetails = new Dictionary<string, string?>();
                    try
                    {
                        foreach (var p in providerType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { providerDetails[p.Name] = p.GetValue(provider)?.ToString(); } catch { }
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        providerType = providerType.Name,
                        hasSettings = settings != null,
                        settings = settingsDetails,
                        providerProperties = providerDetails
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.2 导出监控设置到 XLSX（反射调用 provider.Export(FileInfo[, ExportOptions])）。</summary>
        public string ExportSupervisionSettingsXlsx(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetSupervisionSettingsProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SupervisionSettingsProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    if (!InvokeExport(provider, filePath, out var exportError))
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = exportError,
                            plcName = plc.Name, filePath,
                            providerProperties = ListPublicPropertyNames(provider)
                        }, Formatting.Indented);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "监控设置已导出到 XLSX",
                        plcName = plc.Name, filePath
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.3 从 XLSX 导入监控设置（反射调用 provider.Import(FileInfo[, ImportOptions])）。</summary>
        public string ImportSupervisionSettingsXlsx(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetSupervisionSettingsProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SupervisionSettingsProvider 服务",
                            apiExplored = true, triedPaths
                        });

                    var (ok, err, attempts) = InvokeImport(provider, filePath);
                    if (!ok)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "监控设置导入失败: " + err,
                            plcName = plc.Name, filePath, attempts,
                            providerProperties = ListPublicPropertyNames(provider)
                        }, Formatting.Indented);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "监控设置已从 XLSX 导入",
                        plcName = plc.Name, filePath, attempts
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.4 获取监控设置提供者（仅返回入口点与可用属性，用于 API 诊断）。</summary>
        public string GetSupervisionSettingsProvider(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    var (provider, providerType, triedPaths) = GetSupervisionSettingsProvider(plc);
                    if (provider == null || providerType == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SupervisionSettingsProvider 服务（可能 PLC 不支持监控设置或 TIA 版本不匹配）",
                            apiExplored = true, triedPaths
                        });

                    var details = new Dictionary<string, string?>();
                    try
                    {
                        foreach (var p in providerType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try { details[p.Name] = p.GetValue(provider)?.ToString(); } catch { }
                        }
                    }
                    catch { }

                    // 收集可用方法
                    var methods = new List<string>();
                    try
                    {
                        foreach (var m in providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (m.IsSpecialName) continue;
                            var ps = m.GetParameters();
                            methods.Add($"{m.Name}({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        providerType = providerType.FullName,
                        properties = details,
                        methods
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }
    }
}
