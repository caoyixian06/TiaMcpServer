using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

public partial class McpServer
{
    internal static void RegisterDownloadTools(McpServer server, Lazy<PortalService> tia)
    {
        server.RegisterTool("download_to_folder",
            "将项目软件下载到本地文件夹（离线下载/导出）。",
            Props(new Dictionary<string, object>
            {
                ["folderPath"] = StrProp("目标文件夹路径"),
            }, new[] { "folderPath" }),
            args => tia.Value.DownloadToFolder(
                GetStringArg(args, "folderPath") ?? ""));

        // ── 完整版下载/上传/CPU 控制（V19 反射实现）──

        server.RegisterTool("download_check",
            "下载前检查（DownloadProvider.Check）。\n" +
            "通过反射调用 DownloadProvider 上的 Check 方法，返回 success/checkResult/changesCount。\n" +
            "若当前博途版本未暴露 Check 方法，会返回 apiExplored（可用方法签名列表）供诊断。\n" +
            "plcName 为空时使用项目中的第一个 PLC。",
            Props(new Dictionary<string, object>
            {
                ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
            }),
            args => tia.Value.DownloadCheck(GetStringArg(args, "plcName") ?? ""));

        server.RegisterTool("download_to_device_full",
            "完整参数版下载到设备。\n" +
            "downloadMode: Complete 完整下载 / Differences 差异下载 / StopFirst 停止后下载。\n" +
            "通过反射构建 DownloadConfiguration 并调用 Download(IConfiguration, Delegate, Delegate, DownloadOptions) 4 参数重载。\n" +
            "stopModules=true 或 StopFirst 模式时会先反射调用 Configuration.Stop 停止 PLC。\n" +
            "startAfterDownload=true 且下载成功时会反射调用 Configuration.Start 启动 PLC。\n" +
            "pcInterfaceName 可选，指定 PG/PC 接口名称（含此关键字即可）。\n" +
            "返回 success/downloadMode/result，包含 stopActions/startActions/interfaceActions 操作日志。",
            Props(new Dictionary<string, object>
            {
                ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
                ["downloadMode"] = StrProp("下载模式：Complete / Differences / StopFirst（默认 Complete）"),
                ["stopModules"] = BoolProp("是否先停止模块（默认 false，StopFirst 模式下自动停止）"),
                ["startAfterDownload"] = BoolProp("下载成功后是否启动 CPU（默认 false）"),
                ["confirm"] = BoolProp("兼容参数（默认 false）。V3 安全审批由服务器 GUI 执行，本参数不再作为授权依据；对话框抑制仍由 set_dialog_suppression 单独控制。"),
                ["pcInterfaceName"] = StrProp("PG/PC 接口名称（可选，含此关键字即匹配）"),
            }, new[] { "plcName" }),
            args => tia.Value.DownloadToDeviceFull(
                GetStringArg(args, "plcName") ?? "",
                GetStringArg(args, "downloadMode") ?? "Complete",
                GetBoolArg(args, "stopModules") ?? false,
                GetBoolArg(args, "startAfterDownload") ?? false,
                GetBoolArg(args, "confirm") ?? false,
                GetStringArg(args, "pcInterfaceName")));

        server.RegisterTool("upload_station",
            "完整上传站点。\n" +
            "通过反射获取 StationUploadProvider/UploadProvider 并构建 UploadConfiguration。\n" +
            "modeName: Consistent 一致性上传 / All 全部上传 / SoftwareOnly 仅软件上传。\n" +
            "addressIndex: 目标地址索引（默认 0，即第一个 ConfigurationAddress）。\n" +
            "readPassword/writePassword: 可选的读/写密码（自动转为 SecureString）。\n" +
            "pcInterfaceName: 可选，指定 PG/PC 接口名称。\n" +
            "返回 success/uploadedItems，包含 interfaceActions/passwordActions 操作日志。\n" +
            "★重要★ 反射会依次尝试所有 Upload* 方法重载，失败时返回 triedMethods 供诊断。",
            Props(new Dictionary<string, object>
            {
                ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
                ["modeName"] = StrProp("上传模式：Consistent / All / SoftwareOnly（默认 Consistent）"),
                ["pcInterfaceName"] = StrProp("PG/PC 接口名称（可选，含此关键字即匹配）"),
                ["addressIndex"] = IntProp("目标地址索引（默认 0）"),
                ["readPassword"] = StrProp("读密码（可选，自动转为 SecureString）"),
                ["writePassword"] = StrProp("写密码（可选，自动转为 SecureString）"),
                ["confirm"] = BoolProp("兼容参数（默认 false）。V3 安全审批由服务器 GUI 执行，本参数不作为授权依据。"),
            }, new[] { "plcName" }),
            args => tia.Value.UploadStation(
                GetStringArg(args, "plcName") ?? "",
                GetStringArg(args, "modeName") ?? "Consistent",
                GetStringArg(args, "pcInterfaceName"),
                GetIntArg(args, "addressIndex") ?? 0,
                GetStringArg(args, "readPassword"),
                GetStringArg(args, "writePassword"),
                GetBoolArg(args, "confirm") ?? false));

        server.RegisterTool("upload_check",
            "上传前检查（UploadProvider.Check）。\n" +
            "通过反射获取 StationUploadProvider/UploadProvider 并调用 Check 方法，返回 success/checkResult。\n" +
            "若当前博途版本未暴露 Check 方法，会返回 apiExplored 供诊断。\n" +
            "plcName 为空时使用项目中的第一个 PLC。",
            Props(new Dictionary<string, object>
            {
                ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
            }),
            args => tia.Value.UploadCheck(GetStringArg(args, "plcName") ?? ""));

        server.RegisterTool("cpu_stop",
            "停止 CPU。\n" +
            "通过反射获取 OnlineProvider（回退 DownloadProvider），调用 Configuration.Stop/StopPlc/StopCpu 方法。\n" +
            "返回 success/plcName/action=stop/invokedMethod。\n" +
            "若未找到 Stop 方法，返回 apiExplored 供诊断。",
            Props(new Dictionary<string, object>
            {
                ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
            }, new[] { "plcName" }),
            args => tia.Value.CpuStop(GetStringArg(args, "plcName") ?? ""));

        server.RegisterTool("cpu_start",
            "启动 CPU。\n" +
            "通过反射获取 OnlineProvider（回退 DownloadProvider），调用 Configuration.Start/StartPlc/StartCpu 方法。\n" +
            "返回 success/plcName/action=start/invokedMethod。\n" +
            "若未找到 Start 方法，返回 apiExplored 供诊断。",
            Props(new Dictionary<string, object>
            {
                ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
            }, new[] { "plcName" }),
            args => tia.Value.CpuStart(GetStringArg(args, "plcName") ?? ""));

        server.RegisterTool("scan_devices",
            "扫描网络设备。\n" +
            "通过反射获取 PG/PC 接口的 GetAccessibleDevices() 方法。\n" +
            "pcInterfaceName 可选，指定 PG/PC 接口名称（含此关键字即匹配）；为空时使用第一个接口。\n" +
            "返回 foundDevices 列表（IP/DeviceName/DeviceType/MACAddress）。",
            Props(new Dictionary<string, object>
            {
                ["pcInterfaceName"] = StrProp("PG/PC 接口名称（可选，含此关键字即匹配；为空时使用第一个接口）"),
            }),
            args => tia.Value.ScanDevices(GetStringArg(args, "pcInterfaceName") ?? ""));

        server.RegisterTool("configure_connection",
            "配置在线连接。\n" +
            "设置 PG/PC 接口和目标设备 IP。\n" +
            "pcInterfaceName: 反射尝试在 Configuration.Modes[0].PcInterfaces 中按名称匹配并调用 Select/SetInterface。\n" +
            "deviceIp: 反射尝试在 ConfigurationAddress 上设置 IpAddress/Address 属性或调用 SetAddress/SetIp 方法。\n" +
            "返回 success/plcName/pcInterface/deviceIp/actions，actions 包含每步操作的执行结果描述。",
            Props(new Dictionary<string, object>
            {
                ["plcName"] = StrProp("PLC 名称（可选，多 PLC 项目指定目标 PLC）"),
                ["pcInterfaceName"] = StrProp("PG/PC 接口名称（含此关键字即匹配）"),
                ["deviceIp"] = StrProp("目标设备 IP 地址（如 192.168.0.1）"),
            }, new[] { "plcName", "pcInterfaceName", "deviceIp" }),
            args => tia.Value.ConfigureConnection(
                GetStringArg(args, "plcName") ?? "",
                GetStringArg(args, "pcInterfaceName") ?? "",
                GetStringArg(args, "deviceIp") ?? ""));

        server.RegisterTool("save_as_project",
            "项目另存为。\n" +
            "通过 Project.SaveAs() 方法。\n" +
            "projectPath 可以是目录（自动用目录名作为项目名）或 .apXX/.zapXX 文件路径。\n" +
            "返回 success/newPath。",
            Props(new Dictionary<string, object>
            {
                ["projectPath"] = StrProp("目标路径（目录或 .apXX/.zapXX 文件路径）"),
            }, new[] { "projectPath" }),
            args => tia.Value.SaveAsProject(GetStringArg(args, "projectPath") ?? ""));
    }
}
