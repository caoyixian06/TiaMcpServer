using System;
using System.Collections.Generic;

namespace TiaMcpServer
{
    public partial class McpServer
    {
        internal static void RegisterXmlOrchestrationTools(McpServer server, Lazy<PortalService> tia)
        {
            var orchestration = new XmlOrchestrationService(tia);

            ToolMetadata ReadOnlyIr(string name) => new ToolMetadata
            {
                OriginalName = name, RiskLevel = ToolRiskLevel.Low, ReadOnly = true, Idempotent = true,
                Scope = ToolOperationScope.ReadOnly, MutatesProject = false, RequiresSnapshot = false
            };
            ToolMetadata ProjectWriteIr(string name) => new ToolMetadata
            {
                OriginalName = name, RiskLevel = ToolRiskLevel.Medium, ReadOnly = false, Idempotent = false,
                Scope = ToolOperationScope.Project, MutatesProject = true, RequiresSnapshot = true
            };

            server.RegisterTool("get_xml_ir_capabilities",
                "返回 XML 编排器支持的稳定 IR、LAD 指令目录和 HMI 组件目录。AI 应先调用本工具，不需要读取服务器源码。",
                EmptySchema(), _ => orchestration.GetCapabilities(), ReadOnlyIr("get_xml_ir_capabilities"));

            server.RegisterTool("get_hmi_ir_capabilities",
                "返回画面编排器当前支持的控件别名、必需绑定、动画、事件函数、图层/分组约束和明确不支持项。",
                EmptySchema(), _ => orchestration.GetHmiCapabilities(), ReadOnlyIr("get_hmi_ir_capabilities"));

            server.RegisterTool("compile_lad_ir",
                "把简洁 LAD IR 编译成服务器现有的确定性 LAD 网络模型；不写入 TIA，不需要 UId、Wire、FlgNet 或 Siemens XML。",
                Props(new Dictionary<string, object>
                {
                    ["irJson"] = StrProp("LAD IR JSON。根对象包含 networks；每个网络包含 title、variables、steps。step type 支持 contact/edgeContact/coil/setCoil/resetCoil/box/call/parallel。")
                }, new[] { "irJson" }),
                args => orchestration.CompileLadIr(GetStringArg(args, "irJson") ?? "{}"), ReadOnlyIr("compile_lad_ir"));

            server.RegisterTool("validate_lad_ir",
                "使用正式项目样本规则校验 LAD IR：检查边沿存储位、线圈模式、串并联结构、指令引脚、FB 调用和输出拓扑；不写入 TIA。",
                Props(new Dictionary<string, object>
                {
                    ["irJson"] = StrProp("LAD IR JSON")
                }, new[] { "irJson" }),
                args => orchestration.ValidateLadIr(GetStringArg(args, "irJson") ?? "{}"), ReadOnlyIr("validate_lad_ir"));

            server.RegisterTool("get_lad_reference_profile",
                "返回从用户正式 TIA V19 工程提取的 LAD 指令统计、已支持结构和暂缓结构，AI 无需读取样本 XML。",
                EmptySchema(), _ => orchestration.GetLadReferenceProfile(), ReadOnlyIr("get_lad_reference_profile"));

            server.RegisterTool("apply_lad_ir",
                "推荐的 LAD 写入入口：服务器先编译/校验 IR，再复用现有 FlgNetBuilder 和块 XML 编排链写入，AI 不应手写 XML。",
                Props(new Dictionary<string, object>
                {
                    ["blockName"] = StrProp("目标 LAD 块名称"),
                    ["irJson"] = StrProp("LAD IR JSON"),
                    ["plcName"] = StrProp("PLC 名称（可选）"),
                    ["compileAfter"] = BoolProp("写入后编译，默认 true")
                }, new[] { "blockName", "irJson" }),
                args => orchestration.ApplyLadIr(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "irJson") ?? "{}",
                    GetStringArg(args, "plcName"),
                    GetBoolArg(args, "compileAfter") ?? true), ProjectWriteIr("apply_lad_ir"));

            server.RegisterTool("compile_hmi_ir",
                "把网格或像素 HMI IR 转为正式画面规格。支持 Rectangle/Text/Indicator/Button/Navigation/Switch/IOField/Line/SymbolicIOField/GraphicView/Group、多画面层和经样本验证的动画；不导入 TIA。",
                Props(new Dictionary<string, object>
                {
                    ["irJson"] = StrProp("HMI IR JSON，screen 中包含 width/height/columns/rows/components")
                }, new[] { "irJson" }),
                args => orchestration.CompileHmiIr(GetStringArg(args, "irJson") ?? "{}"), ReadOnlyIr("compile_hmi_ir"));

            server.RegisterTool("validate_hmi_ir",
                "使用与 apply_hmi_ir 相同的控件目录和规格校验器，检查控件绑定、事件签名、动画、图层、Group 成员、依赖和布局；不写入 TIA。",
                Props(new Dictionary<string, object>
                {
                    ["irJson"] = StrProp("HMI IR JSON")
                }, new[] { "irJson" }),
                args => orchestration.ValidateHmiIr(GetStringArg(args, "irJson") ?? "{}"), ReadOnlyIr("validate_hmi_ir"));

            server.RegisterTool("apply_hmi_ir",
                "推荐的 HMI 写入入口：校验并归一化 HMI IR，生成真实 WinCC XML 后导入目标 HMI。AI 不应手写按钮事件或变量绑定 XML。",
                Props(new Dictionary<string, object>
                {
                    ["screenName"] = StrProp("目标画面名称"),
                    ["irJson"] = StrProp("HMI IR JSON"),
                    ["hmiDeviceName"] = StrProp("HMI 设备名称（可选）")
                }, new[] { "screenName", "irJson" }),
                args => orchestration.ApplyHmiIr(
                    GetStringArg(args, "screenName") ?? "",
                    GetStringArg(args, "irJson") ?? "{}",
                    GetStringArg(args, "hmiDeviceName")), ProjectWriteIr("apply_hmi_ir"));
        }
    }
}
