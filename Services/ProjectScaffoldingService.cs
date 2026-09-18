using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcpServer
{
    /// <summary>
    /// 项目脚手架服务：IO 地址自动分配 + 工业项目骨架创建。
    /// 让 AI 能按工业标准自动规划 IO 地址和创建项目结构。
    /// </summary>
    public partial class PortalService
    {
        // ────────────────────────────────────────────────
        // IO 地址自动分配
        // ────────────────────────────────────────────────
        /// <summary>
        /// 根据信号列表自动分配 S7-1200 物理地址。
        /// 遵循字节-位规则：每个字节仅支持 0-7 位，非 Bool 信号需要字节对齐。
        /// </summary>
        public string AutoAllocateIoAddresses(string signalsJson, int? startInputByte, int? startOutputByte)
        {
            try
            {
                var signals = JArray.Parse(signalsJson);
                var inStart = startInputByte ?? 0;
                var outStart = startOutputByte ?? 0;

                // 分配器状态：当前字节和位
                int inByte = inStart, inBit = 0;
                int outByte = outStart, outBit = 0;

                var results = new List<object>();

                foreach (var sig in signals)
                {
                    string name = sig["name"]?.ToString() ?? "";
                    string dataType = (sig["dataType"]?.ToString() ?? "Bool").Trim();
                    string direction = (sig["direction"]?.ToString() ?? "Input").Trim();
                    string comment = sig["comment"]?.ToString() ?? "";

                    // 标准化方向
                    bool isInput = direction.Equals("Input", StringComparison.OrdinalIgnoreCase) ||
                                   direction.Equals("输入", StringComparison.OrdinalIgnoreCase);
                    bool isOutput = direction.Equals("Output", StringComparison.OrdinalIgnoreCase) ||
                                    direction.Equals("输出", StringComparison.OrdinalIgnoreCase);

                    if (!isInput && !isOutput)
                    {
                        results.Add(new { name, dataType, direction, address = "", error = "direction 必须是 Input 或 Output" });
                        continue;
                    }

                    ref int curByte = ref (isInput ? ref inByte : ref outByte);
                    ref int curBit = ref (isInput ? ref inBit : ref outBit);
                    string prefix = isInput ? "I" : "Q";

                    string address;
                    int sizeBits;

                    if (dataType.Equals("Bool", StringComparison.OrdinalIgnoreCase))
                    {
                        // Bool: 1 位
                        if (curBit > 7)
                        {
                            curByte++;
                            curBit = 0;
                        }
                        address = $"%{prefix}{curByte}.{curBit}";
                        curBit++;
                        sizeBits = 1;
                    }
                    else if (dataType.Equals("Byte", StringComparison.OrdinalIgnoreCase) ||
                             dataType.Equals("SByte", StringComparison.OrdinalIgnoreCase))
                    {
                        // Byte: 1 字节，需要对齐
                        if (curBit > 0) { curByte++; curBit = 0; }
                        address = $"%{prefix}B{curByte}";
                        curByte++;
                        sizeBits = 8;
                    }
                    else if (dataType.Equals("Word", StringComparison.OrdinalIgnoreCase) ||
                             dataType.Equals("Int", StringComparison.OrdinalIgnoreCase) ||
                             dataType.Equals("UInt", StringComparison.OrdinalIgnoreCase))
                    {
                        // Int/Word: 2 字节，需要对齐到偶数字节
                        if (curBit > 0) { curByte++; curBit = 0; }
                        if (curByte % 2 != 0) curByte++; // 偶数对齐
                        address = $"%{prefix}W{curByte}";
                        curByte += 2;
                        sizeBits = 16;
                    }
                    else if (dataType.Equals("DWord", StringComparison.OrdinalIgnoreCase) ||
                             dataType.Equals("DInt", StringComparison.OrdinalIgnoreCase) ||
                             dataType.Equals("UDInt", StringComparison.OrdinalIgnoreCase) ||
                             dataType.Equals("Real", StringComparison.OrdinalIgnoreCase))
                    {
                        // DInt/Real: 4 字节，需要对齐到 4 的倍数字节
                        if (curBit > 0) { curByte++; curBit = 0; }
                        while (curByte % 4 != 0) curByte++;
                        address = $"%{prefix}D{curByte}";
                        curByte += 4;
                        sizeBits = 32;
                    }
                    else
                    {
                        results.Add(new { name, dataType, direction, address = "", error = $"不支持的数据类型: {dataType}" });
                        continue;
                    }

                    results.Add(new
                    {
                        name,
                        dataType,
                        direction = isInput ? "Input" : "Output",
                        address,
                        sizeBits,
                        comment
                    });
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = results.Count,
                    inputRange = $"%I{inStart}.0 - %I{inByte}.{Math.Max(0, inBit - 1)}",
                    outputRange = $"%Q{outStart}.0 - %Q{outByte}.{Math.Max(0, outBit - 1)}",
                    nextInputByte = inBit > 0 ? inByte + 1 : inByte,
                    nextOutputByte = outBit > 0 ? outByte + 1 : outByte,
                    signals = results
                });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        // ────────────────────────────────────────────────
        // 工业项目骨架创建
        // ────────────────────────────────────────────────
        /// <summary>
        /// 创建工业级项目骨架：标准 FC/FB/DB 分层结构。
        /// 基于 industrial_patterns.md 的峨胜皮带采样系统架构。
        /// 创建空块，AI 后续用 add_lad_network 填充逻辑。
        /// </summary>
        public string CreateIndustrialProjectSkeleton(string? template, string? customBlocksJson, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★Bug 13: 支持 plcName 指定目标 PLC；未指定时保持原逻辑（第一个 PLC）但多 PLC 时给出警告
                    PlcSoftware plc;
                    var warnings = new List<string>();
                    if (!string.IsNullOrEmpty(plcName))
                    {
                        plc = FindPlcByName(plcName)
                            ?? throw new InvalidOperationException($"未找到 PLC: {plcName}");
                    }
                    else
                    {
                        var plcs = GetPlcSoftwareList();
                        plc = plcs.First();
                        if (plcs.Count > 1)
                        {
                            warnings.Add($"项目中存在 {plcs.Count} 个 PLC，未指定 plcName，默认在第一个 PLC \"{plc.Name}\" 上创建骨架。如需指定其他 PLC，请传入 plcName 参数。");
                        }
                    }
                    var created = new List<object>();
                    var errors = new List<string>();

                    // 选择模板
                    var blocks = string.IsNullOrEmpty(customBlocksJson)
                        ? GetDefaultSkeleton(template ?? "standard")
                        : ParseCustomBlocks(customBlocksJson);

                    foreach (var b in blocks)
                    {
                        try
                        {
                            var lang = b.Type == "DB"
                                ? ProgrammingLanguage.DB
                                : BlockXmlBuilder.ParseProgrammingLanguage(b.Language ?? "LAD");
                            var xml = BlockXmlBuilder.GenerateBlockXml(b.Type, b.Name, b.Number, lang, null, b.InterfaceXml, null);
                            ImportXmlTemp(plc, xml);
                            var cacheKey = LadCacheKey(plc.Name, b.Name);
                            lock (_staticCacheLock)
                            {
                                _ladFlgNetCache.Remove(cacheKey);
                                _ladVarCache.Remove(cacheKey);
                                _ladTitleCache.Remove(cacheKey);
                                if (!string.IsNullOrEmpty(b.InterfaceXml))
                                {
                                    var parsedVars = ParseInterfaceSectionsXml(b.InterfaceXml);
                                    if (parsedVars.Count > 0) _ladVarCache[cacheKey] = parsedVars;
                                }
                            }
                            created.Add(new { name = b.Name, type = b.Type, number = b.Number, language = b.Language });
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{b.Name}: {ex.Message}");
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = errors.Count == 0,
                        plcName = plc.Name,
                        createdCount = created.Count,
                        errorCount = errors.Count,
                        created,
                        errors,
                        warnings,
                        message = errors.Count == 0
                            ? $"已创建 {created.Count} 个块，请用 add_lad_network 填充逻辑"
                            : $"成功 {created.Count} 个，失败 {errors.Count} 个"
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>获取默认骨架模板</summary>
        private List<SkeletonBlock> GetDefaultSkeleton(string template)
        {
            var blocks = new List<SkeletonBlock>();

            if (template.Equals("standard", StringComparison.OrdinalIgnoreCase) ||
                template.Equals("标准", StringComparison.OrdinalIgnoreCase))
            {
                // ── DB 分组（全局变量通信）──
                blocks.Add(new SkeletonBlock
                {
                    Type = "DB", Name = "输入组", Number = 210, Language = "DB",
                    InterfaceXml = "<Section Name=\"Static\"><Member Name=\"I_远程就地\" Datatype=\"Bool\"/><Member Name=\"I_启动\" Datatype=\"Bool\"/><Member Name=\"I_停止\" Datatype=\"Bool\"/><Member Name=\"I_急停\" Datatype=\"Bool\"/></Section>"
                });
                blocks.Add(new SkeletonBlock
                {
                    Type = "DB", Name = "输出组", Number = 200, Language = "DB",
                    InterfaceXml = "<Section Name=\"Static\"><Member Name=\"Q_电机运行\" Datatype=\"Bool\"/><Member Name=\"Q_报警灯\" Datatype=\"Bool\"/><Member Name=\"Q_故障复位\" Datatype=\"Bool\"/></Section>"
                });
                blocks.Add(new SkeletonBlock
                {
                    Type = "DB", Name = "参数设置", Number = 16, Language = "DB",
                    InterfaceXml = "<Section Name=\"Static\"><Member Name=\"延时设定\" Datatype=\"Time\"/><Member Name=\"阈值\" Datatype=\"Real\"/></Section>"
                });
                blocks.Add(new SkeletonBlock
                {
                    Type = "DB", Name = "边沿检测", Number = 1, Language = "DB",
                    InterfaceXml = "<Section Name=\"Static\"><Member Name=\"I_启动\" Datatype=\"Bool\"/><Member Name=\"I_停止\" Datatype=\"Bool\"/></Section>"
                });
                blocks.Add(new SkeletonBlock
                {
                    Type = "DB", Name = "p状态", Number = 2, Language = "DB",
                    InterfaceXml = "<Section Name=\"Static\"><Member Name=\"p\" Datatype=\"Array[0..99] of Bool\"/></Section>"
                });

                // ── FC 功能层 ──
                blocks.Add(new SkeletonBlock { Type = "FC", Name = "输入输出转换", Number = 4, Language = "LAD", InterfaceXml = null });
                blocks.Add(new SkeletonBlock { Type = "FC", Name = "报警程序", Number = 1, Language = "LAD", InterfaceXml = null });
                blocks.Add(new SkeletonBlock { Type = "FC", Name = "上升下降沿处理", Number = 2, Language = "LAD", InterfaceXml = null });
                blocks.Add(new SkeletonBlock { Type = "FC", Name = "手动程序", Number = 3, Language = "LAD", InterfaceXml = null });
                blocks.Add(new SkeletonBlock { Type = "FC", Name = "自动程序", Number = 5, Language = "LAD", InterfaceXml = null });

                // ── OB1 主程序 ──
                blocks.Add(new SkeletonBlock { Type = "OB", Name = "Main", Number = 1, Language = "LAD", InterfaceXml = null });
            }
            else if (template.Equals("minimal", StringComparison.OrdinalIgnoreCase) ||
                     template.Equals("最小", StringComparison.OrdinalIgnoreCase))
            {
                // 最小骨架：1 OB + 1 FC + 1 DB
                blocks.Add(new SkeletonBlock
                {
                    Type = "DB", Name = "数据块", Number = 1, Language = "DB",
                    InterfaceXml = "<Section Name=\"Static\"><Member Name=\"启动\" Datatype=\"Bool\"/><Member Name=\"运行\" Datatype=\"Bool\"/></Section>"
                });
                blocks.Add(new SkeletonBlock { Type = "FC", Name = "逻辑程序", Number = 1, Language = "LAD", InterfaceXml = null });
                blocks.Add(new SkeletonBlock { Type = "OB", Name = "Main", Number = 1, Language = "LAD", InterfaceXml = null });
            }
            else if (template.Equals("fb", StringComparison.OrdinalIgnoreCase))
            {
                // FB 骨架：1 OB + 1 FB + 背景 DB
                blocks.Add(new SkeletonBlock
                {
                    Type = "FB", Name = "FB_Main", Number = 1, Language = "LAD",
                    InterfaceXml = "<Section Name=\"Input\"><Member Name=\"启动\" Datatype=\"Bool\"/><Member Name=\"停止\" Datatype=\"Bool\"/></Section><Section Name=\"Output\"><Member Name=\"运行\" Datatype=\"Bool\"/></Section><Section Name=\"Static\"><Member Name=\"TON_延时\" Datatype=\"TON_TIME\"/></Section>"
                });
                blocks.Add(new SkeletonBlock { Type = "OB", Name = "Main", Number = 1, Language = "LAD", InterfaceXml = null });
            }
            else
            {
                throw new Exception($"未知模板: {template}。可选: standard / minimal / fb");
            }

            return blocks;
        }

        /// <summary>解析自定义块定义 JSON</summary>
        private List<SkeletonBlock> ParseCustomBlocks(string json)
        {
            var arr = JArray.Parse(json);
            var blocks = new List<SkeletonBlock>();
            foreach (var item in arr)
            {
                blocks.Add(new SkeletonBlock
                {
                    Type = item["type"]?.ToString() ?? "FC",
                    Name = item["name"]?.ToString() ?? "",
                    Number = (int)(item["number"] ?? 0),
                    Language = item["language"]?.ToString() ?? "LAD",
                    InterfaceXml = item["interfaceXml"]?.ToString()
                });
            }
            return blocks;
        }

        // ────────────────────────────────────────────────
        // SICAR 标准骨架创建（汽车行业博途项目事实标准）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 创建 SICAR 标准项目骨架（汽车行业博途项目事实标准）。
        /// 包含: OB1/OB100 + FC100/200/300/400/500/600/700 + FB100/300 + DB100/300 + 标准UDT + 标准变量表。
        /// OB1 中自动建立调用关系: FC100→FC200→FC300→FC400→FC500→FC600→FC700。
        /// 创建空块（有接口但无逻辑），AI 后续用 add_lad_network 填充逻辑。
        /// </summary>
        public string CreateSicarSkeleton(string plcName, string stationName = "Station1")
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(plcName))
                        return Err("plcName 不能为空");
                    var plc = FindPlcByName(plcName)
                        ?? throw new InvalidOperationException($"未找到 PLC: {plcName}");

                    var created = new List<object>();
                    var errors = new List<string>();
                    var warnings = new List<string>();

                    // 1. 创建标准 UDT（先创建，后续 FB/DB 可引用）
                    try
                    {
                        CreateSicarUdts(plcName);
                        created.Add(new { category = "UDT", names = new[] { "stMaterial", "stAxis", "stAlarm", "stStation" } });
                    }
                    catch (Exception ex) { warnings.Add($"UDT 创建警告: {ex.Message}"); }

                    // 2. 创建 FC 功能层
                    // SICAR 块编号: FC100=Init, FC200=Auto, FC300=Station, FC400=Safety, FC500=Manual, FC600=Alarm, FC700=Diagnostics
                    foreach (var fc in GetSicarFcDefs())
                    {
                        var (ok, msg) = TryCreateSicarBlock("FC", fc.Name, "LAD", fc.Number, fc.Interface, plcName);
                        if (ok) created.Add(new { category = "FC", name = fc.Name, number = fc.Number });
                        else if (msg.Contains("已存在")) warnings.Add($"FC {fc.Name} 已存在，跳过");
                        else errors.Add($"FC {fc.Name}: {msg}");
                    }

                    // 3. 创建 FB 功能块（FB300 用 StationFB 避免与 FC300 Station 同名冲突）
                    foreach (var fb in GetSicarFbDefs())
                    {
                        var (ok, msg) = TryCreateSicarBlock("FB", fb.Name, "LAD", fb.Number, fb.Interface, plcName);
                        if (ok) created.Add(new { category = "FB", name = fb.Name, number = fb.Number });
                        else if (msg.Contains("已存在")) warnings.Add($"FB {fb.Name} 已存在，跳过");
                        else errors.Add($"FB {fb.Name}: {msg}");
                    }

                    // 4. 创建 DB 数据块（全局 DB，存储设备/工站数据）
                    foreach (var db in GetSicarDbDefs())
                    {
                        var (ok, msg) = TryCreateSicarBlock("DB", db.Name, "DB", db.Number, db.Interface, plcName);
                        if (ok) created.Add(new { category = "DB", name = db.Name, number = db.Number });
                        else if (msg.Contains("已存在")) warnings.Add($"DB {db.Name} 已存在，跳过");
                        else errors.Add($"DB {db.Name}: {msg}");
                    }

                    // 5. 创建 OB（主循环 OB1 + 启动 OB100）
                    var (okMain, msgMain) = TryCreateSicarBlock("OB", "Main", "LAD", 1, null, plcName);
                    if (okMain) created.Add(new { category = "OB", name = "Main", number = 1 });
                    else if (!msgMain.Contains("已存在")) errors.Add($"OB Main: {msgMain}");

                    var (okStartup, msgStartup) = TryCreateSicarBlock("OB", "Startup", "LAD", 100, null, plcName, "Startup");
                    if (okStartup) created.Add(new { category = "OB", name = "Startup", number = 100 });
                    else if (!msgStartup.Contains("已存在")) errors.Add($"OB Startup: {msgStartup}");

                    // 6. OB1 中添加 FC 调用网络（SICAR 调用链: FC100→FC200→FC300→FC400→FC500→FC600→FC700）
                    // 无条件调用（Powerrail → call.EN），符合 OB1 顺序执行语义
                    foreach (var fcCall in GetSicarCallDefs())
                    {
                        try
                        {
                            var netJson = JsonConvert.SerializeObject(new
                            {
                                title = fcCall.Title,
                                rung = new[]
                                {
                                    new
                                    {
                                        call = new
                                        {
                                            blockName = fcCall.BlockName,
                                            blockType = "FC",
                                            pins = new object[0]
                                        }
                                    }
                                }
                            });
                            AddLadNetwork("Main", netJson, plcName);
                            created.Add(new { category = "OB1网络", title = fcCall.Title });
                        }
                        catch (Exception ex) { errors.Add($"OB1 调用 {fcCall.BlockName}: {ex.Message}"); }
                    }

                    // 7. 创建标准变量表
                    try
                    {
                        CreateSicarTagTables(plcName);
                        created.Add(new { category = "变量表", names = new[] { "IO变量表", "工艺变量表", "报警变量表", "系统变量表" } });
                    }
                    catch (Exception ex) { warnings.Add($"变量表创建警告: {ex.Message}"); }

                    return JsonConvert.SerializeObject(new
                    {
                        success = errors.Count == 0,
                        plcName = plc.Name,
                        stationName,
                        createdCount = created.Count,
                        errorCount = errors.Count,
                        created,
                        errors,
                        warnings,
                        sicarStructure = new
                        {
                            OB = new[] { "Main(OB1)", "Startup(OB100)" },
                            FC = new[] { "Init(FC100)", "Auto(FC200)", "Station(FC300)", "Safety(FC400)", "Manual(FC500)", "Alarm(FC600)", "Diagnostics(FC700)" },
                            FB = new[] { "Device(FB100)", "StationFB(FB300)" },
                            DB = new[] { "DeviceDB(DB100)", "StationDB(DB300)" },
                            UDT = new[] { "stMaterial", "stAxis", "stAlarm", "stStation" },
                            TagTables = new[] { "IO变量表", "工艺变量表", "报警变量表", "系统变量表" },
                            OB1CallChain = "FC100→FC200→FC300→FC400→FC500→FC600→FC700"
                        },
                        message = errors.Count == 0
                            ? $"SICAR 骨架创建成功，共 {created.Count} 项。OB1 已建立 FC100→...→FC700 调用链。"
                            : $"SICAR 骨架创建部分成功: {created.Count} 项成功，{errors.Count} 项失败"
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 创建 SICAR 标准用户数据类型（UDT）。
        /// - stMaterial: 物料信息（ID, Name, Position, Status）
        /// - stAxis: 轴信息（Position, Speed, Status, Homed, Enabled）
        /// - stAlarm: 报警信息（Code, Text, Active, Acknowledged）
        /// - stStation: 工站信息（State, Mode, StationNo, FaultCount）
        /// </summary>
        public string CreateSicarUdts(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = FindPlcByName(plcName)
                        ?? throw new InvalidOperationException($"未找到 PLC: {plcName}");

                    var created = new List<string>();
                    var errors = new List<string>();

                    // stMaterial: 物料信息
                    var stMaterialMembers = "<Member Name=\"MaterialID\" Datatype=\"Int\"/>" +
                                            "<Member Name=\"MaterialName\" Datatype=\"String[20]\"/>" +
                                            "<Member Name=\"Position\" Datatype=\"Real\"/>" +
                                            "<Member Name=\"Status\" Datatype=\"Int\"/>";
                    TryCreateUdt(plc, "stMaterial", stMaterialMembers, created, errors);

                    // stAxis: 轴信息
                    var stAxisMembers = "<Member Name=\"Position\" Datatype=\"Real\"/>" +
                                        "<Member Name=\"Speed\" Datatype=\"Real\"/>" +
                                        "<Member Name=\"Status\" Datatype=\"Int\"/>" +
                                        "<Member Name=\"Homed\" Datatype=\"Bool\"/>" +
                                        "<Member Name=\"Enabled\" Datatype=\"Bool\"/>";
                    TryCreateUdt(plc, "stAxis", stAxisMembers, created, errors);

                    // stAlarm: 报警信息
                    var stAlarmMembers = "<Member Name=\"Code\" Datatype=\"Int\"/>" +
                                         "<Member Name=\"Text\" Datatype=\"String[50]\"/>" +
                                         "<Member Name=\"Active\" Datatype=\"Bool\"/>" +
                                         "<Member Name=\"Acknowledged\" Datatype=\"Bool\"/>";
                    TryCreateUdt(plc, "stAlarm", stAlarmMembers, created, errors);

                    // stStation: 工站信息
                    var stStationMembers = "<Member Name=\"State\" Datatype=\"Int\"/>" +
                                           "<Member Name=\"Mode\" Datatype=\"Int\"/>" +
                                           "<Member Name=\"StationNo\" Datatype=\"Int\"/>" +
                                           "<Member Name=\"FaultCount\" Datatype=\"Int\"/>";
                    TryCreateUdt(plc, "stStation", stStationMembers, created, errors);

                    return JsonConvert.SerializeObject(new
                    {
                        success = errors.Count == 0,
                        plcName = plc.Name,
                        createdCount = created.Count,
                        errorCount = errors.Count,
                        created,
                        errors,
                        message = errors.Count == 0
                            ? $"已创建 {created.Count} 个 SICAR 标准 UDT"
                            : $"成功 {created.Count} 个，失败 {errors.Count} 个"
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 创建 SICAR 标准变量表。
        /// - IO变量表: 物理 IO 映射
        /// - 工艺变量表: 工艺参数
        /// - 报警变量表: 报警位
        /// - 系统变量表: 系统状态
        /// </summary>
        public string CreateSicarTagTables(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = FindPlcByName(plcName)
                        ?? throw new InvalidOperationException($"未找到 PLC: {plcName}");

                    var created = new List<string>();
                    var errors = new List<string>();

                    // 1. IO变量表（物理 IO 映射）
                    var ioTags = new[]
                    {
                        ("I_启动", "Bool", "%I0.0", "启动按钮"),
                        ("I_停止", "Bool", "%I0.1", "停止按钮"),
                        ("I_急停", "Bool", "%I0.2", "急停按钮"),
                        ("I_自动模式", "Bool", "%I0.3", "自动模式选择"),
                        ("I_手动模式", "Bool", "%I0.4", "手动模式选择"),
                        ("Q_电机运行", "Bool", "%Q0.0", "电机运行输出"),
                        ("Q_报警灯", "Bool", "%Q0.1", "报警指示灯"),
                        ("Q_就绪灯", "Bool", "%Q0.2", "就绪指示灯"),
                    };
                    CreateSicarTagTable(plcName, "IO变量表", ioTags, created, errors);

                    // 2. 工艺变量表（工艺参数，M 区）
                    var processTags = new[]
                    {
                        ("M_自动模式", "Bool", "%M0.0", "自动模式标志"),
                        ("M_手动模式", "Bool", "%M0.1", "手动模式标志"),
                        ("M_运行状态", "Bool", "%M0.2", "系统运行状态"),
                        ("M_初始化完成", "Bool", "%M0.3", "初始化完成标志"),
                        ("M_当前工站号", "Int", "%MW2", "当前工站编号"),
                        ("M_设备速度", "Real", "%MD4", "设备速度设定"),
                    };
                    CreateSicarTagTable(plcName, "工艺变量表", processTags, created, errors);

                    // 3. 报警变量表（报警位，M 区）
                    var alarmTags = new[]
                    {
                        ("M_故障1", "Bool", "%M1.0", "故障1"),
                        ("M_故障2", "Bool", "%M1.1", "故障2"),
                        ("M_故障3", "Bool", "%M1.2", "故障3"),
                        ("M_故障复位", "Bool", "%M1.3", "故障复位"),
                        ("M_报警激活", "Bool", "%M1.4", "报警激活总标志"),
                        ("M_报警代码", "Int", "%MW10", "报警代码"),
                    };
                    CreateSicarTagTable(plcName, "报警变量表", alarmTags, created, errors);

                    // 4. 系统变量表（系统状态，M 区）
                    var systemTags = new[]
                    {
                        ("M_系统就绪", "Bool", "%M2.0", "系统就绪"),
                        ("M_系统运行", "Bool", "%M2.1", "系统运行中"),
                        ("M_系统故障", "Bool", "%M2.2", "系统故障"),
                        ("M_系统模式", "Int", "%MW12", "系统模式代码"),
                        ("M_诊断代码", "Int", "%MW14", "诊断代码"),
                    };
                    CreateSicarTagTable(plcName, "系统变量表", systemTags, created, errors);

                    return JsonConvert.SerializeObject(new
                    {
                        success = errors.Count == 0,
                        plcName = plc.Name,
                        createdCount = created.Count,
                        errorCount = errors.Count,
                        created,
                        errors,
                        message = errors.Count == 0
                            ? $"已创建 {created.Count} 个 SICAR 标准变量表"
                            : $"成功 {created.Count} 个，失败 {errors.Count} 个"
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ─── SICAR 私有辅助方法 ───

        /// <summary>调用 CreateBlock 并解析 JSON 结果，返回 (成功, 消息)。</summary>
        private (bool success, string message) TryCreateSicarBlock(string blockType, string name, string lang,
            int number, string? interfaceXml, string plcName, string? secondaryType = null)
        {
            var result = CreateBlock(blockType, name, lang, number, interfaceJson: interfaceXml,
                forceOverwrite: false, plcName: plcName, secondaryType: secondaryType);
            try
            {
                var obj = JObject.Parse(result);
                bool success = obj["success"]?.Value<bool>() ?? false;
                string msg = obj["message"]?.Value<string>() ?? obj["error"]?.Value<string>() ?? "";
                return (success, msg);
            }
            catch { return (false, result); }
        }

        /// <summary>生成 UDT (PlcType) XML 并导入到指定 PLC。UDT 创建失败时抛异常。</summary>
        private void TryCreateUdt(PlcSoftware plc, string udtName, string membersXml,
            List<string> created, List<string> errors)
        {
            try
            {
                var xml = GenerateUdtXml(udtName, membersXml);
                var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_Udt_{Guid.NewGuid():N}.xml");
                File.WriteAllText(tempPath, xml, Encoding.UTF8);
                try
                {
                    plc.TypeGroup.Types.Import(new FileInfo(tempPath), ImportOptions.Override);
                    created.Add(udtName);
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{udtName}: {ex.Message}");
            }
        }

        /// <summary>生成 TIA Portal PlcType (UDT) 导入 XML。</summary>
        private static string GenerateUdtXml(string udtName, string membersXml)
        {
            const string ifaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
            var createdTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var sb = new StringBuilder();
            sb.Append(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
            sb.Append("<Document>");
            sb.Append("<Engineering version=\"" + EnvironmentDiscoveryService.CurrentEngineeringVersion() + "\"/>");
            sb.Append("<DocumentInfo>");
            sb.Append($"<Created>{createdTime}</Created>");
            sb.Append("<ExportSetting>WithDefaults</ExportSetting>");
            sb.Append("<InstalledProducts><Product><DisplayName>Totally Integrated Automation Portal</DisplayName><DisplayVersion>" + EnvironmentDiscoveryService.CurrentEngineeringVersion() + "</DisplayVersion></Product></InstalledProducts>");
            sb.Append("</DocumentInfo>");
            sb.Append("<SW.Types.PlcType ID=\"0\">");
            sb.Append("<AttributeList>");
            sb.Append($"<Name>{SecurityElement.Escape(udtName)}</Name>");
            // UDT 使用 Section Name="None"（与 DB 的 Static 不同），成员格式与 DB 接口一致
            sb.Append($"<Interface><Sections xmlns=\"{ifaceNs}\"><Section Name=\"None\">{membersXml}</Section></Sections></Interface>");
            sb.Append("</AttributeList>");
            sb.Append("<ObjectList>");
            sb.Append("<MultilingualText ID=\"1\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"2\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>");
            sb.Append("<MultilingualText ID=\"3\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"4\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>");
            sb.Append("</ObjectList>");
            sb.Append("</SW.Types.PlcType>");
            sb.Append("</Document>");
            return sb.ToString();
        }

        /// <summary>创建变量表并添加变量，错误收集到 errors 列表。</summary>
        private void CreateSicarTagTable(string plcName, string tableName,
            (string name, string dataType, string address, string comment)[] tags,
            List<string> created, List<string> errors)
        {
            try
            {
                // 创建变量表（已存在则跳过创建，继续添加变量）
                var createResult = CreateTagTable(tableName, plcName);
                try
                {
                    var obj = JObject.Parse(createResult);
                    if (obj["success"]?.Value<bool>() == true)
                        created.Add(tableName);
                    else
                    {
                        // 表可能已存在，尝试继续添加变量
                        var msg = obj["error"]?.Value<string>() ?? "";
                        if (!msg.Contains("已存在") && !msg.Contains("exist"))
                            errors.Add($"创建变量表 {tableName}: {msg}");
                    }
                }
                catch { }

                // 添加变量到表
                foreach (var tag in tags)
                {
                    AddTagToTable(tableName, tag.name, tag.dataType, tag.address, tag.comment, plcName);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"变量表 {tableName}: {ex.Message}");
            }
        }

        /// <summary>SICAR 标准 FC 定义列表</summary>
        private static List<SicarBlockDef> GetSicarFcDefs()
        {
            return new List<SicarBlockDef>
            {
                new SicarBlockDef
                {
                    Name = "Init", Number = 100,
                    Interface = "<Section Name=\"Input\"><Member Name=\"Enable\" Datatype=\"Bool\"/></Section><Section Name=\"Output\"><Member Name=\"Done\" Datatype=\"Bool\"/></Section>"
                },
                new SicarBlockDef
                {
                    Name = "Auto", Number = 200,
                    Interface = "<Section Name=\"Input\"><Member Name=\"Start\" Datatype=\"Bool\"/><Member Name=\"Stop\" Datatype=\"Bool\"/></Section><Section Name=\"Output\"><Member Name=\"Running\" Datatype=\"Bool\"/></Section>"
                },
                new SicarBlockDef
                {
                    Name = "Station", Number = 300,
                    Interface = "<Section Name=\"Input\"><Member Name=\"StationNo\" Datatype=\"Int\"/></Section><Section Name=\"Output\"><Member Name=\"Status\" Datatype=\"Int\"/></Section>"
                },
                new SicarBlockDef
                {
                    Name = "Safety", Number = 400,
                    Interface = "<Section Name=\"Input\"><Member Name=\"EStop\" Datatype=\"Bool\"/></Section><Section Name=\"Output\"><Member Name=\"Safe\" Datatype=\"Bool\"/></Section>"
                },
                new SicarBlockDef
                {
                    Name = "Manual", Number = 500,
                    Interface = "<Section Name=\"Input\"><Member Name=\"Enable\" Datatype=\"Bool\"/></Section>"
                },
                new SicarBlockDef
                {
                    Name = "Alarm", Number = 600,
                    Interface = "<Section Name=\"Output\"><Member Name=\"AlarmActive\" Datatype=\"Bool\"/></Section>"
                },
                new SicarBlockDef
                {
                    Name = "Diagnostics", Number = 700,
                    Interface = "<Section Name=\"Output\"><Member Name=\"DiagCode\" Datatype=\"Int\"/></Section>"
                },
            };
        }

        /// <summary>SICAR 标准 FB 定义列表</summary>
        private static List<SicarBlockDef> GetSicarFbDefs()
        {
            return new List<SicarBlockDef>
            {
                new SicarBlockDef
                {
                    Name = "Device", Number = 100,
                    Interface = "<Section Name=\"Input\"><Member Name=\"Enable\" Datatype=\"Bool\"/></Section><Section Name=\"Output\"><Member Name=\"Running\" Datatype=\"Bool\"/></Section><Section Name=\"Static\"><Member Name=\"Ton_Delay\" Datatype=\"TON_TIME\"/></Section>"
                },
                // FB300 用 StationFB 避免与 FC300 Station 同名（FindBlock 无法区分 FC/FB）
                new SicarBlockDef
                {
                    Name = "StationFB", Number = 300,
                    Interface = "<Section Name=\"Input\"><Member Name=\"StationNo\" Datatype=\"Int\"/></Section><Section Name=\"Output\"><Member Name=\"Status\" Datatype=\"Int\"/></Section><Section Name=\"Static\"><Member Name=\"State\" Datatype=\"Int\"/></Section>"
                },
            };
        }

        /// <summary>SICAR 标准 DB 定义列表（全局 DB）</summary>
        private static List<SicarBlockDef> GetSicarDbDefs()
        {
            return new List<SicarBlockDef>
            {
                new SicarBlockDef
                {
                    Name = "DeviceDB", Number = 100,
                    Interface = "<Section Name=\"Static\"><Member Name=\"DeviceEnable\" Datatype=\"Bool\"/><Member Name=\"DeviceRunning\" Datatype=\"Bool\"/><Member Name=\"DeviceFault\" Datatype=\"Bool\"/></Section>"
                },
                new SicarBlockDef
                {
                    Name = "StationDB", Number = 300,
                    Interface = "<Section Name=\"Static\"><Member Name=\"StationNo\" Datatype=\"Int\"/><Member Name=\"StationState\" Datatype=\"Int\"/><Member Name=\"StationMode\" Datatype=\"Int\"/></Section>"
                },
            };
        }

        /// <summary>SICAR OB1 调用链定义（FC100→FC200→FC300→FC400→FC500→FC600→FC700）</summary>
        private static List<(string Title, string BlockName)> GetSicarCallDefs()
        {
            return new List<(string, string)>
            {
                ("调用 FC100 初始化", "Init"),
                ("调用 FC200 自动模式", "Auto"),
                ("调用 FC300 工站控制", "Station"),
                ("调用 FC400 安全监控", "Safety"),
                ("调用 FC500 手动模式", "Manual"),
                ("调用 FC600 报警处理", "Alarm"),
                ("调用 FC700 诊断", "Diagnostics"),
            };
        }

        private class SicarBlockDef
        {
            public string Name { get; set; } = "";
            public int Number { get; set; }
            public string? Interface { get; set; }
        }

        private class SkeletonBlock
        {
            public string Type { get; set; } = "";
            public string Name { get; set; } = "";
            public int Number { get; set; }
            public string? Language { get; set; }
            public string? InterfaceXml { get; set; }
        }
    }
}
