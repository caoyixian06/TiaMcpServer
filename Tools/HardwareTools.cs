using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// 硬件与 IO 映射工具注册。
    /// </summary>
    public partial class McpServer
    {
        internal static void RegisterHardwareTools(McpServer server, Lazy<PortalService> tia)
        {
            server.RegisterTool("list_hardware_modules",
                "列出指定 PLC 的所有硬件 DeviceItem（模块），包含设备名、模块名、类型、订货号、固件版本和槽位号。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（可选，为空时列出所有 PLC）")
                }),
                args => tia.Value.ListHardwareModules(GetStringArg(args, "plcName")));

            server.RegisterTool("get_module_io_addresses",
                "读取指定硬件模块的 IO 地址（Input/Output 起始字节与长度）。",
                Props(new Dictionary<string, object>
                {
                    ["moduleName"] = StrProp("模块名称")
                }, new[] { "moduleName" }),
                args => tia.Value.GetModuleIoAddresses(GetStringArg(args, "moduleName") ?? ""));

            server.RegisterTool("export_io_mapping",
                "导出指定 PLC 的完整 IO 映射：枚举所有硬件模块，收集各模块 IO 地址及匹配的标签。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（可选）"),
                    ["outputPath"] = StrProp("输出 JSON 文件路径（可选，为空时直接返回 JSON）")
                }),
                args => tia.Value.ExportIoMapping(GetStringArg(args, "plcName"), GetStringArg(args, "outputPath")));

            server.RegisterTool("get_io_device_status",
                "获取 PLC 的 PROFINET/PROFIBUS 接口及其连接的 IO 设备摘要。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（可选）")
                }),
                args => tia.Value.GetIoDeviceStatus(GetStringArg(args, "plcName")));

            server.RegisterTool("export_hardware",
                "导出指定 PLC（或所有 PLC）的硬件配置 XML 文件。可选附带 IO 映射 JSON 和网络配置信息。" +
                "单个 PLC 且 outputPath 以 .xml 结尾时为文件模式；否则为目录模式（每个 PLC 生成一个 XML）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（可选，为空时导出所有 PLC 硬件）"),
                    ["outputPath"] = StrProp("输出路径（单个 PLC 且以 .xml 结尾时为文件路径，否则为目录）"),
                    ["includeIoMapping"] = BoolProp("是否同时导出 IO 映射 JSON（默认 true）"),
                    ["includeNetworkConfig"] = BoolProp("是否包含网络配置信息（默认 false）")
                }, new[] { "outputPath" }),
                args => tia.Value.ExportHardware(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "outputPath") ?? "",
                    GetBoolArg(args, "includeIoMapping") ?? true,
                    GetBoolArg(args, "includeNetworkConfig") ?? false));

            server.RegisterTool("export_hardware_cax",
                "使用 CaxProvider 将设备硬件配置导出为 XML 文件（用于跨项目迁移）。" +
                "直接调用 TIA Portal Openness 的 CAx Export 接口，返回 TransferResult 状态与消息。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（用于定位设备所属的 PLC）"),
                    ["deviceName"] = StrProp("设备名称（即 Device.Name，需与博途项目中的设备名一致）"),
                    ["filePath"] = StrProp("输出 XML 文件路径（如 C:\\export\\device.xml）")
                }, new[] { "plcName", "deviceName", "filePath" }),
                args => tia.Value.ExportHardwareCax(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "deviceName") ?? "",
                    GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("import_hardware_cax",
                "使用 CaxProvider 从 XML 文件导入硬件配置（支持跨项目设备迁移）。" +
                "importOptions 可选值：MoveToParkingLot（移至停车区）/OverwriteTiaDevice（覆盖现有设备，默认）/RetainTiaDevice（保留现有设备），" +
                "也支持简写 parking_lot/overwrite/retain。",
                Props(new Dictionary<string, object>
                {
                    ["filePath"] = StrProp("待导入的 XML 文件路径"),
                    ["importOptions"] = StrProp("导入模式（可选，默认 OverwriteTiaDevice）")
                }, new[] { "filePath" }),
                args => tia.Value.ImportHardwareCax(
                    GetStringArg(args, "filePath") ?? "",
                    GetStringArg(args, "importOptions") ?? "OverwriteTiaDevice"));

            // ── IO 地址分配与硬件编译 ──

            server.RegisterTool("get_io_address_map",
                "获取 PLC 的完整 IO 地址映射。通过 PlcSoftware 的 IAddressList 接口枚举所有已分配地址；" +
                "反射失败时回退到遍历 DeviceItem 的 IoAddresses + 标签匹配。" +
                "返回地址列表（地址/变量名/数据类型/长度/区域 I/Q/M），含 apiExplored 和 triedPaths 字段。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（可选，为空时使用默认 PLC）")
                }),
                args => tia.Value.GetIoAddressMap(GetStringArg(args, "plcName")));

            server.RegisterTool("get_assignment_list",
                "获取设备的地址分配列表。通过反射获取 PlcDevice.GetAddressList() 或类似方法。" +
                "areaFilter 可选：I/Q/M/T/C（不区分大小写，支持全称如 Input/Output/Marker）。",
                Props(new Dictionary<string, object>
                {
                    ["deviceName"] = StrProp("设备名称"),
                    ["areaFilter"] = StrProp("区域过滤（可选）：I/Q/M/T/C 或 Input/Output/Marker/Timer/Counter")
                }, new[] { "deviceName" }),
                args => tia.Value.GetAssignmentList(
                    GetStringArg(args, "deviceName") ?? "",
                    GetStringArg(args, "areaFilter")));

            server.RegisterTool("find_next_free_address",
                "查找下一个空闲地址。通过 AddressList.FindNextFree() 反射调用，失败时回退到遍历已分配地址。" +
                "area: I/Q/M；dataType: Bool/Byte/Word/DWord/Real；mode: 可选（aligned/any）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称"),
                    ["area"] = StrProp("区域：I/Q/M"),
                    ["startByte"] = IntProp("起始字节"),
                    ["dataType"] = StrProp("数据类型（可选）：Bool/Byte/Word/DWord/Real"),
                    ["mode"] = StrProp("模式（可选）：aligned/any")
                }, new[] { "plcName", "area", "startByte" }),
                args => tia.Value.FindNextFreeAddress(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "area") ?? "",
                    GetIntArg(args, "startByte") ?? 0,
                    GetStringArg(args, "dataType"),
                    GetStringArg(args, "mode")));

            server.RegisterTool("get_io_address_conflicts",
                "获取 IO 地址冲突列表。通过 IAddressList.GetConflicts() 反射调用。" +
                "areaFilter 可选：I/Q/M/T/C。返回冲突列表（地址/冲突变量1/冲突变量2）。",
                Props(new Dictionary<string, object>
                {
                    ["areaFilter"] = StrProp("区域过滤（可选）：I/Q/M/T/C")
                }),
                args => tia.Value.GetIoAddressConflicts(GetStringArg(args, "areaFilter")));

            server.RegisterTool("compile_hardware",
                "编译硬件配置。通过 ICompilable 接口调用 Compile()，优先编译设备级，回退到 PLC 块组编译。" +
                "plcName 为空时编译默认 PLC。返回编译结果（状态/错误数/警告数）。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（可选，为空时编译默认 PLC）")
                }),
                args => tia.Value.CompileHardware(GetStringArg(args, "plcName")));

            server.RegisterTool("compile_all_hardware",
                "编译所有设备的硬件配置。遍历项目中所有设备并逐个编译，返回每个设备的编译结果。",
                Props(new Dictionary<string, object>
                {
                }),
                args => tia.Value.CompileAllHardware());

            server.RegisterTool("set_network_config",
                "完整网络配置（IP/掩码/网关/路由/IO系统）。通过 NetworkInterface 和 IoController 设置。" +
                "设备为 IO 控制器时创建 IO 系统；为 IO 设备时连接到已有 IO 系统。",
                Props(new Dictionary<string, object>
                {
                    ["deviceName"] = StrProp("设备名称"),
                    ["subnetName"] = StrProp("子网名称（需已存在）"),
                    ["ip"] = StrProp("IP 地址"),
                    ["subnetMask"] = StrProp("子网掩码"),
                    ["gateway"] = StrProp("网关地址（可选）"),
                    ["ioSystemName"] = StrProp("IO 系统名称（可选，PLC 创建、IO 设备连接）"),
                    ["useRouter"] = BoolProp("是否启用路由（默认 false）"),
                    ["routerAddress"] = StrProp("路由地址（可选，useRouter=true 时生效）")
                }, new[] { "deviceName", "ip", "subnetMask" }),
                args => tia.Value.SetNetworkConfig(
                    GetStringArg(args, "deviceName") ?? "",
                    GetStringArg(args, "subnetName") ?? "",
                    GetStringArg(args, "ip") ?? "",
                    GetStringArg(args, "subnetMask") ?? "",
                    GetStringArg(args, "gateway"),
                    GetStringArg(args, "ioSystemName"),
                    GetBoolArg(args, "useRouter") ?? false,
                    GetStringArg(args, "routerAddress")));

            server.RegisterTool("get_full_hardware_config",
                "获取完整硬件配置信息（机架/槽位/模块/IO地址）。plcName 为空时返回所有 PLC 的硬件配置树。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("PLC 名称（可选，为空时返回所有 PLC）")
                }),
                args => tia.Value.GetFullHardwareConfig(GetStringArg(args, "plcName")));

            server.RegisterTool("get_network_configuration",
                "获取完整网络拓扑配置（所有子网/设备/接口/IO系统）。返回每个子网的 IO 系统、连接设备及其 IP 配置。",
                Props(new Dictionary<string, object>
                {
                }),
                args => tia.Value.GetNetworkConfiguration());

            // ── 模拟量通道配置 ──

            server.RegisterTool("set_analog_channel_type",
                "配置模拟量模块(AI/AO)的通道类型（4-20mA/0-10V 等）。" +
                "通过 DeviceItem.Channels 定位指定通道，再经 IEngineeringObject.SetAttribute 设置通道类型属性。" +
                "因不同模块的属性名不一致，会依次尝试 SensorType/OutputType/MeasurementType 等候选属性名（字符串值优先，long 值回退）。" +
                "deviceName 为空时按 moduleIndex 在模拟量候选模块中定位；设置失败时返回可用属性列表辅助排查。",
                Props(new Dictionary<string, object>
                {
                    ["deviceName"] = StrProp("模块设备名称(DeviceItem.Name)；为空时按 moduleIndex 定位"),
                    ["moduleIndex"] = IntProp("模块索引（deviceName 为空时使用，默认 0）"),
                    ["channelNumber"] = IntProp("通道号(0-7，需与模块实际通道范围匹配)"),
                    ["channelType"] = StrProp("通道类型: 4-20mA / 0-10V / 0-20mA / -10V到+10V / ±10V / 1-5V / 0-5V 等"),
                    ["plcName"] = StrProp("PLC 名称（多 PLC 项目时指定，为空时使用默认 PLC）")
                }, new[] { "channelNumber", "channelType" }),
                args => tia.Value.SetAnalogChannelType(
                    GetStringArg(args, "deviceName") ?? "",
                    GetIntArg(args, "moduleIndex") ?? 0,
                    GetIntArg(args, "channelNumber") ?? 0,
                    GetStringArg(args, "channelType") ?? "",
                    GetStringArg(args, "plcName")));
        }
    }
}
