using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// 经典 HMI 全操作工具注册（任务 5.4）。
    /// </summary>
    public partial class McpServer
    {
        internal static void RegisterHmiTools(McpServer server, Lazy<PortalService> tia)
        {
            server.RegisterTool("get_hmi_targets",
                "列出项目内所有 HMI 目标。",
                EmptySchema(),
                _ => tia.Value.GetHmiTargets());

            server.RegisterTool("diagnose_classic_hmi",
                "诊断经典 HMI 设备（名称、类型、画面数、变量数等）。",
                EmptySchema(),
                _ => tia.Value.DiagnoseClassicHmi());

            // ── 画面 ──────────────────────────────────────────────
            server.RegisterTool("list_hmi_screens",
                "列出经典 HMI 下的所有画面（递归子文件夹）。",
                EmptySchema(),
                _ => tia.Value.ListHmiScreens());

            server.RegisterTool("create_hmi_screen",
                "在根画面目录下创建一个新画面。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("画面名称"),
                }, new[] { "name" }),
                args => tia.Value.CreateHmiScreen(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("delete_hmi_screen",
                "删除一个画面（按名称递归查找）。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("画面名称"),
                }, new[] { "name" }),
                args => tia.Value.DeleteHmiScreen(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("export_hmi_screen",
                "导出画面 XML 到文件。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                    ["outputPath"] = StrProp("输出 XML 路径"),
                }, new[] { "screenName", "outputPath" }),
                args => tia.Value.ExportHmiScreen(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "outputPath") ?? ""));

            server.RegisterTool("import_hmi_screen",
                "导入画面 XML 文件。target 支持 screen（默认，常规画面区）/ template（模板画面）/ popup（弹出画面）/ slidein（滑入画面）。\n" +
                "★fix#18★ 模板画面悬空引用清洗流程：export_hmi_screen 导出模板 → 清洗 XML → import_hmi_screen(target=\"template\") 覆盖导回。",
                Props(new Dictionary<string, object>
                {
                    ["filePath"] = StrProp("XML 文件绝对路径"),
                    ["target"] = StrProp("导入目标（可选）：screen/template/popup/slidein，默认 screen"),
                }, new[] { "filePath" }),
                args => tia.Value.ImportHmiScreen(
                    GetStringArg(args, "filePath") ?? "",
                    GetStringArg(args, "target")));

            server.RegisterTool("read_hmi_screen",
                "读取画面的完整 XML。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                }, new[] { "screenName" }),
                args => tia.Value.ReadHmiScreen(
                    GetStringArg(args, "screenName") ?? ""));

            // ── 画面组 ────────────────────────────────────────────
            server.RegisterTool("list_hmi_screen_groups",
                "列出画面组（根画面目录的子文件夹）。",
                EmptySchema(),
                _ => tia.Value.ListHmiScreenGroups());

            server.RegisterTool("create_hmi_screen_group",
                "创建一个画面组。",
                Props(new Dictionary<string, object>
                {
                    ["groupName"] = StrProp("画面组名"),
                }, new[] { "groupName" }),
                args => tia.Value.CreateHmiScreenGroup(
                    GetStringArg(args, "groupName") ?? ""));

            server.RegisterTool("delete_hmi_screen_group",
                "删除一个画面组。",
                Props(new Dictionary<string, object>
                {
                    ["groupName"] = StrProp("画面组名"),
                }, new[] { "groupName" }),
                args => tia.Value.DeleteHmiScreenGroup(
                    GetStringArg(args, "groupName") ?? ""));

            // ── 画面项 ────────────────────────────────────────────
            server.RegisterTool("list_hmi_screen_items",
                "列出画面内所有画面项（通过 Layers[0].ScreenItems 访问）。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                }, new[] { "screenName" }),
                args => tia.Value.ListHmiScreenItems(
                    GetStringArg(args, "screenName") ?? ""));

            server.RegisterTool("diagnose_screen",
                "诊断画面对象结构：列出 Screen 的所有属性和 Layers 的结构（调试用）。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                }, new[] { "screenName" }),
                args => tia.Value.DiagnoseScreen(
                    GetStringArg(args, "screenName") ?? ""));

            server.RegisterTool("create_hmi_screen_item",
                "在画面上创建画面项（XML 往返方式）。支持的类型：button、textfield、iofield、rectangle、switch、circle、gauge、bar、graphicview。可通过 left/top/width/height 指定坐标和尺寸（均为可选，未传时使用各类型默认值：button等=100x30，circle=80x80，gauge=150x150，bar=150x200，graphicview=100x100，默认位置均为 50,50）。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                    ["itemType"] = StrProp("画面项类型：button、textfield、iofield、rectangle、switch、circle、gauge、bar、graphicview"),
                    ["name"] = StrProp("画面项名称"),
                    ["tagName"] = StrProp("绑定的变量名（可选）"),
                    ["left"] = IntProp("X 坐标（可选，默认 50）"),
                    ["top"] = IntProp("Y 坐标（可选，默认 50）"),
                    ["width"] = IntProp("宽度（可选，按类型默认）"),
                    ["height"] = IntProp("高度（可选，按类型默认）"),
                }, new[] { "screenName", "itemType", "name" }),
                args => tia.Value.CreateHmiScreenItem(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "itemType") ?? "",
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "tagName"),
                    GetIntArg(args, "left"),
                    GetIntArg(args, "top"),
                    GetIntArg(args, "width"),
                    GetIntArg(args, "height")));

            server.RegisterTool("delete_hmi_screen_item",
                "删除画面项。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                    ["itemName"] = StrProp("画面项名称"),
                }, new[] { "screenName", "itemName" }),
                args => tia.Value.DeleteHmiScreenItem(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "itemName") ?? ""));

            server.RegisterTool("set_hmi_screen_item_property",
                "设置画面项的某个属性（反射 setProperty）。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                    ["itemName"] = StrProp("画面项名称"),
                    ["propertyName"] = StrProp("属性名"),
                    ["value"] = StrProp("属性值"),
                }, new[] { "screenName", "itemName", "propertyName", "value" }),
                args => tia.Value.SetHmiScreenItemProperty(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "itemName") ?? "",
                    GetStringArg(args, "propertyName") ?? "",
                    GetStringArg(args, "value") ?? ""));

            server.RegisterTool("bind_hmi_screen_item_tag",
                "把画面项绑定到 HMI 变量。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                    ["itemName"] = StrProp("画面项名称"),
                    ["tagName"] = StrProp("HMI 变量名"),
                }, new[] { "screenName", "itemName", "tagName" }),
                args => tia.Value.BindHmiScreenItemTag(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "itemName") ?? "",
                    GetStringArg(args, "tagName") ?? ""));

            server.RegisterTool("explore_hmi_screen_item",
                "反射显示画面项的所有可读属性及当前值（调试用）。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称"),
                    ["itemName"] = StrProp("画面项名称"),
                }, new[] { "screenName", "itemName" }),
                args => tia.Value.ExploreHmiScreenItem(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "itemName") ?? ""));

            // ── 变量 / 变量表 ──────────────────────────────────────
            server.RegisterTool("list_hmi_tags",
                "列出经典 HMI 的所有变量。",
                EmptySchema(),
                _ => tia.Value.ListHmiTags());

            server.RegisterTool("list_hmi_tag_tables",
                "列出经典 HMI 的所有变量表。",
                EmptySchema(),
                _ => tia.Value.ListHmiTagTables());

            server.RegisterTool("create_hmi_tag",
                "创建一个 Classic HMI 变量。统一走 XML 导入并在导入后验证，避免反射接口静默假成功；address 与 plcTag 二选一。",
                Props(new Dictionary<string, object>
                {
                    ["tagName"] = StrProp("HMI 变量名"),
                    ["tagTableName"] = StrProp("变量表名（可选，默认 DefaultTagTable）"),
                    ["connection"] = StrProp("连接名（可选）"),
                    ["plcTag"] = StrProp("对应的 PLC 变量名（可选）"),
                    ["address"] = StrProp("PLC 绝对地址（可选，如 %M0.0 / %MW10 / DB1.DBW0）"),
                    ["dataType"] = StrProp("数据类型（可选，默认 Bool；如 Bool/Int/DInt/Real/String）"),
                }, new[] { "tagName" }),
                args => tia.Value.CreateHmiTag(
                    GetStringArg(args, "tagName") ?? "",
                    GetStringArg(args, "tagTableName") ?? "",
                    GetStringArg(args, "connection"),
                    GetStringArg(args, "plcTag"),
                    GetStringArg(args, "address"),
                    GetStringArg(args, "dataType")));

            server.RegisterTool("delete_hmi_tag",
                "删除一个 HMI 变量。",
                Props(new Dictionary<string, object>
                {
                    ["tagName"] = StrProp("HMI 变量名"),
                }, new[] { "tagName" }),
                args => tia.Value.DeleteHmiTag(
                    GetStringArg(args, "tagName") ?? ""));

            server.RegisterTool("bind_hmi_tag_to_plc",
                "把 HMI 变量绑定到 PLC 变量。",
                Props(new Dictionary<string, object>
                {
                    ["hmiTagName"] = StrProp("HMI 变量名"),
                    ["plcTagName"] = StrProp("PLC 变量名"),
                    ["connectionName"] = StrProp("连接名（可选）"),
                }, new[] { "hmiTagName", "plcTagName" }),
                args => tia.Value.BindHmiTagToPlc(
                    GetStringArg(args, "hmiTagName") ?? "",
                    GetStringArg(args, "plcTagName") ?? "",
                    GetStringArg(args, "connectionName")));

            // ── 变量表组 ──────────────────────────────────────────
            server.RegisterTool("list_hmi_tag_table_groups",
                "列出 HMI 变量表组（TagFolder 子文件夹）。",
                EmptySchema(),
                _ => tia.Value.ListHmiTagTableGroups());

            server.RegisterTool("create_hmi_tag_table_group",
                "创建一个 HMI 变量表组。",
                Props(new Dictionary<string, object>
                {
                    ["groupName"] = StrProp("组名"),
                }, new[] { "groupName" }),
                args => tia.Value.CreateHmiTagTableGroup(
                    GetStringArg(args, "groupName") ?? ""));

            server.RegisterTool("delete_hmi_tag_table_group",
                "删除一个 HMI 变量表组。",
                Props(new Dictionary<string, object>
                {
                    ["groupName"] = StrProp("组名"),
                }, new[] { "groupName" }),
                args => tia.Value.DeleteHmiTagTableGroup(
                    GetStringArg(args, "groupName") ?? ""));

            // ── 连接 ──────────────────────────────────────────────
            // Classic HMI 连接分为两类：
            //   - integrated：通过 PLC/HMI 接入同一 PN/IE 子网并配置同网段 IP 生成
            //   - nonIntegrated：由 HmiTarget.Connections 暴露，可使用导入/导出 XML
            // integrated 模式严禁导入连接 XML。
            server.RegisterTool("list_hmi_connections",
                "列出 HMI 连接及其所有可读属性（通过强类型 GetAttributeInfos + GetAttribute）。\n" +
                "返回每个连接的 Name、属性值（Driver 等）和属性元数据（AccessMode、SupportedTypes）。\n" +
                "若集合为空（已配置连接也可能为空，是 Openness API 已知限制），返回限制说明。",
                EmptySchema(),
                _ => tia.Value.ListHmiConnections());

            server.RegisterTool("create_hmi_connection",
                "创建 Classic HMI 连接。必须明确 connectionMode：\n" +
                "- integrated：同一项目内 PLC 与 HMI 接入同一 PN/IE 子网并配置同网段 IP，由 TIA 生成默认集成连接。\n" +
                "- nonIntegrated：面向外部 PLC 地址的非集成连接，可通过 HmiTarget.Connections 导入/导出。\n" +
                "注意：集成连接 XML 根本无法导出，不要把 templateFilePath 用于 integrated 模式。",
                Props(new Dictionary<string, object>
                {
                    ["connectionName"] = StrProp("连接名（必填）"),
                    ["connectionMode"] = StrProp("integrated 或 nonIntegrated；默认有 plcName 时推断 integrated，否则 nonIntegrated"),
                    ["driver"] = StrProp("通信驱动（非集成连接可选，默认 SIMATIC S7 1200）"),
                    ["plcName"] = StrProp("项目内 PLC 设备名；integrated 模式必填，用于网络组态"),
                    ["hmiIp"] = StrProp("HMI IP（非集成模板占位符，可选）"),
                    ["plcIp"] = StrProp("外部 PLC IP（nonIntegrated 模式）"),
                    ["subnetMask"] = StrProp("子网掩码（非集成模板占位符，可选）"),
                    ["subnetName"] = StrProp("子网名（非集成模板占位符，可选）"),
                    ["templateFilePath"] = StrProp("非集成连接 XML 模板路径；integrated 模式禁止使用"),
                    ["hmiDeviceName"] = StrProp("目标 Classic HMI 设备名（多 HMI 项目建议指定）"),
                }, new[] { "connectionName" }),
                args => tia.Value.CreateHmiConnection(
                    GetStringArg(args, "connectionName") ?? "",
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "hmiIp"),
                    GetStringArg(args, "plcIp"),
                    GetStringArg(args, "subnetMask"),
                    GetStringArg(args, "subnetName"),
                    GetStringArg(args, "driver"),
                    GetStringArg(args, "templateFilePath"),
                    GetStringArg(args, "hmiDeviceName"),
                    GetStringArg(args, "connectionMode")));

            server.RegisterTool("create_unified_hmi_connection",
                "为 WinCC Unified HMI 设备创建连接（仅适用于 Unified HMI，传统 HMI 请使用 create_hmi_connection）。\n" +
                "实现：通过 Siemens.Engineering.HmiUnified.HmiSoftware.Connections.Create(name) 强类型 API 创建 HmiConnection，\n" +
                "      然后用 SetAttribute 设置 CommunicationDriver/Partner/Station/Node 等属性。\n" +
                "参数说明：\n" +
                "  hmiDeviceName: Unified HMI 设备名（必填）\n" +
                "  connectionName: 连接名（必填）\n" +
                "  plcDeviceName: 关联的 PLC 设备名（必填）\n" +
                "  connectionType: 通信驱动类型（可选，默认 SIMATIC S7-1200）\n" +
                "返回：success/connectionName/hmiDeviceName/plcDeviceName/properties/apiExplored/warning\n" +
                "示例：\n" +
                "  {\"hmiDeviceName\": \"HMI_1\", \"connectionName\": \"Connection_1\", \"plcDeviceName\": \"PLC_1\"}",
                Props(new Dictionary<string, object>
                {
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（必填）"),
                    ["connectionName"] = StrProp("连接名（必填）"),
                    ["plcDeviceName"] = StrProp("关联的 PLC 设备名（必填）"),
                    ["connectionType"] = StrProp("通信驱动类型（可选，默认 SIMATIC S7-1200）"),
                }, new[] { "hmiDeviceName", "connectionName", "plcDeviceName" }),
                args => tia.Value.CreateUnifiedHmiConnection(
                    GetStringArg(args, "hmiDeviceName") ?? "",
                    GetStringArg(args, "connectionName") ?? "",
                    GetStringArg(args, "plcDeviceName") ?? "",
                    GetStringArg(args, "connectionType")));

            server.RegisterTool("delete_hmi_connection",
                "删除一个 HMI 连接（通过强类型 Connection.Delete）。",
                Props(new Dictionary<string, object>
                {
                    ["connectionName"] = StrProp("连接名"),
                }, new[] { "connectionName" }),
                args => tia.Value.DeleteHmiConnection(
                    GetStringArg(args, "connectionName") ?? ""));

            server.RegisterTool("probe_hmi_assemblies",
                "★V17探测★ 反射 Siemens.Engineering.Hmi 程序集：列出所有名字含 Connection 的类型与公开方法，及 HmiTarget 自身连接相关成员。用于寻找连接创建入口。",
                EmptySchema(),
                _ => tia.Value.ProbeHmiAssemblies());

            server.RegisterTool("get_hmi_tag_table_xml",
                "同步导出默认 HMI 变量表 XML（含 Connection/LogicalAddress/ControllerTag 等属性），用于验证变量绑定。get_ 前缀保持同步执行。",
                EmptySchema(),
                _ => tia.Value.GetHmiTagTableXml());

            server.RegisterTool("create_hmi_connection_minimal",
                "★V17实验★ 用最小 XML（Name/Driver/InterfaceType/Online + 11 个 AreaPointer）向 Connections.Import 导入非集成连接，成功后返回连接属性元数据。用于绕过 Openness 无 Create 的限制。注意：本工具名以 create_ 开头以保持同步执行（import_ 前缀会被归类为异步长任务）。",
                Props(new Dictionary<string, object>
                {
                    ["connectionName"] = StrProp("连接名（必填）"),
                    ["driver"] = StrProp("通信驱动（默认 SIMATIC S7 1200）"),
                    ["interfaceTypeMode"] = EnumStrProp("Ethernet / None / PNIE(InterfaceType=PN/IE)", "Ethernet", "None", "PNIE"),
                }, new[] { "connectionName" }),
                args => tia.Value.ImportHmiConnectionMinimal(
                    GetStringArg(args, "connectionName") ?? "",
                    GetStringArg(args, "driver"),
                    GetStringArg(args, "interfaceTypeMode")));

            server.RegisterTool("probe_hmi_connection_api",
                "★V17探测★ 反射 HmiTarget.Connections 组合对象：列出方法/属性、尝试 Create(连接名)。用于判定当前博途版本能否通过 Openness 直接创建 HMI 连接。",
                Props(new Dictionary<string, object>
                {
                    ["createName"] = StrProp("可选：尝试创建的连接名（默认 HMI_连接_1）；不传也做方法盘点"),
                }, Array.Empty<string>()),
                args => tia.Value.ProbeHmiConnectionApi(
                    GetStringArg(args, "createName")));

            server.RegisterTool("export_hmi_connection",
                "仅导出 Classic HMI 的非集成连接。\n" +
                "同一项目内 HMI↔PLC 的集成连接不在 HmiTarget.Connections 中暴露，西门子明确不支持导出；遇到集成连接时会返回 HMI_CONNECTION_NOT_EXPORTABLE，而不是生成伪 XML。",
                Props(new Dictionary<string, object>
                {
                    ["connectionName"] = StrProp("非集成连接名（必填）"),
                    ["outputPath"] = StrProp("输出 XML 文件路径（必填）"),
                }, new[] { "connectionName", "outputPath" }),
                args => tia.Value.ExportHmiConnection(
                    GetStringArg(args, "connectionName") ?? "",
                    GetStringArg(args, "outputPath") ?? ""));

            server.RegisterTool("find_hmi_object",
                "用 IEngineeringObject.Children 递归遍历 HmiTarget 整个对象树，查找名为 objectName 的对象，\n" +
                "返回其类型、路径、所有简单属性。用于定位 HMI 连接等在 Openness API 中隐藏的对象。\n" +
                "示例：\n" +
                "  {\"objectName\": \"HMI_连接_1\"}\n" +
                "可选 pattern：忽略大小写的子串匹配（如 \"连接\" 或 \"Connection\"），配合 maxDepth/maxResults 做对象树盘点。",
                Props(new Dictionary<string, object>
                {
                    ["objectName"] = StrProp("要查找的对象名（必填，pattern 模式时仅作回显）"),
                    ["pattern"] = StrProp("可选：子串匹配模式（忽略大小写），与 objectName 二选一优先"),
                    ["maxDepth"] = IntProp("可选：递归深度，默认 10，最大 24"),
                    ["maxResults"] = IntProp("可选：结果上限，默认 500"),
                }, new[] { "objectName" }),
                args => tia.Value.FindHmiObjectByName(
                    GetStringArg(args, "objectName") ?? "",
                    GetStringArg(args, "pattern"),
                    GetIntArg(args, "maxDepth") ?? 10,
                    GetIntArg(args, "maxResults") ?? 500));

            // ── 变量表导出/导入 ─────────────────────────────────────
            server.RegisterTool("export_hmi_tag_table",
                "导出 HMI 变量表到 XML 文件。",
                Props(new Dictionary<string, object>
                {
                    ["tagTableName"] = StrProp("变量表名（可选，默认 DefaultTagTable）"),
                    ["outputPath"] = StrProp("输出 XML 文件路径"),
                }, new[] { "outputPath" }),
                args => tia.Value.ExportHmiTagTable(
                    GetStringArg(args, "tagTableName") ?? "",
                    GetStringArg(args, "outputPath") ?? ""));

            server.RegisterTool("import_hmi_tag_table",
                "从 XML 文件导入 HMI 变量表。",
                Props(new Dictionary<string, object>
                {
                    ["filePath"] = StrProp("XML 文件绝对路径"),
                }, new[] { "filePath" }),
                args => tia.Value.ImportHmiTagTable(
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("batch_create_hmi_tags",
                "批量创建 HMI 变量。Absolute 模式使用 address，Symbolic 模式使用 plcTag；两种模式不可在同一批次混合，导入后会验证变量是否实际存在。",
                Props(new Dictionary<string, object>
                {
                    ["tagsJson"] = StrProp("JSON 数组，每个元素: {tagName, address, dataType, connection}。例: [{\"tagName\":\"Start\",\"address\":\"%I0.0\",\"dataType\":\"Bool\"}]"),
                }, new[] { "tagsJson" }),
                args => tia.Value.BatchCreateHmiTags(
                    GetStringArg(args, "tagsJson") ?? "[]"));

            // ── 运行系统设置 ───────────────────────────────────────
            server.RegisterTool("set_hmi_start_screen",
                "设置 HMI 起始画面（运行系统设置 → 常规 → 画面选项 → 起始画面）。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("起始画面名称，如 Main"),
                }, new[] { "screenName" }),
                args => tia.Value.SetHmiStartScreen(
                    GetStringArg(args, "screenName") ?? ""));

            // ── 报警 / 列表 / 日志（反射） ─────────────────────────
            server.RegisterTool("list_alarm_classes",
                "列出报警类别。",
                EmptySchema(),
                _ => tia.Value.ListAlarmClasses());

            server.RegisterTool("create_alarm_class",
                "创建报警类别。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("类别名"),
                }, new[] { "name" }),
                args => tia.Value.CreateAlarmClass(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("delete_alarm_class",
                "删除报警类别。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("类别名"),
                }, new[] { "name" }),
                args => tia.Value.DeleteAlarmClass(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("list_hmi_discrete_alarms",
                "列出离散量报警。",
                EmptySchema(),
                _ => tia.Value.ListHmiDiscreteAlarms());

            server.RegisterTool("create_hmi_discrete_alarm",
                "创建离散量报警。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("报警名"),
                }, new[] { "name" }),
                args => tia.Value.CreateHmiDiscreteAlarm(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("delete_hmi_discrete_alarm",
                "删除离散量报警。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("报警名"),
                }, new[] { "name" }),
                args => tia.Value.DeleteHmiDiscreteAlarm(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("list_hmi_analog_alarms",
                "列出模拟量报警。",
                EmptySchema(),
                _ => tia.Value.ListHmiAnalogAlarms());

            server.RegisterTool("create_hmi_analog_alarm",
                "创建模拟量报警。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("报警名"),
                }, new[] { "name" }),
                args => tia.Value.CreateHmiAnalogAlarm(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("delete_hmi_analog_alarm",
                "删除模拟量报警。",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("报警名"),
                }, new[] { "name" }),
                args => tia.Value.DeleteHmiAnalogAlarm(
                    GetStringArg(args, "name") ?? ""));

            server.RegisterTool("list_hmi_text_lists",
                "列出文本列表。",
                EmptySchema(),
                _ => tia.Value.ListHmiTextLists());

            // create_hmi_text_list 由 HmiExtendedTools 中的增强实现唯一注册。

            // list_hmi_graphic_lists 由 HmiExtendedTools 中的增强实现唯一注册。

            // create_hmi_graphic_list 由 HmiExtendedTools 中的增强实现唯一注册。

            server.RegisterTool("list_hmi_data_logs",
                "列出数据日志。",
                EmptySchema(),
                _ => tia.Value.ListHmiDataLogs());

            server.RegisterTool("create_hmi_data_log",
                "创建数据日志。\n" +
                "实现：优先在 Unified HMI 上调用 HmiSoftware.DataLogs.Create(name)；" +
                "经典 HMI 的 DataLogs 集合未在 Openness API 公开，将返回明确错误。\n" +
                "参数说明：\n" +
                "  name: 数据日志名称（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选，多 HMI 时指定，仅对 Unified HMI 有效）\n" +
                "返回：success/dataLogName/hmiKind/alreadyExisted/hmiDeviceName；失败时返回 error/note/hint",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("日志名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选，仅对 Unified HMI 有效）"),
                }, new[] { "name" }),
                args => tia.Value.CreateHmiDataLog(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            // ── HMI 完善与优化（5 个新工具）──────────────────────────

            server.RegisterTool("set_hmi_tag_property",
                "通用 HMI 变量属性设置（支持 Unified 和传统 HMI）。\n" +
                "通过 SetAttribute 强类型 API 写入，属性不存在时返回可用属性列表。\n" +
                "常用属性：Address/Comment/DataType/AccessMode/AcquisitionCycle/AcquisitionMode/Connection/InitialValue/LinearScaling 等。\n" +
                "参数说明：\n" +
                "  hmiTagName: HMI 变量名（必填）\n" +
                "  propertyName: 属性名（必填，如 Address/Comment/DataType 等）\n" +
                "  value: 属性值（必填，字符串形式；true/false/数字会自动转换）\n" +
                "  tagTableName: 变量表名（可选，Unified HMI 用于缩小查找范围）\n" +
                "返回：success/tagName/propertyName/oldValue/newValue/hmiKind，失败时返回 availableAttributes\n" +
                "示例：\n" +
                "  {\"hmiTagName\": \"Start\", \"propertyName\": \"Comment\", \"value\": \"启动按钮\"}\n" +
                "  {\"hmiTagName\": \"Speed\", \"propertyName\": \"Address\", \"value\": \"%MW100\"}",
                Props(new Dictionary<string, object>
                {
                    ["hmiTagName"] = StrProp("HMI 变量名（必填）"),
                    ["propertyName"] = StrProp("属性名（必填，如 Address/Comment/DataType 等）"),
                    ["value"] = StrProp("属性值（必填，true/false/数字会自动转换）"),
                    ["tagTableName"] = StrProp("变量表名（可选，Unified HMI 用于缩小查找范围）"),
                }, new[] { "hmiTagName", "propertyName", "value" }),
                args => tia.Value.SetHmiTagProperty(
                    GetStringArg(args, "hmiTagName") ?? "",
                    GetStringArg(args, "propertyName") ?? "",
                    GetStringArg(args, "value") ?? "",
                    GetStringArg(args, "tagTableName")));

            server.RegisterTool("get_hmi_tag_properties",
                "读取 HMI 变量的所有属性（通过 GetAttributeInfos + GetAttribute）。\n" +
                "返回 [{ name, value, accessMode, supportedTypes, canRead, canWrite }] 数组。\n" +
                "支持 Unified 和传统 HMI，自动检测。\n" +
                "参数说明：\n" +
                "  hmiTagName: HMI 变量名（必填）\n" +
                "  tagTableName: 变量表名（可选，Unified HMI 用于缩小查找范围）\n" +
                "示例：\n" +
                "  {\"hmiTagName\": \"Start\"}\n" +
                "  {\"hmiTagName\": \"Speed\", \"tagTableName\": \"DefaultTagTable\"}",
                Props(new Dictionary<string, object>
                {
                    ["hmiTagName"] = StrProp("HMI 变量名（必填）"),
                    ["tagTableName"] = StrProp("变量表名（可选，Unified HMI 用于缩小查找范围）"),
                }, new[] { "hmiTagName" }),
                args => tia.Value.GetHmiTagProperties(
                    GetStringArg(args, "hmiTagName") ?? "",
                    GetStringArg(args, "tagTableName")));

            server.RegisterTool("create_unified_hmi_screen_item_typed",
                "用 Unified HMI 的强类型 Create<T> 方法创建画面项（仅适用于 WinCC Unified HMI）。\n" +
                "通过反射调用泛型 Create<T>(string name)，比字符串名反射更可靠。\n" +
                "支持的画面项类型：\n" +
                "  Widgets: Button, IOField, Text, Bar, Slider, TextBox, ToggleSwitch, Clock, Gauge,\n" +
                "           ListBox, CheckBoxGroup, RadioButtonGroup, SymbolicIOField, TouchArea\n" +
                "  Shapes: Rectangle, Circle, Line, Point, Polygon, Polyline, Ellipse, GraphicView\n" +
                "  Controls: AlarmControl, TrendControl, MediaControl, ProcessControl,\n" +
                "            SystemDiagnosisControl, WebControl, FaceplateContainer\n" +
                "  Screens: ScreenWindow\n" +
                "参数说明：\n" +
                "  screenName: 画面名（必填）\n" +
                "  itemTypeName: 画面项类型名（必填，如 Button/IOField/Text 等）\n" +
                "  itemName: 画面项名称（必填）\n" +
                "  left/top/width/height: 坐标和尺寸（可选，int）\n" +
                "返回：success/screenName/itemName/itemTypeName/actualType/position/properties\n" +
                "示例：\n" +
                "  {\"screenName\": \"Main\", \"itemTypeName\": \"Button\", \"itemName\": \"btnStart\", \"left\": 50, \"top\": 50, \"width\": 100, \"height\": 40}",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名（必填）"),
                    ["itemTypeName"] = StrProp("画面项类型名（必填，如 Button/IOField/Text/Rectangle 等）"),
                    ["itemName"] = StrProp("画面项名称（必填）"),
                    ["left"] = IntProp("X 坐标（可选）"),
                    ["top"] = IntProp("Y 坐标（可选）"),
                    ["width"] = IntProp("宽度（可选）"),
                    ["height"] = IntProp("高度（可选）"),
                }, new[] { "screenName", "itemTypeName", "itemName" }),
                args => tia.Value.CreateUnifiedHmiScreenItemTyped(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "itemTypeName") ?? "",
                    GetStringArg(args, "itemName") ?? "",
                    GetIntArg(args, "left"),
                    GetIntArg(args, "top"),
                    GetIntArg(args, "width"),
                    GetIntArg(args, "height")));

            server.RegisterTool("set_unified_hmi_runtime_setting",
                "通用 Unified HMI 运行时设置（通过 RuntimeSettings.SetAttribute，仅适用于 WinCC Unified HMI）。\n" +
                "支持的属性：StartScreen/ScreenResolution/LanguageAndFonts/MaxLoginRuntimeSettings/\n" +
                "  OpcUaServerRuntimeSettings/ProcessDiagnosticsRuntimeSettings/RuntimeResourceSettings/\n" +
                "  HmiReportingSettings/GMPEnabled/AutoLogOffURL/BitSelection 等。\n" +
                "属性不存在时返回 GetAttributeInfos 列出的可用属性列表。\n" +
                "参数说明：\n" +
                "  propertyName: 属性名（必填，如 StartScreen/ScreenResolution 等）\n" +
                "  value: 属性值（必填，字符串形式）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选，项目中有多个 Unified HMI 时指定）\n" +
                "返回：success/propertyName/oldValue/newValue/hmiDeviceName，失败时返回 availableAttributes\n" +
                "示例：\n" +
                "  {\"propertyName\": \"GMPEnabled\", \"value\": \"true\"}",
                Props(new Dictionary<string, object>
                {
                    ["propertyName"] = StrProp("属性名（必填，如 StartScreen/ScreenResolution/GMPEnabled 等）"),
                    ["value"] = StrProp("属性值（必填，true/false/数字会自动转换）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选，多 Unified HMI 时指定）"),
                }, new[] { "propertyName", "value" }),
                args => tia.Value.SetUnifiedHmiRuntimeSetting(
                    GetStringArg(args, "propertyName") ?? "",
                    GetStringArg(args, "value") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_hmi_logging_tag",
                "为 Unified HMI 变量创建日志记录绑定（LoggingTag，仅适用于 WinCC Unified HMI）。\n" +
                "通过 HmiTag.LoggingTags.Create(loggingTagName) 创建。\n" +
                "参数说明：\n" +
                "  hmiTagName: HMI 变量名（必填）\n" +
                "  loggingTagName: 日志记录绑定名（必填）\n" +
                "  tagTableName: 变量表名（可选，用于缩小查找范围）\n" +
                "返回：success/hmiTagName/loggingTagName/tagTableName\n" +
                "示例：\n" +
                "  {\"hmiTagName\": \"Temperature\", \"loggingTagName\": \"TempLog_1\"}",
                Props(new Dictionary<string, object>
                {
                    ["hmiTagName"] = StrProp("HMI 变量名（必填）"),
                    ["loggingTagName"] = StrProp("日志记录绑定名（必填）"),
                    ["tagTableName"] = StrProp("变量表名（可选，用于缩小查找范围）"),
                }, new[] { "hmiTagName", "loggingTagName" }),
                args => tia.Value.CreateHmiLoggingTag(
                    GetStringArg(args, "hmiTagName") ?? "",
                    GetStringArg(args, "loggingTagName") ?? "",
                    GetStringArg(args, "tagTableName")));

            // ── HMI 全部 8 项功能（任务 5.5）──────────────────────

            server.RegisterTool("export_unified_hmi_tags",
                "Unified HMI 变量批量导出（仅适用于 WinCC Unified HMI）。\n" +
                "tagTableName 非空时只导出指定变量表；为空时遍历所有变量表逐个导出到目录。\n" +
                "API：HmiTagComposition.Export(DirectoryInfo)。\n" +
                "参数说明：\n" +
                "  tagTableName: 变量表名（可选，为空则导出全部变量表）\n" +
                "  directoryPath: 导出目标目录绝对路径（必填，不存在会自动创建）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选，多 Unified HMI 时指定）\n" +
                "返回：success/exportedTables[{name,count}]/directoryPath",
                Props(new Dictionary<string, object>
                {
                    ["tagTableName"] = StrProp("变量表名（可选，为空则导出全部变量表）"),
                    ["directoryPath"] = StrProp("导出目标目录绝对路径（必填）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选）"),
                }, new[] { "directoryPath" }),
                args => tia.Value.ExportUnifiedHmiTags(
                    GetStringArg(args, "tagTableName") ?? "",
                    GetStringArg(args, "directoryPath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("import_unified_hmi_tags",
                "Unified HMI 变量批量导入（仅适用于 WinCC Unified HMI）。\n" +
                "遍历 TagTables，对每个变量表的 Tags 集合调用 Import(DirectoryInfo)。\n" +
                "API：HmiTagComposition.Import(DirectoryInfo)。\n" +
                "参数说明：\n" +
                "  directoryPath: 含已导出变量文件的目录绝对路径（必填）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选）\n" +
                "返回：success/importedCount/perTable[{name,imported}]/directoryPath",
                Props(new Dictionary<string, object>
                {
                    ["directoryPath"] = StrProp("含已导出变量文件的目录绝对路径（必填）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选）"),
                }, new[] { "directoryPath" }),
                args => tia.Value.ImportUnifiedHmiTags(
                    GetStringArg(args, "directoryPath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("export_unified_hmi_scripts",
                "Unified HMI 脚本模块导出（仅适用于 WinCC Unified HMI）。\n" +
                "获取 hmiSoftware.Scripts，调用 Export(DirectoryInfo) 导出全部脚本模块。\n" +
                "API：HmiScriptModuleComposition.Export(DirectoryInfo)。\n" +
                "参数说明：\n" +
                "  directoryPath: 导出目标目录绝对路径（必填，不存在会自动创建）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选）\n" +
                "返回：success/scriptCount/directoryPath",
                Props(new Dictionary<string, object>
                {
                    ["directoryPath"] = StrProp("导出目标目录绝对路径（必填）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选）"),
                }, new[] { "directoryPath" }),
                args => tia.Value.ExportUnifiedHmiScripts(
                    GetStringArg(args, "directoryPath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("import_unified_hmi_scripts",
                "Unified HMI 脚本模块导入（仅适用于 WinCC Unified HMI）。\n" +
                "获取 hmiSoftware.Scripts，调用 Import(DirectoryInfo) 从目录导入脚本模块。\n" +
                "API：HmiScriptModuleComposition.Import(DirectoryInfo)。\n" +
                "参数说明：\n" +
                "  directoryPath: 含已导出脚本文件的目录绝对路径（必填）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选）\n" +
                "返回：success/importedCount/directoryPath",
                Props(new Dictionary<string, object>
                {
                    ["directoryPath"] = StrProp("含已导出脚本文件的目录绝对路径（必填）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选）"),
                }, new[] { "directoryPath" }),
                args => tia.Value.ImportUnifiedHmiScripts(
                    GetStringArg(args, "directoryPath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_unified_hmi_alarm_log",
                "Unified HMI 创建报警日志（仅适用于 WinCC Unified HMI）。\n" +
                "通过 HmiSoftware.AlarmLogs.Create(name) 创建 HmiAlarmLog。\n" +
                "API：HmiAlarmLogComposition.Create(string)。\n" +
                "参数说明：\n" +
                "  name: 报警日志名称（必填）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选）\n" +
                "返回：success/alarmLogName",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("报警日志名称（必填）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateUnifiedHmiAlarmLog(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_unified_hmi_audit_trail",
                "Unified HMI 创建审计跟踪（仅适用于 WinCC Unified HMI）。\n" +
                "尝试通过反射调用 AuditTrails.Create(name) 创建 HmiAuditTrail。\n" +
                "注意：V19 Openness API 中 HmiAuditTrailComposition 可能未公开 Create 方法，\n" +
                "若不可创建将返回可用 Create/Add 方法列表与提示（需在博途 GUI 手动创建）。\n" +
                "参数说明：\n" +
                "  name: 审计跟踪名称（必填）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选）\n" +
                "返回：success/auditTrailName；失败时 availableCreateMethods/note",
                Props(new Dictionary<string, object>
                {
                    ["name"] = StrProp("审计跟踪名称（必填）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选）"),
                }, new[] { "name" }),
                args => tia.Value.CreateUnifiedHmiAuditTrail(
                    GetStringArg(args, "name") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_unified_hmi_event_handler",
                "Unified HMI 为画面项创建事件处理器（仅适用于 WinCC Unified HMI）。\n" +
                "事件类型是控件专属枚举（如 HmiButtonEventType.Click），通过反射解析并调用\n" +
                "EventHandlers.Create(枚举值)。事件类型不识别时返回该控件支持的事件类型列表。\n" +
                "可选 scriptName：按名称查找脚本模块并设置事件处理器的 Script 属性。\n" +
                "参数说明：\n" +
                "  screenName: 画面名称（必填）\n" +
                "  itemName: 画面项名称（必填，需先用 create_unified_hmi_screen_item_typed 创建）\n" +
                "  eventType: 事件类型（必填，如 Click/Pressed/Released，或完整名 HmiButtonEventType.Click）\n" +
                "  scriptName: 脚本模块名（可选，关联到事件处理器）\n" +
                "  hmiDeviceName: Unified HMI 设备名（可选）\n" +
                "返回：success/screenName/itemName/eventType/scriptName/scriptSetResult/apiExplored；\n" +
                "  事件类型不支持时返回 supportedEventTypes",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称（必填）"),
                    ["itemName"] = StrProp("画面项名称（必填）"),
                    ["eventType"] = StrProp("事件类型（必填，如 Click/Pressed/Released 或完整枚举名）"),
                    ["scriptName"] = StrProp("脚本模块名（可选，关联到事件处理器）"),
                    ["hmiDeviceName"] = StrProp("Unified HMI 设备名（可选）"),
                }, new[] { "screenName", "itemName", "eventType" }),
                args => tia.Value.CreateUnifiedHmiEventHandler(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "itemName") ?? "",
                    GetStringArg(args, "eventType") ?? "",
                    GetStringArg(args, "scriptName"),
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("create_classic_hmi_screen_from_template",
                "传统 HMI 从模板/弹出/滑入文件夹创建画面结构（仅适用于经典 HMI WinCC Advanced/Comfort）。\n" +
                "按 templateType 选择 ScreenPopupFolder/ScreenSlideinFolder/ScreenTemplateFolder/ScreenFolder，\n" +
                "若 folderName 非空则在该文件夹下创建子文件夹。\n" +
                "注意：实际画面创建需 MasterCopy 或 XML 文件（配合 import_hmi_screen），\n" +
                "本方法完成文件夹结构准备。\n" +
                "参数说明：\n" +
                "  screenName: 画面名称（必填，记录用）\n" +
                "  templateType: 文件夹类型（必填，Popup/Slidein/Template/Screen）\n" +
                "  folderName: 子文件夹名（可选，为空则不创建子文件夹）\n" +
                "  hmiDeviceName: 传统 HMI 设备名（可选，多 HMI 时指定）\n" +
                "返回：success/screenName/templateType/folderName/hmiDeviceName/note",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称（必填，记录用）"),
                    ["templateType"] = StrProp("文件夹类型（必填：Popup/Slidein/Template/Screen）"),
                    ["folderName"] = StrProp("子文件夹名（可选）"),
                    ["hmiDeviceName"] = StrProp("传统 HMI 设备名（可选）"),
                }, new[] { "screenName", "templateType" }),
                args => tia.Value.CreateClassicHmiScreenFromTemplate(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "templateType") ?? "",
                    GetStringArg(args, "folderName"),
                    GetStringArg(args, "hmiDeviceName")));

            // ── HMI 无人干预全自动生成（编排层）────────────────────────

            server.RegisterTool("create_hmi_from_spec",
                "从 JSON 规格自动创建完整 HMI 设备（画面+变量+报警+连接）。\n" +
                "内部按顺序调用：创建连接 → 创建变量 → 批量绑定 PLC → 创建画面 → 创建画面项 → 绑定标签 → 创建报警 → 设置启动画面。\n" +
                "某一步失败时记录错误但继续执行后续步骤，最终返回汇总结果。\n" +
                "参数说明：\n" +
                "  specJson: JSON 规格字符串，包含 hmiDeviceName/hmiType/connection/screens/tags/alarms/startScreen。\n" +
                "    hmiType 为 \"Unified\" 或 \"Classic\"（默认 Classic）。\n" +
                "    screens 数组每项包含 name 和 items 数组（type/name/left/top/width/height/tag）。\n" +
                "    tags 数组每项包含 name/dataType/connection/address。\n" +
                "    alarms 数组每项包含 name/type（Discrete/Analog）。\n" +
                "返回：success/message/hmiType/steps[{step,success,...}]",
                Props(new Dictionary<string, object>
                {
                    ["specJson"] = StrProp("JSON 规格字符串（必填）"),
                }, new[] { "specJson" }),
                args => tia.Value.CreateHmiFromSpec(
                    GetStringArg(args, "specJson") ?? "{}"));

            server.RegisterTool("export_hmi_workspace",
                "导出完整 HMI 工作区到指定目录。\n" +
                "导出画面(export_hmi_screen)、变量表(export_hmi_tag_table)、连接(export_hmi_connection)，\n" +
                "并生成 workspace.json 索引文件记录所有导出内容。\n" +
                "参数说明：\n" +
                "  directoryPath: 导出目标目录绝对路径（必填，不存在会自动创建）\n" +
                "  hmiDeviceName: HMI 设备名（可选，为空时使用第一个找到的 HMI）\n" +
                "返回：success/workspaceFile/hmiName/hmiType/screenCount/tagTableCount/connectionCount/exportCount/errors",
                Props(new Dictionary<string, object>
                {
                    ["directoryPath"] = StrProp("导出目标目录绝对路径（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "directoryPath" }),
                args => tia.Value.ExportHmiWorkspace(
                    GetStringArg(args, "directoryPath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("import_hmi_workspace",
                "导入完整 HMI 工作区。\n" +
                "读取 workspace.json 索引文件，按依赖顺序导入：连接 → 变量 → 画面 → 设置启动画面。\n" +
                "参数说明：\n" +
                "  directoryPath: 包含 workspace.json 的目录绝对路径（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/workspaceFile/hmiType/importCount/imports/errors",
                Props(new Dictionary<string, object>
                {
                    ["directoryPath"] = StrProp("包含 workspace.json 的目录绝对路径（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "directoryPath" }),
                args => tia.Value.ImportHmiWorkspace(
                    GetStringArg(args, "directoryPath") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            // ── 趋势图与数据日志扩展（任务 1/3/4）──────────────────────

            server.RegisterTool("create_hmi_trend_view",
                "在 HMI 画面上创建趋势图（Trend View）画面项。\n" +
                "实现思路（多路径尝试）：\n" +
                "  路径 1：反射尝试 ScreenItems.Create(name, typeId)，依次尝试 TrendView/HmiScreenTrendView/Hmi.Screen.TrendView\n" +
                "  路径 2：XML 往返方式（导出画面 XML → 注入 Hmi.Screen.TrendView 节点 → 重新导入）\n" +
                "XML 片段含一个默认 TrendCurve 子对象（曲线_1，红色），可后续用 bind_trend_view_tag 绑定变量。\n" +
                "参数说明：\n" +
                "  screenName: 画面名称（必填）\n" +
                "  trendName: 趋势图画面项名称（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选，多 HMI 时指定）\n" +
                "返回：success/message/screenName/trendName/method（Reflection/XmlRoundTrip）/reflectionAttempts",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称（必填）"),
                    ["trendName"] = StrProp("趋势图画面项名称（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "screenName", "trendName" }),
                args => tia.Value.CreateHmiTrendView(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "trendName") ?? "",
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("bind_trend_view_tag",
                "为 HMI 趋势图绑定变量到指定曲线。\n" +
                "支持两种 HMI 类型：\n" +
                "  - Unified HMI：通过 HmiTrendControl.TrendAreas[0].Trends[curveIndex].DataSourceY.Source = tagName\n" +
                "    若 curveIndex 超出已有 Trends 数量，会自动 Create 新的 HmiTrendPart\n" +
                "  - Classic HMI：通过 XML 往返方式修改 TrendCurve 的 Tag 绑定属性\n" +
                "    若 curveIndex 超出现有曲线数，会自动创建占位曲线直至达到索引\n" +
                "参数说明：\n" +
                "  screenName: 画面名称（必填）\n" +
                "  trendItemName: 趋势图画面项名称（必填）\n" +
                "  tagName: 要绑定的 HMI 变量名（必填）\n" +
                "  curveIndex: 曲线索引（从 0 开始，必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/screenName/trendItemName/tagName/curveIndex/hmiKind/oldValue",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("画面名称（必填）"),
                    ["trendItemName"] = StrProp("趋势图画面项名称（必填）"),
                    ["tagName"] = StrProp("要绑定的 HMI 变量名（必填）"),
                    ["curveIndex"] = IntProp("曲线索引（从 0 开始，必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "screenName", "trendItemName", "tagName", "curveIndex" }),
                args => tia.Value.BindTrendViewTag(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "trendItemName") ?? "",
                    GetStringArg(args, "tagName") ?? "",
                    GetIntArg(args, "curveIndex") ?? 0,
                    GetStringArg(args, "hmiDeviceName")));

            server.RegisterTool("add_data_log_variable",
                "为 HMI 数据日志添加变量绑定。\n" +
                "Unified HMI 实现路径：\n" +
                "  1. 在 HmiSoftware.DataLogs 中 Find(dataLogName) 验证数据日志存在\n" +
                "  2. 在所有 TagTables 中 Find(tagName) 找到目标 HmiTag\n" +
                "  3. 调用 tag.LoggingTags.Create(loggingTagName) 创建日志绑定（命名规则：变量名_数据日志名）\n" +
                "  4. 设置 LoggingTag.DataLog = dataLogName（关联到具体数据日志）\n" +
                "经典 HMI 的 DataLogs 集合未在 Openness API 公开，将返回明确错误。\n" +
                "参数说明：\n" +
                "  dataLogName: 数据日志名称（必填）\n" +
                "  tagName: 要绑定的 HMI 变量名（必填）\n" +
                "  hmiDeviceName: HMI 设备名（可选）\n" +
                "返回：success/message/dataLogName/tagName/loggingTagName/hmiKind/oldDataLog/newDataLog",
                Props(new Dictionary<string, object>
                {
                    ["dataLogName"] = StrProp("数据日志名称（必填）"),
                    ["tagName"] = StrProp("要绑定的 HMI 变量名（必填）"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名（可选）"),
                }, new[] { "dataLogName", "tagName" }),
                args => tia.Value.AddDataLogVariable(
                    GetStringArg(args, "dataLogName") ?? "",
                    GetStringArg(args, "tagName") ?? "",
                    GetStringArg(args, "hmiDeviceName")));
        }
    }
}
