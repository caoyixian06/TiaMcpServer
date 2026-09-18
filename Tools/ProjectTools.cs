using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    public partial class McpServer
    {
        internal static void RegisterProjectTools(McpServer server, Lazy<PortalService> tia)
        {
            server.RegisterTool("get_project_info", "获取当前连接项目的基本信息。", EmptySchema(), _ => tia.Value.GetProjectInfo());
            server.RegisterTool("get_project_summary", "获取项目概要。", EmptySchema(), _ => tia.Value.GetProjectSummary());
            server.RegisterTool("create_project", "创建新项目。",
                Props(new Dictionary<string, object> { ["projectPath"] = StrProp("目标目录"), ["projectName"] = StrProp("项目名称") }, new[] { "projectPath" }),
                args => tia.Value.CreateProject(GetStringArg(args, "projectPath") ?? "", GetStringArg(args, "projectName")));
            server.RegisterTool("open_project", "打开已有项目。",
                Props(new Dictionary<string, object> { ["projectPath"] = StrProp("项目目录路径（.apXX 所在目录）") }, new[] { "projectPath" }),
                args => tia.Value.OpenProject(GetStringArg(args, "projectPath") ?? ""));
            server.RegisterTool("close_project", "关闭项目。", EmptySchema(), _ => tia.Value.CloseProject());
            server.RegisterTool("save_project", "保存项目。", EmptySchema(), _ => tia.Value.SaveProject());
            server.RegisterTool("archive_project", "归档为当前 TIA 版本的 .zapXX。",
                Props(new Dictionary<string, object> { ["outputDir"] = StrProp("输出目录"), ["archiveName"] = StrProp("文件名") }, new[] { "outputDir", "archiveName" }),
                args => tia.Value.ArchiveProject(GetStringArg(args, "outputDir") ?? "", GetStringArg(args, "archiveName") ?? ""));
            server.RegisterTool("list_devices", "列出所有设备。", EmptySchema(), _ => tia.Value.ListDevices());
            server.RegisterTool("get_device_structure", "展示设备硬件结构。",
                Props(new Dictionary<string, object> { ["deviceName"] = StrProp("设备名") }, new[] { "deviceName" }),
                args => tia.Value.GetDeviceStructure(GetStringArg(args, "deviceName") ?? ""));
            server.RegisterTool("get_device_item_info", "查询设备项信息。",
                Props(new Dictionary<string, object> { ["deviceName"] = StrProp("设备名"), ["itemName"] = StrProp("设备项名") }, new[] { "deviceName" }),
                args => tia.Value.GetDeviceItemInfo(GetStringArg(args, "deviceName") ?? "", GetStringArg(args, "itemName")));
            server.RegisterTool("find_device_item", "按名称查找设备项。",
                Props(new Dictionary<string, object> { ["deviceItemName"] = StrProp("设备项名") }, new[] { "deviceItemName" }),
                args => tia.Value.FindDeviceItem(GetStringArg(args, "deviceItemName") ?? ""));
            server.RegisterTool("list_device_attributes", "列出设备属性。",
                Props(new Dictionary<string, object> { ["deviceName"] = StrProp("设备名") }, new[] { "deviceName" }),
                args => tia.Value.ListDeviceAttributes(GetStringArg(args, "deviceName") ?? ""));
            server.RegisterTool("create_device", "创建新设备（PLC/HMI/驱动等）。⚠️ deviceItemTypeId 必须是 search_hardware_catalog 返回的 typeIdentifier 值，不能直接填订单号！正确流程：1) 调用 search_hardware_catalog 搜索型号（如 1214C）→ 2) 从返回结果取 typeIdentifier → 3) 用该 typeIdentifier 调用本工具。",
                Props(new Dictionary<string, object> { ["deviceItemTypeId"] = StrProp("TypeIdentifier（必须来自 search_hardware_catalog 的返回值，不是订单号）"), ["deviceItemName"] = StrProp("设备项名（可选；PLC设备填名称如 PLC_1；HMI设备留空，服务层会自动将空字符串转为 null）"), ["deviceName"] = StrProp("设备名（可选；PLC设备填名称如 PLC_1；HMI设备留空，服务层会自动将空字符串转为 null）") }, new[] { "deviceItemTypeId" }),
                args => tia.Value.CreateDevice(GetStringArg(args, "deviceItemTypeId") ?? "", GetStringArg(args, "deviceItemName") ?? "", GetStringArg(args, "deviceName") ?? ""));
            server.RegisterTool("create_device_by_order", "按订单号创建设备（推荐！无需先查 TypeIdentifier）。直接传入西门子订单号即可创建 PLC/HMI 设备。订单号格式：6ES7214-1AG40-0XB0（空格可选，版本号可选）。内部优先从已验证的 TypeIdentifier 库查找（见 list_type_identifiers 工具），库中未找到时再走硬件目录实时搜索。常见 CPU 订单号：S7-1214C DC/DC/DC=6ES7214-1AG40-0XB0，S7-1214C AC/DC/Rly=6ES7214-1BG40-0XB0。",
                Props(new Dictionary<string, object> { ["orderNumber"] = StrProp("西门子订单号，如 6ES7214-1AG40-0XB0"), ["deviceName"] = StrProp("设备名，如 PLC_1") }, new[] { "orderNumber", "deviceName" }),
                args => tia.Value.CreateDeviceByOrder(GetStringArg(args, "orderNumber") ?? "", GetStringArg(args, "deviceName") ?? ""));
            server.RegisterTool("delete_device", "删除设备。",
                Props(new Dictionary<string, object> { ["deviceName"] = StrProp("设备名") }, new[] { "deviceName" }),
                args => tia.Value.DeleteDevice(GetStringArg(args, "deviceName") ?? ""));
            server.RegisterTool("plug_module", "插入模块。",
                Props(new Dictionary<string, object> { ["parentDeviceItemName"] = StrProp("父设备项"), ["typeIdentifier"] = StrProp("TypeIdentifier"), ["name"] = StrProp("模块名"), ["positionNumber"] = IntProp("槽号") }, new[] { "parentDeviceItemName", "typeIdentifier", "name", "positionNumber" }),
                args => tia.Value.PlugModule(GetStringArg(args, "parentDeviceItemName") ?? "", GetStringArg(args, "typeIdentifier") ?? "", GetStringArg(args, "name") ?? "", GetIntArg(args, "positionNumber") ?? 0));
            server.RegisterTool("show_device_in_editor", "打开设备视图。",
                Props(new Dictionary<string, object> { ["deviceName"] = StrProp("设备名"), ["viewName"] = StrProp("视图类型") }, new[] { "deviceName" }),
                args => tia.Value.ShowDeviceInEditor(GetStringArg(args, "deviceName") ?? "", GetStringArg(args, "viewName") ?? "Device"));
            server.RegisterTool("list_subnets", "列出子网。", EmptySchema(), _ => tia.Value.ListSubnets());
            server.RegisterTool("create_subnet", "创建子网。",
                Props(new Dictionary<string, object> { ["typeIdentifier"] = StrProp("TypeIdentifier"), ["name"] = StrProp("子网名") }, new[] { "typeIdentifier", "name" }),
                args => tia.Value.CreateSubnet(GetStringArg(args, "typeIdentifier") ?? "", GetStringArg(args, "name") ?? ""));
            server.RegisterTool("delete_subnet", "删除子网。",
                Props(new Dictionary<string, object> { ["name"] = StrProp("子网名") }, new[] { "name" }),
                args => tia.Value.DeleteSubnet(GetStringArg(args, "name") ?? ""));
            server.RegisterTool("connect_to_subnet", "连接设备到子网。",
                Props(new Dictionary<string, object> { ["deviceName"] = StrProp("设备名"), ["subnetName"] = StrProp("子网名") }, new[] { "deviceName", "subnetName" }),
                args => tia.Value.ConnectToSubnet(GetStringArg(args, "deviceName") ?? "", GetStringArg(args, "subnetName") ?? ""));
            server.RegisterTool("list_network_interfaces", "列出网络接口。", EmptySchema(), _ => tia.Value.ListNetworkInterfaces());
            server.RegisterTool("create_io_system", "在指定子网下创建 PROFINET IO 系统。",
                Props(new Dictionary<string, object> { ["plcName"] = StrProp("PLC 设备名（PROFINET 控制器）"), ["subnetName"] = StrProp("子网名"), ["ioSystemName"] = StrProp("IO 系统名") }, new[] { "plcName", "subnetName", "ioSystemName" }),
                args => tia.Value.CreateIoSystem(GetStringArg(args, "plcName") ?? "", GetStringArg(args, "subnetName") ?? "", GetStringArg(args, "ioSystemName") ?? ""));
            server.RegisterTool("connect_io_device", "将 IO 设备连接到指定 IO 系统。",
                Props(new Dictionary<string, object> { ["plcName"] = StrProp("PLC 设备名"), ["ioDeviceName"] = StrProp("IO 设备名"), ["ioSystemName"] = StrProp("IO 系统名") }, new[] { "plcName", "ioDeviceName", "ioSystemName" }),
                args => tia.Value.ConnectIoDevice(GetStringArg(args, "plcName") ?? "", GetStringArg(args, "ioDeviceName") ?? "", GetStringArg(args, "ioSystemName") ?? ""));
            server.RegisterTool("connect_network_ports", "连接两个设备的网络端口（PROFINET 物理拓扑）。",
                Props(new Dictionary<string, object> { ["plcName"] = StrProp("PLC 设备名"), ["port1DeviceName"] = StrProp("设备1名"), ["port1Index"] = IntProp("设备1端口索引"), ["port2DeviceName"] = StrProp("设备2名"), ["port2Index"] = IntProp("设备2端口索引") }, new[] { "plcName", "port1DeviceName", "port1Index", "port2DeviceName", "port2Index" }),
                args => tia.Value.ConnectNetworkPorts(GetStringArg(args, "plcName") ?? "", GetStringArg(args, "port1DeviceName") ?? "", GetIntArg(args, "port1Index") ?? 0, GetStringArg(args, "port2DeviceName") ?? "", GetIntArg(args, "port2Index") ?? 0));
            server.RegisterTool("set_device_ip", "设置设备网络接口的 IP 地址。",
                Props(new Dictionary<string, object> { ["plcName"] = StrProp("PLC 设备名"), ["deviceName"] = StrProp("设备名"), ["ipAddress"] = StrProp("IP 地址"), ["subnetMask"] = StrProp("子网掩码") }, new[] { "plcName", "deviceName", "ipAddress", "subnetMask" }),
                args => tia.Value.SetDeviceIp(GetStringArg(args, "plcName") ?? "", GetStringArg(args, "deviceName") ?? "", GetStringArg(args, "ipAddress") ?? "", GetStringArg(args, "subnetMask") ?? ""));
            server.RegisterTool("list_io_systems", "列出项目所有 IO 系统。",
                Props(new Dictionary<string, object> { ["plcName"] = StrProp("PLC 设备名") }, new[] { "plcName" }),
                args => tia.Value.ListIoSystems(GetStringArg(args, "plcName") ?? ""));
            server.RegisterTool("list_network_ports", "列出设备所有网络端口。",
                Props(new Dictionary<string, object> { ["plcName"] = StrProp("PLC 设备名"), ["deviceName"] = StrProp("设备名") }, new[] { "plcName", "deviceName" }),
                args => tia.Value.ListNetworkPorts(GetStringArg(args, "plcName") ?? "", GetStringArg(args, "deviceName") ?? ""));
            server.RegisterTool("diagnose_network_connections",
                "深度诊断 HMI↔PLC 网络连接：反射 NetworkInterface.Nodes 的所有属性，找出连接对象的真实存储位置和 PartnerDevice/PartnerInterface 等关系字段。",
                EmptySchema(), _ => tia.Value.DiagnoseNetworkConnections());
            server.RegisterTool("diagnose_project_structure",
                "导出项目完整结构 + 反射诊断：输出设备组树、每个 PLC 的块/变量表/UDT 清单、子网、以及设备组反射信息。用于 AI 一次性了解大型多站项目全貌。",
                EmptySchema(), _ => tia.Value.DiagnoseProjectStructure());
            // list_watch_tables 由 MoreTools 的监视表+强制表实现唯一注册。
            server.RegisterTool("run_cross_reference", "获取交叉引用。", EmptySchema(), _ => tia.Value.RunCrossReference());

            // ── 对话框自动确认（非 HMI 新功能）──
            server.RegisterTool("set_dialog_suppression",
                "启用/禁用 TIA Portal 对话框自动确认（订阅 TiaPortal.Confirmation 事件）。\n" +
                "suppress=true 时所有弹窗被自动应答为 result（默认 Ignore），适合批量自动化操作时跳过确认弹窗。\n" +
                "suppress=false 时恢复手动应答。\n" +
                "result 可选值：Ok/Yes/YesToAll/Abort/Retry/Ignore/No/NoToAll/Cancel（默认 Ignore）。\n" +
                "★注意★ 自动应答为 Ignore 可能跳过必要确认，请仅在确定的批量场景使用；操作完成后建议重新禁用。",
                Props(new Dictionary<string, object> {
                    ["suppress"] = BoolProp("是否抑制（自动确认）对话框，true=自动应答/ture=恢复手动"),
                    ["result"] = StrProp("自动应答结果（可选，默认 Ignore；可选值 Ok/Yes/YesToAll/Abort/Retry/Ignore/No/NoToAll/Cancel）"),
                }, new[] { "suppress" }),
                args => tia.Value.SetDialogSuppression(
                    GetBoolArg(args, "suppress") ?? false,
                    GetStringArg(args, "result")));

            // ── 多语言 / 变量表分组（任务 7）──
            server.RegisterTool("list_project_languages",
                "列出项目支持的所有语言（CultureInfo 列表）。\n" +
                "反射探测 Project.Languages / LanguageSettings.ActiveLanguages 等候选属性，\n" +
                "返回每个语言的 name(如 zh-CN)/displayName/englishName/lcid。\n" +
                "若所有候选属性均不可用，返回 triedPaths 便于诊断。",
                EmptySchema(),
                _ => tia.Value.ListProjectLanguages());

            server.RegisterTool("create_tag_table_group",
                "在指定 PLC 变量表下创建分组（PlcTagTableGroupComposition.Create）。\n" +
                "parentGroupName 为空时在根组下创建；非空时先在根组下查找父组（仅一级），再在其下创建子组。\n" +
                "采用反射调用 Create(string) 方法，兼容不同 TIA 版本签名差异。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 设备名（必填）"),
                    ["groupName"] = StrProp("新分组名（必填）"),
                    ["parentGroupName"] = StrProp("父分组名（可选；为空时在根组下创建）"),
                }, new[] { "plcName", "groupName" }),
                args => tia.Value.CreateTagTableGroup(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "groupName") ?? "",
                    GetStringArg(args, "parentGroupName")));

            // ── OPC UA Server 接口 ──
            server.RegisterTool("list_opcua_server_interfaces",
                "列出 PLC 的 OPC UA Server 接口。\n" +
                "通过 PlcSoftware.GetService<OpcUaProvider>() 反射获取，返回 serverInterfaces 列表（Name/Enabled/Namespaces）。\n" +
                "若 OpcUaProvider 不可用，返回 triedPaths 便于诊断。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 设备名（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.ListOpcUaServerInterfaces(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("create_opcua_server_interface",
                "创建 OPC UA Server 接口。\n" +
                "通过 ServerInterfaceComposition.Create(string) 或 Import(FileInfo) 反射调用。\n" +
                "若提供 xmlFilePath 且文件存在，则从 XML 导入；否则创建空接口。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 设备名（必填）"),
                    ["interfaceName"] = StrProp("接口名（必填）"),
                    ["xmlFilePath"] = StrProp("XML 文件路径（可选；提供时从 XML 导入）"),
                }, new[] { "plcName", "interfaceName" }),
                args => tia.Value.CreateOpcUaServerInterface(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "interfaceName") ?? "",
                    GetStringArg(args, "xmlFilePath")));

            server.RegisterTool("export_opcua_server_interface",
                "导出 OPC UA Server 接口到 XML 文件。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 设备名（必填）"),
                    ["interfaceName"] = StrProp("接口名（必填）"),
                    ["filePath"] = StrProp("导出文件路径（必填）"),
                }, new[] { "plcName", "interfaceName", "filePath" }),
                args => tia.Value.ExportOpcUaServerInterface(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "interfaceName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("import_opcua_server_interface",
                "从 XML 文件导入 OPC UA Server 接口。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 设备名（必填）"),
                    ["filePath"] = StrProp("XML 文件路径（必填）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ImportOpcUaServerInterface(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            // ── 证书管理 ──
            server.RegisterTool("list_certificates",
                "列出项目所有证书。\n" +
                "通过 project.GetService<SecurityProvider>() 反射获取 CertificateComposition。\n" +
                "返回 certificates 列表（Subject/HasPrivateKey/ValidUntil/Id）。",
                EmptySchema(),
                _ => tia.Value.ListCertificates());

            server.RegisterTool("import_certificate",
                "导入证书。\n" +
                "通过 CertificateComposition.Import(FileInfo, SecureString) 方法。\n" +
                "password 为证书私钥密码（可选）。",
                Props(new Dictionary<string, object>
                {
                    ["filePath"] = StrProp("证书文件路径（必填）"),
                    ["password"] = StrProp("私钥密码（可选）"),
                }, new[] { "filePath" }),
                args => tia.Value.ImportCertificate(
                    GetStringArg(args, "filePath") ?? "",
                    GetStringArg(args, "password")));

            server.RegisterTool("create_certificate",
                "创建自签名证书。\n" +
                "通过 CertificateComposition.Create(CertificateTemplate) 方法。\n" +
                "validFrom/validUntil 格式：yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss。",
                Props(new Dictionary<string, object>
                {
                    ["subjectCommonName"] = StrProp("证书主题通用名 CN（必填）"),
                    ["validFrom"] = StrProp("生效日期（可选，格式 yyyy-MM-dd）"),
                    ["validUntil"] = StrProp("失效日期（可选，格式 yyyy-MM-dd）"),
                }, new[] { "subjectCommonName" }),
                args => tia.Value.CreateCertificate(
                    GetStringArg(args, "subjectCommonName") ?? "",
                    GetStringArg(args, "validFrom"),
                    GetStringArg(args, "validUntil")));

            server.RegisterTool("export_certificate",
                "导出证书到文件。",
                Props(new Dictionary<string, object>
                {
                    ["certificateId"] = StrProp("证书 ID（必填，来自 list_certificates）"),
                    ["filePath"] = StrProp("导出文件路径（必填）"),
                }, new[] { "certificateId", "filePath" }),
                args => tia.Value.ExportCertificate(
                    GetStringArg(args, "certificateId") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            // ── 项目文本翻译 ──
            server.RegisterTool("export_project_texts",
                "导出项目文本用于翻译。\n" +
                "通过 Project.ExportProjectTexts(FileInfo, CultureInfo, CultureInfo) 方法。\n" +
                "sourceLanguage/targetLanguage 格式：zh-CN、en-US 等 CultureInfo 名称。",
                Props(new Dictionary<string, object>
                {
                    ["filePath"] = StrProp("导出文件路径（必填）"),
                    ["sourceLanguage"] = StrProp("源语言（可选，如 zh-CN）"),
                    ["targetLanguage"] = StrProp("目标语言（可选，如 en-US）"),
                }, new[] { "filePath" }),
                args => tia.Value.ExportProjectTexts(
                    GetStringArg(args, "filePath") ?? "",
                    GetStringArg(args, "sourceLanguage"),
                    GetStringArg(args, "targetLanguage")));

            server.RegisterTool("import_project_texts",
                "导入翻译后的项目文本。\n" +
                "通过 Project.ImportProjectTexts(FileInfo, bool) 方法。\n" +
                "updateSourceLanguage=true 时同时更新源语言文本。",
                Props(new Dictionary<string, object>
                {
                    ["filePath"] = StrProp("导入文件路径（必填）"),
                    ["updateSourceLanguage"] = BoolProp("是否同时更新源语言文本（默认 false）"),
                }, new[] { "filePath" }),
                args => tia.Value.ImportProjectTexts(
                    GetStringArg(args, "filePath") ?? "",
                    GetBoolArg(args, "updateSourceLanguage") ?? false));

            // ── 项目元信息 ──
            server.RegisterTool("get_project_metadata",
                "获取项目完整元信息。\n" +
                "一次性返回 Author/Copyright/Family/Version/IsModified/IsPrimary/LastModified/LastModifiedBy/Size/Comment/UsedProducts 等所有 ProjectBase 属性。",
                EmptySchema(),
                _ => tia.Value.GetProjectMetadata());

            // ── 函数监督 ──
            server.RegisterTool("export_supervision_xlsx",
                "导出函数监督到 Excel。\n" +
                "通过 PlcSoftware.GetService<SupervisionProvider>() 反射获取并调用 ExportSupervisionsToXlsx。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 设备名（必填）"),
                    ["filePath"] = StrProp("导出 xlsx 文件路径（必填）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ExportSupervisionXlsx(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("import_supervision_xlsx",
                "从 Excel 导入函数监督。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 设备名（必填）"),
                    ["filePath"] = StrProp("导入 xlsx 文件路径（必填）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ImportSupervisionXlsx(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            // ── 项目历史 ──
            server.RegisterTool("list_project_history",
                "列出项目历史记录。\n" +
                "通过 Project.HistoryEntries 反射获取，返回 history 列表（DateTime/Text）。",
                EmptySchema(),
                _ => tia.Value.ListProjectHistory());

            // ── SICAR 标准骨架（汽车行业博途项目事实标准）──
            server.RegisterTool("create_sicar_skeleton",
                "创建 SICAR 标准项目骨架（汽车行业博途项目事实标准）。\n" +
                "一次性创建完整的 SICAR 标准结构：\n" +
                "  OB: Main(OB1) + Startup(OB100)\n" +
                "  FC: Init(FC100) / Auto(FC200) / Station(FC300) / Safety(FC400) / Manual(FC500) / Alarm(FC600) / Diagnostics(FC700)\n" +
                "  FB: Device(FB100) / StationFB(FB300)\n" +
                "  DB: DeviceDB(DB100) / StationDB(DB300)\n" +
                "  UDT: stMaterial / stAxis / stAlarm / stStation\n" +
                "  变量表: IO变量表 / 工艺变量表 / 报警变量表 / 系统变量表\n" +
                "  OB1 调用链: FC100→FC200→FC300→FC400→FC500→FC600→FC700（无条件调用）\n" +
                "创建空块（有接口但无逻辑），AI 后续用 add_lad_network 填充逻辑。\n" +
                "★同名块已存在时跳过创建（不覆盖已有逻辑），记录为警告。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("目标 PLC 名称（必填）"),
                    ["stationName"] = StrProp("工站名称（可选，默认 Station1）"),
                }, new[] { "plcName" }),
                args => tia.Value.CreateSicarSkeleton(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "stationName") ?? "Station1"));

            server.RegisterTool("create_sicar_udts",
                "创建 SICAR 标准用户数据类型（UDT）。\n" +
                "创建 4 个标准 UDT：\n" +
                "  stMaterial: 物料信息（MaterialID/MaterialName/Position/Status）\n" +
                "  stAxis: 轴信息（Position/Speed/Status/Homed/Enabled）\n" +
                "  stAlarm: 报警信息（Code/Text/Active/Acknowledged）\n" +
                "  stStation: 工站信息（State/Mode/StationNo/FaultCount）\n" +
                "UDT 通过 PlcType XML 导入创建，可单独调用或由 create_sicar_skeleton 自动调用。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("目标 PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.CreateSicarUdts(GetStringArg(args, "plcName") ?? ""));

            server.RegisterTool("create_sicar_tag_tables",
                "创建 SICAR 标准变量表。\n" +
                "创建 4 个标准变量表：\n" +
                "  IO变量表: 物理 IO 映射（I_启动/I_停止/I_急停/Q_电机运行 等）\n" +
                "  工艺变量表: 工艺参数（M_自动模式/M_当前工站号/M_设备速度 等）\n" +
                "  报警变量表: 报警位（M_故障1/M_报警激活/M_报警代码 等）\n" +
                "  系统变量表: 系统状态（M_系统就绪/M_系统模式/M_诊断代码 等）\n" +
                "可单独调用或由 create_sicar_skeleton 自动调用。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("目标 PLC 名称（必填）"),
                }, new[] { "plcName" }),
                args => tia.Value.CreateSicarTagTables(GetStringArg(args, "plcName") ?? ""));
        }
    }
}
