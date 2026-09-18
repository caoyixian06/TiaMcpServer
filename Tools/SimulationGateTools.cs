using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TiaMcpServer
{
    /// <summary>阶段2机制工具注册：模拟门禁 + 一遍过写入。</summary>
    public partial class McpServer
    {
        internal static void RegisterSimulationGateTools(McpServer server, Lazy<PortalService> tia)
        {
            server.RegisterTool("verify_lad_simulation",
                "★写入前必模拟★ 对单个 LAD 网络 JSON 执行模拟门禁：\n" +
                "IR/遗留格式 → 候选 FlgNet XML（不进 TIA）→ xml2net → lad-sim 静态执行 50 周期。\n" +
                "返回 {simulationPass, networks, parts, unimplemented[], warnings[]}。\n" +
                "判定：有未实现元件或未求值元件即 FAIL。写入工程前请先跑本工具（write_lad_safe 已内置）。",
                Props(new Dictionary<string, object>
                {
                    ["networkJson"] = StrProp("网络 JSON（IR steps 格式或 read_lad_network 直通格式，JSON 字符串）"),
                    ["cycles"] = IntProp("静态执行周期数（默认 50）"),
                }, new[] { "networkJson" }),
                args => tia.Value.VerifyLadSimulation(
                    GetStringArg(args, "networkJson") ?? "",
                    GetIntArg(args, "cycles") ?? 50));

            server.RegisterTool("write_lad_safe",
                "★一遍过写入★ 模拟门禁 PASS → 写入 TIA → 编译 → 回读验证，任一环节失败立即停止且不污染工程。\n" +
                "target=append 追加网络；target=update 替换 networkIndex（从0开始）指定网络。\n" +
                "networkJson 支持 IR steps 格式与 read_lad_network 直通格式。\n" +
                "返回 {success, verified, simulation, readBack}。除非确有必要，禁止 skipSimulation=true。",
                Props(new Dictionary<string, object>
                {
                    ["blockName"] = StrProp("目标块名称"),
                    ["networkJson"] = StrProp("网络 JSON（IR steps 或直通格式，JSON 字符串）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["target"] = StrProp("append / update（默认 append）"),
                    ["networkIndex"] = IntProp("update 时的网络索引（从 0 开始，默认 0）"),
                    ["compileAfter"] = BoolProp("写入后是否编译（默认 true）"),
                    ["skipSimulation"] = BoolProp("跳过模拟门禁（默认 false，强烈不建议）"),
                }, new[] { "blockName", "networkJson" }),
                args => tia.Value.WriteLadSafe(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "networkJson") ?? "",
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "target") ?? "append",
                    GetIntArg(args, "networkIndex") ?? 0,
                    GetBoolArg(args, "compileAfter") ?? true,
                    GetBoolArg(args, "skipSimulation") ?? false));
        }
    }
}
