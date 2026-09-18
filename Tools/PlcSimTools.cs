using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TiaMcpServer
{
    /// <summary>
    /// PLCSIM 仿真工具注册。
    /// 为 partial class McpServer，在 McpServer.RegisterAllTools 中调用 RegisterPlcSimTools。
    /// PLCSIM API 是独立安装组件中的 DLL（Siemens.Simatic.PlcSim.VplcApi.dll），
    /// 通过反射加载，支持 Vplc1200/Vplc1500 虚拟 PLC 实例的创建、启停、电源、连接、下载/上传。
    /// PLCSIM Advanced 通过 ConfigureAdapters DLL 管理虚拟网络适配器。
    /// 所有 Service 方法返回 JObject，此处统一调用 ToString(Formatting.None) 转为 JSON 字符串。
    /// </summary>
    public partial class McpServer
    {
        internal static void RegisterPlcSimTools(McpServer server, Lazy<PortalService> tia)
        {
            // ═════════════════════════════════════════════════════════════════════════════
            // 一、PLCSIM API 可用性与实例管理
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("plcsim_check_available",
                "检查 PLCSIM API 是否可用。\n" +
                "依次探测候选 DLL 路径（S7-PLCSIM V17\\Bin\\Siemens.Simatic.PlcSim.VplcApi.dll、PLCSIM_V19 等），\n" +
                "加载成功后返回程序集版本、关键类型（VplcApi/VplcFactory/Vplc1200/Vplc1500/VplcState）及 VplcState 枚举值列表。\n" +
                "失败时返回 triedPaths 便于诊断（PLCSIM 可能未安装）。",
                EmptySchema(),
                _ => tia.Value.PlcSimCheckAvailable().ToString(Formatting.None));

            server.RegisterTool("plcsim_probe_classic_api",
                "★V17经典版探测★ 反射 S7-PLCSIM 经典 API（SimulationController/ISimulationRuntimeFactory 等）：\n" +
                "dump 相关类型的方法签名与静态入口，尝试获取控制器单例并列出其实例方法/属性。",
                EmptySchema(),
                _ => tia.Value.PlcSimProbeClassicApi().ToString(Formatting.None));

            server.RegisterTool("plcsim_list_instances",
                "列出所有 PLCSIM 虚拟 PLC 实例。\n" +
                "反射尝试多种入口：VplcApi.Instances 静态属性 / VplcApi.GetInstances() / VplcFactory.GetInstances() / VplcFactory.Instances。\n" +
                "同时合并本地缓存中的实例引用，返回每个实例的 Name/State/Type。",
                EmptySchema(),
                _ => tia.Value.PlcSimListInstances().ToString(Formatting.None));

            server.RegisterTool("plcsim_create_instance",
                "创建 PLCSIM 虚拟 PLC 实例。\n" +
                "cpuType: 1200 或 1500（也接受 S7-1200/S7-1500）。\n" +
                "反射尝试 VplcFactory.CreateVplc1200(string) / CreateVplc1500(string) / Create(string, CpuType) / 构造函数。\n" +
                "创建成功后缓存实例引用，后续 start/stop/power 等操作通过 instanceName 引用。",
                Props(new Dictionary<string, object>
                {
                    ["cpuType"] = StrProp("CPU 类型：1200 或 1500（也接受 S7-1200/S7-1500，必填）"),
                    ["instanceName"] = StrProp("实例名称（必填，后续操作通过此名称引用实例）"),
                }, new[] { "cpuType", "instanceName" }),
                args => tia.Value.PlcSimCreateInstance(
                    GetStringArg(args, "cpuType") ?? "",
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_delete_instance",
                "删除 PLCSIM 虚拟 PLC 实例。\n" +
                "反射尝试 instance.Delete() / instance.Dispose() / VplcFactory.Delete(instance)。\n" +
                "成功后从缓存移除。★注意★ 若 API 调用未成功，仅从缓存移除，可能需手动关闭 PLCSIM。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("实例名称（必填）"),
                }, new[] { "instanceName" }),
                args => tia.Value.PlcSimDeleteInstance(
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            // ═════════════════════════════════════════════════════════════════════════════
            // 二、实例运行控制（启停/电源/状态）
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("plcsim_start_instance",
                "启动 PLCSIM 实例（切换到 Run 模式）。\n" +
                "反射尝试 instance.Run() / Start() / SetState(VplcState.Run)。\n" +
                "若实例未上电，先尝试 PowerOn。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("实例名称（必填）"),
                }, new[] { "instanceName" }),
                args => tia.Value.PlcSimStartInstance(
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_stop_instance",
                "停止 PLCSIM 实例（切换到 Stop 模式）。\n" +
                "反射尝试 instance.Stop() / Pause() / SetState(VplcState.Stop)。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("实例名称（必填）"),
                }, new[] { "instanceName" }),
                args => tia.Value.PlcSimStopInstance(
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_get_instance_state",
                "获取 PLCSIM 实例当前状态（Run/Stop/PowerOff）。\n" +
                "反射读取 instance.State / OperatingState / IsPowered 属性，返回状态字符串与所有可读属性。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("实例名称（必填）"),
                }, new[] { "instanceName" }),
                args => tia.Value.PlcSimGetInstanceState(
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_set_instance_power",
                "设置 PLCSIM 实例电源（on/off）。\n" +
                "on=true 反射调用 PowerOn()；on=false 反射调用 PowerOff()。\n" +
                "失败时回退到 SetPower(bool)。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("实例名称（必填）"),
                    ["on"] = BoolProp("true=上电；false=断电（必填）"),
                }, new[] { "instanceName", "on" }),
                args => tia.Value.PlcSimSetInstancePower(
                    GetStringArg(args, "instanceName") ?? "",
                    GetBoolArg(args, "on") ?? false).ToString(Formatting.None));

            // ═════════════════════════════════════════════════════════════════════════════
            // 三、与 TIA Portal 项目的连接 / 下载 / 上传
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("plcsim_connect_to_plcsim",
                "连接 TIA Portal 中的 PLC 到 PLCSIM 实例。\n" +
                "plcName 对应 TIA Portal 项目中的 PLC 设备，instanceName 对应已创建的 PLCSIM 实例。\n" +
                "反射探测 PLCSIM 实例上的 ConnectToPlc / Connect 方法，以及 PLC 设备上的 PLCSIM Provider 服务。\n" +
                "返回所有尝试路径（attempts）和实例可用方法列表（instanceMethods）便于诊断。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("TIA Portal 项目中的 PLC 设备名称（必填，需与 list_devices 返回一致）"),
                    ["instanceName"] = StrProp("PLCSIM 实例名称（必填，需先 plcsim_create_instance）"),
                }, new[] { "plcName", "instanceName" }),
                args => tia.Value.PlcSimConnectToPlcSim(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_disconnect_from_plcsim",
                "断开 TIA Portal 与 PLCSIM 的连接。\n" +
                "遍历所有缓存的 PLCSIM 实例，反射尝试 Disconnect / DisconnectFromPlc / Detach 方法。\n" +
                "无缓存实例时返回 success=true（无需断开）。",
                EmptySchema(),
                _ => tia.Value.PlcSimDisconnectFromPlcSim().ToString(Formatting.None));

            server.RegisterTool("plcsim_download_to_instance",
                "下载 TIA Portal 项目到 PLCSIM 实例。\n" +
                "plcName 对应 TIA Portal 项目中的 PLC 设备，instanceName 对应已创建并上电的 PLCSIM 实例。\n" +
                "反射探测 instance.Download(plc) / DownloadToPlc(plc) 及 PLC 侧下载配置服务。\n" +
                "若直接下载未成功，建议先 plcsim_connect_to_plcsim 后使用 download_to_device 工具配合 PLCSIM 网络适配器。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("TIA Portal 项目中的 PLC 设备名称（必填）"),
                    ["instanceName"] = StrProp("PLCSIM 实例名称（必填）"),
                }, new[] { "plcName", "instanceName" }),
                args => tia.Value.PlcSimDownloadToInstance(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_upload_from_instance",
                "从 PLCSIM 实例上传到 TIA Portal 项目。\n" +
                "plcName 对应 TIA Portal 项目中的 PLC 设备，instanceName 对应已创建并上电的 PLCSIM 实例。\n" +
                "反射探测 instance.Upload(plc) / ReadBlocks(plc) / ReadFromPlc(plc) 方法。",
                Props(new Dictionary<string, object>
                {
                    ["plcName"] = StrProp("TIA Portal 项目中的 PLC 设备名称（必填）"),
                    ["instanceName"] = StrProp("PLCSIM 实例名称（必填）"),
                }, new[] { "plcName", "instanceName" }),
                args => tia.Value.PlcSimUploadFromInstance(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_get_instance_info",
                "获取 PLCSIM 实例详细信息。\n" +
                "反射收集实例所有可读属性、方法名（methods）、属性名（propertyNames），便于 AI 理解 API 结构。\n" +
                "用于 PLCSIM API 探索与诊断。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("实例名称（必填）"),
                }, new[] { "instanceName" }),
                args => tia.Value.PlcSimGetInstanceInfo(
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            // ═════════════════════════════════════════════════════════════════════════════
            // 四、PLCSIM Advanced 适配器管理
            // ═════════════════════════════════════════════════════════════════════════════

            server.RegisterTool("plcsim_advanced_list_adapters",
                "列出 PLCSIM Advanced 适配器。\n" +
                "加载 ConfigureAdapters DLL（D:\\portal\\PLCSIMADV\\bin\\Siemens.Simatic.PlcSim.Advanced.ConfigureAdapters.dll），\n" +
                "反射调用 ConfigureAdapters.GetAdapters() / ListAdapters() / Adapters 等方法。\n" +
                "失败时返回 triedPaths 和程序集类型列表便于诊断。",
                EmptySchema(),
                _ => tia.Value.PlcSimAdvancedListAdapters().ToString(Formatting.None));

            server.RegisterTool("plcsim_io_probe",
                "★IO闭环探测★ 反射转储 PLCSIM 实例的成员、运行时接口获取路径、\n" +
                "ProcessValue/ProcessValueAddress 类型结构（构造器/属性/字段）与 Types 命名空间全部枚举值。\n" +
                "用于确定 plcsim_write_values / plcsim_read_values 的正确调用方式。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("PLCSIM 实例名称（必填，需先 plcsim_create_instance）"),
                }, new[] { "instanceName" }),
                args => tia.Value.PlcSimIoProbe(
                    GetStringArg(args, "instanceName") ?? "").ToString(Formatting.None));

            server.RegisterTool("plcsim_write_values",
                "★仿真激励注入★ 写 PLCSIM 过程映像值。\n" +
                "writesJson: JSON 数组，元素如 {\"area\":\"I\"|\"M\"|\"Q\",\"byte\":0,\"bit\":0,\"value\":true} 或 {\"area\":\"M\",\"byte\":10,\"value\":1}。\n" +
                "I 区位值走 SetInputBit/SetInput，M 区走 SetTagValue（符号/绝对地址由 ProcessValue 承载）。\n" +
                "返回每个写入的成功状态与反射尝试日志。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("PLCSIM 实例名称（必填）"),
                    ["writesJson"] = StrProp("写入列表 JSON 数组（必填），元素 {area,byte,bit?,value}"),
                }, new[] { "instanceName", "writesJson" }),
                args => tia.Value.PlcSimWriteValues(
                    GetStringArg(args, "instanceName") ?? "",
                    GetStringArg(args, "writesJson") ?? "[]").ToString(Formatting.None));

            server.RegisterTool("plcsim_read_values",
                "★仿真输出观测★ 读 PLCSIM 输出映像（ReadOutput）。\n" +
                "返回十六进制字节与置位位列表（Q 区）。byteOffset 起始字节、count 字节数。\n" +
                "用于仿真测试断言：读 Q0.0/Q0.1/Q0.2 等输出线圈状态。",
                Props(new Dictionary<string, object>
                {
                    ["instanceName"] = StrProp("PLCSIM 实例名称（必填）"),
                    ["byteOffset"] = IntProp("起始字节（默认 0）"),
                    ["count"] = IntProp("读取字节数（默认 4）"),
                }, new[] { "instanceName" }),
                args => tia.Value.PlcSimReadValues(
                    GetStringArg(args, "instanceName") ?? "",
                    GetIntArg(args, "byteOffset") ?? 0,
                    GetIntArg(args, "count") ?? 4).ToString(Formatting.None));
        }
    }
}
