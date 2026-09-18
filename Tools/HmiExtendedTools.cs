using System;
using System.Collections.Generic;

namespace TiaMcpServer
{
    /// <summary>
    /// 传统 HMI 扩展模块工具注册（VBScript/Faceplate/Cycle/Globalization/TextGraphicList）。
    /// <para>本文件为 partial class McpServer，与 HmiTools.cs 共享 Schema 辅助方法。</para>
    /// <para>注册方法 <see cref="RegisterHmiExtendedTools"/> 需在 McpServer.RegisterAllTools 中显式调用，
    /// 因任务要求不修改 McpServer.cs，需由用户在 RegisterAllTools 末尾添加：
    /// <code>RegisterHmiExtendedTools(this, _tia);</code>
    /// 才能激活以下工具。</para>
    /// <para>注意：create_hmi_text_list / delete_hmi_text_list / list_hmi_graphic_lists /
    /// create_hmi_graphic_list / delete_hmi_graphic_list 与 HmiTools.cs 中已有同名工具重复，
    /// 注册顺序在 RegisterHmiTools 之后会覆盖基础版本（增强版支持 entries 数组）。</para>
    /// </summary>
    public partial class McpServer
    {
        internal static void RegisterHmiExtendedTools(McpServer server, Lazy<PortalService> tia)
        {
            // ═════════════════════════════════════════════════════════════════════════════
            // 1. VBScript 脚本管理（Siemens.Engineering.Hmi.RuntimeScripting）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_hmi_vbscripts",
                "列出传统 HMI 中所有 VBScript（递归遍历 VBScriptFolder 子文件夹）。\n" +
                "返回每个脚本的名称、所在文件夹路径、类型名。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: HMI 设备名（可选，多 HMI 时指定）\n" +
                "返回：success/hmiDeviceName/count/vbScripts[{name,folder,typeName}]",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选，多 HMI 时指定）"),
                }),
                args => tia.Value.ListHmiVbScripts(
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("list_vbscript_folders",
                "列出 VBScript 根文件夹下的子文件夹。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/count/folders[{name,typeName}]",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }),
                args => tia.Value.ListVbScriptFolders(
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_vbscript_folder",
                "创建 VBScript 子文件夹（在 VBScriptFolder.Folders 中 Create(name)）。\n" +
                "参数说明：\n" +
                "  name: 文件夹名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/folderName/hmiDeviceName",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("文件夹名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateVbScriptFolder(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("delete_vbscript_folder",
                "删除 VBScript 子文件夹。\n" +
                "参数说明：\n" +
                "  name: 文件夹名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("文件夹名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.DeleteVbScriptFolder(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("read_vbscript_content",
                "读取 VBScript 内容。\n" +
                "先尝试读 Content/Source 属性，失败则用 Export 导出 XML 后返回。\n" +
                "参数说明：\n" +
                "  name: VBScript 名称（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/name/hmiDeviceName/content/exportXml/note",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("VBScript 名称（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.ReadVbScriptContent(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_vbscript",
                "创建 VBScript（在 VBScriptFolder.VBScripts 中创建）。\n" +
                "注意：VBScriptComposition 仅有 CreateFrom(libraryTypeVersion/masterCopy)，无 Create(name)。\n" +
                "本方法先尝试反射 Create(name)，失败则用现有脚本作模板导出→改名→导入。\n" +
                "若两种方式都失败，返回 note 引导用户在博途 GUI 手动创建或从库导入。\n" +
                "参数说明：\n" +
                "  name: VBScript 名称（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/name/hmiDeviceName/note（失败时含可用方式说明）",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("VBScript 名称（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateVbScript(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("delete_vbscript",
                "删除 VBScript（递归查找后 Delete）。\n" +
                "参数说明：\n" +
                "  name: VBScript 名称（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("VBScript 名称（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.DeleteVbScript(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            // ═════════════════════════════════════════════════════════════════════════════
            // 2. Faceplate 面板库（Siemens.Engineering.Hmi.Faceplate）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_hmi_faceplate_types",
                "列出 HMI 面板库类型（FaceplateLibraryType）。\n" +
                "通过反射在 HmiTarget 上查找 FaceplateLibraryTypes 属性，未找到时返回 exploredProperties。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/count/faceplateTypes[{name,author,guid,namespaceName,typeName}]；" +
                "失败时返回 exploredProperties 供调试",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }),
                args => tia.Value.ListHmiFaceplateTypes(
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("get_faceplate_type_info",
                "获取面板类型信息（属性详情）。\n" +
                "参数说明：\n" +
                "  name: 面板类型名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/faceplateType{name,author,guid,namespaceName,status,...}/allProperties",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("面板类型名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.GetFaceplateTypeInfo(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("get_faceplate_type_versions",
                "获取面板类型版本列表（Versions 集合）。\n" +
                "参数说明：\n" +
                "  name: 面板类型名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/faceplateTypeName/count/versions[{versionNumber,author,guid,isDefault,modifiedDate,state}]",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("面板类型名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.GetFaceplateTypeVersions(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            // ═════════════════════════════════════════════════════════════════════════════
            // 3. Cycle 采集周期（Siemens.Engineering.Hmi.Cycle）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_hmi_cycles",
                "列出 HMI 采集周期（Cycle）。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/count/cycles[{name,isSystemObject,typeName}]",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }),
                args => tia.Value.ListHmiCycles(
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_hmi_cycle",
                "创建采集周期。\n" +
                "注意：CycleComposition 仅有 Import/Find，无 Create(name)。\n" +
                "本方法先尝试反射 Create(name)，失败则用现有 Cycle 作模板导出→改名→导入。\n" +
                "参数说明：\n" +
                "  name: 采集周期名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/name/hmiDeviceName/note（失败时含说明）",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("采集周期名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateHmiCycle(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("delete_hmi_cycle",
                "删除采集周期。\n" +
                "参数说明：\n" +
                "  name: 采集周期名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("采集周期名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.DeleteHmiCycle(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            // ═════════════════════════════════════════════════════════════════════════════
            // 4. Globalization 多语言图形（Siemens.Engineering.Hmi.Globalization）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_multilingual_graphics",
                "列出多语言图形（MultiLingualGraphic）。\n" +
                "通过反射在 HmiTarget 上查找 MultilingualGraphics 属性，未找到时返回 exploredProperties。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/count/multilingualGraphics[{name,typeName}]；" +
                "失败时返回 exploredProperties 供调试",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }),
                args => tia.Value.ListMultilingualGraphics(
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_multilingual_graphic",
                "创建多语言图形。\n" +
                "注意：MultiLingualGraphicComposition 仅有 Import/Find，无 Create(name)。\n" +
                "本方法先尝试反射 Create(name)，失败则用现有对象作模板导出→改名→导入。\n" +
                "参数说明：\n" +
                "  name: 多语言图形名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/name/hmiDeviceName/note（失败时含说明）",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("多语言图形名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateMultilingualGraphic(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("delete_multilingual_graphic",
                "删除多语言图形。\n" +
                "参数说明：\n" +
                "  name: 多语言图形名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("多语言图形名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.DeleteMultilingualGraphic(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            // ═════════════════════════════════════════════════════════════════════════════
            // 5. TextGraphicList 文本图形列表（Siemens.Engineering.Hmi.TextGraphicList）
            // 注意：以下 5 个工具名与 HmiTools.cs 中已有同名工具重复，
            // 注册顺序在 RegisterHmiTools 之后会覆盖基础版本（增强版支持 entries 数组）。
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("list_hmi_text_lists_full",
                "列出文本列表（完整版：含条目数 entryCount）。\n" +
                "与基础版 list_hmi_text_lists 区别：本版本额外返回每个文本列表的条目数。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/count/textLists[{name,entryCount,typeName}]",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }),
                args => tia.Value.ListHmiTextListsFull(
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_hmi_text_list",
                "创建文本列表（增强版：支持 entries 数组参数）。\n" +
                "覆盖 HmiTools.cs 中的基础版本。\n" +
                "注意：TextListComposition 仅有 Import/Find，无 Create(name)。\n" +
                "本方法先尝试反射 Create(name)，失败则用现有 TextList 作模板导出→改名→导入。\n" +
                "entries 数组可选，每项 {value, text}；条目添加依赖 Openness API 是否暴露 Entries 集合。\n" +
                "参数说明：\n" +
                "  name: 文本列表名（必填）\n" +
                "  entries: 条目 JSON 数组字符串（可选，例: [{\"value\":\"0\",\"text\":\"停止\"},{\"value\":\"1\",\"text\":\"运行\"}]）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/name/hmiDeviceName/entriesAdded/entriesErrors/note",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("文本列表名（必填）"),
                    ["entries"] = StrProp("条目 JSON 数组字符串（可选，每项 {value,text}）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateHmiTextListFull(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "entries"),
                    GetStringArg(args, "hmiDeviceName")));
        // ── 通用 HMI 对象导航与创建（2026-08-30 新增，解锁 TextList 条目等未公开对象）──
        server.RegisterTool("hmi_object_creation_infos",
            "列出 HMI 对象指定组合(Entries/Items/Text等)的可创建类型与必需参数。path 形如 'TextLists/=模式文本'：段为组合名或 '=名称'(Find)。",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "对象导航路径，如 TextLists/=模式文本" },
                    compositionName = new { type = "string", description = "组合名，如 Entries" },
                    hmiDeviceName = new { type = "string", description = "HMI 设备名（可选）" }
                },
                required = new[] { "path" }
            },
            args => tia.Value.HmiObjectCreationInfos(
                args?["path"]?.ToString() ?? "",
                args?["compositionName"]?.ToString(),
                args?["hmiDeviceName"]?.ToString()),
            new ToolMetadata
            {
                OriginalName = "hmi_object_creation_infos", RiskLevel = ToolRiskLevel.Low,
                ReadOnly = true, Idempotent = true, Scope = ToolOperationScope.Project
            });

        server.RegisterTool("hmi_object_create",
            "在 HMI 对象的组合下创建子对象（通用 IEngineeringObject.Create）。attrsJson 为属性字典。失败时返回 creationInfos 供诊断。",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "父对象导航路径" },
                    compositionName = new { type = "string", description = "组合名，如 Entries" },
                    name = new { type = "string", description = "新对象名称" },
                    typeName = new { type = "string", description = "类型全名或短名（可选）" },
                    attrsJson = new { type = "string", description = "属性 JSON 字典（可选）" },
                    hmiDeviceName = new { type = "string", description = "HMI 设备名（可选）" }
                },
                required = new[] { "path", "compositionName", "name" }
            },
            args => tia.Value.HmiObjectCreate(
                args?["path"]?.ToString() ?? "",
                args?["compositionName"]?.ToString() ?? "",
                args?["name"]?.ToString() ?? "",
                args?["typeName"]?.ToString(),
                args?["attrsJson"]?.ToString(),
                args?["hmiDeviceName"]?.ToString()),
            new ToolMetadata
            {
                OriginalName = "hmi_object_create", RiskLevel = ToolRiskLevel.Medium,
                ReadOnly = false, Idempotent = false, MutatesProject = true, Scope = ToolOperationScope.Project
            });


            server.RegisterTool("delete_hmi_text_list",
                "删除文本列表。\n" +
                "覆盖 HmiTools.cs 中的基础版本（功能等价）。\n" +
                "参数说明：\n" +
                "  name: 文本列表名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("文本列表名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.DeleteHmiTextListFull(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("export_hmi_text_list",
                "导出文本列表 XML 到文件（用于学习类型 ValueListMode 与条目结构）。\n" +
                "参数说明：\n" +
                "  name: 文本列表名（必填）\n" +
                "  outputPath: 输出 XML 文件路径（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/outputPath",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("文本列表名（必填）"),
                    ["outputPath"] = StrProp("输出路径（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name", "outputPath" }),
                args => tia.Value.ExportHmiTextListFull(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "outputPath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("import_hmi_text_list",
                "从 XML 文件导入文本列表（TIA 标准格式，含 ListRange 类型与 TextListEntry 条目）。\n" +
                "同名列表会被覆盖。\n" +
                "参数说明：\n" +
                "  filePath: XML 文件路径（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message",
                Props(new Dictionary<string, object>
                {
                    ["filePath"] = StrProp("XML 文件路径（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "filePath" }),
                args => tia.Value.ImportHmiTextListFull(
                    GetStringArg(args, "filePath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("list_hmi_graphic_lists",
                "列出图形列表（完整版：含条目数 entryCount）。\n" +
                "覆盖 HmiTools.cs 中的基础版本。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/count/graphicLists[{name,entryCount,typeName}]",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }),
                args => tia.Value.ListHmiGraphicListsFull(
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_hmi_graphic_list",
                "创建图形列表（增强版：支持 entries 数组参数）。\n" +
                "覆盖 HmiTools.cs 中的基础版本。\n" +
                "注意：GraphicListComposition 仅有 Import/Find，无 Create(name)。\n" +
                "本方法先尝试反射 Create(name)，失败则用现有 GraphicList 作模板导出→改名→导入。\n" +
                "entries 数组可选，每项 {value, graphicName}；条目添加依赖 Openness API 是否暴露 Entries 集合。\n" +
                "参数说明：\n" +
                "  name: 图形列表名（必填）\n" +
                "  entries: 条目 JSON 数组字符串（可选，每项 {value,graphicName}）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/name/hmiDeviceName/entriesAdded/entriesErrors/note",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("图形列表名（必填）"),
                    ["entries"] = StrProp("条目 JSON 数组字符串（可选，每项 {value,graphicName}）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateHmiGraphicListFull(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "entries"),
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("delete_hmi_graphic_list",
                "删除图形列表。\n" +
                "覆盖 HmiTools.cs 中的基础版本（功能等价）。\n" +
                "参数说明：\n" +
                "  name: 图形列表名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("图形列表名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.DeleteHmiGraphicListFull(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("get_hmi_text_list_entries",
                "获取文本列表条目（反射访问 Entries/Items 集合）。\n" +
                "返回每个条目的 value/text/name/typeName。\n" +
                "若 Openness API 未暴露 Entries 集合，返回空列表与提示。\n" +
                "参数说明：\n" +
                "  name: 文本列表名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/hmiDeviceName/textListName/count/entries[{value,text,name,typeName}]/note",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("文本列表名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.GetHmiTextListEntries(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));
        }
    }
}
