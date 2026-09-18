using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    public partial class McpServer
    {
        internal static void RegisterPlcTools(McpServer server, Lazy<PortalService> tia)
        {
            // Block CRUD
            server.RegisterTool("list_blocks", "列出 PLC 中所有程序块。", EmptySchema(), _ => tia.Value.ListBlocks());

            server.RegisterTool("create_block",
                "创建程序块（OB/FC/FB/DB），支持 LAD/SCL 语言。\n" +
                "★重要★ 参数名是 name 不是 blockName！\n" +
                "★DB变量查询★ 创建DB后用 read_data_block_structure 查询变量（不要用 get_block_interface，它对DB返回空）。\n" +
                "interfaceJson 可直接传入接口 Sections XML 用于 DB 批量变量，格式如：\n" +
                "<Section Name=\"Static\"><Member Name=\"var1\" Datatype=\"Int\"/></Section>\n" +
                "★OB 子类型★ 仅对 OB 生效。默认 ProgramCycle (OB1)。创建 OB30 循环中断（PID 必需）传 blockNumber=30 + secondaryType=\"CyclicInterrupt\"。\n" +
                "  OB 类型对照: OB1=ProgramCycle / OB30=CyclicInterrupt / OB100=Startup / OB200=HardwareInterrupt / OB121=SyncCycleFault / OB122=IAccessFault",
                Props(new Dictionary<string, object> {
                    ["blockType"] = StrProp("OB / FC / FB / DB"),
                    ["name"] = StrProp("块名（注意参数名是 name）"),
                    ["programmingLanguage"] = StrProp("LAD / FBD / SCL（默认 LAD；DB 自动用 DB）"),
                    ["blockNumber"] = IntProp("块号（可选；0 或省略表示自动编号，仅正数为显式编号；OB30 传 30）"),
                    ["interfaceJson"] = StrProp("接口 Sections XML（可选，用于创建含变量的 DB）"),
                    ["forceOverwrite"] = BoolProp("强制覆盖同名块（默认 false，同名块已存在时拒绝创建以防覆盖逻辑）"),
                    ["secondaryType"] = StrProp("OB 子类型（可选，仅 OB 生效，默认 ProgramCycle）。OB30 循环中断用 CyclicInterrupt，OB100 启动用 Startup，OB200 硬件中断用 HardwareInterrupt"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "blockType", "name" }),
                args => tia.Value.CreateBlock(GetStringArg(args, "blockType") ?? "", GetStringArg(args, "name") ?? "", GetStringArg(args, "programmingLanguage"), GetIntArg(args, "blockNumber"), GetStringArg(args, "interfaceJson"), GetBoolArg(args, "forceOverwrite"), GetStringArg(args, "plcName"), GetStringArg(args, "secondaryType")));

            server.RegisterTool("delete_block", "删除程序块。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.DeleteBlock(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("get_block_details", "获取块的详细信息。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.GetBlockDetails(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("get_block_interface", "获取块的接口定义（Input/Output/InOut/Static）。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.GetBlockInterface(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("show_block_in_editor", "在 TIA Portal 编辑器中打开块。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.ShowBlockInEditor(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            // LAD - one network at a time
            server.RegisterTool("add_lad_network",
                "【推荐！安全可靠】向已有块加1个LAD网络。分多次调用构建完整逻辑，不会卡死博途。\n" +
                "★重要★ networkJson 是单个网络对象，不是数组！\n" +
                "★中文支持★ 变量名、网络标题、块名均可用中文！\n" +
                "\n" +
                "【格式1：触点线圈（简单逻辑）】\n" +
                "{\"variables\":[{\"name\":\"启动按钮\",\"datatype\":\"Bool\",\"section\":\"Input\"},{\"name\":\"电机\",\"datatype\":\"Bool\",\"section\":\"Output\"}],\"title\":\"电机控制\",\"rung\":[{\"contact\":{\"contact\":\"启动按钮\"}},{\"coil\":{\"coil\":\"电机\"}}]}\n" +
                "触点name: Contact/NegatedContact；线圈name: Coil/SetCoil/ResetCoil\n" +
                "\n" +
                "【格式2：IEC指令（TON/CTU等）】\n" +
                "{\"variables\":[{\"name\":\"TimerInst\",\"datatype\":\"TON_TIME\",\"section\":\"Static\"},{\"name\":\"In1\",\"datatype\":\"Bool\",\"section\":\"Input\"},{\"name\":\"Out1\",\"datatype\":\"Bool\",\"section\":\"Output\"}],\"sysCall\":{\"inst\":\"TON\",\"instance\":\"TimerInst\",\"pins\":{\"IN\":\"In1\",\"PT\":\"T#5S\",\"Q\":\"Out1\"}}}\n" +
                "★TON/TOF/TP的time_type自动设为Time，T#常量自动识别★\n" +
                "背景DB型指令应使用独立实例；程序会自动分配实例名并执行结构校验。FB 中可用 Static，多种块类型仍必须以实际编译结果为准\n" +
                "\n" +
                "【格式3：rung高级格式（多盒子/分支）】\n" +
                "{\"variables\":[...],\"title\":\"网络标题\",\"rung\":[{\"contact\":{\"contact\":\"Start\"}},{\"box\":{\"box\":\"GT\",\"pins\":{\"in1\":\"Level\",\"in2\":\"80.0\"},\"datatype\":\"Real\"}},{\"coil\":{\"coil\":\"Alarm\"}}]}\n" +
                "box指令: ADD/SUB/MUL/DIV/MOD/EQ/NE/GT/LT/GE/LE/MOVE/CONVERT/AND/OR/XOR/SHL/SHR等\n" +
                "比较指令datatype必须与变量类型匹配（如Real）\n" +
                "\n" +
                "【工作流建议】每次添加网络后设置 compileAfter=true 自动编译验证，符合\"每网络编译一次\"的工业流程",
                Props(new Dictionary<string, object> {
                    ["blockName"] = StrProp("目标块名称（块必须已存在）"),
                    ["networkJson"] = StrProp("【必须】单个网络的JSON对象。每次只传1个对象不要传数组！"),
                    ["compileAfter"] = BoolProp("【可选，默认true】添加网络后自动编译；失败时恢复原块。仅在明确调试 XML 时才设 false"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "blockName", "networkJson" }),
                args => tia.Value.AddLadNetwork(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "networkJson") ?? "{}",
                    GetStringArg(args, "plcName"),
                    GetBoolArg(args, "compileAfter") ?? true));

            server.RegisterTool("validate_lad_network",
                "只校验一个 LAD networkJson，不写入项目。检查元素互斥、必需引脚、重复输出、线圈位置、分支结构和未声明变量。",
                Props(new Dictionary<string, object> {
                    ["networkJson"] = StrProp("单个 LAD 网络 JSON 对象")
                }, new[] { "networkJson" }),
                args => tia.Value.ValidateLadNetworkJson(GetStringArg(args, "networkJson") ?? "{}"));

            server.RegisterTool("validate_lad_program",
                "校验完整 LAD 网络数组，不写入项目。除单网络结构检查外，还检查跨网络重复写输出和定时器/计数器实例复用，并生成基础PLCSIM测试计划。",
                Props(new Dictionary<string, object> {
                    ["networksJson"] = StrProp("LAD 网络 JSON 数组")
                }, new[] { "networksJson" }),
                args => tia.Value.ValidateLadProgramJson(GetStringArg(args, "networksJson") ?? "[]"));

            server.RegisterTool("add_lad_logic",
                "【批量写入，可能卡死！】一次性写入多个LAD网络（≤3个时可用）。超过3个请改用add_lad_network逐一添加！",
                Props(new Dictionary<string, object> {
                    ["blockName"] = StrProp("目标块名称"),
                    ["programmingLanguage"] = StrProp("LAD/FBD（可选）"),
                    ["networksJson"] = StrProp("网络数组JSON。仅≤3个网络时使用。"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "blockName", "networksJson" }),
                args => tia.Value.AddLadLogic(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "programmingLanguage"), GetStringArg(args, "networksJson") ?? "[]", GetStringArg(args, "plcName")));

            server.RegisterTool("add_lad_networks_batch",
                "【推荐】批量追加多个 LAD 网络到已有块（不覆盖已有网络）。每次最多 20 个网络。\n" +
                "★重要★ networksJson 是网络数组，格式如：[{\"title\":\"网络1\",\"rung\":[...]},{\"title\":\"网络2\",\"rung\":[...]}]\n" +
                "★工作流★ AI 先规划好所有网络，再一次性批量导入，比逐个 add_lad_network 快 5 倍。\n" +
                "★编译★ 默认 compileAfter=true，导入后自动编译验证。",
                Props(new Dictionary<string, object> {
                    ["blockName"] = StrProp("目标块名称（块必须已存在）"),
                    ["networksJson"] = StrProp("网络数组JSON（每个网络格式同 add_lad_network 的 networkJson）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["compileAfter"] = BoolProp("【可选，默认true】导入后是否自动编译验证"),
                }, new[] { "blockName", "networksJson" }),
                args => tia.Value.AddLadNetworksBatch(
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "networksJson") ?? "[]",
                    GetStringArg(args, "plcName"),
                    GetBoolArg(args, "compileAfter") ?? true));

            server.RegisterTool("generate_lad_block", "生成LAD块XML到文件，不导入博途。",
                Props(new Dictionary<string, object> {
                    ["blockType"] = StrProp("FB/FC/OB"), ["name"] = StrProp("块名"),
                    ["programmingLanguage"] = StrProp("LAD/FBD"), ["ladNetworkJson"] = StrProp("LAD网络JSON"),
                    ["filePath"] = StrProp("输出路径（可选）"),
                }, new[] { "blockType", "name", "ladNetworkJson" }),
                args => tia.Value.GenerateLadBlock(GetStringArg(args, "blockType") ?? "FB", GetStringArg(args, "name") ?? "", GetStringArg(args, "programmingLanguage"), GetStringArg(args, "ladNetworkJson") ?? "{}", GetStringArg(args, "filePath")));

            // SCL
            server.RegisterTool("add_scl_code", "向已有SCL块写入代码。推荐用LAD替代。",
                Props(new Dictionary<string, object> {
                    ["blockName"] = StrProp("块名"), ["sourceCode"] = StrProp("SCL代码"),
                    ["interfaceJson"] = StrProp("接口定义JSON（可选）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "blockName", "sourceCode" }),
                args => tia.Value.AddSclCode(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "sourceCode") ?? "", GetStringArg(args, "interfaceJson"), GetStringArg(args, "plcName")));

            // Compile
            server.RegisterTool("compile_plc", "编译整个PLC软件。", EmptySchema(), _ => tia.Value.CompilePlc(null));
            server.RegisterTool("compile_block", "编译单个程序块。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.CompileBlock(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            // Export/Import
            server.RegisterTool("import_block_source", "导入源代码创建/更新块。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["sourceCode"] = StrProp("源代码"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName", "sourceCode" }),
                args => tia.Value.ImportBlockSource(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "sourceCode") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("import_block_xml", "直接导入完整的 Openness XML 字符串创建/更新块。支持 LAD/SCL/DB/FB/FC/OB 所有类型。xmlContent 应为 export_block_source 导出的完整 XML（含 <SW.Blocks.xxx> 根元素）。",
                Props(new Dictionary<string, object> { ["xmlContent"] = StrProp("完整的 Openness XML 字符串") }, new[] { "xmlContent" }),
                args => tia.Value.ImportBlockXml(GetStringArg(args, "xmlContent") ?? ""));

            server.RegisterTool("import_block_from_file", "从 XML 文件路径直接导入块（Openness 标准格式，支持 LAD/SCL/DB 等）。filePath 为 export_block_source 导出的完整 XML 文件路径。",
                Props(new Dictionary<string, object> { ["filePath"] = StrProp("XML 文件路径") }, new[] { "filePath" }),
                args => tia.Value.ImportBlockFromFile(GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("write_block_source", "直接写入块源代码。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["sourceCode"] = StrProp("源代码"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName", "sourceCode" }),
                args => tia.Value.WriteBlockSource(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "sourceCode") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("export_block_source", "导出块源代码到文件。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["outputPath"] = StrProp("输出路径"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName", "outputPath" }),
                args => tia.Value.ExportBlockSource(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "outputPath") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("export_all_blocks", "批量导出所有块（含系统块 IEC_TIMER/IEC_COUNTER）到指定目录。filter 可选，按名称过滤。",
                Props(new Dictionary<string, object> { ["outputDir"] = StrProp("输出目录"), ["filter"] = StrProp("名称过滤（可选）"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "outputDir" }),
                args => tia.Value.ExportAllBlocks(GetStringArg(args, "outputDir") ?? "", GetStringArg(args, "filter"), GetStringArg(args, "plcName")));

            server.RegisterTool("export_block_structure", "导出块结构到文件。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["outputPath"] = StrProp("输出路径"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName", "outputPath" }),
                args => tia.Value.ExportBlockStructure(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "outputPath") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("read_data_block_structure", "读取DB块变量结构。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("DB块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.ReadDataBlockStructure(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            // ── 块内容读取（理解项目结构）──
            server.RegisterTool("list_block_networks", "列出块的所有网络（网络号、标题、注释、语言类型）。让 AI 理解块的结构，而非导出XML。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.ListBlockNetworks(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("read_scl_code", "读取 SCL 块的源代码文本（直接返回代码字符串）。让 AI 理解 SCL 逻辑。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.ReadSclCode(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("read_lad_network", "读取单个 LAD 网络的指令结构（盒子类型、引脚、变量连线）。networkNumber 从1开始。让 AI 理解每个网络的指令和连线，可据此用 add_lad_network 重建。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["networkNumber"] = IntProp("网络号（从1开始）"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName", "networkNumber" }),
                args => tia.Value.ReadLadNetwork(GetStringArg(args, "blockName") ?? "", GetIntArg(args, "networkNumber") ?? 1, GetStringArg(args, "plcName")));

            server.RegisterTool("get_block_interface_v2", "稳定读取块接口（含 StartValue/Comment/Remanence）。不依赖 Import 后的反射，对任意块都能返回完整接口。替代 get_block_interface。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.GetBlockInterfaceV2(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("get_block_call_graph", "读取块的所有调用关系（OB1 调用了哪些 FC/FB 及参数传递）。让 AI 理解块间调用关系。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.GetBlockCallGraph(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("get_project_call_graph",
                "全项目调用树：遍历所有 OB/FC/FB 块，提取调用关系，生成跨块调用图（nodes + edges）。可选 plcName 限定单个 PLC。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，不传则遍历所有PLC）")
                }, new string[] { }),
                args => tia.Value.GetProjectCallGraph(GetStringArg(args, "plcName")));

            server.RegisterTool("read_block_full", "一站式读取块完整结构：接口+网络列表+调用关系。适合快速理解整个块。",
                Props(new Dictionary<string, object> { ["blockName"] = StrProp("块名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "blockName" }),
                args => tia.Value.ReadBlockFull(GetStringArg(args, "blockName") ?? "", GetStringArg(args, "plcName")));

            // ── 简化指令盒子创建 ──
            server.RegisterTool("add_lad_box",
                "【简化指令盒子】快捷创建单个 LAD 指令盒子（TON/ADD/MOVE/GT 等），无需手写完整 JSON。\n" +
                "★用法★ 传入 boxType（指令名）、parameters（引脚参数字典）；IEC local 实例名可省略并自动分配，global 实例必须显式提供。\n" +
                "★支持的指令类型★\n" +
                "  定时器/计数器: TON, TOF, TP, TONR, CTU, CTD, CTUD（local 可自动分配唯一实例）\n" +
                "  触发器: R_TRIG, F_TRIG（local 可自动分配唯一实例）；SR, RS\n" +
                "  数学: ADD, SUB, MUL, DIV, MOD, INC, DEC\n" +
                "  比较: GT, LT, GE, LE, EQ, NE, INRANGE, OUTRANGE\n" +
                "  移动/转换: MOVE, CONVERT, ROUND, TRUNC, CEIL, FLOOR\n" +
                "  逻辑: AND, OR, XOR, NOT, INV, SHL, SHR, ROL, ROR, SWAP\n" +
                "  数学函数: ABS, SQRT, SQR, LN, EXP, SIN, COS, TAN, ASIN, ACOS, ATAN\n" +
                "  选择/限幅: SEL, LIMIT, NORM_X, SCALE_X\n" +
                "★示例★ add_lad_box(blockName=\"FB1\", boxType=\"TON\", instanceName=\"T1\", parameters={\"IN\":\"Start\",\"PT\":\"T#5S\",\"Q\":\"Done\"})\n" +
                "内部自动构建正确的 JSON 格式并调用 add_lad_network，无需关心 rung/sysCall 格式细节。",
                Props(new Dictionary<string, object> {
                    ["blockName"] = StrProp("目标块名称（块必须已存在）"),
                    ["boxType"] = StrProp("指令类型（如 TON/ADD/MOVE/GT 等）"),
                    ["instanceName"] = StrProp("IEC 实例名（local 可省略并自动分配唯一名称；global 时必填）"),
                    ["instanceScope"] = StrProp("IEC 实例作用域 local/global（可选，默认 local）"),
                    ["parameters"] = StrProp("引脚参数 JSON 字典（如 {\"IN\":\"Tag1\",\"PT\":\"T#5s\",\"Q\":\"Tag2\"}）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "blockName", "boxType", "parameters" }),
                args => {
                    var parameters = new Dictionary<string, string>();
                    var paramsStr = GetStringArg(args, "parameters");
                    if (!string.IsNullOrEmpty(paramsStr))
                    {
                        try
                        {
                            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(paramsStr!);
                            if (dict != null) parameters = dict;
                        }
                        catch { return "{\"success\":false,\"error\":\"parameters 格式错误，应为 JSON 字典字符串\"}"; }
                    }
                    return tia.Value.AddLadBox(
                        GetStringArg(args, "blockName") ?? "",
                        GetStringArg(args, "boxType") ?? "",
                        GetStringArg(args, "instanceName"),
                        parameters,
                        GetStringArg(args, "instanceScope"),
                        GetStringArg(args, "plcName"));
                });

            server.RegisterTool("add_lad_box_batch",
                "【批量指令盒子】批量创建多个 LAD 指令盒子，一次 XML 导入完成。\n" +
                "★用法★ boxesJson 是 LadBoxDef 数组：[{\"boxType\":\"TON\",\"instanceName\":\"T1\",\"parameters\":{...},\"title\":\"网络1\"},...]\n" +
                "★优势★ 比逐个 add_lad_box 快 5 倍，适合一次性创建多个指令盒子。\n" +
                "★LadBoxDef 字段★ boxType(必填), parameters(引脚字典), instanceName(local可省略/global必填), instanceScope(local/global), datatype(计数值类型), destType(目标类型), title(网络标题), negateOutput(输出取反)\n" +
                "★示例★ boxesJson=[{\"boxType\":\"TON\",\"instanceName\":\"T1\",\"parameters\":{\"IN\":\"Start\",\"PT\":\"T#5S\"},\"title\":\"延时启动\"}]",
                Props(new Dictionary<string, object> {
                    ["blockName"] = StrProp("目标块名称（块必须已存在）"),
                    ["boxesJson"] = StrProp("LadBoxDef 数组 JSON（每个元素的 boxType 和 parameters 必填）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "blockName", "boxesJson" }),
                args => {
                    var boxesJson = GetStringArg(args, "boxesJson");
                    if (string.IsNullOrEmpty(boxesJson)) return "{\"success\":false,\"error\":\"boxesJson 不能为空\"}";
                    List<LadBoxDef>? boxes;
                    try
                    {
                        boxes = JsonConvert.DeserializeObject<List<LadBoxDef>>(boxesJson!);
                    }
                    catch { return "{\"success\":false,\"error\":\"boxesJson 格式错误，应为 LadBoxDef 数组 JSON\"}"; }
                    if (boxes == null || boxes.Count == 0) return "{\"success\":false,\"error\":\"boxes 不能为空\"}";
                    return tia.Value.AddLadBoxBatch(
                        GetStringArg(args, "blockName") ?? "",
                        boxes,
                        GetStringArg(args, "plcName"));
                });

            // ── 项目脚手架（IO 分配 + 骨架创建）──
            server.RegisterTool("auto_allocate_io_addresses",
                "自动分配 S7-1200 物理地址。遵循字节-位规则：每个字节仅 0-7 位，非 Bool 信号自动字节对齐。\n" +
                "★重要★ 这是一个纯计算工具，不连博途，不创建变量。AI 拿到结果后用 add_tag_to_table 创建变量。\n" +
                "signalsJson 格式：[{\"name\":\"启动\",\"dataType\":\"Bool\",\"direction\":\"Input\"},...]\n" +
                "direction: Input/Output；dataType: Bool/Byte/Int/Word/Real/DInt",
                Props(new Dictionary<string, object> {
                    ["signalsJson"] = StrProp("信号列表 JSON 数组"),
                    ["startInputByte"] = IntProp("输入起始字节（默认0）"),
                    ["startOutputByte"] = IntProp("输出起始字节（默认0）")
                }, new[] { "signalsJson" }),
                args => tia.Value.AutoAllocateIoAddresses(GetStringArg(args, "signalsJson") ?? "[]", GetIntArg(args, "startInputByte"), GetIntArg(args, "startOutputByte")));

            server.RegisterTool("create_industrial_project_skeleton",
                "创建工业级项目骨架：标准 FC/FB/DB 分层结构（基于峨胜皮带采样架构）。\n" +
                "template: standard(默认,5DB+5FC+OB1) / minimal(1DB+1FC+OB1) / fb(1FB+OB1)\n" +
                "customBlocksJson: 自定义块列表，格式 [{\"type\":\"FC\",\"name\":\"块名\",\"number\":1,\"language\":\"LAD\",\"interfaceXml\":\"<Section.../>\"}]\n" +
                "plcName: 目标 PLC 名称（可选，多 PLC 项目需指定；未指定时默认第一个 PLC 并给出警告）\n" +
                "创建空块，AI 后续用 add_lad_network 填充逻辑。",
                Props(new Dictionary<string, object> {
                    ["template"] = StrProp("模板名: standard / minimal / fb"),
                    ["customBlocksJson"] = StrProp("自定义块列表 JSON（可选，优先于 template）"),
                    ["plcName"] = StrProp("目标 PLC 名称（可选，多 PLC 项目指定目标 PLC）")
                }, new string[] { }),
                args => tia.Value.CreateIndustrialProjectSkeleton(GetStringArg(args, "template"), GetStringArg(args, "customBlocksJson"), GetStringArg(args, "plcName")));

            // UDT
            server.RegisterTool("list_udts", "列出所有UDT。", EmptySchema(), _ => tia.Value.ListUdts());
            server.RegisterTool("read_udt_structure", "读取UDT结构。",
                Props(new Dictionary<string, object> { ["udtName"] = StrProp("UDT名称"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "udtName" }),
                args => tia.Value.ReadUdtStructure(GetStringArg(args, "udtName") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("export_udt", "导出UDT到文件。",
                Props(new Dictionary<string, object> { ["udtName"] = StrProp("UDT名称"), ["outputPath"] = StrProp("输出路径"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "udtName", "outputPath" }),
                args => tia.Value.ExportUdt(GetStringArg(args, "udtName") ?? "", GetStringArg(args, "outputPath") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("import_udt", "从XML文件导入UDT。",
                Props(new Dictionary<string, object> { ["filePath"] = StrProp("XML文件路径") }, new[] { "filePath" }),
                args => tia.Value.ImportUdt(GetStringArg(args, "filePath") ?? ""));

            // Tag Table
            server.RegisterTool("list_tag_tables", "列出 PLC 中所有变量表。",
                EmptySchema(),
                _ => tia.Value.ListTagTables());
            server.RegisterTool("create_tag_table", "创建一个新的 PLC 变量表。",
                Props(new Dictionary<string, object> { ["name"] = StrProp("变量表名称"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "name" }),
                args => tia.Value.CreateTagTable(GetStringArg(args, "name") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("delete_tag_table", "删除一个 PLC 变量表。",
                Props(new Dictionary<string, object> { ["tagTableName"] = StrProp("变量表名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "tagTableName" }),
                args => tia.Value.DeleteTagTable(GetStringArg(args, "tagTableName") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("add_tag_to_table", "向变量表添加新变量（支持 %I/%Q/%M 绝对地址）。",
                Props(new Dictionary<string, object> { ["tagTableName"] = StrProp("变量表名"), ["tagName"] = StrProp("变量名"), ["dataTypeName"] = StrProp("数据类型 (Bool)"), ["logicalAddress"] = StrProp("逻辑地址 (%I0.0)"), ["comment"] = StrProp("注释(可选)"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "tagTableName", "tagName", "dataTypeName" }),
                args => tia.Value.AddTagToTable(GetStringArg(args, "tagTableName") ?? "", GetStringArg(args, "tagName") ?? "", GetStringArg(args, "dataTypeName") ?? "Bool", GetStringArg(args, "logicalAddress") ?? "", GetStringArg(args, "comment") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("delete_tag", "从变量表删除变量。",
                Props(new Dictionary<string, object> { ["tagTableName"] = StrProp("变量表名"), ["tagName"] = StrProp("变量名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "tagTableName", "tagName" }),
                args => tia.Value.DeleteTag(GetStringArg(args, "tagTableName") ?? "", GetStringArg(args, "tagName") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("read_tag_table", "读取变量表内容。",
                Props(new Dictionary<string, object> { ["tagTableName"] = StrProp("变量表名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "tagTableName" }),
                args => tia.Value.ReadTagTable(GetStringArg(args, "tagTableName") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("export_tag_table", "导出变量表到文件。",
                Props(new Dictionary<string, object> { ["tagTableName"] = StrProp("变量表名"), ["outputPath"] = StrProp("输出路径"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "tagTableName", "outputPath" }),
                args => tia.Value.ExportTagTable(GetStringArg(args, "tagTableName") ?? "", GetStringArg(args, "outputPath") ?? "", GetStringArg(args, "plcName")));
            server.RegisterTool("import_tag_table", "从XML文件导入变量表。",
                Props(new Dictionary<string, object> { ["filePath"] = StrProp("XML文件路径") }, new[] { "filePath" }),
                args => tia.Value.ImportTagTable(GetStringArg(args, "filePath") ?? ""));

            server.RegisterTool("add_db_variable", "向 DB 块添加变量（含起始值）。",
                Props(new Dictionary<string, object> { ["dbName"] = StrProp("DB 块名称"), ["varName"] = StrProp("变量名"), ["dataType"] = StrProp("数据类型"), ["startValue"] = StrProp("起始值"), ["comment"] = StrProp("注释"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "dbName", "varName", "dataType" }),
                args => tia.Value.AddDbVariable(GetStringArg(args, "dbName") ?? "", GetStringArg(args, "varName") ?? "", GetStringArg(args, "dataType") ?? "Bool", GetStringArg(args, "startValue") ?? "", GetStringArg(args, "comment") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("create_db", "创建全局 DB 块，可一次性通过 interfaceJson 传入多个变量。",
                Props(new Dictionary<string, object> {
                    ["name"] = StrProp("DB 块名称"),
                    ["blockNumber"] = IntProp("块号（可选；0 或省略表示自动编号，仅正数为显式编号）"),
                    ["interfaceJson"] = StrProp("接口 Sections XML（可选）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "name" }),
                args => tia.Value.CreateDb(GetStringArg(args, "name") ?? "", GetIntArg(args, "blockNumber"), GetStringArg(args, "interfaceJson"), GetStringArg(args, "plcName")));

            server.RegisterTool("create_iec_timer_db",
                "创建 IEC 定时器全局实例 DB（系统块类型 IEC_TIMER）。TON/TOF/TP 在 OB/FC 中 instanceScope=global 使用前必须先创建。",
                Props(new Dictionary<string, object> {
                    ["instanceDbName"] = StrProp("实例 DB 名称（与 TON 网络 instance 一致）"),
                    ["blockNumber"] = IntProp("块号（可选；0 或省略表示自动编号，仅正数为显式编号）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "instanceDbName" }),
                args => tia.Value.CreateIecTimerDb(
                    GetStringArg(args, "instanceDbName") ?? "",
                    GetIntArg(args, "blockNumber"),
                    GetStringArg(args, "plcName")));

            server.RegisterTool("create_instance_db",
                "为 FB 创建背景 DB（Instance DB）。OB1 调用 FB 时需要背景 DB 存储 FB 的 Static 变量。\n"
                + "★使用流程★ 创建 FB → 调用本工具创建背景 DB → 在 OB1 中调用 FB（instanceName 传背景 DB 名称）。\n"
                + "示例：create_instance_db(instanceDbName=\"DB_Sorting\", fbName=\"FB_SortingControl\")",
                Props(new Dictionary<string, object> {
                    ["instanceDbName"] = StrProp("背景 DB 名称（如 DB_SortingControl）"),
                    ["fbName"] = StrProp("关联的 FB 名称（如 FB_SortingControl）"),
                    ["blockNumber"] = IntProp("块号（可选；0 或省略表示自动编号，仅正数为显式编号）"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "instanceDbName", "fbName" }),
                args => tia.Value.CreateInstanceDb(
                    GetStringArg(args, "instanceDbName") ?? "",
                    GetStringArg(args, "fbName") ?? "",
                    GetIntArg(args, "blockNumber"),
                    GetStringArg(args, "plcName")));

            server.RegisterTool("update_db_variable", "更新 DB 块中已有变量的数据类型、起始值或注释。",
                Props(new Dictionary<string, object> {
                    ["dbName"] = StrProp("DB 块名称"),
                    ["varName"] = StrProp("变量名"),
                    ["newDataType"] = StrProp("新数据类型"),
                    ["newStartValue"] = StrProp("新起始值"),
                    ["newComment"] = StrProp("新注释"),
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                }, new[] { "dbName", "varName" }),
                args => tia.Value.UpdateDbVariable(GetStringArg(args, "dbName") ?? "", GetStringArg(args, "varName") ?? "", GetStringArg(args, "newDataType"), GetStringArg(args, "newStartValue"), GetStringArg(args, "newComment"), GetStringArg(args, "plcName")));

            server.RegisterTool("delete_db_variable", "删除 DB 块中的变量。",
                Props(new Dictionary<string, object> { ["dbName"] = StrProp("DB 块名称"), ["varName"] = StrProp("变量名"), ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）") }, new[] { "dbName", "varName" }),
                args => tia.Value.DeleteDbVariable(GetStringArg(args, "dbName") ?? "", GetStringArg(args, "varName") ?? "", GetStringArg(args, "plcName")));

            // ── 块属性 / 保护 / 头部 / 注释 / 设备上传（非 HMI 新功能）──
            server.RegisterTool("set_block_protection",
                "设置块的保护属性：Know-how 保护 + 密码 + 访问级别。\n" +
                "★工作流★ 内部会先编译块再设置保护属性（避免不一致导致 SetAttribute 失败）。\n" +
                "enableKnowHow=true 时启用 Know-how 保护（不可逆，需谨慎）；password 设置访问密码；accessLevel 设置访问级别（0-5）。\n" +
                "返回当前 Know-how 保护状态以供验证。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["blockName"] = StrProp("块名"),
                    ["enableKnowHow"] = BoolProp("是否启用 Know-how 保护（true=启用，不可逆，谨慎！）"),
                    ["password"] = StrProp("访问密码（可选）"),
                    ["accessLevel"] = IntProp("访问级别 0-5（可选）"),
                }, new[] { "plcName", "blockName", "enableKnowHow" }),
                args => tia.Value.SetBlockProtection(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "blockName") ?? "",
                    GetBoolArg(args, "enableKnowHow") ?? false,
                    GetStringArg(args, "password"),
                    GetIntArg(args, "accessLevel")));

            server.RegisterTool("set_block_header",
                "设置块头部属性（Author 作者 / Family 家族 / Name 名称 / Version 版本）。\n" +
                "强类型属性直接赋值，失败时回退到 SetAttribute。所有参数可选，仅设置非空字段。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["blockName"] = StrProp("块名"),
                    ["author"] = StrProp("头部作者（可选）"),
                    ["family"] = StrProp("头部家族（可选）"),
                    ["name"] = StrProp("头部名称（可选）"),
                    ["version"] = StrProp("头部版本（可选，如 1.0）"),
                }, new[] { "plcName", "blockName" }),
                args => tia.Value.SetBlockHeader(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "author"),
                    GetStringArg(args, "family"),
                    GetStringArg(args, "name"),
                    GetStringArg(args, "version")));

            server.RegisterTool("get_block_properties",
                "读取块的所有属性（完整属性快照）。\n" +
                "返回：strongTyped（强类型属性：Name/Number/ProgrammingLanguage/IsConsistent/IsKnowHowProtected/MemoryLayout/Header*/Date*/AutoNumber/Namespace 等）\n" +
                "     allAttributes（通过 GetAttributeInfos 枚举的所有可读属性及当前值）\n" +
                "     isConsistent / isKnowHowProtected 顶层字段便于快速判断。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["blockName"] = StrProp("块名"),
                }, new[] { "plcName", "blockName" }),
                args => tia.Value.GetBlockProperties(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "blockName") ?? ""));

            server.RegisterTool("set_block_comment",
                "设置块的多语言注释。\n" +
                "language 为空时设置所有语言文本；非空时仅设置匹配语言（如 zh-CN / en-US）。\n" +
                "语言不存在时返回可用语言列表 availableCultures 供选择。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["blockName"] = StrProp("块名"),
                    ["comment"] = StrProp("注释文本"),
                    ["language"] = StrProp("语言代码（可选，如 zh-CN；不传则设置所有语言）"),
                }, new[] { "plcName", "blockName", "comment" }),
                args => tia.Value.SetBlockComment(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "blockName") ?? "",
                    GetStringArg(args, "comment") ?? "",
                    GetStringArg(args, "language")));

            server.RegisterTool("upload_from_device",
                "从 PLC 设备上传到项目（在线上传）。\n" +
                "★重要★ 需先调用 go_online 让 PLC 进入在线状态。\n" +
                "由于 OnlineProvider/StationUploadProvider API 在不同版本差异较大，本工具采用反射探测 + try/catch 兜底：\n" +
                "  1. 依次尝试 plc.GetService<StationUploadProvider>() / plc.GetService<OnlineProvider>()\n" +
                "  2. 反射查找 Upload/StationUpload 等方法并依次尝试调用\n" +
                "  3. 失败时返回可用方法列表和在线状态供诊断\n" +
                "includeHardware/includeSoftware 参数目前仅作为意图标记透传，实际是否生效取决于底层 API。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["targetPath"] = StrProp("上传目标路径（目录或文件，依实现而定）"),
                    ["includeHardware"] = BoolProp("是否包含硬件配置（默认 true）"),
                    ["includeSoftware"] = BoolProp("是否包含软件（块/变量表等，默认 true）"),
                }, new[] { "plcName", "targetPath" }),
                args => tia.Value.UploadFromDevice(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "targetPath") ?? "",
                    GetBoolArg(args, "includeHardware") ?? true,
                    GetBoolArg(args, "includeSoftware") ?? true));

            // ── 在线变量读写 / 增强下载 / 外部源导入 ──
            server.RegisterTool("read_online_variables",
                "在线读取 PLC 变量值（需先 go_online）。\n" +
                "★重要★ V19 OnlineProvider 仅暴露 GoOnline/GoOffline/Configuration/State，未直接支持变量读写。\n" +
                "本工具采用反射探测 Read*/Variable*/Tag* 方法；失败时返回 apiExplored（所有可用方法签名）供诊断。\n" +
                "variableNames 是字符串数组，可一次读取多个变量。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["variableNames"] = ArrProp("要读取的变量名数组（如 [\"DB1.Var1\", \"DB1.Var2\"]）", "string"),
                }, new[] { "plcName", "variableNames" }),
                args => {
                    var names = new List<string>();
                    var arr = args?["variableNames"] as JArray;
                    if (arr != null)
                        foreach (var item in arr) names.Add(item.ToString());
                    return tia.Value.ReadOnlineVariables(GetStringArg(args, "plcName") ?? "", names);
                });

            server.RegisterTool("write_online_variable",
                "在线写入单个 PLC 变量值（需先 go_online）。\n" +
                "与 read_online_variables 同理，反射探测 Write*/Variable*/Tag* 方法；\n" +
                "失败时返回 apiExplored 供诊断。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["variableName"] = StrProp("变量名（如 DB1.Var1）"),
                    ["value"] = StrProp("要写入的值（字符串形式，由底层 API 转换）"),
                }, new[] { "plcName", "variableName", "value" }),
                args => tia.Value.WriteOnlineVariable(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "variableName") ?? "",
                    GetStringArg(args, "value") ?? ""));

            server.RegisterTool("download_to_device_enhanced",
                "增强版下载到设备，支持完整/差异/停止后下载模式。\n" +
                "downloadMode: \"Complete\" 完整下载 / \"Differences\" 差异下载 / \"StopFirst\" 停止后下载。\n" +
                "includeHardware/includeSoftware 控制下载内容；差异模式自动用 SoftwareOnlyChanges 选项。\n" +
                "StopFirst 模式会先尝试反射调用 Configuration.Stop/StopPlc 停止 PLC 再下载。\n" +
                "参考 download_to_device 但提供更细粒度的下载控制。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["downloadMode"] = StrProp("下载模式：Complete / Differences / StopFirst（默认 Complete）"),
                    ["includeHardware"] = BoolProp("是否包含硬件配置（默认 false，与 includeSoftware 同时为 false 时自动完整下载）"),
                    ["includeSoftware"] = BoolProp("是否包含软件（默认 false）"),
                }, new[] { "plcName" }),
                args => tia.Value.DownloadToDeviceEnhanced(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "downloadMode") ?? "Complete",
                    GetBoolArg(args, "includeHardware") ?? false,
                    GetBoolArg(args, "includeSoftware") ?? false));

            server.RegisterTool("import_external_source",
                "导入外部源文件（SCL/STL 源）到 PLC 的外部源组。\n" +
                "★API链★ PlcSoftware.ExternalSourceGroup.ExternalSources.CreateFromFile(name, path)。\n" +
                "sourceName 为空时用文件名（不含扩展名）作为导入后的源名称。\n" +
                "采用反射访问 ExternalSources 命名空间类型，失败时返回 apiExplored 供诊断。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["filePath"] = StrProp("源文件路径（SCL/STL 源文件）"),
                    ["sourceName"] = StrProp("导入后的源名称（可选，为空时用文件名）"),
                }, new[] { "plcName", "filePath" }),
                args => tia.Value.ImportExternalSource(
                    GetStringArg(args, "plcName") ?? "",
                    GetStringArg(args, "filePath") ?? "",
                    GetStringArg(args, "sourceName")));

            // ────────────────────────────────────────────────
            // 块/网络编辑、DB/UDT 字段补全、外部源块生成（10 个新工具）
            // ────────────────────────────────────────────────

            server.RegisterTool("rename_block",
                "重命名程序块（通过 PlcBlock.Name 属性直接修改）。\n" +
                "★API链★ PlcBlock.Name（可读写属性）。\n" +
                "若新名称与已有块冲突会抛异常，建议先 list_blocks 检查。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["oldName"] = StrProp("原块名"),
                    ["newName"] = StrProp("新块名"),
                }, new[] { "oldName", "newName" }),
                args => tia.Value.RenameBlock(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "oldName") ?? "",
                    GetStringArg(args, "newName") ?? ""));

            server.RegisterTool("update_network",
                "更新指定网络（导出XML→替换第 networkIndex 个 CompileUnit→导入）。\n" +
                "★重要★ networkIndex 从 0 开始。networkJson 格式与 add_lad_network 一致。\n" +
                "★工作流★ 先用 read_lad_network 读取原网络确认索引，再调用本工具替换。\n" +
                "替换时会保留原 CompileUnit 的 ID，避免引用错乱。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["blockName"] = StrProp("目标块名称"),
                    ["networkIndex"] = IntProp("网络索引（从 0 开始）"),
                    ["networkJson"] = StrProp("新网络的 JSON 对象（格式同 add_lad_network 的 networkJson）"),
                }, new[] { "blockName", "networkIndex", "networkJson" }),
                args => tia.Value.UpdateNetwork(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "blockName") ?? "",
                    GetIntArg(args, "networkIndex") ?? 0,
                    GetStringArg(args, "networkJson") ?? "{}"));

            server.RegisterTool("delete_network",
                "删除指定网络（导出XML→删除第 networkIndex 个 CompileUnit→导入）。\n" +
                "★重要★ networkIndex 从 0 开始。删除后后续网络索引会前移。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["blockName"] = StrProp("目标块名称"),
                    ["networkIndex"] = IntProp("要删除的网络索引（从 0 开始）"),
                }, new[] { "blockName", "networkIndex" }),
                args => tia.Value.DeleteNetwork(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "blockName") ?? "",
                    GetIntArg(args, "networkIndex") ?? 0));

            server.RegisterTool("add_db_members_batch",
                "批量添加 DB 成员（不覆盖已有变量，一次导入）。\n" +
                "★性能优化★ 相比多次调用 add_db_variable，本工具一次 Export→合并所有 Member→Import。\n" +
                "★失败处理★ Export 失败时会先编译 DB 再重试（逐个添加）。\n" +
                "members 数组中每个对象包含：memberName / dataType / comment / initialValue / remanence / accessible / visible / writable / setpoint。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["dbName"] = StrProp("目标 DB 名称"),
                    ["members"] = ArrProp("DB 成员数组，如 [{\"memberName\":\"Var1\",\"dataType\":\"Int\"},{\"memberName\":\"Var2\",\"dataType\":\"Real\",\"initialValue\":\"0.0\"}]", "object"),
                }, new[] { "dbName", "members" }),
                args => {
                    var members = new List<DbMemberInput>();
                    var arr = args?["members"] as JArray;
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            members.Add(new DbMemberInput
                            {
                                MemberName = item["memberName"]?.ToString() ?? "",
                                DataType = item["dataType"]?.ToString() ?? "Bool",
                                Comment = item["comment"]?.ToString() ?? "",
                                InitialValue = item["initialValue"]?.ToString() ?? "",
                                Remanence = item["remanence"]?.ToString() ?? "NonRetain",
                                Accessible = item["accessible"]?.ToString() ?? "true",
                                Visible = item["visible"]?.ToString() ?? "true",
                                Writable = item["writable"]?.ToString() ?? "true",
                                Setpoint = item["setpoint"]?.ToString() ?? "false"
                            });
                        }
                    }
                    return tia.Value.AddDbMembersBatch(
                        GetStringArg(args, "plcName"),
                        GetStringArg(args, "dbName") ?? "",
                        members);
                });

            server.RegisterTool("update_db_variable_enhanced",
                "增强版 DB 变量更新：支持 Remanence/Accessible/Visible/Writable/Setpoint 属性。\n" +
                "★XML 往返★ Export→替换 Member 节点（附加属性）→Import。\n" +
                "★属性说明★\n" +
                "remanence: Retain / NonRetain（保持型/非保持型）\n" +
                "accessible: true=Public / false=None（HMI 可访问性）\n" +
                "visible: true/false（HMI 可见性）\n" +
                "writable: true/false（HMI 可写性）\n" +
                "setpoint: true/false（是否为设定值）\n" +
                "所有属性参数可选，未传入则保留原值。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["dbName"] = StrProp("目标 DB 名称"),
                    ["varName"] = StrProp("要更新的变量名"),
                    ["newDataType"] = StrProp("新数据类型（可选，如 Int/Real/Bool）"),
                    ["newStartValue"] = StrProp("新初始值（可选）"),
                    ["newComment"] = StrProp("新注释（可选，支持中文）"),
                    ["remanence"] = StrProp("保持性：Retain / NonRetain（可选）"),
                    ["accessible"] = BoolProp("HMI 可访问：true=Public / false=None（可选）"),
                    ["visible"] = BoolProp("HMI 可见：true/false（可选）"),
                    ["writable"] = BoolProp("HMI 可写：true/false（可选）"),
                    ["setpoint"] = BoolProp("是否为设定值：true/false（可选）"),
                }, new[] { "dbName", "varName" }),
                args => tia.Value.UpdateDbVariableEnhanced(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "dbName") ?? "",
                    GetStringArg(args, "varName") ?? "",
                    GetStringArg(args, "newDataType"),
                    GetStringArg(args, "newStartValue"),
                    GetStringArg(args, "newComment"),
                    GetStringArg(args, "remanence"),
                    GetBoolArg(args, "accessible"),
                    GetBoolArg(args, "visible"),
                    GetBoolArg(args, "writable"),
                    GetBoolArg(args, "setpoint")));

            server.RegisterTool("update_udt_member",
                "更新 UDT 成员（XML 往返：Export→替换 Member→Import）。\n" +
                "★API链★ PlcType.Export → 修改 XML → PlcTypeGroup.Types.Import(Override)。\n" +
                "用于修改 UDT 中已存在的成员的数据类型/初始值/注释。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["udtName"] = StrProp("目标 UDT 名称"),
                    ["memberName"] = StrProp("要更新的成员名"),
                    ["newDataType"] = StrProp("新数据类型（可选）"),
                    ["newStartValue"] = StrProp("新初始值（可选）"),
                    ["newComment"] = StrProp("新注释（可选，支持中文）"),
                }, new[] { "udtName", "memberName" }),
                args => tia.Value.UpdateUdtMember(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "udtName") ?? "",
                    GetStringArg(args, "memberName") ?? "",
                    GetStringArg(args, "newDataType"),
                    GetStringArg(args, "newStartValue"),
                    GetStringArg(args, "newComment")));

            server.RegisterTool("generate_blocks_from_source",
                "从外部源生成块（反射调用 PlcExternalSource.GenerateBlocksFromSource）。\n" +
                "★前置★ 先用 import_external_source 导入 .scl/.stl 源文件。\n" +
                "★工作流★ 导入源 → 生成块 → 编译验证。\n" +
                "sourceName 必须是已导入的外部源名称（不含扩展名）。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["sourceName"] = StrProp("外部源名称（已通过 import_external_source 导入）"),
                }, new[] { "sourceName" }),
                args => tia.Value.GenerateBlocksFromSource(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "sourceName") ?? ""));

            server.RegisterTool("generate_external_source",
                "从现有块生成外部源文件（反射调用 PlcExternalSourceSystemGroup.GenerateSource）。\n" +
                "★反向操作★ 与 generate_blocks_from_source 相反，将块导出为 .scl/.stl 源文件。\n" +
                "★用途★ 版本控制、块备份、跨项目迁移。\n" +
                "blockNames 是要导出的块名数组，filePath 是输出源文件路径。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["blockNames"] = ArrProp("要导出的块名数组，如 [\"FC1\", \"FB1\", \"OB1\"]", "string"),
                    ["filePath"] = StrProp("输出源文件路径（如 C:\\\\temp\\\\blocks.scl）"),
                }, new[] { "blockNames", "filePath" }),
                args => {
                    var names = new List<string>();
                    var arr = args?["blockNames"] as JArray;
                    if (arr != null)
                        foreach (var item in arr) names.Add(item.ToString());
                    return tia.Value.GenerateExternalSource(
                        GetStringArg(args, "plcName"),
                        names,
                        GetStringArg(args, "filePath") ?? "");
                });

            server.RegisterTool("add_multi_instance_member",
                "添加 FB 多实例成员（修改 FB Static 区域）。\n" +
                "★用途★ 在 FB 的 Static 区声明一个其他 FB/FC 类型的实例变量，实现多重背景调用。\n" +
                "★XML 往返★ Export→在 Static Section 添加 Member→Import。\n" +
                "instanceType 是被调用块的类型名（如 \"FB_SortingControl\"、\"TON_TIME\"）。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["fbName"] = StrProp("目标 FB 名称（在 Static 区添加成员）"),
                    ["instanceName"] = StrProp("实例变量名（如 \"SortingInst\"）"),
                    ["instanceType"] = StrProp("实例类型（如 \"FB_SortingControl\" 或 \"TON_TIME\"）"),
                }, new[] { "fbName", "instanceName", "instanceType" }),
                args => tia.Value.AddMultiInstanceMember(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "fbName") ?? "",
                    GetStringArg(args, "instanceName") ?? "",
                    GetStringArg(args, "instanceType") ?? ""));

            server.RegisterTool("set_tag_access",
                "设置变量表中变量的访问属性（ExternalAccessible/ExternalVisible/ExternalWritable）。\n" +
                "★API链★ PlcTag.ExternalAccessible / ExternalVisible / ExternalWritable（bool 属性，可读写）。\n" +
                "★用途★ 控制 HMI/OPC UA 对变量的访问权限。\n" +
                "accessible=true 表示 HMI 可读，writable=true 表示 HMI 可写，visible=true 表示在 HMI 变量列表中可见。",
                Props(new Dictionary<string, object> {
                    ["plcName"] = StrProp("PLC名称（可选，多PLC项目指定目标PLC）"),
                    ["tableName"] = StrProp("变量表名称"),
                    ["tagName"] = StrProp("变量名"),
                    ["accessible"] = BoolProp("HMI 可访问：true/false"),
                    ["visible"] = BoolProp("HMI 可见：true/false"),
                    ["writable"] = BoolProp("HMI 可写：true/false"),
                }, new[] { "tableName", "tagName", "accessible", "visible", "writable" }),
                args => tia.Value.SetTagAccess(
                    GetStringArg(args, "plcName"),
                    GetStringArg(args, "tableName") ?? "",
                    GetStringArg(args, "tagName") ?? "",
                    GetBoolArg(args, "accessible") ?? true,
                    GetBoolArg(args, "visible") ?? true,
                    GetBoolArg(args, "writable") ?? true));
        }
    }
}
