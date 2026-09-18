using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

namespace TiaMcpServer
{
    /// <summary>
    /// PLC 软件操作：块、变量表、编译。所有方法由 PlcTools.cs 注册为 MCP 工具。
    /// </summary>
    public partial class PortalService
    {
        /// <summary>缓存每个 LAD 块的 FlgNet 片段列表，用于 add_lad_network 追加网络</summary>
        private static readonly Dictionary<string, List<string>> _ladFlgNetCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>缓存每个 LAD 块的接口变量列表，用于 add_lad_network 追加变量</summary>
        private static readonly Dictionary<string, List<LadVariableDef>> _ladVarCache = new Dictionary<string, List<LadVariableDef>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>缓存每个 LAD 块的网络标题列表，用于 add_lad_network 追加标题（支持中文网络名）</summary>
        private static readonly Dictionary<string, List<string>> _ladTitleCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>静态缓存专用锁：ClearAllLadCaches（静态）与实例方法并发访问 static 缓存时使用，避免与实例锁 _lock 脱节</summary>
        private static readonly object _staticCacheLock = new object();

        /// <summary>构建 LAD 缓存键：项目路径 + PLC + 块名，避免跨项目及多 PLC 同名块串扰。</summary>
        private string LadCacheKey(string? plcName, string blockName)
            => $"{ProjectPath ?? "<no-project>"}::{plcName ?? "<default-plc>"}::{blockName}";

        /// <summary>根据 plcName 获取 PLC 软件（null/空时返回默认 PLC，未找到时抛异常）</summary>
        private PlcSoftware GetPlcSoftwareFor(string? plcName)
        {
            if (string.IsNullOrEmpty(plcName)) return GetPlcSoftware();
            return FindPlcByName(plcName) ?? throw new InvalidOperationException($"未找到 PLC: {plcName}");
        }

        /// <summary>fix#17: 返回当前 PLC 块列表中第一个空闲的正整数编号（1 起）。
        /// V17 Openness 的 CreateInstanceDB(name, autoNumber:true, 0, …) 实测仍产生编号 0 的
        /// 非法块，自动编号场景统一改用本方法取号后显式创建。</summary>
        private static int FindFreeBlockNumber(PlcSoftware plc)
        {
            var used = new HashSet<int>();
            foreach (var b in GetAllBlocks(plc.BlockGroup))
            {
                if (b.Number > 0) used.Add(b.Number);
            }
            int n = 1;
            while (used.Contains(n)) n++;
            return n;
        }

        // ────────────────────────────────────────────────
        // 块列表
        // ────────────────────────────────────────────────
        public string ListBlocks()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var blocks = new List<object>();
                    foreach (var plc in GetPlcSoftwareList())
                    {
                        foreach (var b in GetAllBlocks(plc.BlockGroup))
                        {
                            blocks.Add(new
                            {
                                name = b.Name,
                                type = GetBlockTypeName(b),
                                number = b.Number,
                                language = b.ProgrammingLanguage.ToString(),
                                plcName = plc.Name
                            });
                        }
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = blocks.Count, blocks });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────
        // 创建块（XML Import 方式，支持 OB/FC/FB/DB）
        // blockNumber = 0 表示自动编号
        // ────────────────────────────────────────────────
        public string CreateBlock(string blockType, string name, string? programmingLanguage, int? blockNumber, string? interfaceJson, bool? forceOverwrite = null, string? plcName = null, string? secondaryType = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    // ★安全检查：同名块已存在时拒绝创建（防止覆盖已有逻辑）
                    var existing = FindBlock(name, plcName);
                    if (existing != null && !(forceOverwrite ?? false))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"块 '{name}' 已存在！为防止覆盖已有逻辑，拒绝创建。如需强制覆盖，设 forceOverwrite=true。"
                        });
                    }

                    var normalizedBlockType = (blockType ?? "").Trim().ToUpperInvariant();
                    if (normalizedBlockType != "OB" && normalizedBlockType != "FC"
                        && normalizedBlockType != "FB" && normalizedBlockType != "DB")
                        return Err($"不支持的块类型: {blockType}。仅支持 OB/FC/FB/DB");

                    var lang = normalizedBlockType == "DB"
                        ? ProgrammingLanguage.DB
                        : BlockXmlBuilder.ParseProgrammingLanguage(programmingLanguage ?? "LAD");
                    // 0 或未提供块号均表示自动编号；仅正数才作为显式编号传递。
                    var num = blockNumber.GetValueOrDefault() > 0 ? blockNumber.Value : 0;
                    // SCL 块: 传空字符串生成含空 Text 元素的 StructuredText
                    var sclCode = lang == ProgrammingLanguage.SCL ? "" : null;
                    // interfaceJson: 直接传入接口 Sections XML（用于 DB 或块接口变量）
                    var sectionsXml = !string.IsNullOrEmpty(interfaceJson) ? interfaceJson : null;

                    // 所有块类型统一按实际 blockType 生成 XML 并导入。
                    // 旧实现对 OB/FC/FB 一律调用 CreateFB，导致请求 FC/OB 时静默创建成 FB。
                    var xml = BlockXmlBuilder.GenerateBlockXml(
                        normalizedBlockType, name, num, lang, null, sectionsXml, sclCode, null, secondaryType);
                    ImportXmlTemp(plc, xml);

                    var created = FindBlock(name, plcName);
                    if (created == null)
                        return Err($"块导入调用已完成，但未在 PLC 中反查到块: {name}");
                    var actualType = GetBlockTypeName(created);
                    if (!actualType.Equals(normalizedBlockType, StringComparison.OrdinalIgnoreCase))
                    {
                        try { created.Delete(); } catch { }
                        return Err($"块类型验证失败：请求 {normalizedBlockType}，实际创建为 {actualType}。已尝试删除错误对象");
                    }
                    // DB 的自动编号不能落成 0；创建后必须回读到合法正数，
                    // 否则立即删除本次对象并报告失败，禁止返回伪成功。
                    if (normalizedBlockType == "DB" && created.Number <= 0)
                    {
                        try { created.Delete(); } catch { }
                        return Err($"DB 自动编号失败：TIA 回读编号为 {created.Number}。已尝试删除非法 DB 对象");
                    }
                    var cacheKey = LadCacheKey(plcName, name);
                    lock (_staticCacheLock)
                    {
                        _ladFlgNetCache.Remove(cacheKey);
                        _ladVarCache.Remove(cacheKey);
                        _ladTitleCache.Remove(cacheKey);
                        // ★ 如果传入了 interfaceJson，解析其中的变量到缓存
                        // 这样 add_lad_network 重新导入时能保留接口变量
                        if (!string.IsNullOrEmpty(sectionsXml))
                        {
                            var parsedVars = ParseInterfaceSectionsXml(sectionsXml);
                            if (parsedVars.Count > 0)
                            {
                                _ladVarCache[cacheKey] = parsedVars;
                            }
                        }
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建块: {name}",
                        blockName = created.Name,
                        blockType = normalizedBlockType,
                        actualBlockType = actualType,
                        blockNumber = created.Number,
                        requestedBlockNumber = num,
                        verified = true
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "创建块"); }
            }
        }

        public string DeleteBlock(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");
                    block.Delete();
                    // ★ 删除块时清除 LAD 缓存，避免残留污染新块（键含 plcName 防止多PLC串扰）
                    var cacheKey = LadCacheKey(plcName, blockName);
                    lock (_staticCacheLock)
                    {
                        _ladFlgNetCache.Remove(cacheKey);
                        _ladVarCache.Remove(cacheKey);
                        _ladTitleCache.Remove(cacheKey);
                    }
                    return Ok($"已删除块: {blockName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 清除所有 LAD 缓存（项目创建/打开/切换时调用，避免跨项目缓存污染）。
        /// </summary>
        public static void ClearAllLadCaches()
        {
            lock (_staticCacheLock)
            {
                _ladFlgNetCache.Clear();
                _ladVarCache.Clear();
                _ladTitleCache.Clear();
            }
        }

        public string GetBlockDetails(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        name = block.Name,
                        type = GetBlockTypeName(block),
                        number = block.Number,
                        language = block.ProgrammingLanguage.ToString(),
                        plcName = GetParentPlcName(block)
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string GetBlockInterface(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var members = new List<object>();
                    try
                    {
                        var secObj = block.GetType().GetProperty("Interface")?.GetValue(block);
                        var sections = secObj as IEnumerable;
                        if (sections != null)
                        {
                            foreach (var sec in sections)
                            {
                                var secName = sec?.GetType().GetProperty("Name")?.GetValue(sec)?.ToString() ?? "Unknown";
                                var mems = sec?.GetType().GetProperty("Members")?.GetValue(sec) as IEnumerable;
                                if (mems != null)
                                {
                                    foreach (var m in mems)
                                    {
                                        var mName = m?.GetType().GetProperty("Name")?.GetValue(m)?.ToString() ?? "";
                                        var mType = m?.GetType().GetProperty("DataType")?.GetValue(m)?.ToString() ?? "";
                                        if (!string.IsNullOrEmpty(mName))
                                            members.Add(new { section = secName, name = mName, datatype = mType });
                                    }
                                }
                            }
                        }
                    }
                    catch { /* 反射失败不阻塞 */ }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName,
                        type = GetBlockTypeName(block),
                        interfaceMembers = members
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ShowBlockInEditor(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");
                    var engObj = (IEngineeringObject)block;
                    var showMethod = engObj.GetType().GetMethod("ShowInEditor",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (showMethod == null)
                        return Err($"当前博途版本不支持在编辑器中打开块: {blockName}");
                    showMethod.Invoke(engObj, null);
                    return Ok($"已在编辑器中打开块: {blockName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────
        // LAD 网络（逐网络添加，避免大块卡死博途）
        // ────────────────────────────────────────────────
        public string AddLadNetwork(string blockName, string networkJson, string? plcName = null, bool compileAfter = true)
        {
            lock (_lock)
            {
                try
                {
                    if (!compileAfter) return Err("正式 LAD 工程写入必须 compileAfter=true；仅 XML 预览工具允许跳过编译。");
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var blockType = GetBlockTypeName(block);
                    var blockNumber = block.Number;
                    var originalBlockXml = GetBlockXmlString(block);

                    // ★V17 修复★ 兼容 XML IR 的 steps 格式（与 validate_lad_ir 同款输入）：
                    //   {"title":"...","steps":[{"type":"contact","tag":"..."},...]}
                    //   或 {"networks":[...]} 包装（仅接受单网络；多网络请用 add_lad_networks_batch）。
                    // 自动复用 IR 编译器转换为遗留 rung 格式，避免两种格式并存导致误用。
                    try
                    {
                        var root = JObject.Parse(networkJson);
                        var hasSteps = root["steps"] is JArray;
                        var networks = root["networks"] as JArray;
                        if (hasSteps || (networks != null && networks.Count > 0))
                        {
                            if (networks != null && networks.Count > 1)
                                return Err($"IR 格式包含 {networks.Count} 个网络；add_lad_network 每次只接受一个网络，请改用 add_lad_networks_batch。");
                            var irJson = hasSteps ? root.ToString(Formatting.None) : networks![0].ToString(Formatting.None);
                            var compiled = new XmlOrchestrationService(new Lazy<PortalService>(() => this)).CompileLadIr(irJson);
                            var compiledObj = JObject.Parse(compiled);
                            if (compiledObj["success"]?.Value<bool>() != true)
                                return compiled;
                            var legacyArr = compiledObj["legacyNetworks"] as JArray;
                            if (legacyArr == null || legacyArr.Count == 0)
                                return Err("IR 转换失败：未生成网络定义");
                            networkJson = legacyArr[0]!.ToString(Formatting.None);
                        }
                    }
                    catch (JsonException)
                    {
                        // 非 JSON 或非 IR 格式 → 按遗留 contacts/coils/rung 格式继续处理
                    }

                    var def = JsonConvert.DeserializeObject<LadNetworkDef>(networkJson)
                              ?? throw new InvalidOperationException("无法解析 networkJson");

                    if (!ValidateBlockInstanceCompatibility(def, blockType, out var instanceCompatibilityError))
                        return Err(instanceCompatibilityError);

                    // ★ 从缓存获取已有变量（不再从块读取，因为 Import 后块不一致）
                    // 缓存键含 plcName，防止多 PLC 项目中同名块缓存串扰
                    var cacheKey = LadCacheKey(plcName, blockName);
                    List<LadVariableDef> allVars;
                    lock (_staticCacheLock)
                    {
                        if (!_ladVarCache.TryGetValue(cacheKey, out allVars))
                        {
                            allVars = new List<LadVariableDef>();
                            _ladVarCache[cacheKey] = allVars;
                        }
                    }

                    // ★ 如果缓存为空，尝试从块接口读取现有变量（create_block 通过 interfaceJson 创建的变量）
                    // 这样可以避免 add_lad_network 重新导入时用空接口覆盖原有接口
                    if (allVars.Count == 0)
                    {
                        var existingVars = ReadExistingInterfaceVars(block);
                        if (existingVars.Count > 0)
                        {
                            allVars.AddRange(existingVars);
                        }
                    }
                    var varCountBefore = allVars.Count;

                    var singleNetwork = new List<LadNetworkDef> { def };
                    if (!PrepareLadProgram(singleNetwork, allVars, ExtractLadInstanceNames(originalBlockXml),
                            out var sharedValidationErrors, out var sharedValidationWarnings))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "LAD 网络未通过 validate/apply 共享规则，未写入项目",
                            validationErrors = sharedValidationErrors,
                            validationWarnings = sharedValidationWarnings
                        }, Formatting.Indented);
                    }
                    def = singleNetwork[0];

                    def.variables ??= new List<LadVariableDef>();

                    // 合并新网络的变量到缓存；同名变量类型/区域冲突必须报错，不能静默沿用旧声明。
                    if (def.variables != null)
                    {
                        foreach (var v in def.variables)
                        {
                            if (string.IsNullOrWhiteSpace(v.name)) continue;
                            var existingVar = allVars.FirstOrDefault(ev => ev.name.Equals(v.name, StringComparison.OrdinalIgnoreCase));
                            if (existingVar == null)
                                allVars.Add(v);
                            else if (!existingVar.datatype.Equals(v.datatype, StringComparison.OrdinalIgnoreCase)
                                     || !existingVar.section.Equals(v.section, StringComparison.OrdinalIgnoreCase))
                            {
                                while (allVars.Count > varCountBefore) allVars.RemoveAt(allVars.Count - 1);
                                return Err($"变量 '{v.name}' 与块现有接口声明冲突：现有 {existingVar.section}/{existingVar.datatype}，请求 {v.section}/{v.datatype}");
                            }
                        }
                    }

                    // 将缓存中的所有变量同步到 def.variables，
                    // 以便 FlgNetBuilder 能正确识别它们为 LocalVariable（避免跨网络引用被误判为 GlobalVariable）
                    if (def.variables == null) def.variables = new List<LadVariableDef>();
                    foreach (var ev in allVars)
                    {
                        if (!def.variables.Any(dv => dv.name.Equals(ev.name, StringComparison.OrdinalIgnoreCase)))
                        {
                            def.variables.Add(new LadVariableDef
                            {
                                name = ev.name,
                                datatype = ev.datatype,
                                section = ev.section
                            });
                        }
                    }

                    if (!ValidateLadNetworkDefinition(def, out var validationErrors, out var validationWarnings))
                    {
                        while (allVars.Count > varCountBefore) allVars.RemoveAt(allVars.Count - 1);
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "LAD 网络未通过结构/语义预检查，未写入项目",
                            validationErrors,
                            validationWarnings
                        }, Formatting.Indented);
                    }

                    // Build only the new FlgNet fragments. The original block XML is patched in place below;
                    // existing CompileUnits and block metadata are never regenerated from a reduced model.
                    var newFlgNetList = FlgNetBuilder.Build(def);

                    // ★ 从缓存获取已有网络的 FlgNet，追加新网络
                    List<string> cachedFlgNets;
                    List<string> cachedTitles;
                    lock (_staticCacheLock)
                    {
                        if (!_ladFlgNetCache.TryGetValue(cacheKey, out cachedFlgNets))
                        {
                            cachedFlgNets = new List<string>();
                            _ladFlgNetCache[cacheKey] = cachedFlgNets;
                        }
                        // ★ 同步缓存网络标题（支持中文网络名）
                        if (!_ladTitleCache.TryGetValue(cacheKey, out cachedTitles))
                        {
                            cachedTitles = new List<string>();
                            _ladTitleCache[cacheKey] = cachedTitles;
                        }
                    }
                    int cacheCountBefore = cachedFlgNets.Count;
                    int titleCountBefore = cachedTitles.Count;
                    // 当缓存为空时，从块现有 XML 读取已有网络 FlgNet
                    if (cacheCountBefore == 0)
                    {
                        var existingFlgNets = new List<string>();
                        var existingTitles = new List<string>();
                        try
                        {
                            var blockXml = GetBlockXmlString(block);
                            var doc = XDocument.Parse(blockXml);
                            var compileUnits = doc.Descendants()
                                .Where(e => e.Name.LocalName.EndsWith("CompileUnit"))
                                .ToList();
                            foreach (var cu in compileUnits)
                            {
                                var netSource = cu.Elements()
                                    .FirstOrDefault(e => e.Name.LocalName == "AttributeList")
                                    ?.Elements()
                                    .FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
                                if (netSource != null)
                                {
                                    var flgXml = netSource.FirstNode?.ToString();
                                    if (!string.IsNullOrEmpty(flgXml))
                                        existingFlgNets.Add(flgXml);
                                }
                                var titleMl = cu.Descendants()
                                    .FirstOrDefault(e => e.Name.LocalName == "MultilingualText"
                                        && e.Attribute("CompositionName")?.Value == "Title");
                                var titleText = titleMl?.Descendants()
                                    .FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ?? "";
                                existingTitles.Add(titleText);
                            }
                        }
                        catch { }
                        foreach (var n in existingFlgNets) cachedFlgNets.Add(n);
                        foreach (var t in existingTitles) cachedTitles.Add(t);
                        cacheCountBefore = cachedFlgNets.Count;
                        titleCountBefore = cachedTitles.Count;
                    }
                    cachedFlgNets.AddRange(newFlgNetList);
                    // 收集本次网络标题（FlgNetBuilder.Build 可能返回多个网络，按顺序对应）
                    foreach (var _ in newFlgNetList)
                    {
                        cachedTitles.Add(def.title ?? "");
                    }
                    var allFlgNetList = cachedFlgNets.ToList();
                    var allTitlesList = cachedTitles.ToList();

                    // originalBlockXml 已在校验前读取，用于实例冲突检测和失败回滚。
                    var xml = BlockXmlBuilder.PatchExistingLadBlockXml(
                        originalBlockXml, blockType, newFlgNetList,
                        Enumerable.Repeat(def.title ?? "", newFlgNetList.Count),
                        ProgrammingLanguage.LAD, allVars);

                    try
                    {
                        // ★V17修复★ V17 块级 Import(Override) 对已有 CompileUnits 的块报
                        // "Objects modeled as IOrdered must be empty before importing"：
                        // 先删除原块再导入同名同号重建（调用引用按名解析恢复），
                        // 失败时用 originalBlockXml 恢复（块已删，可直接创建）。
                        try
                        {
                            block.Delete();
                            Console.Error.WriteLine("[tia-mcp] block deleted ok, exists=" + (FindBlock(blockName, plcName) != null));
                        }
                        catch (Exception delEx)
                        {
                            Console.Error.WriteLine("[tia-mcp] block.Delete failed: " + delEx.Message);
                        }
                        ImportXmlTemp(plc, xml);
                    }
                    catch (Exception importEx)
                    {
                        while (cachedFlgNets.Count > cacheCountBefore)
                            cachedFlgNets.RemoveAt(cachedFlgNets.Count - 1);
                        while (cachedTitles.Count > titleCountBefore)
                            cachedTitles.RemoveAt(cachedTitles.Count - 1);
                        while (allVars.Count > varCountBefore)
                            allVars.RemoveAt(allVars.Count - 1);
                        try { ImportXmlTemp(plc, originalBlockXml); } catch { }
                        throw new Exception(importEx.Message, importEx);
                    }

                    JObject? compilePayload = null;
                    if (compileAfter)
                    {
                        var compileJson = CompileBlock(blockName, plcName);
                        if (!TryReadSuccess(compileJson, out var compileOk, out compilePayload) || !compileOk)
                        {
                            var rollbackOk = true;
                            string? rollbackError = null;
                            try { ImportXmlTemp(plc, originalBlockXml); }
                            catch (Exception rex) { rollbackOk = false; rollbackError = rex.Message; }
                            while (cachedFlgNets.Count > cacheCountBefore)
                                cachedFlgNets.RemoveAt(cachedFlgNets.Count - 1);
                            while (cachedTitles.Count > titleCountBefore)
                                cachedTitles.RemoveAt(cachedTitles.Count - 1);
                            while (allVars.Count > varCountBefore)
                                allVars.RemoveAt(allVars.Count - 1);
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "LAD 网络导入后编译失败，已尝试恢复原块",
                                compile = compilePayload ?? JToken.FromObject(compileJson),
                                rolledBack = rollbackOk,
                                rollbackError,
                                validationWarnings
                            }, Formatting.Indented);
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已添加 LAD 网络到块: {blockName}",
                        blockName,
                        totalNetworks = allFlgNetList.Count,
                        compileAfter,
                        compile = compilePayload,
                        validationWarnings
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "添加 LAD 网络"); }
            }
        }

        /// <summary>
        /// 读取块现有接口变量列表（通过反射）
        /// </summary>
        private List<LadVariableDef> ReadExistingInterfaceVars(object block)
        {
            var vars = new List<LadVariableDef>();
            try
            {
                // 优先尝试反射读取
                var secObj = block.GetType().GetProperty("Interface")?.GetValue(block);
                var sections = secObj as IEnumerable;
                if (sections != null)
                {
                    foreach (var sec in sections)
                    {
                        var secName = sec?.GetType().GetProperty("Name")?.GetValue(sec)?.ToString() ?? "Temp";
                        var mems = sec?.GetType().GetProperty("Members")?.GetValue(sec) as IEnumerable;
                        if (mems != null)
                        {
                            foreach (var m in mems)
                            {
                                var mName = m?.GetType().GetProperty("Name")?.GetValue(m)?.ToString() ?? "";
                                var mType = m?.GetType().GetProperty("DataType")?.GetValue(m)?.ToString() ?? "Bool";
                                if (!string.IsNullOrEmpty(mName))
                                    vars.Add(new LadVariableDef { name = mName, datatype = mType, section = secName });
                            }
                        }
                    }
                }
                // ★ 反射读取为空时（Import 后块不一致），改用导出 XML 解析接口变量
                // 项目记忆：get_block_interface 对 Import 后块返回空，需用 read_data_block_structure 或导出 XML
                if (vars.Count == 0)
                {
                    vars = ReadInterfaceVarsFromExport(block);
                }
            }
            catch { /* 反射失败不阻塞 */ }
            return vars;
        }

        /// <summary>通过导出块 XML 解析接口变量（反射读取失败时的回退方案）</summary>
        private List<LadVariableDef> ReadInterfaceVarsFromExport(object block)
        {
            var vars = new List<LadVariableDef>();
            try
            {
                var blockName = block.GetType().GetProperty("Name")?.GetValue(block)?.ToString() ?? "";
                if (string.IsNullOrEmpty(blockName)) return vars;

                // 导出到临时文件
                var tempPath = Path.Combine(Path.GetTempPath(), $"_iface_{blockName}_{Guid.NewGuid():N}.xml");
                try
                {
                    var exportMethodInfo = block.GetType().GetMethod("Export");
                    if (exportMethodInfo == null) return vars;
                    exportMethodInfo.Invoke(block, new object[] { new FileInfo(tempPath), ExportOptions.WithDefaults });

                    if (!File.Exists(tempPath)) return vars;
                    var xml = File.ReadAllText(tempPath, Encoding.UTF8);
                    vars = ParseInterfaceSectionsXml(xml);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
            catch { /* 导出失败不阻塞 */ }
            return vars;
        }

        /// <summary>解析接口 Sections XML，提取变量列表</summary>
        private static List<LadVariableDef> ParseInterfaceSectionsXml(string xml)
        {
            var vars = new List<LadVariableDef>();
            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                var sections = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections"
                    && e.Parent?.Name.LocalName == "Interface");
                if (sections == null) return vars;

                // Only direct Section/Member children are block-interface declarations. Nested Members belong
                // to timer/trigger/UDT structures and must not be promoted to block-level variables.
                foreach (var section in sections.Elements().Where(e => e.Name.LocalName == "Section"))
                {
                    var sectionName = section.Attribute("Name")?.Value ?? "Temp";
                    foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
                    {
                        var name = member.Attribute("Name")?.Value ?? "";
                        var datatype = member.Attribute("Datatype")?.Value ?? "Bool";
                        if (string.IsNullOrWhiteSpace(name)
                            || name.Equals("Initial_Call", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Remanence", StringComparison.OrdinalIgnoreCase))
                            continue;
                        vars.Add(new LadVariableDef { name = name, datatype = datatype, section = sectionName });
                    }
                }
            }
            catch
            {
                // Export parsing is a fallback. The caller treats an empty result as unavailable and must not
                // overwrite the original block interface based on partial regex matches.
            }
            return vars;
        }

        public string AddLadLogic(string blockName, string? programmingLanguage, string networksJson, string? plcName = null)
        {
            if (!string.IsNullOrWhiteSpace(programmingLanguage)
                && !programmingLanguage!.Equals("LAD", StringComparison.OrdinalIgnoreCase))
                return Err("add_lad_logic 仅允许 LAD。FBD 请使用专用生成器，不能套用 LAD 结构校验规则");
            // 旧入口统一走有校验、编译和失败回滚的批量实现，避免无检查覆盖整块。
            return AddLadNetworksBatch(blockName, networksJson, plcName, compileAfter: true);
        }

        /// <summary>批量追加多个 LAD 网络到已有块（不覆盖已有网络）。</summary>
        public string AddLadNetworksBatch(string blockName, string networksJson, string? plcName = null, bool compileAfter = true)
        {
            lock (_lock)
            {
                try
                {
                    if (!compileAfter) return Err("正式 LAD 工程写入必须 compileAfter=true；仅 XML 预览工具允许跳过编译。");
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var nets = JsonConvert.DeserializeObject<List<LadNetworkDef>>(networksJson);
                    if (nets == null || nets.Count == 0) return Err("networksJson 为空或格式错误");
                    if (nets.Count > 20) return Err("单次最多追加 20 个 LAD 网络；请拆分调用，避免整块重建失败时扩大影响范围");

                    var blockType = GetBlockTypeName(block);
                    var blockNumber = block.Number;
                    var lang = block.ProgrammingLanguage;
                    var originalBlockXml = GetBlockXmlString(block);
                    var cacheKey = LadCacheKey(plcName, blockName);

                    List<LadVariableDef> cachedVars;
                    List<string> cachedFlgNets;
                    List<string> cachedTitles;
                    lock (_staticCacheLock)
                    {
                        if (!_ladVarCache.TryGetValue(cacheKey, out cachedVars))
                            _ladVarCache[cacheKey] = cachedVars = new List<LadVariableDef>();
                        if (!_ladFlgNetCache.TryGetValue(cacheKey, out cachedFlgNets))
                            _ladFlgNetCache[cacheKey] = cachedFlgNets = new List<string>();
                        if (!_ladTitleCache.TryGetValue(cacheKey, out cachedTitles))
                            _ladTitleCache[cacheKey] = cachedTitles = new List<string>();
                    }

                    var workingVars = cachedVars
                        .Select(v => new LadVariableDef { name = v.name, datatype = v.datatype, section = v.section })
                        .ToList();
                    if (workingVars.Count == 0)
                        workingVars.AddRange(ReadExistingInterfaceVars(block));

                    var workingFlgNets = cachedFlgNets.ToList();
                    var workingTitles = cachedTitles.ToList();
                    var newFlgNets = new List<string>();
                    var newTitles = new List<string>();
                    if (workingFlgNets.Count == 0)
                    {
                        try
                        {
                            var doc = XDocument.Parse(originalBlockXml);
                            foreach (var cu in doc.Descendants().Where(e => e.Name.LocalName.EndsWith("CompileUnit")))
                            {
                                var networkSource = cu.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")
                                    ?.Elements().FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
                                var flg = networkSource?.FirstNode?.ToString();
                                if (!string.IsNullOrWhiteSpace(flg)) workingFlgNets.Add(flg!);
                                var title = cu.Descendants()
                                    .FirstOrDefault(e => e.Name.LocalName == "MultilingualText"
                                                         && e.Attribute("CompositionName")?.Value == "Title")
                                    ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ?? "";
                                workingTitles.Add(title);
                            }
                        }
                        catch (Exception ex)
                        {
                            return Err("读取原块网络失败，为避免覆盖原逻辑已停止批量追加: " + ex.Message);
                        }
                    }

                    if (!PrepareLadProgram(
                            nets,
                            workingVars,
                            ExtractLadInstanceNames(originalBlockXml),
                            out var sharedValidationErrors,
                            out var sharedValidationWarnings))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "LAD 批次未通过 validate/apply 共享规则，未写入项目",
                            validationErrors = sharedValidationErrors,
                            validationWarnings = sharedValidationWarnings
                        }, Formatting.Indented);
                    }

                    var validationDetails = new List<object>();
                    var allWarnings = new List<string>(sharedValidationWarnings);

                    for (var i = 0; i < nets.Count; i++)
                    {
                        var net = nets[i];
                        net.variables ??= new List<LadVariableDef>();

                        if (!ValidateBlockInstanceCompatibility(net, blockType, out var instanceCompatibilityError))
                            return Err($"网络 {i + 1}: {instanceCompatibilityError}");

                        foreach (var v in net.variables)
                        {
                            var existing = workingVars.FirstOrDefault(ev => ev.name.Equals(v.name, StringComparison.OrdinalIgnoreCase));
                            if (existing == null)
                                workingVars.Add(new LadVariableDef { name = v.name, datatype = v.datatype, section = v.section });
                            else if (!existing.datatype.Equals(v.datatype, StringComparison.OrdinalIgnoreCase)
                                     || !existing.section.Equals(v.section, StringComparison.OrdinalIgnoreCase))
                                return Err($"网络 {i + 1} 中变量 '{v.name}' 与现有接口声明冲突");
                        }
                        foreach (var v in workingVars)
                        {
                            if (!net.variables.Any(nv => nv.name.Equals(v.name, StringComparison.OrdinalIgnoreCase)))
                                net.variables.Add(new LadVariableDef { name = v.name, datatype = v.datatype, section = v.section });
                        }

                        if (!ValidateLadNetworkDefinition(net, out var errors, out var warnings))
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"第 {i + 1} 个 LAD 网络未通过结构/语义预检查，整个批次未写入",
                                networkIndex = i + 1,
                                title = net.title,
                                validationErrors = errors,
                                validationWarnings = warnings
                            }, Formatting.Indented);
                        }
                        allWarnings.AddRange(warnings.Select(w => $"网络{i + 1}: {w}"));

                        var flgNets = FlgNetBuilder.Build(net);
                        if (flgNets == null || flgNets.Count == 0)
                            return Err($"第 {i + 1} 个网络 FlgNetBuilder 返回空，整个批次未写入");
                        workingFlgNets.AddRange(flgNets);
                        newFlgNets.AddRange(flgNets);
                        foreach (var _ in flgNets)
                        {
                            workingTitles.Add(net.title ?? "");
                            newTitles.Add(net.title ?? "");
                        }
                        validationDetails.Add(new { index = i + 1, title = net.title ?? "", flgNetCount = flgNets.Count, warnings });
                    }

                    var xml = BlockXmlBuilder.PatchExistingLadBlockXml(
                        originalBlockXml, blockType, newFlgNets, newTitles, lang, workingVars);
                    // ★V17修复★ V17 块级 Import(Override) 对已有 CompileUnits 的块报
                    // "Objects modeled as IOrdered must be empty before importing"：
                    // 先删除原块再导入同名同号重建，失败时用 originalBlockXml 恢复。
                    try
                    {
                        try
                        {
                            block.Delete();
                            Console.Error.WriteLine("[tia-mcp] batch block.Delete ok, exists=" + (FindBlock(blockName, plcName) != null));
                            // ★刷新★ 删除后保存项目 + 短暂等待，确保 TIA 内部块表清理完成
                            //（V17 实测删块后立即导入含多 CompileUnit 的 XML 仍报 CompileUnits 非空）
                            try { _project?.Save(); } catch { }
                            System.Threading.Thread.Sleep(1500);
                        }
                        catch (Exception delEx)
                        {
                            Console.Error.WriteLine("[tia-mcp] batch block.Delete failed: " + delEx.Message);
                        }
                        ImportXmlTemp(plc, xml);
                    }
                    catch (Exception importEx)
                    {
                        // ★诊断★ 失败时把合并 XML 落盘供离线分析
                        try
                        {
                            var dbgDir = Path.Combine(Path.GetTempPath(), "tia_mcp_debug");
                            Directory.CreateDirectory(dbgDir);
                            File.WriteAllText(Path.Combine(dbgDir, $"failed_lad_{blockName}_{DateTime.Now:HHmmss}.xml"), xml, new UTF8Encoding(true));
                        }
                        catch { }
                        try { ImportXmlTemp(plc, originalBlockXml); } catch { }
                        throw new Exception(importEx.Message, importEx);
                    }

                    JObject? compilePayload = null;
                    if (compileAfter)
                    {
                        var compileJson = CompileBlock(blockName, plcName);
                        if (!TryReadSuccess(compileJson, out var compileOk, out compilePayload) || !compileOk)
                        {
                            var rollbackOk = true;
                            string? rollbackError = null;
                            try { ImportXmlTemp(plc, originalBlockXml); }
                            catch (Exception rex) { rollbackOk = false; rollbackError = rex.Message; }
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "批量 LAD 导入后编译失败，缓存未提交，并已尝试恢复原块",
                                compile = compilePayload ?? JToken.FromObject(compileJson),
                                rolledBack = rollbackOk,
                                rollbackError,
                                validationWarnings = allWarnings
                            }, Formatting.Indented);
                        }
                    }

                    lock (_staticCacheLock)
                    {
                        cachedVars.Clear();
                        cachedVars.AddRange(workingVars);
                        cachedFlgNets.Clear();
                        cachedFlgNets.AddRange(workingFlgNets);
                        cachedTitles.Clear();
                        cachedTitles.AddRange(workingTitles);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName,
                        addedCount = nets.Count,
                        totalNetworks = workingFlgNets.Count,
                        compileAfter,
                        compile = compilePayload,
                        validationWarnings = allWarnings,
                        networkDetails = validationDetails
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "批量添加 LAD 网络"); }
            }
        }

        public string GenerateLadBlock(string blockType, string name, string? programmingLanguage,
            string ladNetworkJson, string? filePath)
        {
            try
            {
                var def = JsonConvert.DeserializeObject<LadNetworkDef>(ladNetworkJson)
                          ?? throw new Exception("无法解析 ladNetworkJson");
                var lang = BlockXmlBuilder.ParseProgrammingLanguage(programmingLanguage ?? "LAD");
                if (lang != ProgrammingLanguage.LAD)
                    return Err("generate_lad_block 当前只接受 LAD 网络定义");

                var generatedNetworks = new List<LadNetworkDef> { def };
                if (!PrepareLadProgram(generatedNetworks, null, null, out var sharedErrors, out var sharedWarnings))
                    return JsonConvert.SerializeObject(new { success = false, error = "LAD 定义未通过共享校验", validationErrors = sharedErrors, validationWarnings = sharedWarnings }, Formatting.Indented);
                def = generatedNetworks[0];

                if (!ValidateBlockInstanceCompatibility(def, blockType, out var instanceCompatibilityError))
                    return Err(instanceCompatibilityError);
                if (!ValidateLadNetworkDefinition(def, out var errors, out var warnings))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "LAD 定义未通过结构/语义预检查，未生成 XML",
                        validationErrors = errors,
                        validationWarnings = warnings
                    }, Formatting.Indented);
                }

                var sectionsXml = BlockXmlBuilder.BuildInterfaceSections(def);
                var flgNetList = FlgNetBuilder.Build(def);
                var xml = BlockXmlBuilder.GenerateBlockXml(blockType, name, 0, lang, flgNetList, sectionsXml, null);
                var path = filePath ?? Path.Combine(Path.GetTempPath(), $"{name}.xml");
                File.WriteAllText(path, xml, new UTF8Encoding(true));
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "LAD 块 XML 已生成并通过静态预检查",
                    filePath = path,
                    validationWarnings = warnings
                }, Formatting.Indented);
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        // ────────────────────────────────────────────────
        // SCL 代码写入
        //
        // 策略: 先 Export 块 XML → 替换 StructuredText 内容 → Import 回去
        // 这是最可靠的方式，因为 TIA V19 的 StructuredText XML 格式严格
        // ────────────────────────────────────────────────
        public string AddSclCode(string blockName, string sourceCode, string? interfaceJson, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var blockTypeName = GetBlockTypeName(block);
                    var sectionsXml = !string.IsNullOrEmpty(interfaceJson)
                        ? interfaceJson
                        : null;

                    // 方式1: 直接用 GenerateBlockXml + Import（尝试纯文本格式）
                    var xml = BlockXmlBuilder.GenerateBlockXml(
                        blockTypeName, block.Name, block.Number,
                        ProgrammingLanguage.SCL, null, sectionsXml, sourceCode);

                    ImportXmlTemp(plc, xml);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"SCL 代码已写入块: {blockName}",
                        blockName
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "写入 SCL 代码"); }
            }
        }

        // ────────────────────────────────────────────────
        // 编译
        // ────────────────────────────────────────────────
        public string CompilePlc(string? plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    PlcSoftware plc;
                    if (string.IsNullOrEmpty(plcName))
                        plc = GetPlcSoftware();
                    else
                        plc = GetPlcSoftwareList().FirstOrDefault(p => p.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidOperationException($"未找到 PLC: {plcName}");

                    // 尝试通过反射获取块级编译服务
                    var blockGroupCompilable = plc.BlockGroup.GetService<ICompilable>();
                    if (blockGroupCompilable != null)
                    {
                        var result = blockGroupCompilable.Compile();
                        return SerializeCompileResult(result, plcName ?? "PLC");
                    }

                    // 退而求其次：逐个编译块
                    var errors = new List<string>();
                    int compiled = 0, failed = 0;
                    foreach (var block in plc.BlockGroup.Blocks)
                    {
                        try
                        {
                            var comp = block.GetService<ICompilable>();
                            if (comp != null)
                            {
                                comp.Compile();
                                compiled++;
                            }
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = failed == 0,
                        plc = plcName ?? "PLC",
                        compiled,
                        failed,
                        message = failed == 0 ? $"编译完成: {compiled} 个块" : $"编译完成: {compiled} 成功, {failed} 失败"
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "编译 PLC"); }
            }
        }

        public string CompileBlock(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var compilable = block.GetService<ICompilable>();
                    if (compilable == null) return Err("该块不支持编译");
                    var result = compilable.Compile();
                    return SerializeCompileResult(result, blockName);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "编译块"); }
            }
        }

        // ────────────────────────────────────────────────
        // 导入/导出
        // ────────────────────────────────────────────────
        public string ImportBlockSource(string blockName, string sourceCode, string? plcName = null)
        {
            lock (_lock)
            {
                try { RequireProject(); return AddSclCode(blockName, sourceCode, null, plcName); }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // 直接导入完整的 Openness XML 文件（支持 LAD/SCL/DB 等所有类型）
        // xmlContent 应为 export_block_source 导出的完整 XML
                public string ImportBlockXml(string xmlContent)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★防护★ 用户可控 XML 先做良构校验，坏 XML 曾直接终止 TIA 进程
                    var vErr = ValidateImportXmlString(xmlContent, "导入块");
                    if (vErr != null) return Err(vErr);
                    var plc = GetPlcSoftware();

                    // 必须带 UTF-8 BOM，否则 TIA Portal Import 报错
                    var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_{Guid.NewGuid():N}.xml");
                    var bom = new UTF8Encoding(true);
                    File.WriteAllText(tempPath, xmlContent, bom);
                    try
                    {
                        plc.BlockGroup.Blocks.Import(new FileInfo(tempPath), ImportOptions.Override);
                    }
                    finally
                    {
                        try { File.Delete(tempPath); } catch { }
                    }

                    // 尝试从 XML 解析块名和类型
                    string parsedName = "";
                    string parsedType = "";
                    int parsedNumber = 0;
                    try
                    {
                        var doc = XDocument.Parse(xmlContent);
                        var blockElem = doc.Descendants().FirstOrDefault(e =>
                            e.Name.LocalName.StartsWith("SW.Blocks."));
                        if (blockElem != null)
                        {
                            parsedType = blockElem.Name.LocalName.Replace("SW.Blocks.", "");
                            var nameElem = blockElem.Element("AttributeList")?.Element("Name");
                            if (nameElem != null) parsedName = nameElem.Value;
                            var numElem = blockElem.Element("AttributeList")?.Element("Number");
                            if (numElem != null && int.TryParse(numElem.Value, out var n)) parsedNumber = n;
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"块已通过 XML 导入: {parsedName} ({parsedType} {parsedNumber})",
                        blockName = parsedName,
                        blockType = parsedType,
                        blockNumber = parsedNumber
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // 从 XML 文件路径导入块（直接调用 Openness Import）
        public string ImportBlockFromFile(string xmlFilePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (!File.Exists(xmlFilePath)) return Err($"XML 文件不存在: {xmlFilePath}");
                    var xmlContent = File.ReadAllText(xmlFilePath, Encoding.UTF8);
                    return ImportBlockXml(xmlContent);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string WriteBlockSource(string blockName, string sourceCode, string? plcName = null)
        {
            lock (_lock)
            {
                try { RequireProject(); return AddSclCode(blockName, sourceCode, null, plcName); }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportBlockSource(string blockName, string outputPath, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");
                    EnsureDir(outputPath);
                    block.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                    return Ok($"块源代码已导出到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // 批量导出所有块（含系统块 IEC_TIMER/IEC_COUNTER）到指定目录
        public string ExportAllBlocks(string outputDir, string? filter, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    EnsureDir(outputDir + "\\dummy");
                    var exported = new List<object>();
                    int failed = 0;
                    foreach (var plc in GetPlcSoftwareList())
                    {
                        if (!string.IsNullOrEmpty(plcName) && !plc.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (var b in GetAllBlocks(plc.BlockGroup))
                        {
                            var name = b.Name;
                            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter))
                                continue;
                            try
                            {
                                var path = Path.Combine(outputDir, name + ".xml");
                                b.Export(new FileInfo(path), ExportOptions.WithDefaults);
                                exported.Add(new { name, number = b.Number, type = GetBlockTypeName(b), language = b.ProgrammingLanguage.ToString() });
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                exported.Add(new { name, error = ex.Message });
                            }
                        }
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        exported = exported.Count,
                        failed,
                        outputDir,
                        blocks = exported
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportBlockStructure(string blockName, string outputPath, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var sb = new StringBuilder();
                    sb.AppendLine($"Block: {block.Name}");
                    sb.AppendLine($"Type: {GetBlockTypeName(block)}");
                    sb.AppendLine($"Number: {block.Number}");
                    sb.AppendLine($"Language: {block.ProgrammingLanguage}");

                    EnsureDir(outputPath);
                    File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
                    return Ok($"块结构已导出到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 块内容读取（理解项目结构）
        // 基于 Export XML + XDocument 解析，稳定可靠，不依赖 Import 后的反射
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>获取块的完整 Openness XML 字符串（通过 Export，含网络/接口/调用）。块不一致时自动编译后重试。</summary>
        private string GetBlockXmlString(PlcBlock block)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_ExportBlock_{Guid.NewGuid():N}.xml");
            try
            {
                try
                {
                    block.Export(new FileInfo(tempPath), ExportOptions.WithDefaults);
                    return File.ReadAllText(tempPath, Encoding.UTF8);
                }
                catch (Exception ex) when (ex.Message.Contains("Inconsistent") || ex.Message.Contains("cannot be exported"))
                {
                    try { block.GetService<ICompilable>()?.Compile(); } catch { }
                    block.Export(new FileInfo(tempPath), ExportOptions.WithDefaults);
                    return File.ReadAllText(tempPath, Encoding.UTF8);
                }
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>读取块的所有网络列表（网络号、标题、注释、语言类型）</summary>
        public string ListBlockNetworks(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    return ListBlockNetworksCore(block);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>ListBlockNetworks 的核心实现，接受已查找的块引用，避免重复 FindBlock（C4 性能优化）。</summary>
        private string ListBlockNetworksCore(PlcBlock block)
        {
            var xml = GetBlockXmlString(block);
            var doc = XDocument.Parse(xml);

            // 找所有 CompileUnit（每个对应一个网络）
            // 注意：TIA Portal Openness XML 中元素名是 SW.Blocks.CompileUnit（带命名空间前缀）
            // LocalName 返回完整的 "SW.Blocks.CompileUnit"，不是 "CompileUnit"
            var compileUnits = doc.Descendants()
                .Where(e => e.Name.LocalName.EndsWith("CompileUnit"))
                .ToList();

            var networks = new List<object>();
            for (int i = 0; i < compileUnits.Count; i++)
            {
                var cu = compileUnits[i];
                // 标题和注释在 CompileUnit > ObjectList > MultilingualText[CompositionName='Title'/'Comment']
                string title = "";
                string comment = "";
                // 用 Descendants 查找（MultilingualText 在 ObjectList 下，非 CompileUnit 直接子元素）
                var mlTexts = cu.Descendants().Where(e => e.Name.LocalName == "MultilingualText");
                foreach (var ml in mlTexts)
                {
                    var compName = ml.Attribute("CompositionName")?.Value ?? "";
                    // MultilingualTextItem > AttributeList > Text（Text 不是直接子元素，需要用 Descendants）
                    var txt = ml.Descendants()
                        .Where(e => e.Name.LocalName == "MultilingualTextItem")
                        .Select(e => e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Text")?.Value)
                        .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
                    if (compName == "Title") title = txt;
                    else if (compName == "Comment") comment = txt;
                }

                // 判断网络语言类型
                string netLang = "Unknown";
                var netSource = cu.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")
                    ?.Elements().FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
                if (netSource != null)
                {
                    var srcContent = netSource.FirstNode as XElement;
                    if (srcContent != null)
                    {
                        var localName = srcContent.Name.LocalName;
                        if (localName == "FlgNet") netLang = "LAD/FBD";
                        else if (localName == "StructuredText") netLang = "SCL";
                        else netLang = localName;
                    }
                }

                networks.Add(new
                {
                    number = i + 1,
                    title,
                    comment,
                    language = netLang
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                blockName = block.Name,
                blockType = GetBlockTypeName(block),
                language = block.ProgrammingLanguage.ToString(),
                networkCount = networks.Count,
                networks
            });
        }

        /// <summary>读取 SCL 块的源代码文本（直接返回代码字符串）</summary>
        public string ReadSclCode(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var xml = GetBlockXmlString(block);
                    var doc = XDocument.Parse(xml);

                    // SCL 网络源是 <StructuredText> 下的 Token/Access/Blank/NewLine 等结构化元素序列
                    // 需要递归重建为代码文本
                    var codeParts = new List<string>();
                    var stElements = doc.Descendants()
                        .Where(e => e.Name.LocalName == "StructuredText")
                        .ToList();

                    foreach (var st in stElements)
                    {
                        var sb = new StringBuilder();
                        foreach (var node in st.Nodes().OfType<XElement>())
                            AppendSclNode(sb, node);
                        codeParts.Add(sb.ToString());
                    }

                    var fullCode = string.Join("\n", codeParts).Trim();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName,
                        blockType = GetBlockTypeName(block),
                        language = block.ProgrammingLanguage.ToString(),
                        codeLength = fullCode.Length,
                        networkCount = stElements.Count,
                        code = fullCode
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "读取 SCL 代码"); }
            }
        }

        /// <summary>递归重建 SCL 节点为代码文本</summary>
        private static void AppendSclNode(StringBuilder sb, XElement node)
        {
            var ln = node.Name.LocalName;
            switch (ln)
            {
                case "Token":
                    sb.Append(node.Attribute("Text")?.Value ?? "");
                    break;
                case "Blank":
                    var num = node.Attribute("Num")?.Value ?? "1";
                    sb.Append(new string(' ', int.TryParse(num, out var n) ? n : 1));
                    break;
                case "NewLine":
                    sb.AppendLine();
                    break;
                case "Access":
                    var scope = node.Attribute("Scope")?.Value ?? "";
                    if (scope == "LiteralConstant")
                    {
                        var cv = node.Descendants().FirstOrDefault(e => e.Name.LocalName == "ConstantValue")?.Value ?? "";
                        sb.Append(cv);
                    }
                    else
                    {
                        // Symbol 或 Call
                        var sym = node.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
                        if (sym != null)
                        {
                            var comps = sym.Elements().Where(e => e.Name.LocalName == "Component");
                            sb.Append(string.Join(".", comps.Select(c => c.Attribute("Name")?.Value)));
                        }
                        else
                        {
                            // 可能是 Call/Instruction
                            foreach (var child in node.Nodes().OfType<XElement>())
                                AppendSclNode(sb, child);
                        }
                    }
                    break;
                case "Instruction":
                    sb.Append(node.Attribute("Name")?.Value ?? "");
                    foreach (var child in node.Nodes().OfType<XElement>())
                        AppendSclNode(sb, child);
                    break;
                case "Parameter":
                    var pName = node.Attribute("Name")?.Value ?? "";
                    sb.Append(pName);
                    // Parameter 子元素通常有 Blank + Token(":=") + Blank + Access(value)
                    foreach (var child in node.Nodes().OfType<XElement>())
                        AppendSclNode(sb, child);
                    break;
                case "Comment":
                    // StructuredText 中的注释格式：<Comment><MultiLanguageText Lang="zh-CN">文本</MultiLanguageText></Comment>
                    var cmtMlt = node.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText");
                    if (cmtMlt != null) sb.Append("// ").Append(cmtMlt.Value);
                    break;
                default:
                    // 递归处理未知节点
                    foreach (var child in node.Nodes().OfType<XElement>())
                        AppendSclNode(sb, child);
                    break;
            }
        }

        /// <summary>读取单个 LAD 网络的指令结构（盒子、引脚、变量、连线）</summary>
        public string ReadLadNetwork(string blockName, int networkNumber, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var xml = GetBlockXmlString(block);
                    var doc = XDocument.Parse(xml);

                    var compileUnits = doc.Descendants()
                        .Where(e => e.Name.LocalName.EndsWith("CompileUnit"))
                        .ToList();
                    if (networkNumber < 1 || networkNumber > compileUnits.Count)
                        return Err($"网络号超出范围: {networkNumber}（共 {compileUnits.Count} 个网络）");

                    var cu = compileUnits[networkNumber - 1];

                    // 标题/注释（MultilingualText 在 CompileUnit > ObjectList 下，用 Descendants 查找）
                    string title = "";
                    string comment = "";
                    var mlTexts = cu.Descendants().Where(e => e.Name.LocalName == "MultilingualText");
                    foreach (var ml in mlTexts)
                    {
                        var compName = ml.Attribute("CompositionName")?.Value ?? "";
                        // MultilingualTextItem > AttributeList > Text（Text 不是直接子元素，需要用 Descendants）
                        var txt = ml.Descendants()
                            .Where(e => e.Name.LocalName == "MultilingualTextItem")
                            .Select(e => e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Text")?.Value)
                            .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
                        if (compName == "Title") title = txt;
                        else if (compName == "Comment") comment = txt;
                    }

                    // 解析 NetworkSource/FlgNet
                    var netSource = cu.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")
                        ?.Elements().FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
                    var flgNet = netSource?.Elements().FirstOrDefault(e => e.Name.LocalName == "FlgNet");

                    var parts = new List<object>();
                    var accesses = new List<object>();
                    var calls = new List<object>();
                    var wires = new List<object>();

                    if (flgNet != null)
                    {
                        // FlgNet 结构：<FlgNet><Parts>Access*/Part*/Call*</Parts><Wires>Wire*</Wires></FlgNet>
                        // Access/Part/Call 都在 Parts 容器下，需要先找 Parts 容器
                        var partsContainer = flgNet.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");

                        // 解析 Access（变量/常量引用）
                        // 用 Dictionary 存储 uid→symbol 映射，便于 Call 参数解析
                        var accessMap = new Dictionary<int, string>();
                        var accessElems = partsContainer?.Elements().Where(e => e.Name.LocalName == "Access")
                            ?? Enumerable.Empty<XElement>();
                        foreach (var acc in accessElems)
                        {
                            var uid = (int?)acc.Attribute("UId") ?? 0;
                            var scope = acc.Attribute("Scope")?.Value ?? "";
                            string symbol = "";
                            string accessType = "";
                            var sym = acc.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
                            var constant = acc.Elements().FirstOrDefault(e => e.Name.LocalName == "Constant");
                            if (sym != null)
                            {
                                accessType = "Symbol";
                                var comps = sym.Elements().Where(e => e.Name.LocalName == "Component");
                                symbol = string.Join(".", comps.Select(c => c.Attribute("Name")?.Value));
                            }
                            else if (constant != null)
                            {
                                accessType = "Constant";
                                var cv = constant.Elements().FirstOrDefault(e => e.Name.LocalName == "ConstantValue");
                                symbol = cv?.Value ?? "";
                            }
                            accessMap[uid] = symbol;
                            accesses.Add(new { uid, scope, type = accessType, symbol });
                        }

                        // 解析 Part（指令盒子/触点/线圈）
                        var partElems = partsContainer?.Elements().Where(e => e.Name.LocalName == "Part")
                            ?? Enumerable.Empty<XElement>();
                        foreach (var p in partElems)
                        {
                            var uid = (int?)p.Attribute("UId") ?? 0;
                            var name = p.Attribute("Name")?.Value ?? "";
                            var version = p.Attribute("Version")?.Value ?? "";
                            var disabledEno = p.Attribute("DisabledENO")?.Value ?? "";
                            // Negated 子元素（常闭触点等）
                            var negated = p.Elements().Where(e => e.Name.LocalName == "Negated")
                                .Select(e => e.Attribute("Name")?.Value ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                            // Instance（TON 等的背景实例）
                            string instance = "";
                            string instanceScope = "";
                            var inst = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                            if (inst != null)
                            {
                                instanceScope = inst.Attribute("Scope")?.Value ?? "";
                                var comps = inst.Elements().Where(e => e.Name.LocalName == "Component");
                                instance = string.Join(".", comps.Select(c => c.Attribute("Name")?.Value));
                            }
                            // TemplateValue（如 time_type, Card, SrcType 等）
                            var templates = new Dictionary<string, string>();
                            foreach (var tv in p.Elements().Where(e => e.Name.LocalName == "TemplateValue"))
                            {
                                var tvName = tv.Attribute("Name")?.Value ?? "";
                                var tvType = tv.Attribute("Type")?.Value ?? "";
                                templates[tvName] = tv.Value;
                            }
                            parts.Add(new { uid, name, version, disabledEno, negated, instance, instanceScope, templates });
                        }

                        // 解析 Call（FB/FC 调用）
                        var callElems = partsContainer?.Elements().Where(e => e.Name.LocalName == "Call")
                            ?? Enumerable.Empty<XElement>();
                        foreach (var c in callElems)
                        {
                            var uid = (int?)c.Attribute("UId") ?? 0;
                            var callInfo = c.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
                            var calleeName = callInfo?.Attribute("Name")?.Value ?? "";
                            var blockType = callInfo?.Attribute("BlockType")?.Value ?? "";
                            string instance = "";
                            string instanceScope = "";
                            var inst = callInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                            if (inst != null)
                            {
                                instanceScope = inst.Attribute("Scope")?.Value ?? "";
                                var comps = inst.Elements().Where(e => e.Name.LocalName == "Component");
                                instance = string.Join(".", comps.Select(cc => cc.Attribute("Name")?.Value));
                            }
                            // 调用参数
                            var parameters = new List<object>();
                            var paramElems = callInfo?.Elements().Where(e => e.Name.LocalName == "Parameter");
                            if (paramElems != null)
                            {
                                foreach (var param in paramElems)
                                {
                                    var pName = param.Attribute("Name")?.Value ?? "";
                                    var pSection = param.Attribute("Section")?.Value ?? "";
                                    var pType = param.Attribute("Type")?.Value ?? "";
                                    // 参数值：可能引用 Access UId 或直接内联
                                    var paramAccess = param.Elements().FirstOrDefault(e => e.Name.LocalName == "Access");
                                    string pValue = "";
                                    if (paramAccess != null)
                                    {
                                        var pUid = (int?)paramAccess.Attribute("UId");
                                        if (pUid.HasValue && accessMap.TryGetValue(pUid.Value, out var sym2))
                                            pValue = sym2;
                                    }
                                    parameters.Add(new { name = pName, section = pSection, type = pType, value = pValue });
                                }
                            }
                            calls.Add(new { uid, calleeName, blockType, instance, instanceScope, parameters });
                        }

                        // 解析 Wire（连线：Powerrail/NameCon/IdentCon/OpenCon）
                        var wireElems = flgNet.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires")
                            ?.Elements().Where(e => e.Name.LocalName == "Wire");
                        if (wireElems != null)
                        {
                            foreach (var w in wireElems)
                            {
                                var endpoints = new List<string>();
                                foreach (var node in w.Nodes().OfType<XElement>())
                                {
                                    var ln = node.Name.LocalName;
                                    if (ln == "Powerrail")
                                    {
                                        endpoints.Add("Powerrail");
                                    }
                                    else if (ln == "NameCon")
                                    {
                                        var nu = node.Attribute("UId")?.Value ?? "";
                                        var nName = node.Attribute("Name")?.Value ?? "";
                                        endpoints.Add($"{nu}.{nName}");
                                    }
                                    else if (ln == "IdentCon")
                                    {
                                        var iu = node.Attribute("UId")?.Value ?? "";
                                        endpoints.Add($"#{iu}");
                                    }
                                    else if (ln == "OpenCon")
                                    {
                                        endpoints.Add("Open");
                                    }
                                    else
                                    {
                                        endpoints.Add(ln);
                                    }
                                }
                                wires.Add(new { endpoints });
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName,
                        networkNumber,
                        title,
                        comment,
                        parts,
                        accesses,
                        calls,
                        wires
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "读取 LAD 网络"); }
            }
        }

        /// <summary>稳定读取块接口（含 StartValue/Comment/Remanence），不依赖 Import 后的反射</summary>
        public string GetBlockInterfaceV2(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    return GetBlockInterfaceV2Core(block);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>GetBlockInterfaceV2 的核心实现，接受已查找的块引用，避免重复 FindBlock（C4 性能优化）。</summary>
        private string GetBlockInterfaceV2Core(PlcBlock block)
        {
            var xml = GetBlockXmlString(block);
            var doc = XDocument.Parse(xml);

            var interfaceElem = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Interface");
            if (interfaceElem == null)
                return JsonConvert.SerializeObject(new { success = true, blockName = block.Name, sections = new object[0] });

            var sections = new List<object>();
            // Interface > Sections > Section*（Sections 是复数容器，Section 是单数节点）
            // 用 Descendants 找所有 Section（避免 Elements 只取到 Sections 容器的问题）
            var sectionElems = interfaceElem.Descendants().Where(e => e.Name.LocalName == "Section");
            foreach (var sec in sectionElems)
            {
                var secName = sec.Attribute("Name")?.Value ?? "";
                var members = new List<object>();
                foreach (var m in sec.Elements().Where(e => e.Name.LocalName == "Member"))
                {
                    var mName = m.Attribute("Name")?.Value ?? "";
                    var mType = m.Attribute("Datatype")?.Value ?? "";
                    var remanence = m.Attribute("Remanence")?.Value ?? "";
                    var accessibility = m.Attribute("Accessibility")?.Value ?? "";
                    // StartValue
                    string startValue = "";
                    var sv = m.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue");
                    if (sv != null) startValue = sv.Value;
                    // Comment
                    string comment = "";
                    var cmt = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment");
                    if (cmt != null)
                    {
                        // Comment 格式：<Comment><MultiLanguageText Lang="zh-CN">文本</MultiLanguageText></Comment>
                        var mlt = cmt.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText");
                        var text = mlt?.Value;
                        if (!string.IsNullOrEmpty(text)) comment = text;
                    }
                    members.Add(new { name = mName, dataType = mType, startValue, comment, remanence, accessibility });
                }
                sections.Add(new { section = secName, memberCount = members.Count, members });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                blockName = block.Name,
                blockType = GetBlockTypeName(block),
                number = block.Number,
                language = block.ProgrammingLanguage.ToString(),
                sections
            });
        }

        /// <summary>读取块的所有调用关系（OB1 调用了哪些 FC/FB 及参数传递）</summary>
        public string GetBlockCallGraph(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    return GetBlockCallGraphCore(block);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "读取块调用图"); }
            }
        }

        /// <summary>GetBlockCallGraph 的核心实现，接受已查找的块引用，避免重复 FindBlock（C4 性能优化）。</summary>
        private string GetBlockCallGraphCore(PlcBlock block)
        {
            var xml = GetBlockXmlString(block);
            var doc = XDocument.Parse(xml);

            var compileUnits = doc.Descendants()
                .Where(e => e.Name.LocalName.EndsWith("CompileUnit"))
                .ToList();

            var allCalls = new List<object>();
            for (int i = 0; i < compileUnits.Count; i++)
            {
                var cu = compileUnits[i];
                var calls = cu.Descendants().Where(e => e.Name.LocalName == "Call");
                foreach (var c in calls)
                {
                    var callInfo = c.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
                    if (callInfo == null) continue;
                    var calleeName = callInfo.Attribute("Name")?.Value ?? "";
                    var blockType = callInfo.Attribute("BlockType")?.Value ?? "";
                    string instance = "";
                    var inst = callInfo.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                    if (inst != null)
                    {
                        var comps = inst.Elements().Where(e => e.Name.LocalName == "Component");
                        instance = string.Join(".", comps.Select(cc => cc.Attribute("Name")?.Value));
                    }
                    var parameters = new List<object>();
                    foreach (var param in callInfo.Elements().Where(e => e.Name.LocalName == "Parameter"))
                    {
                        var pName = param.Attribute("Name")?.Value ?? "";
                        var pSection = param.Attribute("Section")?.Value ?? "";
                        parameters.Add(new { name = pName, section = pSection });
                    }
                    allCalls.Add(new
                    {
                        network = i + 1,
                        callee = calleeName,
                        blockType,
                        instanceDb = instance,
                        parameters
                    });
                }
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                blockName = block.Name,
                blockType = GetBlockTypeName(block),
                callCount = allCalls.Count,
                calls = allCalls
            });
        }

        /// <summary>全项目调用树：遍历所有 OB/FC/FB 块，提取调用关系，生成跨块调用图。</summary>
        public string GetProjectCallGraph(string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    List<PlcSoftware> plcs;
                    if (string.IsNullOrEmpty(plcName))
                    {
                        plcs = GetPlcSoftwareList();
                    }
                    else
                    {
                        var plc = FindPlcByName(plcName);
                        if (plc == null) return Err($"未找到 PLC: {plcName}");
                        plcs = new List<PlcSoftware> { plc };
                    }

                    var allNodes = new List<object>();
                    var allEdges = new List<object>();
                    var errors = new List<object>();

                    foreach (var plc in plcs)
                    {
                        var blocks = GetAllBlocks(plc.BlockGroup);
                        foreach (var block in blocks)
                        {
                            var blockType = GetBlockTypeName(block);
                            // 跳过 DB 块（DB 不调用其他块）
                            if (blockType == "DB") continue;

                            try
                            {
                                // 节点信息
                                allNodes.Add(new
                                {
                                    name = block.Name,
                                    blockType,
                                    number = block.Number,
                                    language = block.ProgrammingLanguage.ToString(),
                                    plcName = plc.Name
                                });

                                // 提取调用关系
                                var xml = GetBlockXmlString(block);
                                var doc = XDocument.Parse(xml);
                                var compileUnits = doc.Descendants()
                                    .Where(e => e.Name.LocalName.EndsWith("CompileUnit"))
                                    .ToList();

                                for (int i = 0; i < compileUnits.Count; i++)
                                {
                                    var cu = compileUnits[i];
                                    var calls = cu.Descendants().Where(e => e.Name.LocalName == "Call");
                                    foreach (var c in calls)
                                    {
                                        var callInfo = c.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
                                        if (callInfo == null) continue;
                                        var calleeName = callInfo.Attribute("Name")?.Value ?? "";
                                        var calleeType = callInfo.Attribute("BlockType")?.Value ?? "";
                                        string instance = "";
                                        var inst = callInfo.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                                        if (inst != null)
                                        {
                                            var comps = inst.Elements().Where(e => e.Name.LocalName == "Component");
                                            instance = string.Join(".", comps.Select(cc => cc.Attribute("Name")?.Value));
                                        }
                                        allEdges.Add(new
                                        {
                                            source = block.Name,
                                            target = calleeName,
                                            sourcePlc = plc.Name,
                                            network = i + 1,
                                            blockType = calleeType,
                                            instanceDb = instance
                                        });
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add(new { block = block.Name, plcName = plc.Name, error = ex.Message });
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcCount = plcs.Count,
                        nodeCount = allNodes.Count,
                        edgeCount = allEdges.Count,
                        nodes = allNodes,
                        edges = allEdges,
                        errors
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "读取项目调用图"); }
            }
        }

        /// <summary>一站式读取块完整结构：接口+网络列表+调用关系</summary>
        public string ReadBlockFull(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    // 组合各读取能力（★C4 优化：直接复用已查找的 block，避免每个子方法重复 FindBlock）
                    var interfaceResult = JsonConvert.DeserializeObject(GetBlockInterfaceV2Core(block));
                    var networksResult = JsonConvert.DeserializeObject(ListBlockNetworksCore(block));
                    var callsResult = JsonConvert.DeserializeObject(GetBlockCallGraphCore(block));

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName,
                        blockType = GetBlockTypeName(block),
                        number = block.Number,
                        language = block.ProgrammingLanguage.ToString(),
                        interfaceInfo = interfaceResult,
                        networks = networksResult,
                        callGraph = callsResult
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "读取块完整信息"); }
            }
        }


        // ────────────────────────────────────────────────
        // ────────────────────────────────────────────────
        // 数据块完整管理
        // ────────────────────────────────────────────────

        /// <summary>
        /// 创建全局 DB 块，可一次性携带多个变量（通过 interfaceJson）。
        /// </summary>
        public string CreateDb(string name, int? blockNumber, string? interfaceJson, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    // 0 或未提供块号均表示自动编号；BlockXmlBuilder 会省略 Number=0。
                    var num = blockNumber.GetValueOrDefault() > 0 ? blockNumber.Value : 0;
                    var sectionsXml = !string.IsNullOrEmpty(interfaceJson)
                        ? interfaceJson
                        : @"<Section Name=""Static"" />";
                    var xml = BlockXmlBuilder.GenerateBlockXml("DB", name, num,
                        ProgrammingLanguage.DB, null, sectionsXml, null);
                    ImportXmlTemp(plc, xml);

                    var created = FindBlock(name, plcName);
                    if (created == null)
                        return Err($"DB 导入调用已完成，但未在 PLC 中反查到 DB: {name}");
                    var actualType = GetBlockTypeName(created);
                    if (!actualType.Equals("DB", StringComparison.OrdinalIgnoreCase) || created.Number <= 0)
                    {
                        var actualNumber = created.Number;
                        try { created.Delete(); } catch { }
                        return Err($"DB 创建后验证失败：实际类型={actualType}，实际编号={actualNumber}。已尝试删除错误对象");
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建 DB: {name}",
                        blockName = created.Name,
                        blockNumber = created.Number,
                        requestedBlockNumber = num,
                        verified = true
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 创建 IEC 定时器全局实例 DB（系统块类型 IEC_Timer）。
        /// TON/TOF/TP 在 OB/FC 中以 instanceScope=global 使用时，必须先存在此 DB。
        /// 先枚举系统块组中的候选类型名（IEC_Timer/IEC_TIMER/TON_TIME 等），逐个尝试 CreateInstanceDB。
        /// </summary>
        public string CreateIecTimerDb(string instanceDbName, int? blockNumber = null, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    // 1) 枚举系统块组候选名
                    var candidates = new List<string>();
                    try
                    {
                        var all = GetAllBlocks(plc.BlockGroup);
                        foreach (var b in all)
                        {
                            var bn = b.Name ?? "";
                            if (bn.IndexOf("IEC", StringComparison.OrdinalIgnoreCase) >= 0
                                || bn.IndexOf("Timer", StringComparison.OrdinalIgnoreCase) >= 0
                                || bn.IndexOf("TON", StringComparison.OrdinalIgnoreCase) >= 0)
                                candidates.Add(bn);
                        }
                    }
                    catch { }
                    // 2) 追加常见名
                    foreach (var n in new[] { "IEC_Timer", "IEC_TIMER", "IEC_TIMER_0", "TON_TIME", "IEC_TIMER_TYPE" })
                        if (!candidates.Contains(n)) candidates.Add(n);
                    candidates = candidates.Distinct().ToList();

                    var attempts = new List<object>();
                    PlcBlock? created = null;
                    string? createdInstanceOf = null;
                    string? lastError = null;
                    foreach (var cand in candidates)
                    {
                        try
                        {
                            PlcBlock db;
                            if (blockNumber.HasValue && blockNumber.Value > 0)
                                db = plc.BlockGroup.Blocks.CreateInstanceDB(instanceDbName, false, blockNumber.Value, cand);
                            else
                                // fix#17: V17 Openness 的 CreateInstanceDB(name, true, 0, …) 实测仍会生成
                                // 编号 0 的非法 DB。自动编号改为：扫描现有块取第一个空闲正编号后显式创建。
                                db = plc.BlockGroup.Blocks.CreateInstanceDB(instanceDbName, false, FindFreeBlockNumber(plc), cand);
                            created = db;
                            createdInstanceOf = cand;
                            attempts.Add(new { instanceOf = cand, ok = true, blockName = db.Name, blockNumber = db.Number });
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex.Message;
                            attempts.Add(new { instanceOf = cand, ok = false, error = (ex.InnerException?.Message ?? ex.Message).Split('\n')[0] });
                        }
                    }

                    if (created == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未能用任何系统类型名创建 IEC 定时器实例 DB。",
                            lastError,
                            attempts
                        }, Formatting.Indented);

                    var createdType = GetBlockTypeName(created);
                    if (!created.Name.Equals(instanceDbName, StringComparison.OrdinalIgnoreCase)
                        || !createdType.Equals("DB", StringComparison.OrdinalIgnoreCase)
                        || created.Number <= 0)
                    {
                        var actualName = created.Name;
                        var actualNumber = created.Number;
                        try { created.Delete(); } catch { }
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"IEC 定时器实例 DB 创建后验证失败：实际名称={actualName}，实际类型={createdType}，实际编号={actualNumber}。已尝试删除错误对象",
                            attempts
                        }, Formatting.Indented);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建 IEC 定时器实例 DB: {instanceDbName}",
                        blockName = created.Name,
                        blockNumber = created.Number,
                        instanceOf = createdInstanceOf,
                        verified = true,
                        attempts
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 为 FB 创建背景 DB（Instance DB）。
        /// 使用 TIA Portal Openness API 的 PlcBlockComposition.CreateInstanceDB 方法。
        /// OB1 调用 FB 时需要背景 DB 存储 FB 的 Static 变量。
        /// </summary>
        public string CreateInstanceDb(string instanceDbName, string fbName, int? blockNumber, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);

                    // 验证 FB 是否存在
                    var fb = GetAllBlocks(plc.BlockGroup)
                        .FirstOrDefault(b => b.Name.Equals(fbName, StringComparison.OrdinalIgnoreCase));
                    if (fb == null)
                        return Err($"未找到 FB: {fbName}");

                    // 使用 Openness API 创建背景 DB
                    // CreateInstanceDB(name, isAutoNumbered, number, instanceOfName)
                    PlcBlock instanceDb;
                    if (blockNumber.HasValue && blockNumber.Value > 0)
                    {
                        instanceDb = plc.BlockGroup.Blocks.CreateInstanceDB(instanceDbName, false, blockNumber.Value, fbName);
                    }
                    else
                    {
                        // fix#17: 0 或未提供块号时，V17 Openness 的 (name, true, 0, …) 实测仍生成
                        // 编号 0 的非法 DB。自动编号改为：扫描现有块取第一个空闲正编号后显式创建。
                        instanceDb = plc.BlockGroup.Blocks.CreateInstanceDB(instanceDbName, false, FindFreeBlockNumber(plc), fbName);
                    }

                    var actualType = GetBlockTypeName(instanceDb);
                    if (!actualType.Equals("DB", StringComparison.OrdinalIgnoreCase) || instanceDb.Number <= 0)
                    {
                        var actualNumber = instanceDb.Number;
                        try { instanceDb.Delete(); } catch { }
                        return Err($"背景 DB 创建后验证失败：实际类型={actualType}，实际编号={actualNumber}。已尝试删除错误对象");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建背景 DB: {instanceDbName}（关联 FB: {fbName}）",
                        blockName = instanceDb.Name,
                        blockNumber = instanceDb.Number,
                        associatedFB = fbName,
                        verified = true
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string AddDbVariable(string dbName, string varName, string dataType,
            string startValue, string comment, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(dbName, plcName);
                    if (block == null) return Err($"未找到 DB: {dbName}");

                    var memberXml = BuildMemberXml(varName, dataType, startValue, comment);

                    // 方式1: Export → 合并 Member → Import（不覆盖已有变量）
                    try
                    {
                        var merged = MergeDbMemberXml(block, memberXml, varName);
                        ImportXmlTemp(plc, merged);

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = $"已添加 DB 成员: {varName} ({dataType})",
                            variableName = varName,
                            dataType,
                            startValue
                        });
                    }
                    catch (Exception exportEx)
                    {
                        // Export 失败通常因为 DB 未编译（处于不一致状态）
                        // 先编译 DB，再重试 Export → 合并 → Import
                        try
                        {
                            var compilable = block.GetService<ICompilable>();
                            compilable?.Compile();
                            var merged = MergeDbMemberXml(block, memberXml, varName);
                            ImportXmlTemp(plc, merged);

                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = $"已添加 DB 成员(编译后重试): {varName} ({dataType})",
                                variableName = varName,
                                dataType,
                                startValue
                            });
                        }
                        catch
                        {
                            // 编译后仍失败，回退到直接 XML Import 方式
                            // 注意：此方式仅保留新变量，会丢失已有变量
                            var fallbackSections = "<Section Name=\"Static\">" + memberXml + "</Section>";
                            var fallbackXml = BlockXmlBuilder.GenerateBlockXml("DB", dbName, 0,
                                ProgrammingLanguage.DB, null, fallbackSections, null);
                            ImportXmlTemp(plc, fallbackXml);
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已添加 DB 成员(回退方式): {varName} ({dataType})",
                        variableName = varName,
                        dataType,
                        startValue
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string UpdateDbVariable(string dbName, string varName, string? newDataType,
            string? newStartValue, string? newComment, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(dbName, plcName);
                    if (block == null) return Err($"未找到 DB: {dbName}");

                    var xml = ExportBlockXml(block);
                    var updated = ReplaceMemberInXml(xml, varName, (memberXml) =>
                    {
                        // 替换 Datatype
                        if (!string.IsNullOrEmpty(newDataType))
                            memberXml = Regex.Replace(memberXml,
                                @"Datatype=""[^""]+""",
                                $"Datatype=\"{SecurityElement.Escape(newDataType)}\"");
                        // 替换 StartValue
                        if (newStartValue != null)
                        {
                            var startValueXml = string.IsNullOrEmpty(newStartValue)
                                ? ""
                                : $"<StartValue>{SecurityElement.Escape(newStartValue)}</StartValue>";
                            memberXml = Regex.Replace(memberXml,
                                @"<StartValue>.*?</StartValue>", startValueXml,
                                RegexOptions.Singleline);
                            if (!Regex.IsMatch(memberXml, @"<StartValue>") && !string.IsNullOrEmpty(newStartValue))
                            {
                                memberXml = Regex.Replace(memberXml,
                                    @"(</Member>)$", $"{startValueXml}</Member>", RegexOptions.Singleline);
                            }
                        }
                        // 替换 Comment
                        if (newComment != null)
                        {
                            var commentXml = string.IsNullOrEmpty(newComment)
                                ? ""
                                : $"<Comment><MultilingualText Lang=\"zh-CN\"><Text>{SecurityElement.Escape(newComment)}</Text></MultilingualText></Comment>";
                            memberXml = Regex.Replace(memberXml,
                                @"<Comment>.*?</Comment>", commentXml, RegexOptions.Singleline);
                            if (!Regex.IsMatch(memberXml, @"<Comment>") && !string.IsNullOrEmpty(newComment))
                            {
                                memberXml = Regex.Replace(memberXml,
                                    @"(</Member>)$", $"{commentXml}</Member>", RegexOptions.Singleline);
                            }
                        }
                        return memberXml;
                    });

                    ImportXmlTemp(plc, updated);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已更新 DB 成员: {varName}",
                        variableName = varName,
                        dataType = newDataType,
                        startValue = newStartValue
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteDbVariable(string dbName, string varName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(dbName, plcName);
                    if (block == null) return Err($"未找到 DB: {dbName}");

                    var xml = ExportBlockXml(block);
                    var deleted = RemoveMemberFromXml(xml, varName);
                    if (deleted == null) return Err($"DB 中未找到变量: {varName}");

                    ImportXmlTemp(plc, deleted);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已删除 DB 成员: {varName}",
                        variableName = varName
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────
        // DB XML 操作辅助
        // ────────────────────────────────────────────────

        private static string BuildMemberXml(string varName, string dataType,
            string startValue, string comment)
        {
            var sb = new StringBuilder();
            sb.Append($"<Member Name=\"{SecurityElement.Escape(varName)}\" Datatype=\"{SecurityElement.Escape(dataType)}\"");
            var hasStartValue = !string.IsNullOrEmpty(startValue);
            var hasComment = !string.IsNullOrEmpty(comment);
            if (!hasStartValue && !hasComment)
            {
                sb.Append(" />");
                return sb.ToString();
            }
            sb.Append(">");
            if (hasStartValue)
                sb.Append($"<StartValue>{SecurityElement.Escape(startValue)}</StartValue>");
            if (hasComment)
            {
                sb.Append("<Comment>");
                sb.Append($"<MultiLanguageText Lang=\"zh-CN\">{SecurityElement.Escape(comment)}</MultiLanguageText>");
                sb.Append("</Comment>");
            }
            sb.Append("</Member>");
            return sb.ToString();
        }

        /// <summary>导出块为 XML 字符串（使用临时文件）。委托给 ExportBlockXmlSafe。</summary>
        private static string ExportBlockXml(object block)
        {
            return ExportBlockXmlSafe(block);
        }

        /// <summary>安全导出块为 XML。先尝试原生 Export，失败时通过反射构建 XML。
        /// 解决 "Inconsistent blocks and PLC data types (UDT) cannot be exported" 问题。</summary>
        private static string ExportBlockXmlSafe(object block)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_ExportDb_{Guid.NewGuid():N}.xml");
                try
                {
                    ((PlcBlock)block).Export(new FileInfo(tempPath), ExportOptions.WithDefaults);
                    return File.ReadAllText(tempPath, Encoding.UTF8);
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExportBlockXmlSafe 原生 Export 失败: {ex.Message}");
            }

            try
            {
                var blockName = GetProperty(block, "Name")?.ToString() ?? "";
                var numObj = GetProperty(block, "Number");
                var blockNumber = numObj == null ? 0 : (int)Convert.ChangeType(numObj, typeof(int));

                var sb = new StringBuilder();
                var secObj = block.GetType().GetProperty("Interface")?.GetValue(block);
                var sections = secObj as IEnumerable;
                if (sections != null)
                {
                    foreach (var sec in sections)
                    {
                        var secName = sec?.GetType().GetProperty("Name")?.GetValue(sec)?.ToString() ?? "";
                        sb.Append($"<Section Name=\"{SecurityElement.Escape(secName)}\">");
                        var mems = sec?.GetType().GetProperty("Members")?.GetValue(sec) as IEnumerable;
                        if (mems != null)
                        {
                            foreach (var m in mems)
                            {
                                var mName = GetProperty(m, "Name") as string ?? "";
                                var mType = GetProperty(m, "DataTypeName") as string
                                          ?? GetProperty(m, "DataType") as string ?? "Bool";
                                if (string.IsNullOrEmpty(mName)) continue;

                                var startValue = GetProperty(m, "StartValue")?.ToString() ?? "";

                                if (string.IsNullOrEmpty(startValue))
                                {
                                    sb.Append($"<Member Name=\"{SecurityElement.Escape(mName)}\" Datatype=\"{SecurityElement.Escape(mType)}\" />");
                                }
                                else
                                {
                                    sb.Append($"<Member Name=\"{SecurityElement.Escape(mName)}\" Datatype=\"{SecurityElement.Escape(mType)}\">");
                                    sb.Append($"<StartValue>{SecurityElement.Escape(startValue)}</StartValue>");
                                    sb.Append("</Member>");
                                }
                            }
                        }
                        sb.Append("</Section>");
                    }
                }

                return BlockXmlBuilder.GenerateBlockXml(
                    "DB", blockName, blockNumber,
                    ProgrammingLanguage.DB, null,
                    sb.ToString(), null);
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidOperationException($"导出块 XML 失败: {fallbackEx.Message}", fallbackEx);
            }
        }

        /// <summary>将新 Member 合并到现有 DB XML 的 Static Section 中（DOM 修改，不覆盖已有同名变量）。</summary>
        private static string MergeDbMemberXml(object block, string newMemberXml, string varName)
            => MergeMemberIntoInterfaceSection(ExportBlockXml(block), "Static", newMemberXml, varName);

        /// <summary>将 Member 合并到 Interface/Sections 下的指定直接 Section，避免正则截断嵌套 Sections。</summary>
        private static string MergeMemberIntoInterfaceSection(string xml, string sectionName, string newMemberXml, string varName)
        {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var sections = FindInterfaceSections(doc)
                ?? throw new InvalidOperationException("无法定位块的 Interface Sections");

            var section = sections.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Section"
                    && string.Equals((string?)e.Attribute("Name"), sectionName, StringComparison.OrdinalIgnoreCase));
            if (section == null)
            {
                section = new XElement(sections.Name.Namespace + "Section", new XAttribute("Name", sectionName));
                sections.Add(section);
            }

            if (section.Elements().Any(e => e.Name.LocalName == "Member"
                && string.Equals((string?)e.Attribute("Name"), varName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{sectionName} 区域中已存在变量: {varName}");

            var parsed = XElement.Parse(newMemberXml, LoadOptions.PreserveWhitespace);
            section.Add(CloneElementToNamespace(parsed, section.Name.Namespace));
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>替换 Interface 顶层 Section 中指定 Member 的完整 DOM，保留嵌套结构边界。</summary>
        private static string ReplaceMemberInXml(string xml, string varName, Func<string, string> transform)
        {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var matches = FindTopLevelInterfaceMembers(doc, varName).ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"接口中未找到变量: {varName}");
            if (matches.Count > 1)
                throw new InvalidOperationException($"接口中存在多个同名变量 {varName}，请指定唯一作用域后再修改");

            var target = matches[0];
            var replacementText = transform(target.ToString(SaveOptions.DisableFormatting));
            var replacement = XElement.Parse(replacementText, LoadOptions.PreserveWhitespace);
            target.ReplaceWith(CloneElementToNamespace(replacement, target.Name.Namespace));
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>从 Interface 顶层 Section 中移除指定 Member；未找到返回 null。</summary>
        private static string? RemoveMemberFromXml(string xml, string varName)
        {
            var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var matches = FindTopLevelInterfaceMembers(doc, varName).ToList();
            if (matches.Count == 0) return null;
            if (matches.Count > 1)
                throw new InvalidOperationException($"接口中存在多个同名变量 {varName}，拒绝模糊删除");
            matches[0].Remove();
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        private static XElement? FindInterfaceSections(XDocument doc)
        {
            var iface = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Interface");
            return iface?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections");
        }

        private static IEnumerable<XElement> FindTopLevelInterfaceMembers(XDocument doc, string varName)
        {
            var sections = FindInterfaceSections(doc);
            if (sections == null) return Enumerable.Empty<XElement>();
            return sections.Elements()
                .Where(e => e.Name.LocalName == "Section")
                .SelectMany(section => section.Elements().Where(e => e.Name.LocalName == "Member"))
                .Where(member => string.Equals((string?)member.Attribute("Name"), varName, StringComparison.OrdinalIgnoreCase));
        }

        private static XElement CloneElementToNamespace(XElement source, XNamespace targetNamespace)
        {
            var clone = new XElement(targetNamespace + source.Name.LocalName);
            foreach (var attribute in source.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                clone.Add(attribute.Name.Namespace == XNamespace.None
                    ? new XAttribute(attribute.Name.LocalName, attribute.Value)
                    : new XAttribute(attribute.Name, attribute.Value));
            }
            foreach (var node in source.Nodes())
            {
                if (node is XElement child)
                    clone.Add(CloneElementToNamespace(child, targetNamespace));
                else
                    clone.Add(node);
            }
            return clone;
        }

        /// <summary>反射方式尝试添加 DB 成员（适用于部分博途版本）。</summary>
        private string? TryReflectAddDbMember(object block, string varName, string dataType,
            string startValue, string comment)
        {
            try
            {
                var membersProp = block.GetType().GetProperty("Members");
                if (membersProp == null) return null;
                var members = membersProp.GetValue(block);
                var createMethod = members?.GetType().GetMethod("Create");
                if (createMethod == null) return null;
                var newMember = createMethod.Invoke(members, null);
                if (newMember == null) return null;

                SetProperty(newMember, "Name", varName);
                SetProperty(newMember, "DataTypeName", dataType);
                if (!string.IsNullOrEmpty(startValue))
                {
                    try
                    {
                        object typedValue = dataType.ToUpperInvariant() switch
                        {
                            "REAL" or "LREAL" => double.TryParse(startValue,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double d) ? d : 0.0,
                            "INT" or "SINT" or "DINT" or "LINT" or "USINT"
                                or "UINT" or "UDINT" or "ULINT" => int.TryParse(startValue,
                                System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out int i) ? i : 0,
                            "BOOL" => bool.TryParse(startValue, out bool b) && b,
                            "TIME" => System.TimeSpan.TryParse(startValue,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var ts) ? ts : System.TimeSpan.Zero,
                            _ => (object)startValue
                        };
                        SetProperty(newMember, "StartValue", typedValue);
                    }
                    catch { SetProperty(newMember, "StartValue", startValue); }
                }
                if (!string.IsNullOrEmpty(comment))
                    SetMultilingualText(newMember, "Comment", comment);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"已添加 DB 成员(反射方式): {varName} ({dataType})",
                    variableName = varName,
                    dataType,
                    startValue
                });
            }
            catch { return null; }
        }

        public string ReadDataBlockStructure(string blockName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var vars = new List<object>();

                    // 方式1: 通过反射访问 Interface.Sections.Members
                    try
                    {
                        var secObj = block.GetType().GetProperty("Interface")?.GetValue(block);
                        var sections = secObj as IEnumerable;
                        if (sections != null)
                        {
                            foreach (var sec in sections)
                            {
                                var secName = sec?.GetType().GetProperty("Name")?.GetValue(sec)?.ToString() ?? "";
                                var mems = sec?.GetType().GetProperty("Members")?.GetValue(sec) as IEnumerable;
                                if (mems != null)
                                {
                                    foreach (var m in mems)
                                    {
                                        vars.Add(new
                                        {
                                            section = secName,
                                            name = GetProperty(m, "Name") as string ?? "",
                                            dataType = GetProperty(m, "DataTypeName") as string
                                                       ?? GetProperty(m, "DataType") as string ?? "",
                                            startValue = GetProperty(m, "StartValue")?.ToString() ?? "",
                                            comment = GetMultilingualText(m, "Comment")
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // 方式2: 如果反射失败，尝试通过 Export XML 解析
                    if (vars.Count == 0)
                    {
                        try
                        {
                            var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_ReadDb_{Guid.NewGuid():N}.xml");
                            block.Export(new FileInfo(tempPath), ExportOptions.WithDefaults);
                            try
                            {
                                var xml = File.ReadAllText(tempPath, Encoding.UTF8);
                                // 解析 <Member Name="..." Datatype="..."> 格式
                                var memberMatches = System.Text.RegularExpressions.Regex.Matches(
                                    xml, @"<Member\s+Name=""([^""]+)""\s+Datatype=""([^""]+)""[^>]*(?:>(.*?)</Member|/)>",
                                    System.Text.RegularExpressions.RegexOptions.Singleline);
                                foreach (System.Text.RegularExpressions.Match match in memberMatches)
                                {
                                    var mName = match.Groups[1].Value;
                                    var mType = match.Groups[2].Value;
                                    var mStartValue = "";
                                    // 从内部内容提取 StartValue
                                    if (match.Groups[3].Success)
                                    {
                                        var svMatch = System.Text.RegularExpressions.Regex.Match(
                                            match.Groups[3].Value, @"<StartValue>(.*?)</StartValue>");
                                        if (svMatch.Success) mStartValue = svMatch.Groups[1].Value;
                                    }
                                    vars.Add(new
                                    {
                                        section = "Static",
                                        name = mName,
                                        dataType = mType,
                                        startValue = mStartValue,
                                        comment = ""
                                    });
                                }
                            }
                            finally
                            {
                                try { File.Delete(tempPath); } catch { }
                            }
                        }
                        catch { }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName,
                        variableCount = vars.Count,
                        variables = vars
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────
        // 变量表
        // ────────────────────────────────────────────────
        public string ListTagTables()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var tables = new List<object>();
                    foreach (var plc in GetPlcSoftwareList())
                    {
                        foreach (var t in GetAllTagTables(plc.TagTableGroup))
                            tables.Add(new { name = t.Name, plcName = plc.Name });
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = tables.Count, tagTables = tables });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateTagTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = GetPlcSoftwareFor(plcName);
                    var table = plc.TagTableGroup.TagTables.Create(name);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建变量表: {name}",
                        tableName = table.Name
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteTagTable(string name, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindTagTable(name, plcName);
                    if (table == null) return Err($"未找到变量表: {name}");
                    table.Delete();
                    return Ok($"已删除变量表: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 向变量表添加变量。支持绝对地址 %I / %Q / %M。
        /// </summary>
        public string AddTagToTable(string tagTableName, string tagName, string dataTypeName,
            string logicalAddress, string comment, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindTagTable(tagTableName, plcName);
                    if (table == null) return Err($"未找到变量表: {tagTableName}");

                    var tag = table.Tags.Create(tagName);
                    tag.DataTypeName = dataTypeName;
                    if (!string.IsNullOrEmpty(logicalAddress))
                    {
                        var addr = logicalAddress.TrimStart('%');
                        tag.LogicalAddress = addr;
                    }
                    if (!string.IsNullOrEmpty(comment))
                    {
                        // MultilingualText 是只读属性，通过 Items 集合设置文本
                        var mlText = tag.Comment;
                        var items = mlText?.GetType().GetProperty("Items")?.GetValue(mlText) as IEnumerable;
                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                item.GetType().GetProperty("Text")?.SetValue(item, comment);
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已添加变量: {tagName}",
                        tagName,
                        dataType = dataTypeName,
                        address = logicalAddress
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteTag(string tagTableName, string tagName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindTagTable(tagTableName, plcName);
                    if (table == null) return Err($"未找到变量表: {tagTableName}");
                    var tag = table.Tags.FirstOrDefault(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase));
                    if (tag == null) return Err($"未找到变量: {tagName}");
                    tag.Delete();
                    return Ok($"已删除变量: {tagName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ReadTagTable(string tagTableName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindTagTable(tagTableName, plcName);
                    if (table == null) return Err($"未找到变量表: {tagTableName}");

                    var tags = table.Tags.Select(t =>
                    {
                        string commentStr = "";
                        try
                        {
                            var mlText = t.Comment;
                            if (mlText != null)
                            {
                                var items = mlText.GetType().GetProperty("Items")?.GetValue(mlText) as IEnumerable;
                                if (items != null)
                                {
                                    foreach (var item in items)
                                    {
                                        var txt = item.GetType().GetProperty("Text")?.GetValue(item) as string;
                                        if (!string.IsNullOrEmpty(txt)) { commentStr = txt; break; }
                                    }
                                }
                            }
                        }
                        catch { }
                        return new
                        {
                            name = t.Name,
                            dataType = t.DataTypeName,
                            address = "%" + t.LogicalAddress,
                            comment = commentStr
                        };
                    }).ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tableName = tagTableName,
                        tagCount = tags.Count,
                        tags
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportTagTable(string tagTableName, string outputPath, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var table = FindTagTable(tagTableName, plcName);
                    if (table == null) return Err($"未找到变量表: {tagTableName}");
                    EnsureDir(outputPath);
                    table.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                    return Ok($"变量表已导出到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

                public string ImportTagTable(string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★防护★ 用户可控 XML 文件先做良构校验，坏 XML 曾直接终止 TIA 进程
                    var vErr = ValidateImportXmlFile(filePath, "导入变量表");
                    if (vErr != null) return Err(vErr);
                    var plc = GetPlcSoftware();
                    plc.TagTableGroup.TagTables.Import(new FileInfo(filePath), ImportOptions.Override);
                    return Ok("变量表已导入");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────
        // UDT
        // ────────────────────────────────────────────────
        public string ListUdts()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var udts = new List<object>();
                    foreach (var plc in GetPlcSoftwareList())
                    {
                        foreach (var u in GetAllTypes(plc.TypeGroup))
                            udts.Add(new { name = u.Name, plcName = plc.Name });
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = udts.Count, udts });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ReadUdtStructure(string udtName, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var udt = FindUdt(udtName, plcName);
                    if (udt == null) return Err($"未找到 UDT: {udtName}");

                    var members = new List<object>();
                    try
                    {
                        var mems = udt.GetType().GetProperty("Members")?.GetValue(udt) as IEnumerable;
                        if (mems != null)
                        {
                            foreach (var m in mems)
                                members.Add(new
                                {
                                    name = GetProperty(m, "Name") as string ?? "",
                                    dataType = GetProperty(m, "DataTypeName") as string ?? ""
                                });
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new { success = true, udtName, members });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportUdt(string udtName, string outputPath, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var udt = FindUdt(udtName, plcName);
                    if (udt == null) return Err($"未找到 UDT: {udtName}");
                    EnsureDir(outputPath);
                    udt.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                    return Ok($"UDT 已导出到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

                public string ImportUdt(string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★防护★ 用户可控 XML 文件先做良构校验，坏 XML 曾直接终止 TIA 进程
                    var vErr = ValidateImportXmlFile(filePath, "导入 UDT");
                    if (vErr != null) return Err(vErr);
                    var plc = GetPlcSoftware();
                    plc.TypeGroup.Types.Import(new FileInfo(filePath), ImportOptions.Override);
                    return Ok("UDT 已导入");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────
        // 辅助
        // ────────────────────────────────────────────────

        // 反射辅助: 安全读取对象属性
        private static object? GetProperty(object? obj, string propName)
        {
            if (obj == null) return null;
            try
            {
                var prop = obj.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                return prop?.GetValue(obj);
            }
            catch { return null; }
        }

        // 辅助: 将 XML 内容写入临时文件后导入 TIA，完成后删除临时文件
        // ★修复★ 导入前做良构校验：无效 XML 导入曾直接终止 TIA 进程（disposed）。
        // 良构校验（XDocument.Parse）可拦截语法错误，避免坏 XML 到达 TIA Import。
        /// <summary>导入块 XML（临时文件 + UTF-8 BOM + 良构预检）。</summary>
        private static void ImportXmlTemp(PlcSoftware plc, string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent))
                throw new InvalidOperationException("导入的 XML 内容为空。");
            try
            {
                XDocument.Parse(xmlContent);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("导入的 XML 不是良构文档，已阻止导入以防止博途进程终止: " + ex.Message);
            }
            var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_{Guid.NewGuid():N}.xml");
            File.WriteAllText(tempPath, xmlContent, new UTF8Encoding(true));
            try
            {
                plc.BlockGroup.Blocks.Import(new FileInfo(tempPath), ImportOptions.Override);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        // 反射辅助: 安全设置对象属性
        private static void SetProperty(object? obj, string propName, object? value)
        {
            if (obj == null) return;
            try
            {
                var prop = obj.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(obj, value);
            }
            catch { }
        }

        // 反射辅助: 从 MultilingualText 类型属性读取首个文本
        private static string GetMultilingualText(object? obj, string propName)
        {
            if (obj == null) return "";
            try
            {
                var mlText = GetProperty(obj, propName);
                if (mlText == null) return "";
                var items = mlText.GetType().GetProperty("Items",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(mlText) as IEnumerable;
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var txt = GetProperty(item, "Text") as string;
                        if (!string.IsNullOrEmpty(txt)) return txt;
                    }
                }
                return mlText?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // 反射辅助: 向 MultilingualText 类型属性写入文本
        private static void SetMultilingualText(object? obj, string propName, string text)
        {
            if (obj == null || string.IsNullOrEmpty(text)) return;
            try
            {
                var mlText = GetProperty(obj, propName);
                if (mlText == null) return;
                var items = mlText.GetType().GetProperty("Items",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(mlText) as IEnumerable;
                if (items != null)
                {
                    foreach (var item in items)
                        SetProperty(item, "Text", text);
                }
            }
            catch { }
        }

        private static void EnsureDir(string outputPath)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 块属性 / 保护 / 头部 / 注释 / 设备上传（非 HMI 新功能）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 设置块的保护属性：Know-how 保护 + 密码 + 访问级别。
        /// 项目记忆：先编译块再设置保护属性，避免不一致导致 SetAttribute 失败。
        /// </summary>
        public string SetBlockProtection(string plcName, string blockName, bool enableKnowHow,
            string? password = null, int? accessLevel = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    // 先编译再设置保护（项目记忆）
                    try { block.GetService<ICompilable>()?.Compile(); } catch { }

                    var eo = (IEngineeringObject)block;
                    bool passwordSet = false;
                    bool accessLevelSet = false;

                    if (enableKnowHow)
                    {
                        try { eo.SetAttribute("KnowHowProtection", true); }
                        catch (Exception exKH)
                        {
                            var attrs = ListAttributeNames(eo);
                            return Err($"启用 Know-how 保护失败 ({exKH.Message})。可用属性: {string.Join(", ", attrs)}");
                        }
                    }

                    if (!string.IsNullOrEmpty(password))
                    {
                        try { eo.SetAttribute("Password", password); passwordSet = true; }
                        catch (Exception exPwd)
                        {
                            var attrs = ListAttributeNames(eo);
                            return Err($"设置密码失败 ({exPwd.Message})。可用属性: {string.Join(", ", attrs)}");
                        }
                    }

                    if (accessLevel.HasValue)
                    {
                        try { eo.SetAttribute("AccessProtectionLevel", accessLevel.Value); accessLevelSet = true; }
                        catch (Exception exAL)
                        {
                            var attrs = ListAttributeNames(eo);
                            return Err($"设置访问级别失败 ({exAL.Message})。可用属性: {string.Join(", ", attrs)}");
                        }
                    }

                    // 读回当前 Know-how 状态（IsKnowHowProtected 只读）
                    bool currentKhp = false;
                    try { currentKhp = block.IsKnowHowProtected; } catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName = block.Name,
                        knowHowProtected = enableKnowHow,
                        passwordSet,
                        accessLevelSet,
                        accessLevel = accessLevel,
                        currentKnowHowProtected = currentKhp
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "设置块保护"); }
            }
        }

        /// <summary>
        /// 设置块头部属性（Author/Family/Name/Version）。强类型属性直接赋值，失败回退 SetAttribute。
        /// </summary>
        public string SetBlockHeader(string plcName, string blockName,
            string? author = null, string? family = null,
            string? name = null, string? version = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var eo = (IEngineeringObject)block;

                    var setOK = new System.Collections.Generic.List<string>();
                    var readonlyAttrs = new System.Collections.Generic.List<string>();

                    // V19 Openness API 中 HeaderAuthor/HeaderFamily/HeaderName 是只读属性（CanWrite=false），
                    // SetAttribute 写入会静默失败。这里先检测属性是否可写，对只读属性直接标记，
                    // 避免无意义的写入尝试和误导性的"成功"返回。

                    // 读取当前值（同时用于返回）
                    string curAuthor = "", curFamily = "", curName = "", curVersion = "";
                    try { curAuthor = block.HeaderAuthor ?? ""; } catch { }
                    try { curFamily = block.HeaderFamily ?? ""; } catch { }
                    try { curName = block.HeaderName ?? ""; } catch { }
                    try { var v = block.HeaderVersion; curVersion = v?.ToString() ?? ""; } catch { }

                    // 检测各 Header 属性是否可写（V19 中均为只读）
                    var headerAttrMap = new (string attrName, string? newVal, string curVal)[]
                    {
                        ("HeaderAuthor", author, curAuthor),
                        ("HeaderFamily", family, curFamily),
                        ("HeaderName", name, curName),
                        ("HeaderVersion", version, curVersion)
                    };

                    foreach (var (attrName, newVal, _) in headerAttrMap)
                    {
                        if (string.IsNullOrEmpty(newVal)) continue;
                        // 通过反射检查属性的 CanWrite，判断是否可写
                        var prop = block.GetType().GetProperty(attrName,
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        bool canWrite = prop?.CanWrite ?? false;
                        if (canWrite)
                        {
                            // 属性可写，尝试 SetAttribute
                            try { eo.SetAttribute(attrName, newVal!); setOK.Add(attrName); }
                            catch { readonlyAttrs.Add(attrName); }
                        }
                        else
                        {
                            // V19 只读属性，不可通过 Openness API 修改
                            readonlyAttrs.Add(attrName);
                        }
                    }

                    // 重新读回当前值（SetAttribute 可能对可写属性生效）
                    try { curAuthor = block.HeaderAuthor ?? ""; } catch { }
                    try { curFamily = block.HeaderFamily ?? ""; } catch { }
                    try { curName = block.HeaderName ?? ""; } catch { }
                    try { var v = block.HeaderVersion; curVersion = v?.ToString() ?? ""; } catch { }

                    var result = new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["success"] = readonlyAttrs.Count == 0,
                        ["blockName"] = block.Name,
                        ["author"] = curAuthor,
                        ["family"] = curFamily,
                        ["name"] = curName,
                        ["version"] = curVersion
                    };
                    if (readonlyAttrs.Count > 0)
                    {
                        // V19 Openness API 不支持修改 Header 属性，返回明确错误而非抛异常
                        result["error"] = "V19 Openness API 不支持修改 Header 属性，请在博途 GUI 手动修改。" +
                            $"以下属性为只读: {string.Join(", ", readonlyAttrs)}";
                        result["readonlyAttributes"] = readonlyAttrs;
                    }
                    return JsonConvert.SerializeObject(result);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "设置块头部属性"); }
            }
        }

        /// <summary>
        /// 读取块的所有属性（强类型快照 + GetAttributeInfos 全量枚举）。
        /// 参考 ExploreHmiScreenItem 的反射探测模式。
        /// </summary>
        public string GetBlockProperties(string plcName, string blockName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var strongTyped = new Dictionary<string, object?>();
                    Action<string, Func<object?>> addStrong = (k, getter) =>
                    {
                        try { strongTyped[k] = getter(); } catch { strongTyped[k] = null; }
                    };
                    addStrong("Name", () => block.Name);
                    addStrong("Number", () => block.Number);
                    addStrong("ProgrammingLanguage", () => block.ProgrammingLanguage.ToString());
                    addStrong("IsConsistent", () => block.IsConsistent);
                    addStrong("IsKnowHowProtected", () => block.IsKnowHowProtected);
                    addStrong("MemoryLayout", () => block.MemoryLayout.ToString());
                    addStrong("HeaderAuthor", () => block.HeaderAuthor);
                    addStrong("HeaderFamily", () => block.HeaderFamily);
                    addStrong("HeaderName", () => block.HeaderName);
                    addStrong("HeaderVersion", () => block.HeaderVersion);
                    addStrong("CreationDate", () => block.CreationDate.ToString());
                    addStrong("ModifiedDate", () => block.ModifiedDate.ToString());
                    addStrong("CodeModifiedDate", () => block.CodeModifiedDate.ToString());
                    addStrong("CompileDate", () => block.CompileDate.ToString());
                    addStrong("InterfaceModifiedDate", () => block.InterfaceModifiedDate.ToString());
                    addStrong("StructureModified", () => block.StructureModified.ToString());
                    addStrong("ParameterModified", () => block.ParameterModified.ToString());
                    addStrong("AutoNumber", () => block.AutoNumber);
                    // Namespace 为 V19+ 属性（V17 无），反射读取
                    addStrong("Namespace", () => block.GetType().GetProperty("Namespace")?.GetValue(block)?.ToString() ?? "");
                    addStrong("BlockType", () => GetBlockTypeName(block));
                    addStrong("PlcName", () => GetParentPlcName(block));

                    // GetAttributeInfos 枚举所有可读属性
                    var allAttributes = new List<object>();
                    try
                    {
                        var eo = (IEngineeringObject)block;
                        foreach (var info in eo.GetAttributeInfos())
                        {
                            try
                            {
                                var infoType = info.GetType();
                                var name = infoType.GetProperty("Name")?.GetValue(info)?.ToString() ?? "";
                                var accessMode = infoType.GetProperty("AccessMode")?.GetValue(info)?.ToString() ?? "";
                                object? value = null;
                                try { value = eo.GetAttribute(name); } catch { }
                                allAttributes.Add(new
                                {
                                    name,
                                    value = value?.ToString() ?? "",
                                    accessMode
                                });
                            }
                            catch { }
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName = block.Name,
                        strongTyped,
                        allAttributes,
                        isConsistent = strongTyped.TryGetValue("IsConsistent", out var ic) ? ic : null,
                        isKnowHowProtected = strongTyped.TryGetValue("IsKnowHowProtected", out var kp) ? kp : null
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "读取块属性"); }
            }
        }

        /// <summary>
        /// 设置块的多语言注释。
        /// 块注释通过 IEngineeringObject.GetAttribute("Comment") 获取，类型为 MultilingualText。
        /// language 为空时设置所有语言文本，非空时仅设置匹配语言。
        /// </summary>
        public string SetBlockComment(string plcName, string blockName, string comment, string? language = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    var eo = (IEngineeringObject)block;
                    object? mlText = null;

                    // 尝试路径1: IEngineeringObject.GetAttribute("Comment")
                    try { mlText = eo.GetAttribute("Comment"); } catch { }

                    // 尝试路径2: 块对象自身的 Comment 属性
                    if (mlText == null)
                    {
                        try
                        {
                            var commentProp = block.GetType().GetProperty("Comment",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (commentProp != null) mlText = commentProp.GetValue(block);
                        }
                        catch { }
                    }

                    // 尝试路径3: 直接 SetAttribute("Comment", ...) 方式（某些块类型不支持 GetAttribute 但支持 SetAttribute）
                    if (mlText == null)
                    {
                        try
                        {
                            // 尝试直接反射创建 MultiLanguageText 并设置
                            eo.SetAttribute("Comment", comment);
                            var curComment = "";
                            try { curComment = eo.GetAttribute("Comment")?.ToString() ?? ""; } catch { }
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                blockName = block.Name,
                                comment = curComment,
                                method = "SetAttributeDirect"
                            });
                        }
                        catch (Exception)
                        {
                            // 继续尝试其他路径
                        }
                    }

                    if (mlText == null)
                        return Err("块不支持注释属性（Comment 为空）。已尝试: IEngineeringObject.GetAttribute/Comment属性/SetAttribute");

                    var items = mlText.GetType().GetProperty("Items",
                        BindingFlags.Public | BindingFlags.Instance)?.GetValue(mlText) as IEnumerable;

                    if (items == null)
                        return Err("块注释 Items 集合不可访问");

                    var availableCultures = new List<string>();
                    var matched = false;
                    foreach (var item in items)
                    {
                        // MultilingualTextItem.Language 是 Language 类型对象（非字符串），
                        // 需通过 Language.Culture（CultureInfo）的 Name 属性获取语言标识（如 "zh-CN"）。
                        // 原代码用 Language.ToString() 匹配，永远无法等于 "zh-CN"，导致注释写入失败。
                        var langObj = item.GetType().GetProperty("Language")?.GetValue(item);
                        string cultStr = "";
                        if (langObj != null)
                        {
                            var cultureVal = langObj.GetType().GetProperty("Culture")?.GetValue(langObj)
                                as System.Globalization.CultureInfo;
                            cultStr = cultureVal?.Name ?? "";
                        }
                        if (!string.IsNullOrEmpty(cultStr)) availableCultures.Add(cultStr);

                        if (!string.IsNullOrEmpty(language))
                        {
                            if (cultStr.Equals(language, StringComparison.OrdinalIgnoreCase))
                            {
                                item.GetType().GetProperty("Text")?.SetValue(item, comment);
                                matched = true;
                            }
                        }
                        else
                        {
                            // 未指定语言：设置所有 item 的文本（与 SetMultilingualText 行为一致）
                            item.GetType().GetProperty("Text")?.SetValue(item, comment);
                            matched = true;
                        }
                    }

                    if (!matched && !string.IsNullOrEmpty(language))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"语言 '{language}' 不在块注释可用语言列表中",
                            blockName,
                            availableCultures
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        blockName = block.Name,
                        comment,
                        language = language ?? "(all)",
                        availableCultures
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "设置块注释"); }
            }
        }

        /// <summary>
        /// 从 PLC 设备上传到项目。
        /// 由于 OnlineProvider/StationUploadProvider API 在不同版本差异较大，
        /// 采用反射探测 + try/catch 兜底，依次尝试 StationUploadProvider / OnlineProvider。
        /// </summary>
        public string UploadFromDevice(string plcName, string targetPath,
            bool includeHardware = true, bool includeSoftware = true)
        {
        SafeOnlineExecutor.DemandAuthorized("UploadFromDevice");
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = string.IsNullOrEmpty(plcName)
                        ? GetPlcSoftware()
                        : (FindPlcByName(plcName) ?? throw new InvalidOperationException($"未找到 PLC: {plcName}"));

                    // 反射探测上传服务：依次尝试 StationUploadProvider / OnlineProvider
                    string[] candidateTypeNames =
                    {
                        "Siemens.Engineering.Upload.StationUploadProvider",
                        "Siemens.Engineering.Online.OnlineProvider"
                    };

                    object? uploadProvider = null;
                    string? providerTypeName = null;
                    foreach (var tn in candidateTypeNames)
                    {
                        try
                        {
                            var providerType = typeof(TiaPortal).Assembly.GetType(tn);
                            if (providerType == null) continue;

                            // PlcSoftware 实现 IEngineeringServiceProvider.GetService<T>()
                            // 同时查找 Public 和 NonPublic（显式接口实现可能是 private）
                            var getServiceMethod = plc.GetType().GetMethods(
                                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);

                            // 回退：从接口类型查找
                            if (getServiceMethod == null)
                            {
                                getServiceMethod = typeof(IEngineeringServiceProvider)
                                    .GetMethods()
                                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);
                            }
                            if (getServiceMethod == null) continue;

                            var generic = getServiceMethod.MakeGenericMethod(providerType);
                            uploadProvider = generic.Invoke(plc, null);
                            providerTypeName = tn;
                            if (uploadProvider != null) break;
                        }
                        catch { }
                    }

                    if (uploadProvider == null)
                        return Err($"PLC '{plc.Name}' 未提供上传服务（StationUploadProvider/OnlineProvider 均不可用）。候选类型: {string.Join(", ", candidateTypeNames)}");

                    var providerRuntimeType = uploadProvider.GetType();

                    // 探测在线状态
                    bool isOnline = false;
                    try
                    {
                        var onlineProp = providerRuntimeType.GetProperty("Online")
                                         ?? providerRuntimeType.GetProperty("IsOnline");
                        if (onlineProp != null)
                        {
                            var v = onlineProp.GetValue(uploadProvider);
                            if (v is bool b) isOnline = b;
                            else isOnline = "Online".Equals(v?.ToString(), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch { }

                    // 反射查找上传方法（名字可能为 Upload / UploadFromPlc / StationUpload 等）
                    var uploadMethods = providerRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => !m.IsSpecialName
                                    && (m.Name.IndexOf("Upload", StringComparison.OrdinalIgnoreCase) >= 0
                                        || m.Name.IndexOf("StationUpload", StringComparison.OrdinalIgnoreCase) >= 0))
                        .ToList();

                    if (uploadMethods.Count == 0)
                    {
                        var allMethods = providerRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName)
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                            .ToList();
                        return Err($"上传服务 {providerRuntimeType.Name} 未暴露 Upload 方法。可用方法: {string.Join("; ", allMethods)}");
                    }

                    // 依次尝试调用每个 Upload 方法
                    Exception? lastErr = null;
                    var triedMethods = new List<string>();
                    foreach (var m in uploadMethods)
                    {
                        triedMethods.Add(m.Name);
                        try
                        {
                            var ps = m.GetParameters();
                            var args = ps.Select(p => p.HasDefaultValue ? p.DefaultValue : Type.Missing).ToArray();
                            var result = m.Invoke(uploadProvider, args);
                            var uploadedItems = SerializeUploadResult(result);
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                plcName = plc.Name,
                                targetPath,
                                includeHardware,
                                includeSoftware,
                                providerType = providerRuntimeType.Name,
                                providerTypeName,
                                invokedMethod = m.Name,
                                uploadedItems,
                                isOnline
                            });
                        }
                        catch (Exception ex)
                        {
                            lastErr = ex.InnerException ?? ex;
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"所有 Upload 方法调用失败: {lastErr?.Message}",
                        plcName = plc.Name,
                        providerType = providerRuntimeType.Name,
                        providerTypeName,
                        triedMethods,
                        isOnline,
                        hint = isOnline ? "" : "PLC 可能未在线，请先调用 go_online"
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "从设备上传"); }
            }
        }

        /// <summary>枚举 IEngineeringObject 的所有可用属性名（用于错误提示）。</summary>
        private static List<string> ListAttributeNames(IEngineeringObject eo)
        {
            var names = new List<string>();
            try
            {
                foreach (var info in eo.GetAttributeInfos())
                {
                    try
                    {
                        var n = info.GetType().GetProperty("Name")?.GetValue(info)?.ToString();
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
                    catch { }
                }
            }
            catch { }
            return names;
        }

        /// <summary>序列化上传/操作结果对象（反射读取 State/ErrorCount/WarningCount）。</summary>
        private static List<object> SerializeUploadResult(object? result)
        {
            var list = new List<object>();
            if (result == null) return list;
            try
            {
                var t = result.GetType();
                var state = t.GetProperty("State")?.GetValue(result)?.ToString();
                var errCount = t.GetProperty("ErrorCount")?.GetValue(result);
                var warnCount = t.GetProperty("WarningCount")?.GetValue(result);
                list.Add(new
                {
                    state = state ?? "",
                    errorCount = errCount?.ToString() ?? "0",
                    warningCount = warnCount?.ToString() ?? "0",
                    rawType = t.Name
                });
            }
            catch { }
            return list;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 外部源导入（External Sources：SCL/STL 源）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 导入外部源文件（SCL/STL 源）到 PLC 的外部源组。
        /// API 链：PlcSoftware.ExternalSourceGroup → PlcExternalSourceGroup.ExternalSources →
        ///         PlcExternalSourceComposition.CreateFromFile(string name, string path)。
        /// 由于 ExternalSources 命名空间类型未直接 using，采用反射访问属性和方法。
        /// sourceName 为空时用文件名（不含扩展名）作为导入后的源名称。
        /// </summary>
        public string ImportExternalSource(string plcName, string filePath, string? sourceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath))
                        return Err("filePath 不能为空");
                    if (!File.Exists(filePath))
                        return Err($"源文件不存在: {filePath}");

                    var plc = string.IsNullOrEmpty(plcName)
                        ? GetPlcSoftware()
                        : (FindPlcByName(plcName) ?? throw new InvalidOperationException($"未找到 PLC: {plcName}"));

                    // 反射获取 plc.ExternalSourceGroup 属性
                    var externalSourceGroupProp = typeof(PlcSoftware).GetProperty("ExternalSourceGroup",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (externalSourceGroupProp == null)
                        return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性（当前版本可能不支持外部源）");

                    var externalSourceGroup = externalSourceGroupProp.GetValue(plc);
                    if (externalSourceGroup == null)
                        return Err($"PLC '{plc.Name}' 的 ExternalSourceGroup 为空");

                    var groupType = externalSourceGroup.GetType();

                    // 反射获取 ExternalSources 子集合
                    var externalSourcesProp = groupType.GetProperty("ExternalSources",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (externalSourcesProp == null)
                    {
                        var groupProps = groupType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Select(p => p.Name).ToList();
                        return Err($"ExternalSourceGroup 未暴露 ExternalSources 属性。可用属性: {string.Join(", ", groupProps)}");
                    }

                    var externalSources = externalSourcesProp.GetValue(externalSourceGroup);
                    if (externalSources == null)
                        return Err("ExternalSources 集合为空");

                    var compositionType = externalSources.GetType();

                    // 探测可用方法（apiExplored）
                    var apiExplored = new List<string>();
                    foreach (var m in compositionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                 .Where(m => !m.IsSpecialName))
                    {
                        var ps = m.GetParameters();
                        apiExplored.Add($"{m.Name}({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                    }

                    // 解析源名称：sourceName 为空时用文件名（不含扩展名）
                    string finalSourceName = !string.IsNullOrEmpty(sourceName)
                        ? sourceName!
                        : Path.GetFileNameWithoutExtension(filePath);

                    // 反射查找 CreateFromFile(string name, string path) 方法
                    var createFromFileMethod = compositionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "CreateFromFile"
                                             && m.GetParameters().Length == 2
                                             && m.GetParameters()[0].ParameterType == typeof(string)
                                             && m.GetParameters()[1].ParameterType == typeof(string));

                    if (createFromFileMethod == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 CreateFromFile(string, string) 方法",
                            plcName = plc.Name,
                            filePath,
                            sourceName = finalSourceName,
                            apiExplored
                        }, Formatting.Indented);
                    }

                    object? createdSource;
                    try
                    {
                        createdSource = createFromFileMethod.Invoke(externalSources, new object[] { finalSourceName, filePath });
                    }
                    catch (Exception ex)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"CreateFromFile 调用失败: {ex.InnerException?.Message ?? ex.Message}",
                            plcName = plc.Name,
                            filePath,
                            sourceName = finalSourceName,
                            apiExplored
                        }, Formatting.Indented);
                    }

                    // 读取已创建源的 Name 属性
                    string createdName = "";
                    try
                    {
                        createdName = createdSource?.GetType().GetProperty("Name")?.GetValue(createdSource)?.ToString() ?? "";
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        filePath,
                        sourceName = finalSourceName,
                        createdName = string.IsNullOrEmpty(createdName) ? finalSourceName : createdName,
                        invokedMethod = createFromFileMethod.Name,
                        apiExplored
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "导入外部源"); }
            }
        }

        // ────────────────────────────────────────────────
        // 简化指令盒子创建（AddLadBox / AddLadBoxBatch / GetInstructionDetails）
        // ────────────────────────────────────────────────

        /// <summary>支持的普通 box 型指令（不需要 instance）</summary>
        private static readonly HashSet<string> _boxInstructionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ADD", "SUB", "MUL", "DIV", "MOD",
            "MOVE", "CONVERT",
            "GT", "LT", "GE", "LE", "EQ", "NE", "INRANGE", "OUTRANGE",
            "AND", "OR", "XOR", "NOT", "INV",
            "SHL", "SHR", "ROL", "ROR", "SWAP",
            "ABS", "SQRT", "SQR", "LN", "EXP", "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN",
            "SEL", "LIMIT", "NORM_X", "SCALE_X", "ROUND", "TRUNC", "CEIL", "FLOOR",
            "INC", "DEC", "NEG",
            "S_MOVE", "RBITFIELD", "MOVEBLOCKI", "FILLBLOCKI",
            "CHARS_TO_STRG", "STRG_TO_CHARS", "RESETIECTIMERCOIL"
        };

        /// <summary>
        /// 简化指令盒子创建：根据 boxType 自动构建 sysCall 或 rung 格式 JSON 并调用 AddLadNetwork。
        /// </summary>
        public string AddLadBox(string blockName, string boxType, string? instanceName,
            Dictionary<string, string>? pinBindings, string? instanceScope = null, string? plcName = null)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return Err("blockName 不能为空");
            if (string.IsNullOrWhiteSpace(boxType)) return Err("boxType 不能为空");

            var definition = new LadBoxDef
            {
                boxType = boxType,
                instanceName = instanceName,
                instanceScope = instanceScope,
                parameters = pinBindings ?? new Dictionary<string, string>(),
                title = $"指令盒子: {boxType}"
            };
            return AddLadBoxBatch(blockName, new List<LadBoxDef> { definition }, plcName);
        }

        /// <summary>
        /// 批量添加多个指令盒子。所有项目均转换为强类型 LadNetworkDef，
        /// 再统一进入 AddLadNetworksBatch 的校验、唯一实例分配、导入、编译与回滚链路。
        /// </summary>
        public string AddLadBoxBatch(string blockName, List<LadBoxDef> boxDefinitions, string? plcName = null)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return Err("blockName 不能为空");
            if (boxDefinitions == null || boxDefinitions.Count == 0) return Err("boxes 不能为空");

            var networks = new List<LadNetworkDef>();
            for (var index = 0; index < boxDefinitions.Count; index++)
            {
                var boxDefinition = boxDefinitions[index];
                if (boxDefinition == null || string.IsNullOrWhiteSpace(boxDefinition.boxType))
                    return Err($"boxes[{index}].boxType 不能为空");

                var instruction = boxDefinition.boxType.Trim().ToUpperInvariant();
                var isIecBackground = LadInstructionCatalog.IsBackground(instruction);
                var isLegacyFlipFlop = instruction == "SR" || instruction == "RS";
                var isBox = _boxInstructionTypes.Contains(instruction) || isLegacyFlipFlop;
                if (!isIecBackground && !isBox)
                    return Err($"不支持的指令类型: {boxDefinition.boxType}");

                var pinBindings = boxDefinition.parameters ?? new Dictionary<string, string>();
                if (isIecBackground)
                {
                    string valueType;
                    try { valueType = LadInstructionCatalog.NormalizeValueDatatype(instruction, boxDefinition.datatype); }
                    catch (Exception ex) { return Err($"boxes[{index}].datatype: {ex.Message}"); }
                    networks.Add(new LadNetworkDef
                    {
                        title = boxDefinition.title ?? $"指令盒子: {instruction}",
                        variables = new List<LadVariableDef>(),
                        sysCall = new SysCallDef
                        {
                            inst = instruction,
                            instance = boxDefinition.instanceName,
                            instanceScope = boxDefinition.instanceScope,
                            pins = pinBindings,
                            datatype = valueType,
                            destType = boxDefinition.destType,
                            negateOutput = boxDefinition.negateOutput
                        }
                    });
                }
                else
                {
                    networks.Add(new LadNetworkDef
                    {
                        title = boxDefinition.title ?? $"指令盒子: {instruction}",
                        variables = new List<LadVariableDef>(),
                        rung = new List<RungElement>
                        {
                            new RungElement
                            {
                                box = new RungBox
                                {
                                    box = instruction,
                                    pins = pinBindings,
                                    instance = boxDefinition.instanceName,
                                    instanceScope = boxDefinition.instanceScope,
                                    datatype = boxDefinition.datatype,
                                    destType = boxDefinition.destType,
                                    negateOutput = boxDefinition.negateOutput
                                }
                            }
                        }
                    });
                }
            }

            return AddLadNetworksBatch(blockName, JsonConvert.SerializeObject(networks), plcName, compileAfter: true);
        }

        /// <summary>
        /// 获取指定指令的详细参数信息（基于指令模板库）。
        /// </summary>
        public string GetInstructionDetails(string instructionName)
        {
            if (string.IsNullOrEmpty(instructionName))
                return Err("instructionName 不能为空");

            // 先尝试精确匹配
            var lib = GetInstructionLibrary();
            var match = lib.FirstOrDefault(t =>
                t.Name.Equals(instructionName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                // 模糊匹配
                match = lib.FirstOrDefault(t =>
                    t.Name.IndexOf(instructionName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (match == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = $"未找到指令: {instructionName}。使用 query_instruction_template 或 list_instruction_categories 查看可用指令。",
                    instructionName
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                instruction = new
                {
                    name = match.Name,
                    category = match.Category,
                    description = match.Description,
                    verified = match.Verified,
                    jsonTemplate = match.JsonTemplate,
                    pins = match.Pins.Select(p => new { name = p.Name, type = p.Type, description = p.Description }).ToList(),
                    example = match.Example,
                    notes = match.Notes
                }
            });
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 块/网络编辑、DB/UDT 字段补全、外部源块生成（10 个新方法）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>重命名块（通过 PlcBlock.Name 属性）。</summary>
        public string RenameBlock(string plcName, string oldName, string newName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(oldName)) return Err("oldName 不能为空");
                    if (string.IsNullOrEmpty(newName)) return Err("newName 不能为空");

                    var block = FindBlock(oldName, plcName);
                    if (block == null) return Err($"未找到块: {oldName}");

                    string oldBlockName = block.Name;
                    block.Name = newName;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"块已重命名: {oldBlockName} → {newName}",
                        oldName = oldBlockName,
                        newName = block.Name,
                        plcName = plcName ?? GetParentPlcName(block)
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "重命名块"); }
            }
        }

        /// <summary>更新指定网络（导出XML→替换第 networkIndex 个 CompileUnit→导入）。</summary>
        public string UpdateNetwork(string plcName, string blockName, int networkIndex, string networkJson)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(blockName)) return Err("blockName 不能为空");
                    if (string.IsNullOrEmpty(networkJson)) return Err("networkJson 不能为空");
                    if (networkIndex < 0) return Err("networkIndex 不能为负数");

                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    // 导出块 XML
                    string xml = GetBlockXmlString(block);

                    // 使用 DOM 定位 CompileUnit，避免正则在嵌套 FlgNet 中提前截断。
                    var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                    var compileUnits = document.Descendants()
                        .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit")
                        .ToList();
                    if (compileUnits.Count == 0)
                        return Err($"块 {blockName} 中未找到任何网络（CompileUnit）。可能是 SCL 块或空块。");
                    if (networkIndex >= compileUnits.Count)
                        return Err($"网络索引超出范围: 索引 {networkIndex}，块共有 {compileUnits.Count} 个网络（0~{compileUnits.Count - 1}）。");

                    // ★阶段2 统一输入★ 兼容 IR steps 格式（与 add_lad_network 同源转换），
                    // 消除"update 只吃直通格式、add 只吃 IR 格式"的调用陷阱。
                    var irConverted = TryConvertIrToLegacyJson(networkJson);
                    if (irConverted != null) networkJson = irConverted;

                    var netDef = JsonConvert.DeserializeObject<LadNetworkDef>(networkJson);
                    if (netDef == null) return Err("networkJson 解析失败，请检查格式。");

                    List<string> flgNetList;
                    if (netDef.parts != null && netDef.parts.Count > 0)
                    {
                        // ★ 直通路径: read_lad_network 返回的原始结构(parts/accesses/calls/wires),
                        // 保真重建 FlgNet。read 输出与 FlgNetBuilder 简写格式(contacts/coils)不兼容,
                        // 直接反序列化会全部丢失导致空网络,导入报误导性 ID 错误。
                        var rawCalls = new List<LadRawCallDef>();
                        try
                        {
                            rawCalls = JObject.Parse(networkJson)["calls"]?.ToObject<List<LadRawCallDef>>()
                                ?? new List<LadRawCallDef>();
                        }
                        catch { }
                        flgNetList = new List<string> { FlgNetBuilder.BuildRaw(netDef, rawCalls) };
                    }
                    else
                    {
                        var targetInstances = new HashSet<string>(compileUnits[networkIndex].Descendants()
                            .Where(e => e.Name.LocalName == "Instance")
                            .SelectMany(e => e.Descendants().Where(x => x.Name.LocalName == "Component"))
                            .Select(e => e.Attribute("Name")?.Value ?? "")
                            .Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
                        var reservedInstances = ExtractLadInstanceNames(xml).Where(x => !targetInstances.Contains(x));
                        var updatedNetworks = new List<LadNetworkDef> { netDef };
                        if (!PrepareLadProgram(updatedNetworks, ReadExistingInterfaceVars(block), reservedInstances,
                                out var validationErrors, out var validationWarnings))
                            return JsonConvert.SerializeObject(new { success = false, error = "网络校验失败", validationErrors, validationWarnings }, Formatting.Indented);
                        netDef = updatedNetworks[0];
                        if (!ValidateBlockInstanceCompatibility(netDef, GetBlockTypeName(block), out var compatibilityError))
                            return Err(compatibilityError);
                        flgNetList = FlgNetBuilder.Build(netDef);
                    }
                    if (flgNetList == null || flgNetList.Count == 0)
                        return Err("FlgNet 构建失败，请检查 networkJson 内容。");
                    var flgNet = XElement.Parse(flgNetList[0], LoadOptions.PreserveWhitespace);

                    // ★ 修复: FlgNetBuilder 生成的 UId 从 21 起,与块内现有网络 UId 冲突;
                    // 按网络索引偏移保证块内唯一（引用与定义同步偏移,导入后 TIA 重新分配）
                    if (flgNet != null)
                    {
                        long uidOffset = (networkIndex + 1L) * 1000;
                        foreach (var uidAttr in flgNet.DescendantsAndSelf().Attributes("UId"))
                        {
                            if (long.TryParse(uidAttr.Value, out var uid))
                                uidAttr.Value = (uid + uidOffset).ToString();
                        }
                    }

                    var compileUnit = compileUnits[networkIndex];
                    // ★ 说明: 原生导出的 CompileUnit ID(短 hex 如 3/8/D) 导入是合法的(整块原生 XML
                    // 导入已验证成功),此前 "Simatic ML ID '3' is not supported" 是空 FlgNet 的误导性报错,
                    // 根因是 read 输出与 FlgNetBuilder 简写格式不兼容,已由直通路径(BuildRaw)修复。
                    var attributeList = compileUnit.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")
                        ?? throw new InvalidOperationException("CompileUnit 缺少 AttributeList");
                    var networkSource = attributeList.Elements().FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
                    if (networkSource == null)
                    {
                        networkSource = new XElement(attributeList.Name.Namespace + "NetworkSource");
                        attributeList.AddFirst(networkSource);
                    }
                    networkSource.RemoveNodes();
                    networkSource.Add(flgNet);

                    var language = attributeList.Elements().FirstOrDefault(e => e.Name.LocalName == "ProgrammingLanguage");
                    if (language == null)
                        attributeList.Add(new XElement(attributeList.Name.Namespace + "ProgrammingLanguage", "LAD"));
                    else
                        language.Value = "LAD";

                    if (!string.IsNullOrWhiteSpace(netDef.title))
                    {
                        var title = compileUnit.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName == "MultilingualText" && string.Equals((string?)e.Attribute("CompositionName"), "Title", StringComparison.OrdinalIgnoreCase));
                        var text = title?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text");
                        if (text != null) text.Value = netDef.title;
                    }

                    string newXml = document.ToString(SaveOptions.DisableFormatting);
                    // ★ 调试: 落盘生成的 XML 供对比分析（定位 CompileUnit 创建失败原因）
                    try
                    {
                        var debugDir = Path.Combine(Path.GetTempPath(), "TiaMcpDebug");
                        Directory.CreateDirectory(debugDir);
                        File.WriteAllText(Path.Combine(debugDir, $"upd_{blockName}_{networkIndex}.xml"), newXml, new UTF8Encoding(false));
                    }
                    catch { }
                    // ★ 修复: Import 成功后旧块引用被 Dispose,必须在导入前缓存父 PLC 名
                    string parentPlcName = plcName ?? GetParentPlcName(block);
                    ImportXmlTemp(plc, newXml);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已更新块 {blockName} 的网络 #{networkIndex}",
                        blockName,
                        networkIndex,
                        totalNetworks = compileUnits.Count,
                        newTitle = netDef.title ?? "",
                        plcName = parentPlcName
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "更新网络"); }
            }
        }

        /// <summary>删除指定网络（导出XML→删除第 networkIndex 个 CompileUnit→导入）。</summary>
        public string DeleteNetwork(string plcName, string blockName, int networkIndex)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(blockName)) return Err("blockName 不能为空");
                    if (networkIndex < 0) return Err("networkIndex 不能为负数");

                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(blockName, plcName);
                    if (block == null) return Err($"未找到块: {blockName}");

                    string xml = GetBlockXmlString(block);

                    var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                    var compileUnits = document.Descendants()
                        .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit")
                        .ToList();
                    if (compileUnits.Count == 0)
                        return Err($"块 {blockName} 中未找到任何网络（CompileUnit）。");
                    if (networkIndex >= compileUnits.Count)
                        return Err($"网络索引超出范围: 索引 {networkIndex}，块共有 {compileUnits.Count} 个网络（0~{compileUnits.Count - 1}）。");

                    compileUnits[networkIndex].Remove();
                    // ★ 修复: 剩余网络的 CompileUnit ID 归一化为 A+hex 并保证唯一,否则导入失败
                    NormalizeCompileUnitIds(document);
                    string newXml = document.ToString(SaveOptions.DisableFormatting);
                    ImportXmlTemp(plc, newXml);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已删除块 {blockName} 的网络 #{networkIndex}",
                        blockName,
                        networkIndex,
                        remainingNetworks = compileUnits.Count - 1,
                        plcName = plcName ?? GetParentPlcName(block)
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "删除网络"); }
            }
        }

        /// <summary>将文档中所有 CompileUnit 的 ID 归一化为十进制并保证唯一。
        /// V19 导入器对 CompileUnit ID 有严格约束:导出的短 hex(3/8/D) 或 A+hex 均报
        /// "Simatic ML ID 'x' is not supported",十进制递增 ID 为 Openness 官方格式。</summary>
        private static void NormalizeCompileUnitIds(XDocument document)
        {
            var usedIds = new HashSet<string>(
                document.Descendants().Attributes("ID").Select(a => a.Value),
                StringComparer.OrdinalIgnoreCase);
            foreach (var cu in document.Descendants()
                .Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit").ToList())
            {
                var idAttr = cu.Attribute("ID");
                if (idAttr == null) continue;
                idAttr.Value = ToUniqueNumericId(usedIds);
            }
        }

        /// <summary>生成块内最大数字 ID + 1 的十进制 ID（Openness 官方格式,与现有对象不冲突）。</summary>
        private static string ToUniqueNumericId(HashSet<string> usedIds)
        {
            long maxId = 0;
            foreach (var v in usedIds)
            {
                if (long.TryParse(v, out var n) && n > maxId) maxId = n;
            }
            while (!usedIds.Add((++maxId).ToString())) { }
            return maxId.ToString();
        }

        /// <summary>批量添加 DB 成员（不覆盖已有变量）。</summary>
        public string AddDbMembersBatch(string plcName, string dbName, List<DbMemberInput>? members)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(dbName)) return Err("dbName 不能为空");
                    if (members == null || members.Count == 0) return Err("members 不能为空");

                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(dbName, plcName);
                    if (block == null) return Err($"未找到 DB: {dbName}");

                    var added = new List<object>();
                    var failed = new List<object>();
                    int successCount = 0;

                    // 方式1: Export → 合并所有 Member → Import（一次导入，避免多次往返）
                    try
                    {
                        var xml = ExportBlockXmlSafe(block);
                        bool xmlModified = false;

                        foreach (var m in members)
                        {
                            if (string.IsNullOrEmpty(m.MemberName) || string.IsNullOrEmpty(m.DataType))
                            {
                                failed.Add(new { memberName = m.MemberName ?? "", error = "MemberName 或 DataType 为空" });
                                continue;
                            }

                            try
                            {
                                var memberXml = BuildMemberXml(m.MemberName, m.DataType, m.InitialValue, m.Comment);
                                xml = MergeDbMemberXmlCore(xml, memberXml, m.MemberName);
                                added.Add(new
                                {
                                    memberName = m.MemberName,
                                    dataType = m.DataType,
                                    initialValue = m.InitialValue ?? "",
                                    comment = m.Comment ?? ""
                                });
                                successCount++;
                                xmlModified = true;
                            }
                            catch (Exception mex)
                            {
                                failed.Add(new { memberName = m.MemberName, error = mex.Message });
                            }
                        }

                        if (xmlModified)
                        {
                            ImportXmlTemp(plc, xml);
                        }
                    }
                    catch (Exception exportEx)
                    {
                        // Export 失败 → 编译 DB 后重试一次
                        try
                        {
                            var compilable = block.GetService<ICompilable>();
                            compilable?.Compile();
                        }
                        catch { }

                        // 重试时改为逐个添加
                        added.Clear();
                        failed.Clear();
                        successCount = 0;

                        foreach (var m in members)
                        {
                            if (string.IsNullOrEmpty(m.MemberName) || string.IsNullOrEmpty(m.DataType))
                            {
                                failed.Add(new { memberName = m.MemberName ?? "", error = "MemberName 或 DataType 为空" });
                                continue;
                            }

                            try
                            {
                                var memberXml = BuildMemberXml(m.MemberName, m.DataType, m.InitialValue, m.Comment);
                                var merged = MergeDbMemberXml(block, memberXml, m.MemberName);
                                ImportXmlTemp(plc, merged);
                                added.Add(new
                                {
                                    memberName = m.MemberName,
                                    dataType = m.DataType,
                                    initialValue = m.InitialValue ?? "",
                                    comment = m.Comment ?? ""
                                });
                                successCount++;
                            }
                            catch (Exception mex)
                            {
                                failed.Add(new { memberName = m.MemberName, error = mex.Message });
                            }
                        }
                    }

                    // ★ 修复: Import 后旧块引用被 Dispose,须在导入前缓存父 PLC 名
                    string parentPlcName = plcName ?? GetParentPlcName(block);
                    return JsonConvert.SerializeObject(new
                    {
                        success = successCount > 0,
                        message = $"批量添加 DB 成员完成: 成功 {successCount}/{members.Count}",
                        dbName,
                        addedCount = successCount,
                        failedCount = failed.Count,
                        added,
                        failed,
                        plcName = parentPlcName
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "批量添加 DB 成员"); }
            }
        }

        /// <summary>核心合并函数（DOM 模式，不依赖 ExportBlockXml）。</summary>
        private static string MergeDbMemberXmlCore(string xml, string newMemberXml, string varName)
            => MergeMemberIntoInterfaceSection(xml, "Static", newMemberXml, varName);

        /// <summary>增强版 DB 变量更新：支持 Remanence/Accessible/Visible/Writable/Setpoint 属性。</summary>
        public string UpdateDbVariableEnhanced(string plcName, string dbName, string varName,
            string? newDataType, string? newStartValue, string? newComment,
            string? remanence, bool? accessible, bool? visible, bool? writable, bool? setpoint)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(dbName)) return Err("dbName 不能为空");
                    if (string.IsNullOrEmpty(varName)) return Err("varName 不能为空");

                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(dbName, plcName);
                    if (block == null) return Err($"未找到 DB: {dbName}");

                    string xml = GetBlockXmlString(block);

                    // 替换 Member 节点
                    bool memberFound = false;
                    string updatedXml = ReplaceMemberInXml(xml, varName, memberXml =>
                    {
                        memberFound = true;
                        var member = XElement.Parse(memberXml, LoadOptions.PreserveWhitespace);
                        var ns = member.Name.Namespace;
                        if (!string.IsNullOrWhiteSpace(newDataType))
                            member.SetAttributeValue("Datatype", newDataType);

                        if (!string.IsNullOrWhiteSpace(remanence))
                        {
                            var rem = remanence!.Equals("Retain", StringComparison.OrdinalIgnoreCase) ? "Retain" : "NonRetain";
                            member.SetAttributeValue("Remanence", rem);
                        }
                        if (accessible.HasValue) member.SetAttributeValue("Accessibility", accessible.Value ? "Public" : "None");
                        if (visible.HasValue) member.SetAttributeValue("Visible", visible.Value.ToString().ToLowerInvariant());
                        if (writable.HasValue) member.SetAttributeValue("Writable", writable.Value.ToString().ToLowerInvariant());
                        if (setpoint.HasValue) member.SetAttributeValue("Setpoint", setpoint.Value.ToString().ToLowerInvariant());

                        if (!string.IsNullOrEmpty(newStartValue))
                        {
                            var start = member.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue");
                            if (start == null) member.Add(new XElement(ns + "StartValue", newStartValue));
                            else start.Value = newStartValue;
                        }
                        if (!string.IsNullOrEmpty(newComment))
                        {
                            var comment = member.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment");
                            if (comment == null)
                            {
                                comment = new XElement(ns + "Comment");
                                member.Add(comment);
                            }
                            var text = comment.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText");
                            if (text == null)
                                comment.Add(new XElement(ns + "MultiLanguageText", new XAttribute("Lang", "zh-CN"), newComment));
                            else
                                text.Value = newComment;
                        }
                        return member.ToString(SaveOptions.DisableFormatting);
                    });

                    if (!memberFound)
                        return Err($"DB {dbName} 中未找到变量: {varName}");

                    ImportXmlTemp(plc, updatedXml);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已更新 DB 变量: {varName}",
                        dbName,
                        varName,
                        newDataType = newDataType ?? "(未修改)",
                        newStartValue = newStartValue ?? "(未修改)",
                        newComment = newComment ?? "(未修改)",
                        remanence = remanence ?? "(未修改)",
                        accessible = accessible?.ToString() ?? "(未修改)",
                        visible = visible?.ToString() ?? "(未修改)",
                        writable = writable?.ToString() ?? "(未修改)",
                        setpoint = setpoint?.ToString() ?? "(未修改)",
                        plcName = plcName ?? GetParentPlcName(block)
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "增强更新 DB 变量"); }
            }
        }

        /// <summary>更新 UDT 成员（XML 往返模式）。</summary>
        public string UpdateUdtMember(string plcName, string udtName, string memberName,
            string? newDataType, string? newStartValue, string? newComment)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(udtName)) return Err("udtName 不能为空");
                    if (string.IsNullOrEmpty(memberName)) return Err("memberName 不能为空");

                    var plc = GetPlcSoftwareFor(plcName);
                    var udt = FindUdt(udtName, plcName);
                    if (udt == null) return Err($"未找到 UDT: {udtName}");

                    // 导出 UDT XML
                    string xml;
                    var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_Udt_{Guid.NewGuid():N}.xml");
                    try
                    {
                        udt.Export(new FileInfo(tempPath), ExportOptions.WithDefaults);
                        xml = File.ReadAllText(tempPath, Encoding.UTF8);
                    }
                    finally
                    {
                        try { File.Delete(tempPath); } catch { }
                    }

                    // 替换 Member 节点
                    bool memberFound = false;
                    string updatedXml = ReplaceMemberInXml(xml, memberName, memberXml =>
                    {
                        memberFound = true;
                        var member = XElement.Parse(memberXml, LoadOptions.PreserveWhitespace);
                        var ns = member.Name.Namespace;
                        if (!string.IsNullOrWhiteSpace(newDataType))
                            member.SetAttributeValue("Datatype", newDataType);
                        if (!string.IsNullOrEmpty(newStartValue))
                        {
                            var start = member.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue");
                            if (start == null) member.Add(new XElement(ns + "StartValue", newStartValue));
                            else start.Value = newStartValue;
                        }
                        if (!string.IsNullOrEmpty(newComment))
                        {
                            var comment = member.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment");
                            if (comment == null)
                            {
                                comment = new XElement(ns + "Comment");
                                member.Add(comment);
                            }
                            var text = comment.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText");
                            if (text == null)
                                comment.Add(new XElement(ns + "MultiLanguageText", new XAttribute("Lang", "zh-CN"), newComment));
                            else
                                text.Value = newComment;
                        }
                        return member.ToString(SaveOptions.DisableFormatting);
                    });

                    if (!memberFound)
                        return Err($"UDT {udtName} 中未找到成员: {memberName}");

                    // 导入更新后的 XML（通过 PlcTypeComposition.Import）
                    var typesProp = plc.GetType().GetProperty("TypeGroup");
                    object? typeGroup = typesProp?.GetValue(plc);
                    var typesCollection = typeGroup?.GetType().GetProperty("Types")?.GetValue(typeGroup);
                    if (typesCollection == null)
                        return Err("无法获取 PlcTypeGroup.Types 集合（API 限制）");

                    var importMethod = typesCollection.GetType().GetMethod("Import",
                        new[] { typeof(FileInfo), typeof(ImportOptions) });
                    if (importMethod == null)
                        return Err("PlcTypeGroup.Types 未暴露 Import(FileInfo, ImportOptions) 方法");

                    var importPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_UdtImport_{Guid.NewGuid():N}.xml");
                    File.WriteAllText(importPath, updatedXml, Encoding.UTF8);
                    try
                    {
                        importMethod.Invoke(typesCollection, new object[] { new FileInfo(importPath), ImportOptions.Override });
                    }
                    finally
                    {
                        try { File.Delete(importPath); } catch { }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已更新 UDT 成员: {udtName}.{memberName}",
                        udtName,
                        memberName,
                        newDataType = newDataType ?? "(未修改)",
                        newStartValue = newStartValue ?? "(未修改)",
                        newComment = newComment ?? "(未修改)",
                        plcName = plcName ?? ""
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "更新 UDT 成员"); }
            }
        }

        /// <summary>从外部源生成块（反射调用 PlcExternalSource.GenerateBlocksFromSource）。</summary>
        public string GenerateBlocksFromSource(string plcName, string sourceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(sourceName)) return Err("sourceName 不能为空");

                    var plc = string.IsNullOrEmpty(plcName)
                        ? GetPlcSoftware()
                        : (FindPlcByName(plcName) ?? throw new InvalidOperationException($"未找到 PLC: {plcName}"));

                    // 反射获取 ExternalSourceGroup
                    var externalSourceGroupProp = typeof(PlcSoftware).GetProperty("ExternalSourceGroup",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (externalSourceGroupProp == null)
                        return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var externalSourceGroup = externalSourceGroupProp.GetValue(plc);
                    if (externalSourceGroup == null)
                        return Err($"PLC '{plc.Name}' 的 ExternalSourceGroup 为空");

                    // 反射获取 ExternalSources 集合
                    var externalSourcesProp = externalSourceGroup.GetType().GetProperty("ExternalSources",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (externalSourcesProp == null)
                        return Err("ExternalSourceGroup 未暴露 ExternalSources 属性");

                    var externalSources = externalSourcesProp.GetValue(externalSourceGroup);
                    if (externalSources == null)
                        return Err("ExternalSources 集合为空");

                    // 查找指定名称的外部源
                    object? targetSource = null;
                    var sourcesEnum = externalSources as IEnumerable;
                    if (sourcesEnum != null)
                    {
                        foreach (var s in sourcesEnum)
                        {
                            var name = s?.GetType().GetProperty("Name")?.GetValue(s)?.ToString();
                            if (name != null && name.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                            {
                                targetSource = s;
                                break;
                            }
                        }
                    }
                    if (targetSource == null)
                        return Err($"未找到外部源: {sourceName}");

                    // 反射调用 GenerateBlocksFromSource 方法
                    var sourceType = targetSource.GetType();
                    var generateMethod = sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GenerateBlocksFromSource"
                                          && m.GetParameters().Length == 0);
                    if (generateMethod == null)
                    {
                        var methods = sourceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name.Contains("Generate")).Select(m =>
                            {
                                var ps = m.GetParameters();
                                return $"{m.Name}({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})";
                            }).ToList();
                        return Err($"未找到无参 GenerateBlocksFromSource 方法。可用 Generate 方法: {string.Join("; ", methods)}");
                    }

                    object? result;
                    try
                    {
                        result = generateMethod.Invoke(targetSource, null);
                    }
                    catch (Exception ex)
                    {
                        return Err($"GenerateBlocksFromSource 调用失败: {ex.InnerException?.Message ?? ex.Message}");
                    }

                    // 解析生成的块信息
                    var generatedBlocks = new List<object>();
                    if (result is IEnumerable enumResult)
                    {
                        foreach (var r in enumResult)
                        {
                            generatedBlocks.Add(new
                            {
                                name = r?.GetType().GetProperty("Name")?.GetValue(r)?.ToString() ?? "",
                                type = r?.GetType().Name ?? ""
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已从外部源 {sourceName} 生成 {generatedBlocks.Count} 个块",
                        plcName = plc.Name,
                        sourceName,
                        generatedBlocks,
                        blockCount = generatedBlocks.Count
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "从外部源生成块"); }
            }
        }

        /// <summary>生成外部源文件（反射调用 PlcExternalSourceSystemGroup.GenerateSource）。</summary>
        public string GenerateExternalSource(string plcName, List<string> blockNames, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (blockNames == null || blockNames.Count == 0) return Err("blockNames 不能为空");
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");

                    var plc = string.IsNullOrEmpty(plcName)
                        ? GetPlcSoftware()
                        : (FindPlcByName(plcName) ?? throw new InvalidOperationException($"未找到 PLC: {plcName}"));

                    // 查找所有指定的块（作为 IGenerateSource）
                    var generateSources = new List<object>();
                    var notFound = new List<string>();
                    foreach (var bn in blockNames)
                    {
                        var blk = FindBlock(bn, plcName);
                        if (blk == null) notFound.Add(bn);
                        else generateSources.Add(blk);
                    }
                    if (generateSources.Count == 0)
                        return Err($"未找到任何块: {string.Join(", ", blockNames)}");

                    // 反射获取 ExternalSourceGroup
                    var externalSourceGroupProp = typeof(PlcSoftware).GetProperty("ExternalSourceGroup",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (externalSourceGroupProp == null)
                        return Err("PlcSoftware 未暴露 ExternalSourceGroup 属性");

                    var externalSourceGroup = externalSourceGroupProp.GetValue(plc);
                    if (externalSourceGroup == null)
                        return Err($"PLC '{plc.Name}' 的 ExternalSourceGroup 为空");

                    // 反射调用 GenerateSource(IEnumerable<IGenerateSource>, FileInfo)
                    var groupType = externalSourceGroup.GetType();
                    var generateSourceMethod = groupType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GenerateSource"
                                          && m.GetParameters().Length == 2
                                          && m.GetParameters()[0].ParameterType.Name.StartsWith("IEnumerable")
                                          && m.GetParameters()[1].ParameterType == typeof(FileInfo));
                    if (generateSourceMethod == null)
                    {
                        var methods = groupType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name.Contains("Generate")).Select(m =>
                            {
                                var ps = m.GetParameters();
                                return $"{m.Name}({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})";
                            }).ToList();
                        return Err($"未找到 GenerateSource(IEnumerable, FileInfo) 方法。可用 Generate 方法: {string.Join("; ", methods)}");
                    }

                    // 转换 IEnumerable<object> 为 IEnumerable<IGenerateSource>
                    var sourceListType = generateSourceMethod.GetParameters()[0].ParameterType;
                    var elementType = sourceListType.GetGenericArguments().FirstOrDefault() ?? typeof(object);
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    var sourceList = (System.Collections.IList)Activator.CreateInstance(listType)!;
                    foreach (var s in generateSources)
                    {
                        if (elementType.IsAssignableFrom(s.GetType()))
                            sourceList.Add(s);
                        else
                            return Err($"块 {s.GetType().GetProperty("Name")?.GetValue(s)} 不实现 IGenerateSource 接口");
                    }

                    var fileInfo = new FileInfo(filePath);
                    EnsureDir(fileInfo.FullName);

                    object? result;
                    try
                    {
                        result = generateSourceMethod.Invoke(externalSourceGroup, new object[] { sourceList, fileInfo });
                    }
                    catch (Exception ex)
                    {
                        return Err($"GenerateSource 调用失败: {ex.InnerException?.Message ?? ex.Message}");
                    }

                    bool fileExists = File.Exists(filePath);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = fileExists
                            ? $"已生成外部源文件: {filePath}（包含 {generateSources.Count} 个块）"
                            : $"GenerateSource 调用成功，但文件未生成: {filePath}",
                        plcName = plc.Name,
                        filePath,
                        blockCount = generateSources.Count,
                        blockNames = generateSources.Select(b => b.GetType().GetProperty("Name")?.GetValue(b)?.ToString() ?? "").ToList(),
                        notFoundBlocks = notFound,
                        fileExists,
                        result = result?.ToString() ?? ""
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "生成外部源"); }
            }
        }

        /// <summary>添加 FB 多实例成员（修改 Static 区域）。</summary>
        public string AddMultiInstanceMember(string plcName, string fbName, string instanceName, string instanceType)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(fbName)) return Err("fbName 不能为空");
                    if (string.IsNullOrEmpty(instanceName)) return Err("instanceName 不能为空");
                    if (string.IsNullOrEmpty(instanceType)) return Err("instanceType 不能为空");

                    var plc = GetPlcSoftwareFor(plcName);
                    var block = FindBlock(fbName, plcName);
                    if (block == null) return Err($"未找到 FB: {fbName}");

                    // FB 多实例成员：在 Static 区域添加一个类型为 FB/FC 的成员
                    // XML 往返：Export → 添加 Member → Import
                    string xml = GetBlockXmlString(block);

                    // 构建 Member XML（多实例成员的 Datatype 是 FB 类型名）
                    var memberXml = $"<Member Name=\"{SecurityElement.Escape(instanceName)}\" Datatype=\"{SecurityElement.Escape(instanceType)}\" />";

                    // 在 Static 区域以 DOM 方式添加成员，嵌套实例接口不会被正则截断。
                    string updatedXml = MergeMemberIntoInterfaceSection(xml, "Static", memberXml, instanceName);
                    ImportXmlTemp(plc, updatedXml);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已添加 FB 多实例成员: {fbName}.{instanceName} ({instanceType})",
                        fbName,
                        instanceName,
                        instanceType,
                        plcName = plcName ?? GetParentPlcName(block)
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "添加 FB 多实例成员"); }
            }
        }

        /// <summary>设置变量表中变量的访问属性（ExternalAccessible/ExternalVisible/ExternalWritable）。</summary>
        public string SetTagAccess(string plcName, string tableName, string tagName,
            bool accessible, bool visible, bool writable)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(tableName)) return Err("tableName 不能为空");
                    if (string.IsNullOrEmpty(tagName)) return Err("tagName 不能为空");

                    var table = FindTagTable(tableName, plcName);
                    if (table == null) return Err($"未找到变量表: {tableName}");

                    // 在变量表中查找指定变量
                    PlcTag? targetTag = null;
                    foreach (var t in table.Tags)
                    {
                        if (t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetTag = t;
                            break;
                        }
                    }
                    if (targetTag == null)
                        return Err($"变量表 {tableName} 中未找到变量: {tagName}");

                    // 读取原始值用于对比
                    bool origAccessible = targetTag.ExternalAccessible;
                    bool origVisible = targetTag.ExternalVisible;
                    bool origWritable = targetTag.ExternalWritable;

                    // 设置新值
                    targetTag.ExternalAccessible = accessible;
                    targetTag.ExternalVisible = visible;
                    targetTag.ExternalWritable = writable;

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已设置变量访问属性: {tableName}.{tagName}",
                        tableName,
                        tagName,
                        accessible,
                        visible,
                        writable,
                        originalAccessible = origAccessible,
                        originalVisible = origVisible,
                        originalWritable = origWritable,
                        plcName = plcName ?? ""
                    });
                }
                catch (Exception ex) { return ErrWithRecovery(ex, "设置变量访问属性"); }
            }
        }
    }

    /// <summary>DB 成员输入模型（AddDbMembersBatch 使用）。</summary>
    public class DbMemberInput
    {
        public string MemberName { get; set; } = "";
        public string DataType { get; set; } = "Bool";
        public string Comment { get; set; } = "";
        public string InitialValue { get; set; } = "";
        public string Remanence { get; set; } = "NonRetain";
        public string Accessible { get; set; } = "true";
        public string Visible { get; set; } = "true";
        public string Writable { get; set; } = "true";
        public string Setpoint { get; set; } = "false";
    }
}
