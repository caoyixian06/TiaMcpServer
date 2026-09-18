using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

/// <summary>
/// 阶段2机制：LAD 写入前的模拟门禁 + 写后回读验证。
/// 管线：IR steps JSON → CompileLadIr → FlgNetBuilder.Build → 候选块XML（不进TIA）
///       → node simulator/simcheck.mjs（xml2net + lad-sim 静态执行）→ PASS 才允许写入。
/// </summary>
public partial class PortalService
{
    /// <summary>模拟器 CLI 相对路径（随仓库分发，见 simulator/ 目录）。</summary>
    private static readonly string SimCheckRelPath = Path.Combine("simulator", "simcheck.mjs");

    /// <summary>
    /// 统一输入入口：识别 IR steps/networks 格式并转换为遗留 LadNetworkDef JSON。
    /// 与 AddLadNetwork 内联逻辑同源；供门禁与 update_network 复用。
    /// 返回 null 表示输入不是 IR 格式（按原样继续走遗留解析）。
    /// </summary>
    internal string? TryConvertIrToLegacyJson(string networkJson)
    {
        try
        {
            var root = JObject.Parse(networkJson);
            var hasSteps = root["steps"] is JArray;
            var networks = root["networks"] as JArray;
            if (!hasSteps && !(networks != null && networks.Count > 0)) return null;
            if (networks != null && networks.Count > 1)
                throw new InvalidOperationException($"IR 格式包含 {networks.Count} 个网络；本工具每次只接受一个网络。");
            var irJson = hasSteps ? root.ToString(Formatting.None) : networks![0]!.ToString(Formatting.None);
            var compiled = new XmlOrchestrationService(new Lazy<PortalService>(() => this)).CompileLadIr(irJson);
            var compiledObj = JObject.Parse(compiled);
            if (compiledObj["success"]?.Value<bool>() != true)
                throw new InvalidOperationException("IR 编译失败: " + compiledObj["error"]?.ToString());
            var legacyArr = compiledObj["legacyNetworks"] as JArray;
            if (legacyArr == null || legacyArr.Count == 0)
                throw new InvalidOperationException("IR 编译失败：未生成网络定义");
            return legacyArr[0]!.ToString(Formatting.None);
        }
        catch (JsonException)
        {
            return null; // 非 JSON → 按遗留格式处理
        }
    }

    /// <summary>
    /// 由网络 JSON（IR 或遗留格式）构建候选 FlgNet XML 并包装成最小块文档。
    /// 不接触 TIA 工程，仅供模拟门禁使用。
    /// </summary>
    private (string? xml, string? error) BuildCandidateBlockXml(string networkJson)
    {
        try
        {
            var legacy = TryConvertIrToLegacyJson(networkJson) ?? networkJson;
            var def = JsonConvert.DeserializeObject<LadNetworkDef>(legacy);
            if (def == null) return (null, "networkJson 解析失败");
            var flgNetList = FlgNetBuilder.Build(def);
            if (flgNetList == null || flgNetList.Count == 0) return (null, "FlgNet 构建失败（触点/线圈/盒子为空?）");
            var wrapped =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Document>\n  <Engineering version=\"V17\" />\n" +
                "  <SW.Blocks.FB>\n    <ObjectList>\n" +
                "      <SW.Blocks.CompileUnit ID=\"A\" CompositionName=\"CompileUnits\">\n" +
                "        <AttributeList>\n" +
                "          <NetworkSource>" + flgNetList[0] + "</NetworkSource>\n" +
                "          <ProgrammingLanguage>LAD</ProgrammingLanguage>\n" +
                "        </AttributeList>\n" +
                "      </SW.Blocks.CompileUnit>\n" +
                "    </ObjectList>\n  </SW.Blocks.FB>\n</Document>";
            return (wrapped, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>定位模拟器 simcheck.mjs（仓库根相对，兼容不同工作目录）。</summary>
    private static string? LocateSimCheck()
    {
        foreach (var baseDir in new[]
                 {
                     AppDomain.CurrentDomain.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            var candidate = Path.Combine(baseDir, SimCheckRelPath);
            if (File.Exists(candidate)) return candidate;
            // bin\Release\net48 → 仓库根上溯三级
            var up = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", SimCheckRelPath));
            if (File.Exists(up)) return up;
        }
        return null;
    }

    /// <summary>
    /// 模拟门禁：对网络 JSON 执行候选 XML 生成 + lad-sim 静态执行。
    /// 返回 { success, simulationPass, networks, parts, unimplemented[], warnings[], error }。
    /// </summary>
    public string VerifyLadSimulation(string networkJson, int cycles = 50)
    {
        lock (_lock)
        {
            try
            {
                var (xml, buildErr) = BuildCandidateBlockXml(networkJson);
                if (xml == null) return MakeError("候选 XML 构建失败: " + buildErr).ToString(Formatting.None);

                var simCheck = LocateSimCheck();
                if (simCheck == null)
                    return MakeError("未找到 simulator/simcheck.mjs（模拟门禁依赖缺失，请检查仓库完整性）").ToString(Formatting.None);

                var tempXml = Path.Combine(Path.GetTempPath(), "tia_sim_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tempXml, xml);
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "node",
                        Arguments = $"\"{simCheck}\" \"{tempXml}\" {Math.Max(1, cycles)}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi)!;
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(120_000))
                    {
                        try { proc.Kill(); } catch { }
                        return MakeError("模拟执行超时（120s）").ToString(Formatting.None);
                    }
                    if (string.IsNullOrWhiteSpace(stdout))
                        return MakeError("模拟器无输出: " + stderr.Substring(0, Math.Min(200, stderr.Length))).ToString(Formatting.None);

                    var result = JObject.Parse(stdout.Trim());
                    result["success"] = result["pass"]?.Value<bool>() == true;
                    result["simulationPass"] = result["pass"]?.Value<bool>() == true;
                    result["gate"] = "lad-sim 静态执行（写入前必模拟）";
                    return result.ToString(Formatting.None);
                }
                finally
                {
                    try { File.Delete(tempXml); } catch { }
                }
            }
            catch (Exception ex) { return MakeException(ex).ToString(Formatting.None); }
        }
    }

    /// <summary>
    /// 一遍过写入：模拟门禁 PASS → 写入（append/update）→ 编译 → 回读验证。
    /// 任一环节失败立即返回，不污染工程。
    /// networkJson 支持 IR steps 格式与 read_lad_network 直通格式。
    /// </summary>
    public string WriteLadSafe(string blockName, string networkJson, string? plcName = null,
        string target = "append", int networkIndex = 0, bool compileAfter = true, bool skipSimulation = false)
    {
        lock (_lock)
        {
            try
            {
                RequireProject();
                if (string.IsNullOrEmpty(blockName)) return MakeError("blockName 不能为空").ToString(Formatting.None);

                // ① 模拟门禁
                JObject sim;
                if (skipSimulation)
                {
                    sim = new JObject
                    {
                        ["success"] = false,
                        ["simulationPass"] = false,
                        ["skipped"] = true,
                        ["warning"] = "已显式跳过模拟门禁——此行为会被记录",
                    };
                }
                else
                {
                    var vr = VerifyLadSimulation(networkJson);
                    sim = JObject.Parse(vr);
                    if (sim["simulationPass"]?.Value<bool>() != true)
                    {
                        sim["action"] = "已拒绝写入 TIA 工程（先修复逻辑再重试）";
                        return sim.ToString(Formatting.Indented);
                    }
                }

                // ② 记录写入前网络数（append 回读用）
                int beforeCount = -1;
                if (!string.Equals(target, "update", StringComparison.OrdinalIgnoreCase))
                {
                    var lb = ListBlockNetworks(blockName, plcName);
                    var lbObj = JObject.Parse(lb);
                    beforeCount = lbObj["networks"] is JArray arr ? arr.Count : -1;
                }

                // ③ 写入
                string writeResult = string.Equals(target, "update", StringComparison.OrdinalIgnoreCase)
                    ? UpdateNetwork(plcName, blockName, networkIndex, networkJson)
                    : AddLadNetwork(blockName, networkJson, plcName, compileAfter);
                var writeObj = JObject.Parse(writeResult);
                if (writeObj["success"]?.Value<bool>() != true)
                {
                    writeObj["stage"] = "write";
                    return writeObj.ToString(Formatting.Indented);
                }

                // ④ 编译（update 路径由 UpdateNetwork 自行保证；append 已在 AddLadNetwork 编译）
                // ⑤ 回读验证
                int readNumber = string.Equals(target, "update", StringComparison.OrdinalIgnoreCase)
                    ? networkIndex + 1
                    : beforeCount + 1;
                var rb = ReadLadNetwork(blockName, readNumber, plcName);
                var rbObj = JObject.Parse(rb);
                bool verified = rbObj["parts"] is JArray partsArr && partsArr.Count > 0;

                return new JObject
                {
                    ["success"] = true,
                    ["verified"] = verified,
                    ["stage"] = "done",
                    ["target"] = target,
                    ["blockName"] = blockName,
                    ["networkNumber"] = readNumber,
                    ["simulation"] = sim,
                    ["readBack"] = new JObject
                    {
                        ["title"] = rbObj["title"]?.ToString() ?? "",
                        ["partCount"] = rbObj["parts"] is JArray pa ? pa.Count : 0,
                    },
                    ["message"] = verified
                        ? $"已写入并回读验证：网{readNumber}「{rbObj["title"]}」"
                        : $"已写入但回读异常，请用 read_lad_network 检查网{readNumber}",
                }.ToString(Formatting.Indented);
            }
            catch (Exception ex) { return MakeException(ex).ToString(Formatting.None); }
        }
    }
}
