using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TiaMcpServer
{
    /// <summary>
    /// PLC 软件扩展工具注册：
    /// - SW.Alarm 完整 API（PlcAlarmTextProvider / AlarmTextLists / AlarmClasses）
    /// - SW.Blocks.Interface 强类型接口（PlcBlockInterface / Member / MemberComposition）
    /// - SW.ExternalSources 完整 API（PlcExternalSourceComposition / PlcExternalSourceGroup）
    /// - SW.OpcUa 完整 API（OpcUaProvider / ServerInterface / CommunicationGroups）
    /// - SW.Supervision 完整 API（SupervisionSettingsProvider）
    /// 为 partial class McpServer，在 McpServer.RegisterAllTools 中调用 RegisterPlcSoftwareExtendedTools。
    /// 所有 Service 方法返回 JSON 字符串，直接透传。
    /// </summary>
    public partial class McpServer
    {
        internal static void RegisterPlcSoftwareExtendedTools(McpServer server, Lazy<PortalService> tia)
        {
            // ═════════════════════════════════════════════════════════════════════════════
            // 一、SW.Alarm 完整 API（Siemens.Engineering.SW.Alarm）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_plc_alarm_textlists_full",
                "列出 PLC 报警文本表（完整版，使用官方 PlcAlarmTextProvider API）。\n" +
                "反射探测 PlcAlarmTextProvider 入口（plc.PlcAlarmTextProvider 属性 / plc.GetService<PlcAlarmTextProvider>()），\n" +
                "返回每个文本表的 Name/TypeName/EntryCount。\n" +
                "与 list_plc_alarm_textlists 的区别：完整版直接使用 PlcAlarmTextProvider 官方 API，并记录 entryPoint 与 triedPaths 便于诊断。\n" +
                "若 PLC 未暴露报警文本 API，返回 apiExplored=true 及 triedPaths。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填，需与 list_devices 返回的名称一致）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListPlcAlarmTextlistsFull(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("create_plc_alarm_user_textlist",
                "创建用户报警文本表（反射调用 AlarmTextLists.CreateUserAlarmTextList(name)）。\n" +
                "PlcAlarmTextProvider 入口探测失败时返回 apiExplored=true 与 triedPaths。\n" +
                "创建成功后回读 Name 属性确认。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["textlistName"] = StrProp("用户报警文本表名称（必填）"),
                }, new[] { "plcName", "textlistName" }),
                args => tia.Value.CreatePlcAlarmUserTextlist(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "textlistName") ?? ""));

            server.RegisterTool("create_plc_alarm_system_textlist",
                "创建系统报警文本表（反射调用 AlarmTextLists.CreateSystemAlarmTextList(name[, type])）。\n" +
                "systemType 为可选枚举值（SystemAlarmTextListType，大小写不敏感）；\n" +
                "提供 systemType 时优先匹配 CreateSystemAlarmTextList(string, SystemAlarmTextListType) 双参重载，\n" +
                "未提供或重载不存在时回退到 CreateSystemAlarmTextList(string) 单参重载。\n" +
                "枚举解析失败时返回 availableSystemTypes 可用值列表便于诊断。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["textlistName"] = StrProp("系统报警文本表名称（必填）"),
                    ["systemType"] = StrProp("系统报警文本表类型（可选，SystemAlarmTextListType 枚举值，大小写不敏感）"),
                }, new[] { "plcName", "textlistName" }),
                args => tia.Value.CreatePlcAlarmSystemTextlist(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "textlistName") ?? "",
                    GetStringArg(args, "systemType")));

            server.RegisterTool("delete_plc_alarm_textlist",
                "删除报警文本表（按名称查找后调用 composition.Delete / Remove）。\n" +
                "★注意★ 删除不可撤销。\n" +
                "反射尝试多种删除 API：composition.Delete(item) / item.Delete() / composition.Remove(item)。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["textlistName"] = StrProp("要删除的报警文本表名称（必填）"),
                }, new[] { "plcName", "textlistName" }),
                args => tia.Value.DeletePlcAlarmTextlist(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "textlistName") ?? ""));

            server.RegisterTool("export_plc_alarm_texts_xlsx",
                "导出报警文本到 XLSX（反射调用 provider.ExportTexts(FileInfo[, ExportOptions])）。\n" +
                "依次尝试 ExportTexts(FileInfo, ExportOptions) / ExportTexts(FileInfo) / 通用 Export(FileInfo[, ExportOptions])，\n" +
                "attempts 字段记录所有尝试过的方法与异常，便于诊断。\n" +
                "失败时返回 providerProperties 供 AI 探索可用属性。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["filePath"] = StrProp("输出 XLSX 文件路径（如 C:\\\\export\\\\alarm_texts.xlsx）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ExportPlcAlarmTextsXlsx(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("import_plc_alarm_texts_xlsx",
                "从 XLSX 导入报警文本（反射调用 provider.ImportTexts(FileInfo[, ImportOptions])）。\n" +
                "依次尝试 ImportTexts(FileInfo, ImportOptions) / ImportTexts(FileInfo) / 通用 Import(FileInfo[, ImportOptions])，\n" +
                "attempts 字段记录所有尝试过的方法与异常。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["filePath"] = StrProp("要导入的 XLSX 文件路径（必填，文件必须存在）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ImportPlcAlarmTextsXlsx(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("get_plc_alarm_class_data",
                "获取报警类数据（反射获取 PlcAlarmTextProvider.AlarmClasses 集合并按名称查找）。\n" +
                "alarmClassName 为空时返回所有报警类；指定时只返回匹配项（大小写不敏感）。\n" +
                "返回每个报警类的 Name/TypeName 及全部可读属性（properties 字典）。\n" +
                "allPropertyNames 收集所有出现过的属性名，便于 AI 探索可用字段。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["alarmClassName"] = StrProp("报警类名称（可选，为空时返回所有报警类）"),
                }, new[] { "plcName" }),
                args => tia.Value.GetPlcAlarmClassData(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "alarmClassName")));

            server.RegisterTool("export_plc_alarm_classes_full",
                "导出报警类（完整版，反射调用 AlarmClasses 集合的 Export(FileInfo[, ExportOptions])）。\n" +
                "与 export_plc_alarm_classes 的区别：完整版从 PlcAlarmTextProvider 官方 API 入口获取 AlarmClasses 集合，\n" +
                "并记录 entryPoint 便于诊断。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["filePath"] = StrProp("输出 XML 文件路径"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ExportPlcAlarmClassesFull(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("import_plc_alarm_classes_full",
                "导入报警类（完整版，反射调用 AlarmClasses 集合的 Import(FileInfo[, ImportOptions])）。\n" +
                "与 import_plc_alarm_classes 的区别：完整版从 PlcAlarmTextProvider 官方 API 入口获取 AlarmClasses 集合。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["filePath"] = StrProp("要导入的 XML 文件路径（必填，文件必须存在）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ImportPlcAlarmClassesFull(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            // ═════════════════════════════════════════════════════════════════════════════
            // 二、SW.Blocks.Interface 强类型接口（Siemens.Engineering.SW.Blocks.Interface）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("get_block_interface_typed",
                "获取块接口（强类型版本，通过 PlcBlock.Interface / GetService<PlcBlockInterface>）。\n" +
                "反射探测入口：1) block.Interface 属性；2) block.GetService<PlcBlockInterface>()。\n" +
                "按 Section（Input/Output/InOut/Static/Temp/Constant/Return）分组返回所有成员，\n" +
                "每个成员返回 Name/DataType/Comment 及全部可读属性（properties 字典）。\n" +
                "与 get_block_interface_v2 的区别：v2 通过 XML 解析；本工具通过强类型 API 直接读取成员对象属性。",
                Props(new Dictionary<string, object>
                {
                    ["blockName"] = StrProp("块名称（必填）"),
                    ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
                }, new[] { "blockName" }),
                args => tia.Value.GetBlockInterfaceTyped(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "plcName")));

            server.RegisterTool("list_block_interface_members",
                "列出接口成员（按 Section 分组返回成员名称与类型，轻量级）。\n" +
                "与 get_block_interface_typed 的区别：本工具仅返回 Name/DataType/TypeName，不返回全部属性，\n" +
                "适合快速浏览接口结构。",
                Props(new Dictionary<string, object>
                {
                    ["blockName"] = StrProp("块名称（必填）"),
                    ["plcName"] = StrProp("PLC 名称（可选）"),
                }, new[] { "blockName" }),
                args => tia.Value.ListBlockInterfaceMembers(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "plcName")));

            server.RegisterTool("get_block_interface_member",
                "获取单个接口成员详情（按 section + memberName 定位）。\n" +
                "section 为空时在所有 Section 中查找；指定时只在该 Section 中查找（大小写不敏感）。\n" +
                "未找到时 found=false。返回成员的 Name/DataType/Comment 及全部可读属性。",
                Props(new Dictionary<string, object>
                {
                    ["blockName"] = StrProp("块名称（必填）"),
                    ["memberName"] = StrProp("成员名称（必填）"),
                    ["section"] = StrProp("Section 名称（可选，如 Input/Output/InOut/Static/Temp/Constant/Return）"),
                    ["plcName"] = StrProp("PLC 名称（可选）"),
                }, new[] { "blockName", "memberName" }),
                args => tia.Value.GetBlockInterfaceMember(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "memberName") ?? "",
                    GetStringArg(args, "section"),
                    GetStringArg(args, "plcName")));

            server.RegisterTool("add_block_interface_member",
                "添加接口成员（反射调用 section.Create(name, dataType)）。\n" +
                "section 为 Input/Output/InOut/Static/Temp/Constant/Return 之一（大小写不敏感）。\n" +
                "dataType 为类型字符串；若 Section 集合的 Create 第二参数为枚举（如 PlcDataType），\n" +
                "内部反射解析枚举值（大小写不敏感），解析失败时返回 availableValues 可用值列表。\n" +
                "comment 为可选注释，创建后通过反射设置 Comment 属性。",
                Props(new Dictionary<string, object>
                {
                    ["blockName"] = StrProp("块名称（必填）"),
                    ["section"] = StrProp("Section 名称（必填，如 Input/Output/InOut/Static/Temp/Constant/Return）"),
                    ["memberName"] = StrProp("成员名称（必填）"),
                    ["dataType"] = StrProp("数据类型（必填，如 Bool/Int/Real/Time/String）"),
                    ["comment"] = StrProp("注释（可选）"),
                    ["plcName"] = StrProp("PLC 名称（可选）"),
                }, new[] { "blockName", "section", "memberName", "dataType" }),
                args => tia.Value.AddBlockInterfaceMember(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "section") ?? "",
                    GetStringArg(args, "memberName") ?? "",
                    GetStringArg(args, "dataType") ?? "",
                    GetStringArg(args, "comment"),
                    GetStringArg(args, "plcName")));

            server.RegisterTool("delete_block_interface_member",
                "删除接口成员（按 section + memberName 定位后调用 composition.Delete）。\n" +
                "section 为空时在所有 Section 中查找。\n" +
                "反射尝试 composition.Delete(item) / item.Delete() / composition.Remove(item)。\n" +
                "★注意★ 删除不可撤销。",
                Props(new Dictionary<string, object>
                {
                    ["blockName"] = StrProp("块名称（必填）"),
                    ["memberName"] = StrProp("成员名称（必填）"),
                    ["section"] = StrProp("Section 名称（可选，指定时仅在该 Section 中查找）"),
                    ["plcName"] = StrProp("PLC 名称（可选）"),
                }, new[] { "blockName", "memberName" }),
                args => tia.Value.DeleteBlockInterfaceMember(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "memberName") ?? "",
                    GetStringArg(args, "section"),
                    GetStringArg(args, "plcName")));

            // ═════════════════════════════════════════════════════════════════════════════
            // 三、SW.ExternalSources 完整 API（Siemens.Engineering.SW.ExternalSources）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_external_sources",
                "列出外部源（PlcSoftware.ExternalSourceGroup.ExternalSources 集合）。\n" +
                "返回每个外部源的 Name/TypeName/Type。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListExternalSources(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("list_external_source_groups",
                "列出外部源分组（PlcSoftware.ExternalSourceGroup.Groups 集合）。\n" +
                "返回每个分组的 Name/TypeName。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListExternalSourceGroups(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("create_external_source_group",
                "创建外部源分组（反射调用 Groups.Create(name)）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["groupName"] = StrProp("分组名称（必填）"),
                }, new[] { "plcName", "groupName" }),
                args => tia.Value.CreateExternalSourceGroup(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "groupName") ?? ""));

            server.RegisterTool("delete_external_source",
                "删除外部源（按名称查找后调用 composition.Delete）。\n" +
                "反射尝试 composition.Delete(item) / item.Delete() / composition.Remove(item)。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["sourceName"] = StrProp("外部源名称（必填）"),
                }, new[] { "plcName", "sourceName" }),
                args => tia.Value.DeleteExternalSource(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "sourceName") ?? ""));

            server.RegisterTool("generate_block_from_external_source",
                "从外部源生成块（反射调用 source.GenerateBlockFromSource / GenerateBlocksFromSource）。\n" +
                "优先调用 GenerateBlockFromSource()（单块返回），未暴露时回退到 GenerateBlocksFromSource()（多块返回）。\n" +
                "返回 invokedMethod 与生成的块列表（generatedBlocks）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["sourceName"] = StrProp("外部源名称（必填）"),
                }, new[] { "plcName", "sourceName" }),
                args => tia.Value.GenerateBlockFromExternalSource(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "sourceName") ?? ""));

            server.RegisterTool("generate_blocks_from_external_source",
                "批量从外部源生成块（对多个 sourceName 依次调用生成方法）。\n" +
                "对每个 sourceName 反射查找 GenerateBlocksFromSource() 或 GenerateBlockFromSource() 并调用，\n" +
                "perSource 返回每个源的处理结果，notFoundSources 列出未找到的源。\n" +
                "★用途★ 一次性从多个 SCL/STL 源文件批量生成块。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["sourceNames"] = ArrProp("外部源名称数组，如 [\"Source1\", \"Source2\"]", "string"),
                }, new[] { "plcName", "sourceNames" }),
                args =>
                {
                    var names = new List<string>();
                    var arr = args?["sourceNames"] as Newtonsoft.Json.Linq.JArray;
                    if (arr != null)
                        foreach (var item in arr) names.Add(item.ToString());
                    return tia.Value.GenerateBlocksFromExternalSource(
                        GetStringArg(args, "plcName") ?? "",
                        names);
                });

            server.RegisterTool("get_generate_source_options",
                "获取生成源选项（反射读取 ExternalSourceGroup.GenerateSourceOptions 属性）。\n" +
                "返回 optionsTypeName 与所有可读属性（options 字典）。\n" +
                "失败时返回 groupProperties 供 AI 探索 ExternalSourceGroup 可用属性。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.GetGenerateSourceOptions(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("set_generate_source_options",
                "设置生成源选项（反射设置 GenerateSourceOptions 上的可写属性）。\n" +
                "options 是 JSON 字典字符串，如 {\"OverwriteBlocks\":true,\"GenerateFromString\":false}。\n" +
                "反射设置每个键值对，类型不匹配时尝试 Convert.ChangeType。\n" +
                "返回 finalOptions（回读最终值）与 attempts（每个键的设置结果）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["options"] = StrProp("选项 JSON 字典字符串（如 {\"OverwriteBlocks\":true}）"),
                }, new[] { "plcName", "options" }),
                args =>
                {
                    var options = new Dictionary<string, string>();
                    var optionsStr = GetStringArg(args, "options");
                    if (!string.IsNullOrEmpty(optionsStr))
                    {
                        try
                        {
                            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(optionsStr!);
                            if (dict != null) options = dict;
                        }
                        catch
                        {
                            return "{\"success\":false,\"error\":\"options 格式错误，应为 JSON 字典字符串\"}";
                        }
                    }
                    return tia.Value.SetGenerateSourceOptions(
                        GetStringArg(args, "plcName") ?? "",
                        options);
                });

            // ═════════════════════════════════════════════════════════════════════════════
            // 四、SW.OpcUa 完整 API（Siemens.Engineering.SW.OpcUa）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_opcua_server_interfaces_full",
                "列出 OPC UA 服务器接口（完整版，包含 Enabled/Namespaces 等详情）。\n" +
                "反射获取 OpcUaProvider（plc.GetService<OpcUaProvider>()，候选命名空间 SW.OpcUa / OpcUa），\n" +
                "遍历 ServerInterfaces 集合返回 Name/Enabled/TypeName/Namespaces。\n" +
                "与 list_opcua_server_interfaces 的区别：完整版同时尝试 SW.OpcUa.OpcUaProvider 与 OpcUa.OpcUaProvider 两个候选命名空间。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListOpcUaServerInterfacesFull(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("list_opcua_communication_groups",
                "列出 OPC UA 通信组（OpcUaProvider.CommunicationGroups）。\n" +
                "返回每个通信组的 Name/TypeName 及全部可读属性（properties 字典）。\n" +
                "失败时返回 providerProperties 供 AI 探索 OpcUaProvider 可用属性。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListOpcUaCommunicationGroups(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("create_opcua_communication_group",
                "创建 OPC UA 通信组（反射调用 CommunicationGroups.Create(name)）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["groupName"] = StrProp("通信组名称（必填）"),
                }, new[] { "plcName", "groupName" }),
                args => tia.Value.CreateOpcUaCommunicationGroup(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "groupName") ?? ""));

            server.RegisterTool("list_opcua_server_interface_groups",
                "列出服务器接口组（OpcUaProvider.ServerInterfaceGroups）。\n" +
                "返回每个组的 Name/TypeName。\n" +
                "失败时返回 providerProperties 供 AI 探索 OpcUaProvider 可用属性。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListOpcUaServerInterfaceGroups(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("list_opcua_simatic_interfaces",
                "列出 SIMATIC 接口（OpcUaProvider.SimaticInterfaces）。\n" +
                "返回每个接口的 Name/TypeName。\n" +
                "失败时返回 providerProperties 供 AI 探索 OpcUaProvider 可用属性。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListOpcUaSimaticInterfaces(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("list_opcua_reference_namespaces",
                "列出引用命名空间（OpcUaProvider.ReferenceNamespaces）。\n" +
                "返回每个命名空间的 Name/TypeName 及全部可读属性（properties 字典）。\n" +
                "失败时返回 providerProperties 供 AI 探索 OpcUaProvider 可用属性。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListOpcUaReferenceNamespaces(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("create_opcua_reference_namespace",
                "创建引用命名空间（反射调用 ReferenceNamespaces.Create(name, uri) / Create(name)）。\n" +
                "提供 uri 时优先匹配 Create(string, string) 双参重载；未提供或重载不存在时回退到 Create(string) 单参重载。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["namespaceName"] = StrProp("命名空间名称（必填）"),
                    ["uri"] = StrProp("命名空间 URI（可选）"),
                }, new[] { "plcName", "namespaceName" }),
                args => tia.Value.CreateOpcUaReferenceNamespace(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "namespaceName") ?? "",
                    GetStringArg(args, "uri")));

            // ═════════════════════════════════════════════════════════════════════════════
            // 五、SW.Supervision 完整 API（Siemens.Engineering.SW.Supervision）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("get_supervision_settings",
                "获取监控设置（反射读取 SupervisionSettingsProvider.Settings 或 provider 上所有属性）。\n" +
                "反射获取 SupervisionSettingsProvider（plc.GetService<SupervisionSettingsProvider>()，\n" +
                "候选命名空间 SW.Supervision / Supervision），\n" +
                "返回 providerType/hasSettings/settings（Settings 对象所有属性）/providerProperties（provider 自身属性）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.GetSupervisionSettings(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("export_supervision_settings_xlsx",
                "导出监控设置到 XLSX（反射调用 provider.Export(FileInfo[, ExportOptions])）。\n" +
                "★用途★ 将 PLC 监控设置（连接监督、循环时间监控等）导出为 XLSX 文件便于备份或迁移。\n" +
                "失败时返回 providerProperties 供 AI 探索 SupervisionSettingsProvider 可用属性。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["filePath"] = StrProp("输出 XLSX 文件路径"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ExportSupervisionSettingsXlsx(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("import_supervision_settings_xlsx",
                "从 XLSX 导入监控设置（反射调用 provider.Import(FileInfo[, ImportOptions])）。\n" +
                "★用途★ 从备份的 XLSX 文件恢复 PLC 监控设置。\n" +
                "attempts 字段记录所有尝试过的方法与异常，便于诊断。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                    ["filePath"] = StrProp("要导入的 XLSX 文件路径（必填，文件必须存在）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ImportSupervisionSettingsXlsx(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("get_supervision_settings_provider",
                "获取监控设置提供者（仅返回入口点与可用属性/方法，用于 API 诊断）。\n" +
                "返回 providerType（完整类型名）/properties（所有属性值）/methods（所有公开方法签名）。\n" +
                "★用途★ 当 SupervisionSettingsProvider API 在不同 TIA 版本签名不确定时，\n" +
                "用本工具探索可用属性与方法，再决定调用方式。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.GetSupervisionSettingsProvider(GetStringArg(args, "plcName") ?? ""));
        }
    }
}
