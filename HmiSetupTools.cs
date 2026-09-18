using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// HMI 快速配置工具注册（setup_network_and_hmi_connection / import_hmi_tags_absolute / create_hmi_screen_from_spec）
    /// </summary>
    public partial class McpServer
    {
        internal static void RegisterHmiSetupTools(McpServer server, Lazy<PortalService> tia)
        {
            // ────────────────────────────────────────────────
            // 工具1: setup_network_and_hmi_connection
            // ────────────────────────────────────────────────
            server.RegisterTool("setup_network_and_hmi_connection",
                "配置 PROFINET 子网及 PLC/HMI IP。\n" +
                "integrated（默认）：将 PLC PN 接口与 HMI 以太网接口接入同一 PN/IE 子网并设置同网段 IP；TIA 自动生成默认集成连接名，不单独创建或导入连接 XML。\n" +
                "nonIntegrated：仅在明确要求独立 IP 连接时创建可导入/导出的非集成连接，变量通常使用绝对地址。\n" +
                "注意：connectionMode=nonIntegrated 时必须提供 connectionTemplateFilePath（从现有项目导出的连接 XML，可用 export_hmi_connection 导出），否则创建会失败。",
                Props(new Dictionary<string, object>
                {
                    ["subnetName"] = StrProp("子网名称，默认'PN/IE_1'"),
                    ["plcDeviceName"] = StrProp("PLC设备名称，默认自动查找第一个PLC"),
                    ["hmiDeviceName"] = StrProp("HMI设备名称，默认自动查找第一个HMI"),
                    ["plcIp"] = StrProp("PLC IP地址，默认'192.168.0.1'"),
                    ["hmiIp"] = StrProp("HMI IP地址，默认'192.168.0.2'"),
                    ["subnetMask"] = StrProp("子网掩码，默认'255.255.255.0'"),
                    ["connectionName"] = StrProp("仅 nonIntegrated 使用；integrated 模式由 TIA 自动生成默认连接名"),
                    ["connectionDriver"] = StrProp("仅 nonIntegrated 使用，默认 SIMATIC S7 1200"),
                    ["connectionMode"] = StrProp("integrated 或 nonIntegrated，默认 integrated"),
                    ["connectionTemplateFilePath"] = StrProp("仅用于 nonIntegrated；必须提供（从现有项目导出的连接 XML，可用 export_hmi_connection 导出；集成连接不能导出，不能提供集成模板）"),
                }),
                args => tia.Value.SetupNetworkAndHmiConnection(
                    GetStringArg(args, "subnetName"),
                    GetStringArg(args, "plcDeviceName"),
                    GetStringArg(args, "hmiDeviceName"),
                    GetStringArg(args, "plcIp"),
                    GetStringArg(args, "hmiIp"),
                    GetStringArg(args, "subnetMask"),
                    GetStringArg(args, "connectionName"),
                    GetStringArg(args, "connectionDriver"),
                    GetStringArg(args, "connectionTemplateFilePath"),
                    GetStringArg(args, "connectionMode")
                ));

            // ────────────────────────────────────────────────
            // 工具2: import_hmi_tags_absolute
            // ────────────────────────────────────────────────
            server.RegisterTool("import_hmi_tags_absolute",
                "批量导入 HMI 标签（Absolute 绝对地址模式）。直接绑定 PLC 绝对地址（如%M0.0、%MW2、%MD4），\n" +
                "无需预先在 PLC 变量表中创建符号变量，标签通过LogicalAddress直接访问PLC存储区。\n" +
                "\n" +
                "tagsJson 参数格式（JSON数组）：\n" +
                "[\n" +
                "  {\"name\": \"上行按钮\", \"dataType\": \"Bool\", \"address\": \"%M0.0\"},\n" +
                "  {\"name\": \"当前楼层\", \"dataType\": \"Int\", \"address\": \"%MW2\"},\n" +
                "  {\"name\": \"目标楼层\", \"dataType\": \"DInt\", \"address\": \"%MD4\", \"acquisitionCycle\": \"100 ms\"}\n" +
                "]\n" +
                "\n" +
                "支持的dataType: Bool, Byte, Word, Int, UInt, DWord, DInt, UDInt, Real(浮点，IEEE754)\n" +
                "支持的地址格式: %I0.0(输入), %Q0.0(输出), %M0.0(M位), %MB0(M字节), %MW2(M字), %MD4(M双字)",
                Props(new Dictionary<string, object>
                {
                    ["tagsJson"] = StrProp("标签定义JSON数组，每项含name/dataType/address字段"),
                    ["connectionName"] = StrProp("HMI连接名称，默认自动检测已有连接（需先调用setup_network_and_hmi_connection）"),
                    ["targetTableName"] = StrProp("目标变量表名称，默认使用默认变量表"),
                }, new[] { "tagsJson" }),
                args => tia.Value.ImportHmiTagsAbsolute(
                    GetStringArg(args, "tagsJson") ?? "[]",
                    GetStringArg(args, "connectionName"),
                    GetStringArg(args, "targetTableName")
                ));

            // ────────────────────────────────────────────────
            // 工具3: create_hmi_screen_from_spec
            // ────────────────────────────────────────────────
            server.RegisterTool("validate_hmi_screen_spec",
                "只校验 HMI 画面规格，不写入项目。与 create/apply 共用控件目录，检查边界、文本适配、绑定依赖、按钮事件、动画、图层、Group 成员及显著重叠，并返回归一化规格。",
                Props(new Dictionary<string, object>
                {
                    ["specJson"] = StrProp("画面规格JSON"),
                    ["screenWidth"] = IntProp("可选画布宽度"),
                    ["screenHeight"] = IntProp("可选画布高度")
                }, new[] { "specJson" }),
                args => tia.Value.ValidateHmiScreenSpecJson(
                    GetStringArg(args, "specJson") ?? "{}",
                    GetIntArg(args, "screenWidth"),
                    GetIntArg(args, "screenHeight")));

            server.RegisterTool("create_hmi_screen_from_spec",
                "按 JSON 规格创建 Classic HMI 画面。支持 rectangle、text、indicator、button/navigation、switch、iofield、line、symboliciofield、graphicview、group。\n" +
                "支持 layer=0..31、screen.layers 命名、多参数按钮事件，以及 visibility/singleBitVisibility/enabling 动画。\n" +
                "通常不要传 width/height：程序优先读取目标 HMI 的真实画布尺寸；所有入口共用同一套导入前语义与布局校验。\n" +
                "新增控件示例：{\"type\":\"graphicview\",\"name\":\"MotorIcon\",\"picture\":\"Motor\",\"visibleWhen\":{\"tag\":\"Motor.Run\",\"bitPosition\":0}}。",
                Props(new Dictionary<string, object>
                {
                    ["specJson"] = StrProp("画面规格JSON，包含 screenName/backColor/items；通常不要传 width/height，交由程序读取目标画布"),
                    ["hmiDeviceName"] = StrProp("HMI设备名称，默认自动查找第一个HMI"),
                }, new[] { "specJson" }),
                args => tia.Value.CreateHmiScreenFromSpec(
                    GetStringArg(args, "specJson") ?? "{}",
                    GetStringArg(args, "hmiDeviceName")
                ));
        }
    }
}
