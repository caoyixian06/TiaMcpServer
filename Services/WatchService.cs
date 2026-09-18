using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.WatchAndForceTables;

namespace TiaMcpServer
{
    /// <summary>
    /// 监视表 / 强制表 CRUD（通过 TIA Openness 的 WatchAndForceTableGroup）。
    /// 由于不同博途版本 API 差异较大，条目统一用 IEngineeringObject.Get/SetAttribute 处理。
    /// </summary>
    public partial class PortalService
    {
        // ═════════════════════════════════════════════════════════════════════════════
        // 查找辅助
        // ═════════════════════════════════════════════════════════════════════════════
        private PlcWatchTable? FindWatchTable(string name, string? plcName = null)
        {
            var plc = GetPlcSoftwareFor(plcName);
            var group = plc.WatchAndForceTableGroup;
            if (group == null) return null;

            var table = group.WatchTables
                .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (table != null) return table;

            foreach (var g in group.Groups)
            {
                table = g.WatchTables
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (table != null) return table;
            }
            return null;
        }

        private PlcForceTable? FindForceTable(string name, string? plcName = null)
        {
            var plc = GetPlcSoftwareFor(plcName);
            var group = plc.WatchAndForceTableGroup;
            if (group == null) return null;

            var table = group.ForceTables
                .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (table != null) return table;

            foreach (var g in group.Groups)
            {
                table = g.ForceTables
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (table != null) return table;
            }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 监视表 CRUD
        // ═════════════════════════════════════════════════════════════════════════════
        public string CreateWatchTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = plc.WatchAndForceTableGroup;
                    if (group == null) throw new InvalidOperationException("该 PLC 不支持监视/强制表");

                    var existing = group.WatchTables
                        .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) return Err($"监视表已存在: {name}");

                    var table = group.WatchTables.Create(name);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建监视表: {name}",
                        tableName = table.Name
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteWatchTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindWatchTable(name, plcName);
                    if (table == null) return Err($"未找到监视表: {name}");
                    table.Delete();
                    return Ok($"已删除监视表: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ReadWatchTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindWatchTable(name, plcName);
                    if (table == null) return Err($"未找到监视表: {name}");

                    var entries = ReadEntries(table.Entries, includeForceValue: false);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tableName = name,
                        entryCount = entries.Count,
                        entries
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string AddWatchVariable(string tableName, string varName, string? address, string? dataType, string? comment, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindWatchTable(tableName, plcName);
                    if (table == null) return Err($"未找到监视表: {tableName}");

                    return AddTableEntry(table.Entries, varName, address, dataType, comment, forceValue: null);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteWatchVariable(string tableName, string varName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindWatchTable(tableName, plcName);
                    if (table == null) return Err($"未找到监视表: {tableName}");

                    return DeleteTableEntry(table.Entries, varName);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 强制表 CRUD
        // ═════════════════════════════════════════════════════════════════════════════
        public string CreateForceTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = plc.WatchAndForceTableGroup;
                    if (group == null) throw new InvalidOperationException("该 PLC 不支持监视/强制表");

                    var existing = group.ForceTables
                        .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) return Err($"强制表已存在: {name}");

                    // PlcForceTableComposition 没有 public Create(string)，通过 IEngineeringComposition 创建
                    var engComp = (IEngineeringComposition)group.ForceTables;
                    var tableObj = engComp.Create(typeof(PlcForceTable),
                        new Dictionary<string, object> { ["Name"] = name });
                    var table = (PlcForceTable)tableObj;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建强制表: {name}",
                        tableName = table.Name
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteForceTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindForceTable(name, plcName);
                    if (table == null) return Err($"未找到强制表: {name}");

                    // PlcForceTable 没有 public Delete，通过 IEngineeringObject 调用 Delete
                    ((IEngineeringObject)table).Invoke("Delete", null);
                    return Ok($"已删除强制表: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ReadForceTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindForceTable(name, plcName);
                    if (table == null) return Err($"未找到强制表: {name}");

                    var entries = ReadEntries(table.Entries, includeForceValue: true);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tableName = name,
                        entryCount = entries.Count,
                        entries
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportWatchTable(string tableName, string outputPath, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindWatchTable(tableName, plcName);
                    if (table == null) return Err($"未找到监视表: {tableName}");
                    if (string.IsNullOrEmpty(outputPath)) return Err("outputPath 不能为空");
                    var fi = new FileInfo(outputPath);
                    table.Export(fi, ExportOptions.WithDefaults);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导出监视表: {tableName}",
                        outputPath = fi.FullName,
                        size = fi.Exists ? fi.Length : 0
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ImportWatchTable(string filePath, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return Err($"文件不存在: {filePath}");
                    var group = plc.WatchAndForceTableGroup;
                    var imported = group.WatchTables.Import(new FileInfo(filePath), ImportOptions.Override);
                    var names = imported?.Select(t => t.Name).ToList() ?? new List<string>();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "已导入监视表",
                        imported = names
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ProbeWatchApi(string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var group = plc.WatchAndForceTableGroup;
                    var result = new JObject();
                    void Dump(string key, object? obj)
                    {
                        var items = new List<object>();
                        if (obj != null)
                        {
                            foreach (var m in obj.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                            {
                                if (m.IsSpecialName) continue;
                                items.Add(m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name);
                            }
                            result[key] = JArray.FromObject(items);
                            result[key + "_type"] = obj.GetType().FullName;
                        }
                        else result[key] = null;
                    }
                    Dump("groupMethods", group);
                    Dump("watchTablesComposition", group?.WatchTables);
                    if (group?.WatchTables != null)
                    {
                        try
                        {
                            var t = group.WatchTables.FirstOrDefault();
                            Dump("firstTableMethods", t);
                            if (t != null) Dump("tableEntriesComposition", t.Entries);
                        }
                        catch { }
                    }
                    Dump("forceTablesComposition", group?.ForceTables);
                    return result.ToString(Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ModifyWatchVariable(string tableName, string varName, string value, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindWatchTable(tableName, plcName);
                    if (table == null) return Err($"未找到监视表: {tableName}");

                    PlcWatchTableEntry? target = null;
                    foreach (var entry in table.Entries)
                    {
                        var rawName = GetAttributeString((IEngineeringObject)entry, "Name");
                        // XML 导入的条目 Name 属性可能带引号（如 "\"模式选择\""），归一化比较
                        var name = rawName.Trim().Trim('"');
                        if (name.Equals(varName, StringComparison.OrdinalIgnoreCase))
                        {
                            target = entry as PlcWatchTableEntry;
                            break;
                        }
                    }
                    if (target == null) return Err($"未找到条目: {varName}");

                    var eo = (IEngineeringObject)target;
                    var attempts = new List<object>();

                    // 0) 盘点条目/表可写属性与方法（V17 差异大，先发现再修改）
                    var attrInfos = new List<string>();
                    try { foreach (var ai in target.GetAttributeInfos()) attrInfos.Add(ai.Name); } catch { }
                    var invocationInfos = new List<string>();
                    try
                    {
                        foreach (var ii in eo.GetInvocationInfos())
                            invocationInfos.Add(ii.Name);
                    }
                    catch { }
                    var entryMethods = new List<string>();
                    try
                    {
                        foreach (var m in target.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            if (!m.IsSpecialName) entryMethods.Add(m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                    }
                    catch { }
                    var tableMethods = new List<string>();
                    try
                    {
                        foreach (var m in table.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            if (!m.IsSpecialName) tableMethods.Add(m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                    }
                    catch { }

                    // 方式1: SetAttribute("ModifyValue", value)
                    try
                    {
                        eo.SetAttribute("ModifyValue", value);
                        attempts.Add(new { mode = "SetAttribute(ModifyValue)", ok = true });
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new { mode = "SetAttribute(ModifyValue)", ok = false, err = ex.Message });
                    }

                    // 方式2: ModifyIntention + ModifyTrigger（V17 机制）
                    try
                    {
                        eo.SetAttribute("ModifyIntention", value);
                        attempts.Add(new { mode = "SetAttribute(ModifyIntention)", ok = true });
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new { mode = "SetAttribute(ModifyIntention)", ok = false, err = ex.Message });
                    }
                    try
                    {
                        eo.SetAttribute("ModifyTrigger", true);
                        attempts.Add(new { mode = "SetAttribute(ModifyTrigger=true)", ok = true });
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new { mode = "SetAttribute(ModifyTrigger=true)", ok = false, err = ex.Message });
                    }

                    // 方式2: 触发修改（条目/表级方法）
                    foreach (var methodName in new[] { "Modify", "TriggerModify", "ApplyModify", "ActivateModify", "Trigger" })
                    {
                        try
                        {
                            var m = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (m != null && m.GetParameters().Length == 0)
                            {
                                m.Invoke(target, null);
                                attempts.Add(new { mode = "entry." + methodName + "()", ok = true });
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            attempts.Add(new { mode = "entry." + methodName + "()", ok = false, err = ex.Message });
                        }
                    }
                    // 方式3: 表级触发
                    try
                    {
                        foreach (var methodName in new[] { "Modify", "TriggerModify", "ApplyModify", "ActivateModify", "Trigger" })
                        {
                            var m = table.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (m != null && m.GetParameters().Length == 0)
                            {
                                m.Invoke(table, null);
                                attempts.Add(new { mode = "table." + methodName + "()", ok = true });
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new { mode = "table.Modify()", ok = false, err = ex.Message });
                    }

                    // 回读 ModifyValue 确认
                    var readBack = GetAttributeString(eo, "ModifyValue");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tableName,
                        entryName = varName,
                        value,
                        modifyValueReadBack = readBack,
                        attributeInfos = attrInfos,
                        invocationInfos = invocationInfos,
                        entryMethods = entryMethods.Take(40).ToList(),
                        tableMethods = tableMethods.Take(40).ToList(),
                        attempts
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string AddForceVariable(string tableName, string varName, string? address, string? dataType, string? comment, string? forceValue, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindForceTable(tableName, plcName);
                    if (table == null) return Err($"未找到强制表: {tableName}");

                    return AddTableEntry(table.Entries, varName, address, dataType, comment, forceValue);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteForceVariable(string tableName, string varName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindForceTable(tableName, plcName);
                    if (table == null) return Err($"未找到强制表: {tableName}");

                    return DeleteTableEntry(table.Entries, varName);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 条目操作
        // ═════════════════════════════════════════════════════════════════════════════
        private List<object> ReadEntries(IEnumerable collection, bool includeForceValue)
        {
            var result = new List<object>();
            foreach (var entry in collection)
            {
                if (entry == null) continue;
                var eo = (IEngineeringObject)entry;
                // ★V17★ 监视值需先触发 MonitorTrigger 才会刷新
                try { eo.SetAttribute("MonitorTrigger", true); } catch { }
                var name = GetAttributeString(eo, "Name").Trim().Trim('"');
                var address = GetAttributeString(eo, "Address");
                var dataType = GetAttributeString(eo, "DataTypeName");
                if (string.IsNullOrEmpty(dataType))
                    dataType = GetAttributeString(eo, "DataType");
                var comment = GetAttributeString(eo, "Comment");
                var monitorValue = GetAttributeString(eo, "MonitorValue");

                if (includeForceValue)
                {
                    var forceValue = GetAttributeString(eo, "ForceValue");
                    result.Add(new { name, address, dataType, comment, forceValue, monitorValue });
                }
                else
                {
                    result.Add(new { name, address, dataType, comment, monitorValue });
                }
            }
            return result;
        }

        private string AddTableEntry(PlcTableCommentEntryComposition entries, string varName,
            string? address, string? dataType, string? comment, string? forceValue)
        {
            // 检查是否已存在同名条目
            foreach (var existing in entries)
            {
                var existingName = GetAttributeString((IEngineeringObject)existing, "Name");
                if (existingName.Equals(varName, StringComparison.OrdinalIgnoreCase))
                    return Err($"表中已存在条目: {varName}");
            }

            var entry = entries.Create();
            var eo = (IEngineeringObject)entry;

            // ★ 修复: Create() 默认创建注释条目(PlcTableCommentEntry)不支持 Name 属性;
            // 变量条目应创建 PlcWatchTableEntry（通过 IEngineeringComposition 指定类型与名称）
            var actualType = entry.GetType();
            if (actualType.Name.Contains("Comment"))
            {
                try
                {
                    var engComp = (IEngineeringComposition)entries;
                    var obj = engComp.Create(typeof(PlcWatchTableEntry),
                        new Dictionary<string, object> { ["Name"] = varName });
                    eo = (IEngineeringObject)obj;
                }
                catch (Exception ce)
                {
                    return Err("创建变量条目失败: " + ce.Message);
                }
            }
            else
            {
                eo.SetAttribute("Name", varName);
            }

            if (!string.IsNullOrEmpty(address))
                eo.SetAttribute("Address", address);

            if (!string.IsNullOrEmpty(dataType))
            {
                eo.SetAttribute("DataTypeName", dataType);
                eo.SetAttribute("DataType", dataType);
            }

            if (!string.IsNullOrEmpty(comment))
                eo.SetAttribute("Comment", comment);

            if (!string.IsNullOrEmpty(forceValue))
                eo.SetAttribute("ForceValue", forceValue);

            return JsonConvert.SerializeObject(new
            {
                success = true,
                message = $"已添加条目: {varName}",
                entryName = varName,
                address,
                dataType,
                forceValue
            });
        }

        private string DeleteTableEntry(PlcTableCommentEntryComposition entries, string varName)
        {
            PlcTableCommentEntry? target = null;
            foreach (var entry in entries)
            {
                var name = GetAttributeString((IEngineeringObject)entry, "Name");
                if (name.Equals(varName, StringComparison.OrdinalIgnoreCase))
                {
                    target = entry;
                    break;
                }
            }

            if (target == null) return Err($"未找到条目: {varName}");

            target.Delete();
            return Ok($"已删除条目: {varName}");
        }

        private static string GetAttributeString(IEngineeringObject eo, string attrName)
        {
            try
            {
                var value = eo.GetAttribute(attrName);
                return value?.ToString() ?? "";
            }
            catch { return ""; }
        }
    }
}
