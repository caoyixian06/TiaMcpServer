using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TiaMcpServer
{
    /// <summary>
    /// LAD FlgNet XML 生成器。把 JSON 描述的网络转成符合
    ///   SW.PlcBlocks.LADFBD_v5.xsd   （FlgNet/Parts/Wire/Part/Call/NameCon/IdentCon/Powerrail/OpenCon）
    ///   SW.PlcBlocks.Access_v5.xsd   （Access/CallInfo/Parameter/Instance/Symbol/Component/Constant）
    /// 的 FlgNet 片段。
    ///
    /// XSD 摘要（每一行生成的元素都能在下面找到出处）：
    ///   FlgNet_T  → Labels?, Parts, Wires?                       (LADFBD_v5.xsd:28)
    ///   Parts_T   → choice*: Access | Part | Call （三者平级）    (LADFBD_v5.xsd:114)
    ///   Part_T    → (Equation|Instance)?, TemplateValue*, AutomaticTyped*, Invisible*, Negated*, Comment?
    ///              属性: UId(必填), Name(必填), Version?, DisabledENO?  (LADFBD_v5.xsd:82)
    ///   Call_T    → CallInfo, PartSequence_G(TemplateValue* 等)   (LADFBD_v5.xsd:15)
    ///   Wire_T    → choice*: Powerrail|NameCon|IdentCon|Openbranch|OpenCon  (LADFBD_v5.xsd:134)
    ///   NameCon_T → UId(必填), Name(必填)                         (LADFBD_v5.xsd:108)
    ///   IdentCon_T→ UId(必填)                                     (LADFBD_v5.xsd:38)
    ///   OpenCon_T → UId(必填)                                     (LADFBD_v5.xsd:10)
    ///   CallInfo_T→ choice*: IntegerAttribute|DateAttribute|Instance|NamelessParameter*|Token*|Parameter*
    ///              属性: BlockType(必填), Name?, UId?            (Access_v5.xsd:155)
    ///   Parameter_T→ IntegerAttribute?, StringAttribute?, BooleanAttribute*, Access?
    ///              属性: Name(必填), Section(Input/Output/InOut)?, Type?, UId?  (Access_v5.xsd:388)
    ///   Instance_T→ Component|AbsoluteOffset|Token|Address (多选); 属性: Scope(必填), UId?  (Access_v5.xsd:310)
    ///   Access_T  → IntegerAttribute?, (Label|Constant|CallInfo|Instruction|Indirect|Statusword
    ///              |PredefinedVariable|Expression|Symbol|Address|DataType|Reference), Comment?
    ///              属性: Scope(必填), UId?                        (Access_v5.xsd:25)
    ///
    /// 4 种网络模式：
    ///   1. 串联（默认）   触点串联 → 线圈
    ///   2. 自锁(latch)   触点并联到 O 门（共享一条 Powerrail）→ O.out → 线圈；
    ///                     自锁变量同名双 Access，线圈用 _write UID
    ///   3. FB 调用        &lt;Call&gt; → &lt;CallInfo&gt; → &lt;Instance&gt; + &lt;Parameter&gt;
    ///   4. IEC 指令       &lt;Part Name="CTU"&gt; → &lt;Instance&gt; + &lt;TemplateValue&gt;，引脚走 Wire NameCon
    ///
    /// 对照基准：D:\mcp服务器\mcp\official_ctu.xml（博途 V19 导出，编译 0 错误）。
    /// </summary>
    internal class FlgNetBuilder
    {
        // ★V17 校准：V17 使用 FlgNet/v4 命名空间，V19+ 为 v5。按当前绑定版本选择。
        private static string FlgNetNs =>
            EnvironmentDiscoveryService.CurrentEngineeringVersion().StartsWith("V17", StringComparison.OrdinalIgnoreCase)
                ? "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4"
                : "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

        /// <summary>★V17校准★ FlgNet v4 与 v5 的格式差异开关：
        /// v4(V17) 要求 Instance UId 引用已定义的变量 Access，且不接受 AutomaticTyped
        /// （ADD/SUB 的 SrcType、CONVERT 的 DestType 必须显式 TemplateValue）；v5(V19+) 反之。</summary>
        private static bool IsV17FlgNet =>
            FlgNetNs.EndsWith("/v4", StringComparison.OrdinalIgnoreCase);

        // Parts 命名空间 UId 起点（与 official_ctu.xml 的 21+ 对齐）。
        private const int PartUidStart = 21;
        // Wire 命名空间 UId 起点（与 official_ctu.xml 的 28+ 同段，这里用 100 起避免与 Part 段混淆）。
        private const int WireUidStart = 100;

        private static readonly HashSet<string> BoxInstructions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ADD", "SUB", "MUL", "DIV", "MOD",
            "ABS", "SQRT", "SQR", "LN", "EXP",
            "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN",
            "EQ", "NE", "GT", "LT", "GE", "LE",
            "INRANGE", "OUTRANGE", "NOT",
            "CONVERT", "ROUND", "TRUNC", "CEIL", "FLOOR",
            "AND", "OR", "XOR",
            "SHL", "SHR", "ROL", "ROR",
            "MOVE",
            // LOG: TIA Portal 无此指令，只有 LN（自然对数）
            // SR/RS: 特殊逻辑元素（无 en/eno），走 BuildLatch 路径
            // ── 新增指令（实证来源：示例程序_V19 / ref2 导出 XML）──
            "INC",           // 自增1: en/operand, DestType  (运行时间累计.xml)
            "SEL",           // 二选一: en/G/IN0/IN1/OUT, value_type  (流量累积.xml)
            "LIMIT",         // 限幅: en/MIN/IN/MAX/OUT, value_type  (模拟量输出_阀门_控制.xml)
            "NORMALIZE",     // NORM_X 标准化: en/min/value/max/out/eno, SrcType+DestType  (Assistant_Axis.xml)
            "NORM_X",        // ★修复★ IR/编排器目录使用 NORM_X 别名（XmlOrchestrationService.Instructions），
                            // 缺此条时 IsBoxInstruction=false → 不生成 SrcType/DestType、触发引脚错落 IN，TIA 拒绝导入
            "SCALE_X",       // SCALE_X 缩放: en/min/value/max/out/eno, SrcType+DestType  (Assistant_Axis.xml)
            "SWAP",          // 字节交换: en/in/out, SrcType  (FB158_3rd_FanucRobot.xml)
            "INV",           // 取反: en/in/out, SrcType  (FB_MOVIFIT_Classic.xml)
            "CALC",          // 计算盒子: en/in1..inN/out, Equation+Card+SrcType  (流量累积.xml)
            "CONCAT",        // 字符串连接: en/in1..inN/out, card+str_type  (FB53_DTL_TO_STRING.xml)
            "S_CONV",        // 字符串转换: en/in/out, src_type+dest_type  (10进制转ASCLL.xml)
            "T_CONV",        // 时间转换: en/in/out, src_type+dest_type  (FB3034_设定闹钟.xml)
            "UPPER_BOUND",   // 数组上限: en/ARR/DIM/OUT, src_type  (获取数组中的最大值和最小值.xml)
            "LOWER_BOUND",   // 数组下限: en/ARR/DIM/OUT, src_type  (获取数组中的最大值和最小值.xml)
            "BLKMOV",        // 块移动: en/SRC/DEST, blk_type  (FB151_3D视觉控制.xml)
            "FILL",          // 填充: en/SRC/DEST, ptr_type  (FB_MOVIFIT_Classic.xml)
            "MOVEBLOCKI",    // 块移动(变长): en/SRC/DEST  (FB474_Vision.xml)
            "FILLBLOCKI",    // 填充(变长): en/SRC/DEST  (ROBOT_KUKA_FB.xml)
            "VAL_STRG",      // 数值转字符串: en/in1..inN/OUT, str_type  (FB53_DTL_TO_STRING.xml)
            "REPLACE",       // 字符串替换: en/IN1/IN2/IN3/OUT, str_type  (FB53_DTL_TO_STRING.xml)
            "DELETE",        // 字符串删除: en/IN1/IN2/OUT, str_type  (GetTransaction_ID.xml)
            "RD_LOC_T",      // 读本地时间: en/OUT, date_type  (FB3034_设定闹钟.xml)
            "RD_SYS_T",      // 读系统时间: en/OUT, date_type  (FB3008_系统时间读取与设置.xml)
            "WR_SYS_T",      // 写系统时间: en/IN, date_type  (FB3008_系统时间读取与设置.xml)
            // ── 1号厂项目新增指令（实证：plant1_xml 导出 XML）──
            "S_MOVE",        // 字符串移动: en/in/out/eno  (编码字符串转换.xml)
            "CHARS_TO_STRG", // 字符数组转字符串: en/Strg/Cnt/Chars/pChars/eno  (编码字符串转换.xml)
            "STRG_TO_CHARS", // 字符串转字符数组: en/Strg/pChars/Chars/Cnt/eno  (Main.xml)
            "RBITFIELD",     // 位字段复位: en/n/operand  (自动制样动作程序.xml) - 特殊线圈型
        };

        /// <summary>
        /// 直通重建: 由 read_lad_network 返回的 parts/accesses/calls/wires 生成原生 FlgNet XML。
        /// 保真保留 Negated/Instance/TemplateValue;常量类型按值推断;模板 Type 按名称映射。
        /// 参数值(value=符号名)反向匹配 Access UId;匹配不到时省略参数(仅影响 CALL 网络)。
        /// Wire 的 UId 用 900000 段分配,避免与 Part/Access UId 冲突。
        /// </summary>
        public static string BuildRaw(LadNetworkDef def, List<LadRawCallDef>? rawCalls)
        {
            var calls = rawCalls ?? def.rawCalls ?? new List<LadRawCallDef>();
            var sb = new StringBuilder();
            sb.AppendLine($"<FlgNet xmlns=\"{FlgNetNs}\">");
            sb.AppendLine("<Parts>");
            // Access 先写（Parts 容器中 Access 在前）
            foreach (var acc in def.accesses ?? Enumerable.Empty<LadAccessDef>())
            {
                sb.AppendLine($"    <Access Scope=\"{AccessHelper.Esc(acc.scope)}\" UId=\"{acc.uid}\">");
                if (string.Equals(acc.type, "Constant", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("      <Constant>");
                    sb.AppendLine($"        <ConstantType>{AccessHelper.Esc(InferConstantType(acc.symbol))}</ConstantType>");
                    sb.AppendLine($"        <ConstantValue>{AccessHelper.Esc(acc.symbol)}</ConstantValue>");
                    sb.AppendLine("      </Constant>");
                }
                else
                {
                    sb.AppendLine("      <Symbol>");
                    foreach (var comp in acc.symbol.Split('.'))
                        sb.AppendLine($"        <Component Name=\"{AccessHelper.Esc(comp)}\" />");
                    sb.AppendLine("      </Symbol>");
                }
                sb.AppendLine("    </Access>");
            }
            // Part（触点/线圈/指令盒）
            foreach (var p in def.parts ?? Enumerable.Empty<LadPartDef>())
            {
                sb.Append($"    <Part Name=\"{AccessHelper.Esc(p.name)}\" UId=\"{p.uid}\"");
                if (!string.IsNullOrEmpty(p.version)) sb.Append($" Version=\"{AccessHelper.Esc(p.version)}\"");
                if (!string.IsNullOrEmpty(p.disabledEno)) sb.Append($" DisabledENO=\"{AccessHelper.Esc(p.disabledEno)}\"");
                sb.AppendLine(">");
                if (p.negated != null)
                    foreach (var n in p.negated)
                        sb.AppendLine($"      <Negated Name=\"{AccessHelper.Esc(n)}\" />");
                if (!string.IsNullOrEmpty(p.instance))
                {
                    sb.AppendLine($"      <Instance Scope=\"{AccessHelper.Esc(string.IsNullOrEmpty(p.instanceScope) ? "Global" : p.instanceScope)}\">");
                    foreach (var comp in p.instance.Split('.'))
                        sb.AppendLine($"        <Component Name=\"{AccessHelper.Esc(comp)}\" />");
                    sb.AppendLine("      </Instance>");
                }
                if (p.templates != null)
                    foreach (var kv in p.templates)
                        sb.AppendLine($"      <TemplateValue Name=\"{AccessHelper.Esc(kv.Key)}\" Type=\"{TemplateTypeName(kv.Key)}\">{AccessHelper.Esc(kv.Value)}</TemplateValue>");
                sb.AppendLine("    </Part>");
            }
            // Call（FB/FC 调用）
            foreach (var c in calls)
            {
                sb.AppendLine($"    <Call UId=\"{c.uid}\">");
                sb.Append($"      <CallInfo Name=\"{AccessHelper.Esc(c.calleeName)}\" BlockType=\"{AccessHelper.Esc(c.blockType)}\"");
                sb.AppendLine(">");
                if (!string.IsNullOrEmpty(c.instance))
                {
                    sb.AppendLine($"        <Instance Scope=\"{AccessHelper.Esc(string.IsNullOrEmpty(c.instanceScope) ? "Global" : c.instanceScope)}\">");
                    foreach (var comp in c.instance.Split('.'))
                        sb.AppendLine($"          <Component Name=\"{AccessHelper.Esc(comp)}\" />");
                    sb.AppendLine("        </Instance>");
                }
                foreach (var param in c.parameters ?? Enumerable.Empty<LadRawParamDef>())
                {
                    sb.Append($"        <Parameter Name=\"{AccessHelper.Esc(param.name)}\"");
                    if (!string.IsNullOrEmpty(param.section)) sb.Append($" Section=\"{AccessHelper.Esc(param.section)}\"");
                    if (!string.IsNullOrEmpty(param.type)) sb.Append($" Type=\"{AccessHelper.Esc(param.type)}\"");
                    var uid = FindAccessUid(def, param.value);
                    if (uid.HasValue)
                        sb.AppendLine($"><Access UId=\"{uid.Value}\" /></Parameter>");
                    else
                        sb.AppendLine(" />");
                }
                sb.AppendLine("      </CallInfo>");
                sb.AppendLine("    </Call>");
            }
            sb.AppendLine("</Parts>");
            sb.AppendLine("<Wires>");
            int wireUid = 900000;
            foreach (var w in def.wires ?? Enumerable.Empty<LadRawWireDef>())
            {
                sb.AppendLine($"    <Wire UId=\"{wireUid++}\">");
                foreach (var ep in w.endpoints ?? Enumerable.Empty<string>())
                {
                    if (string.Equals(ep, "Powerrail", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("      <Powerrail />");
                    else if (ep.StartsWith("#"))
                        sb.AppendLine($"      <IdentCon UId=\"{ep.Substring(1)}\" />");
                    else if (string.Equals(ep, "Open", StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("      <OpenCon />");
                    else
                    {
                        var idx = ep.IndexOf('.');
                        if (idx > 0)
                            sb.AppendLine($"      <NameCon UId=\"{ep.Substring(0, idx)}\" Name=\"{AccessHelper.Esc(ep.Substring(idx + 1))}\" />");
                        else
                            sb.AppendLine($"      <OpenCon />");
                    }
                }
                sb.AppendLine("    </Wire>");
            }
            sb.AppendLine("</Wires>");
            sb.AppendLine("</FlgNet>");
            return sb.ToString();
        }

        /// <summary>常量类型按值推断（read 只返回值,类型丢失）。</summary>
        private static string InferConstantType(string v)
        {
            if (string.IsNullOrEmpty(v)) return "Int";
            var s = v.Trim();
            if (s.StartsWith("T#", StringComparison.OrdinalIgnoreCase)) return "Time";
            if (bool.TryParse(s, out _)) return "Bool";
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d == Math.Truncate(d) ? "Int" : "Real";
            return "String";
        }

        /// <summary>TemplateValue 的 Type 属性映射（read 只返回值,Type 丢失）。</summary>
        private static string TemplateTypeName(string name) =>
            name.Equals("Card", StringComparison.OrdinalIgnoreCase) ? "Cardinality" : "Type";

        /// <summary>按符号名反向匹配 Access UId（CALL 参数引用）。</summary>
        private static int? FindAccessUid(LadNetworkDef def, string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return null;
            foreach (var a in def.accesses ?? Enumerable.Empty<LadAccessDef>())
                if (string.Equals(a.symbol, symbol, StringComparison.Ordinal)) return a.uid;
            return null;
        }

        /// <summary>
        /// 把多个网络转成 FlgNet 片段列表（每个片段对应一个 CompileUnit）。
        /// 入参既可以是单网络（contacts/coils 在顶层）也可以是 networks 数组。
        /// </summary>
        public static List<string> Build(LadNetworkDef def)
        {
            // ★ 直通路径: read_lad_network 返回的 parts/accesses/wires 原始结构,保真重建
            if (def.parts != null && def.parts.Count > 0)
                return new List<string> { BuildRaw(def, null) };

            var allNets = def.networks != null && def.networks.Count > 0
                ? def.networks
                : new List<LadNetworkDef> { def };

            // ★ 先收集所有网络的接口变量到全局集合，确保跨网络引用的变量被正确标记为 LocalVariable
            var allInterfaceVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var net in allNets)
                foreach (var v in net.variables ?? Enumerable.Empty<LadVariableDef>())
                    if (!string.IsNullOrEmpty(v.name)) allInterfaceVars.Add(v.name);

            var flgNetList = new List<string>();
            foreach (var net in allNets)
            {
                string flgNet;
                if (net.rung != null && net.rung.Count > 0)
                    flgNet = BuildRung(net, allInterfaceVars);
                else if (net.sysCall != null)
                    flgNet = BuildSysCall(net, allInterfaceVars);
                else if (string.Equals(net.type, "latch", StringComparison.OrdinalIgnoreCase))
                    flgNet = BuildLatch(net, allInterfaceVars);
                else
                    flgNet = BuildSeries(net, allInterfaceVars);

                flgNetList.Add(flgNet);
            }
            return flgNetList;
        }

        // ════════════════════════════════════════════════════════════
        // UId 分配器：单一连续命名空间，覆盖 Access / Part / Call / Instance / OpenCon
        //   对照 official_ctu.xml：Tag_4=21、Constant=22、Contact=23、CTU=24、Instance=25、
        //   OpenCon(R)=26、OpenCon(CV)=27 —— 全部在 21+ 段。
        //   Wire 走独立的 wireUid 计数器（100 起）。
        // ════════════════════════════════════════════════════════════
        private sealed class UidAllocator
        {
            public int PartUid = PartUidStart - 1;  // 先 ++ 再用
            public int WireUid = WireUidStart - 1;
            /// <summary>记录每个 Part/Call UId 的逻辑流输出引脚名（如 "out"/"Q"/"eno"）。
            /// 用于下游元素正确引用上游输出的引脚名。</summary>
            public Dictionary<int, string> OutputPins = new();

            public int NextPart() => ++PartUid;
            public int NextWire() => ++WireUid;

            /// <summary>获取指定 UId 的逻辑流输出引脚名，默认 "out"。</summary>
            public string GetOutputPin(int uid)
                => OutputPins.TryGetValue(uid, out var pin) ? pin : "out";
        }

        // ────────────────────────────────────────────────────────────
        // 变量解析：把"符号名 / 数字字面量"分到两条 Access 通道。
        //   符号名   → 走 Symbol Access（GlobalVariable/LocalVariable），UId 由 allocator 统一发放
        //   数字字面量 → 走 Constant Access（LiteralConstant）
        //   通道内同名只分配一次（dedup），返回值用于后续 IdentCon 引用。
        // ────────────────────────────────────────────────────────────
        private sealed class VarRegistry
        {
            private readonly Dictionary<string, int> _symbolUid =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _constUid =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _interfaceVars;
            private readonly HashSet<string> _consumedSymbols =
                new(StringComparer.OrdinalIgnoreCase);

            public VarRegistry(HashSet<string> interfaceVars) => _interfaceVars = interfaceVars;

            /// <summary>登记一个符号名（若尚未登记），返回其 UId。null/空/数字字面量返回 false。</summary>
            public bool TryRegisterSymbol(string? name, UidAllocator alloc, out int uid)
            {
                uid = 0;
                if (string.IsNullOrEmpty(name) || IsNumericLiteral(name)) return false;
                string key = name!;
                if (_symbolUid.TryGetValue(key, out uid)) return true;
                uid = alloc.NextPart();
                _symbolUid[key] = uid;
                return true;
            }

            /// <summary>登记一个数字字面量常量（若尚未登记），返回其 UId。</summary>
            public bool TryRegisterConstant(string? literal, UidAllocator alloc, out int uid)
            {
                uid = 0;
                if (string.IsNullOrEmpty(literal) || !IsNumericLiteral(literal)) return false;
                string key = literal!;
                if (_constUid.TryGetValue(key, out uid)) return true;
                uid = alloc.NextPart();
                _constUid[key] = uid;
                return true;
            }

            /// <summary>解析任意值：符号名 → Symbol Access UId；数字字面量 → Constant Access UId。</summary>
            public bool TryResolve(string? value, out int uid)
            {
                if (string.IsNullOrEmpty(value)) { uid = 0; return false; }
                string key = value!;
                if (_symbolUid.TryGetValue(key, out uid)) return true;
                if (_constUid.TryGetValue(key, out uid)) return true;
                uid = 0;
                return false;
            }

            /// <summary>fix#13: 消费已注册符号（每个注册 Access 只被一个触点复用一次）。
            /// 并联支路内同名变量出现在多条支路时，第一条支路消费预注册 Access，
            /// 后续支路各自创建独立 Access，既避免多 Wire 复用同一 UId，也避免预注册 Access 成为孤儿。</summary>
            public bool TryConsume(string? value, out int uid)
            {
                if (string.IsNullOrEmpty(value)) { uid = 0; return false; }
                string key = value!;
                if (_symbolUid.TryGetValue(key, out uid) && _consumedSymbols.Add(key)) return true;
                uid = 0;
                return false;
            }

            public IEnumerable<KeyValuePair<string, int>> Symbols => _symbolUid;
            public IEnumerable<KeyValuePair<string, int>> Constants => _constUid;
        }

        private static HashSet<string> CollectInterfaceVars(LadNetworkDef net)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in net.variables ?? Enumerable.Empty<LadVariableDef>())
                if (!string.IsNullOrEmpty(v.name)) set.Add(v.name);
            return set;
        }

        // ════════════════════════════════════════════════════════════
        // 1) 普通串联：触点串联 → (调用) → 线圈
        // ════════════════════════════════════════════════════════════
        private static string BuildSeries(LadNetworkDef net, HashSet<string> allInterfaceVars)
        {
            var contacts = net.contacts ?? new List<LadContactDef>();
            var coils = net.coils ?? new List<LadCoilDef>();
            var calls = net.calls ?? new List<LadCallDef>();

            var alloc = new UidAllocator();
            var vars = new VarRegistry(allInterfaceVars);
            var parts = new StringBuilder();
            var wires = new StringBuilder();

            // 预登记全部引用变量（Access 在 Parts 段最前面输出）
            foreach (var c in contacts) vars.TryRegisterSymbol(c.variable, alloc, out _);
            foreach (var c in coils) vars.TryRegisterSymbol(c.variable, alloc, out _);
            foreach (var call in calls)
                foreach (var p in call.pins ?? new List<LadPinDef>())
                    vars.TryRegisterSymbol(p.variable, alloc, out _);

            // 自锁变量（同时在 contacts 和 coils）线圈写用独立 UID（_write key）
            var latchVars = LatchVariables(contacts, coils);
            foreach (var v in latchVars)
            {
                vars.TryRegisterSymbol(v + "_write", alloc, out _);
            }

            // 1a) 变量 Access（Symbol）—— Parts_T/Access
            AppendSymbolAccesses(parts, vars, interfaceVarsOnly: false, interfaceVars: allInterfaceVars);

            // 1b) 触点串联
            int leftPadUid = 0;
            foreach (var c in contacts)
            {
                int cUid = alloc.NextPart();
                parts.AppendLine(AccessHelper.BuildContactPart(c.name, cUid));

                // 电源轨/上一级 out → 本触点 in
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (leftPadUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{leftPadUid}\" Name=\"out\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");

                // operand → 变量（输入引脚：IdentCon 在前）
                if (vars.TryResolve(c.variable, out int vUid))
                {
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                    wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                    wires.AppendLine("    </Wire>");
                }
                leftPadUid = cUid;
            }

            // 1c) FB 调用（Call + CallInfo + Instance + Parameter）
            foreach (var call in calls)
            {
                int callUid = alloc.NextPart();
                parts.AppendLine(BuildCallElement(callUid, call, alloc, vars, allInterfaceVars));

                // EN 连线：上一级 → EN
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (leftPadUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{leftPadUid}\" Name=\"out\" />");
                wires.AppendLine($"      <NameCon UId=\"{callUid}\" Name=\"EN\" />");
                wires.AppendLine("    </Wire>");

                foreach (var pin in call.pins ?? new List<LadPinDef>())
                {
                    if (string.IsNullOrEmpty(pin.variable)) continue;
                    if (!vars.TryResolve(pin.variable, out int vUid)) continue;
                    bool isOut = string.Equals(pin.direction, "out", StringComparison.OrdinalIgnoreCase);
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    if (isOut)
                    {
                        // 输出引脚：NameCon 在前
                        wires.AppendLine($"      <NameCon UId=\"{callUid}\" Name=\"{AccessHelper.Esc(pin.name)}\" />");
                        wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                    }
                    else
                    {
                        // 输入引脚：IdentCon 在前
                        wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                        wires.AppendLine($"      <NameCon UId=\"{callUid}\" Name=\"{AccessHelper.Esc(pin.name)}\" />");
                    }
                    wires.AppendLine("    </Wire>");
                }
                leftPadUid = callUid;
            }

            // 1d) 线圈
            foreach (var c in coils)
            {
                int cUid = alloc.NextPart();
                parts.AppendLine(AccessHelper.BuildCoilPart(c.name, cUid));

                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (leftPadUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{leftPadUid}\" Name=\"out\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");

                // 自锁变量线圈用 _write UID
                var key = latchVars.Contains(c.variable) ? c.variable + "_write" : c.variable;
                if (vars.TryResolve(key, out int v2Uid))
                {
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <IdentCon UId=\"{v2Uid}\" />");
                    wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                    wires.AppendLine("    </Wire>");
                }
                leftPadUid = cUid;
            }

            return WrapFlgNet(parts, wires);
        }

        /// <summary>
        /// FB 调用：&lt;Call&gt; → &lt;CallInfo BlockType="FB" Name="..."&gt; →
        ///   &lt;Instance Scope="GlobalVariable"&gt;&lt;Component Name="instance"/&gt;&lt;/Instance&gt;
        ///   + 每个引脚 &lt;Parameter Name="..." Section="Input/Output"&gt;&lt;Access&gt;...&lt;/Access&gt;&lt;/Parameter&gt;
        /// 对照 Access_v5.xsd：CallInfo_T / Instance_T / Parameter_T / Access_T。
        /// 引脚的变量 Access 作为 Parameter 的子元素（Access_G），UId 来自 vars。
        /// </summary>
        private static string BuildCallElement(int callUid, LadCallDef call,
            UidAllocator alloc, VarRegistry vars, HashSet<string> allInterfaceVars)
        {
            var blockType = string.IsNullOrWhiteSpace(call.blockType) ? "FB" : call.blockType.Trim();
            if (string.IsNullOrWhiteSpace(call.blockName))
                throw new InvalidOperationException("FB/FC 调用缺少 blockName。");

            var sb = new StringBuilder();
            sb.AppendLine($"    <Call UId=\"{callUid}\">");
            sb.Append($"      <CallInfo Name=\"{AccessHelper.Esc(call.blockName)}\" BlockType=\"{AccessHelper.Esc(blockType)}\"");
            sb.AppendLine(">");

            var isFcCall = string.Equals(blockType, "FC", StringComparison.OrdinalIgnoreCase);
            if (!isFcCall)
            {
                if (string.IsNullOrWhiteSpace(call.instanceName))
                    throw new InvalidOperationException($"FB 调用 '{call.blockName}' 必须显式提供 instanceName。");
                var global = string.Equals(call.instanceScope, "global", StringComparison.OrdinalIgnoreCase);
                var local = string.Equals(call.instanceScope, "local", StringComparison.OrdinalIgnoreCase);
                if (!global && !local)
                    throw new InvalidOperationException($"FB 调用 '{call.blockName}' 必须显式提供 instanceScope='local' 或 'global'，禁止按名称猜测实例作用域。");

                var instUid = alloc.NextPart();
                var scope = global ? "GlobalVariable" : "LocalVariable";
                sb.AppendLine($"        <Instance Scope=\"{scope}\" UId=\"{instUid}\">");
                // ★修复★ 支持点分实例路径（如 "DB_背景.实例" → 多个 Component）。
                foreach (var comp in AccessHelper.DisplayName(call.instanceName).Split('.'))
                    sb.AppendLine($"          <Component Name=\"{AccessHelper.Esc(comp)}\" />");
                sb.AppendLine("        </Instance>");
            }

            foreach (var pin in call.pins ?? new List<LadPinDef>())
            {
                if (string.IsNullOrWhiteSpace(pin.name) || string.IsNullOrWhiteSpace(pin.variable))
                    throw new InvalidOperationException($"调用 '{call.blockName}' 存在空引脚名称或变量。");
                if (string.Equals(pin.direction, "inout", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"调用 '{call.blockName}' 的 InOut 引脚 '{pin.name}' 尚未通过 TIA 双向连线回归，当前版本拒绝生成。");
                if (!string.Equals(pin.direction, "in", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pin.direction, "out", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"调用 '{call.blockName}' 的引脚 '{pin.name}' direction 只能是 in/out。");
                if (string.IsNullOrWhiteSpace(pin.datatype))
                    throw new InvalidOperationException($"调用 '{call.blockName}' 的引脚 '{pin.name}' 必须显式提供 datatype，禁止按变量名猜测类型。");
                if (pin.datatype!.IndexOfAny(new[] { '<', '>', '&', '\r', '\n', '\t' }) >= 0)
                    throw new InvalidOperationException($"调用 '{call.blockName}' 的引脚 '{pin.name}' datatype 含非法字符。");
                if (!vars.TryResolve(pin.variable, out _))
                    throw new InvalidOperationException($"调用 '{call.blockName}' 的引脚 '{pin.name}' 变量 '{pin.variable}' 无法解析；旧 calls 表示法暂不支持字面量参数。");

                var section = string.Equals(pin.direction, "out", StringComparison.OrdinalIgnoreCase) ? "Output" : "Input";
                var baseName = AccessHelper.BaseVarName(pin.variable);
                var explicitGlobal = string.Equals(pin.scope, "global", StringComparison.OrdinalIgnoreCase);
                var explicitLocal = string.Equals(pin.scope, "local", StringComparison.OrdinalIgnoreCase);
                var accessScope = explicitGlobal ? "GlobalVariable"
                    : explicitLocal ? "LocalVariable"
                    : allInterfaceVars.Contains(baseName) ? "LocalVariable" : "GlobalVariable";

                sb.AppendLine($"        <Parameter Name=\"{AccessHelper.Esc(pin.name)}\" Section=\"{section}\" Type=\"{AccessHelper.Esc(pin.datatype)}\">");
                sb.AppendLine($"          <Access Scope=\"{accessScope}\">");
                sb.AppendLine("            <Symbol>");
                foreach (var comp in AccessHelper.DisplayName(pin.variable).Split('.'))
                {
                    if (!string.IsNullOrEmpty(comp))
                        sb.AppendLine($"              <Component Name=\"{SecurityElementEscape(comp)}\" />");
                }
                sb.AppendLine("            </Symbol>");
                sb.AppendLine("          </Access>");
                sb.AppendLine("        </Parameter>");
            }

            sb.AppendLine("      </CallInfo>");
            sb.Append("    </Call>");
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════
        // 2) Latch 自锁：触点并联 → O 门 → 线圈
        //   对照 debug_latch_full.xml：
        //     - O 门 TemplateValue Card=触点数
        //     - 每个触点 out → O 门 in1/in2...（小写，1 起始）
        //     - 所有触点共享一条 Powerrail 线（Powerrail + 多个 NameCon in）
        //     - 自锁变量同名双 Access，线圈用 _write UID
        // ════════════════════════════════════════════════════════════
        // ════════════════════════════════════════════════════════════
        // P1: Rung 格式解析器
        //   rung 是从左到右的元素数组，支持：
        //   - 触点（contact）：NO/NC
        //   - 线圈（coil）：普通/置位/复位
        //   - 指令盒子（box）：IEC/box 指令
        //   - 并联分支（branch）：每条支路是 rung 子数组
        //   - FB 调用（call）：用户 FB
        // ════════════════════════════════════════════════════════════
        private static string BuildRung(LadNetworkDef net, HashSet<string> allInterfaceVars)
        {
            var rung = net.rung!;
            var alloc = new UidAllocator();
            var vars = new VarRegistry(allInterfaceVars);
            var parts = new StringBuilder();
            var wires = new StringBuilder();

            // 预扫描：收集所有引用的变量名
            CollectRungSymbols(rung, vars, alloc);

            // 自锁变量检测：同一变量同时出现在触点和线圈中
            // 需要为线圈创建单独的 _write Access（对照 水塔自动控制.xml：系统启动有两个 Access）
            var latchVars = DetectRungLatchVariables(rung);
            foreach (var v in latchVars)
                vars.TryRegisterSymbol(v + "_write", alloc, out _);

            // 输出所有 Access
            var staticVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (net.variables != null)
                foreach (var v in net.variables)
                    if (string.Equals(v.section, "Static", StringComparison.OrdinalIgnoreCase))
                        staticVars.Add(v.name);
            AppendSymbolAccesses(parts, vars, interfaceVarsOnly: false, interfaceVars: allInterfaceVars, staticVars: staticVars);

            // 从电源轨开始，逐元素处理
            int sourceUid = 0; // 0 = 电源轨
            ProcessRungElements(rung, parts, wires, vars, alloc, ref sourceUid, allInterfaceVars, latchVars: latchVars);

            return WrapFlgNet(parts, wires);
        }

        /// <summary>预扫描 rung 元素，收集所有变量符号</summary>
        private static void CollectRungSymbols(List<RungElement> rung, VarRegistry vars, UidAllocator alloc)
        {
            foreach (var elem in rung)
            {
                if (elem.contact != null) vars.TryRegisterSymbol(elem.contact.contact, alloc, out _);
                if (elem.coil != null) vars.TryRegisterSymbol(elem.coil.coil, alloc, out _);
                if (elem.box != null)
                {
                    // box 引脚变量不预注册：由 ProcessBox 创建 inline Access
                    // 避免分支场景下预注册的 Access 成为孤立引用
                    if (!string.IsNullOrEmpty(elem.box.instance))
                        { }
                }
                if (elem.call != null)
                    foreach (var p in elem.call.pins ?? new List<RungCallPin>())
                        vars.TryRegisterSymbol(p.variable, alloc, out _);
                if (elem.branch != null && elem.branch.branch != null)
                    foreach (var subRung in elem.branch.branch)
                        CollectRungSymbols(subRung, vars, alloc);
            }
        }

        /// <summary>检测 rung 中的自锁变量（同一变量同时出现在触点和线圈中）</summary>
        private static HashSet<string> DetectRungLatchVariables(List<RungElement> rung)
        {
            var contactVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coilVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectContactAndCoilVars(rung, contactVars, coilVars);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in coilVars)
                if (contactVars.Contains(v))
                    result.Add(v);
            return result;
        }

        /// <summary>递归收集 rung 中所有触点和线圈的变量名</summary>
        private static void CollectContactAndCoilVars(List<RungElement> rung, HashSet<string> contactVars, HashSet<string> coilVars)
        {
            foreach (var elem in rung)
            {
                if (elem.contact != null && !string.IsNullOrEmpty(elem.contact.contact))
                    contactVars.Add(elem.contact.contact);
                if (elem.coil != null && !string.IsNullOrEmpty(elem.coil.coil))
                    coilVars.Add(elem.coil.coil);
                if (elem.branch != null && elem.branch.branch != null)
                    foreach (var subRung in elem.branch.branch)
                        CollectContactAndCoilVars(subRung, contactVars, coilVars);
            }
        }

        /// <summary>处理 rung 元素列表，返回最后一个元素的 out UId</summary>
        private static void ProcessRungElements(List<RungElement> rung, StringBuilder parts, StringBuilder wires,
            VarRegistry vars, UidAllocator alloc, ref int sourceUid, HashSet<string> allInterfaceVars,
            List<(int uid, string pin)>? deferredPowerrailIns = null, HashSet<string>? latchVars = null, bool inBranch = false)
        {
            // fix#12: 跟踪逻辑流上最近的普通触点变量，供 ConnectTriggerCondition 消重。
            // fix#14: 只有"普通常开触点"才能安全消重——常闭/边沿触点与触发变量同名时
            // 逻辑不等价（NOT X AND X ≡ 恒假），必须保留独立触发触点。
            string? lastContactVar = null;
            bool lastContactSafe = false;
            // fix#17: 分支支路内仅"首元件"的输入挂分支母线（deferredPowerrailIns），
            // 后续元件必须从前一级 out 串联，否则支路内串联条件悬空（电流排序/语义错误）。
            bool firstElem = true;
            foreach (var elem in rung)
            {
                List<(int uid, string pin)>? elemDeferred = firstElem ? deferredPowerrailIns : null;
                if (elem.contact != null)
                {
                    ProcessContact(elem.contact, parts, wires, vars, alloc, ref sourceUid, elemDeferred, inBranch, allInterfaceVars);
                    if (!string.IsNullOrEmpty(elem.contact.contact))
                    {
                        lastContactVar = elem.contact.contact;
                        bool isEdge = string.Equals(elem.contact.type, "pcontact", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(elem.contact.type, "ncontact", StringComparison.OrdinalIgnoreCase);
                        // 兼容直接 rung 输入中的 type="NegatedContact"/"NotContact"；
                        // 这些别名必须按常闭触点处理，不能被当作普通常开触点。
                        lastContactSafe = !IsNegatedContact(elem.contact) && !isEdge;
                    }
                    else
                    {
                        lastContactVar = null;
                        lastContactSafe = false;
                    }
                }
                else if (elem.coil != null)
                {
                    ProcessCoil(elem.coil, parts, wires, vars, alloc, ref sourceUid, elemDeferred, latchVars);
                    lastContactVar = null; lastContactSafe = false;
                }
                else if (elem.box != null)
                {
                    ProcessBox(elem.box, parts, wires, vars, alloc, ref sourceUid, allInterfaceVars, elemDeferred, inBranch, lastContactVar, lastContactSafe);
                    lastContactVar = null; lastContactSafe = false;
                }
                else if (elem.branch != null && elem.branch.branch != null)
                {
                    ProcessBranch(elem.branch.branch, parts, wires, vars, alloc, ref sourceUid, allInterfaceVars, elemDeferred, latchVars);
                    lastContactVar = null; lastContactSafe = false;
                }
                else if (elem.call != null)
                {
                    ProcessCall(elem.call, parts, wires, vars, alloc, ref sourceUid, elemDeferred, allInterfaceVars);
                    lastContactVar = null; lastContactSafe = false;
                }
                else if (elem.or != null)
                {
                    ProcessOr(elem.or, parts, wires, vars, alloc, ref sourceUid, allInterfaceVars, elemDeferred);
                    lastContactVar = null; lastContactSafe = false;
                }
                firstElem = false;
            }
        }

        /// <summary>处理紧凑 OR 门（O 门），对照峨胜项目 FC1 报警程序。
        /// 元素顺序：Access → Contact×N → O → Coil(可选)。
        /// Wire 结构：共享 Powerrail → 所有 Contact.in（扇出）；每个 Contact.out → O.in{i+1}；O.out → Coil.in。
        /// </summary>
        private static void ProcessOr(RungOr or, StringBuilder parts, StringBuilder wires,
            VarRegistry vars, UidAllocator alloc, ref int sourceUid, HashSet<string> allInterfaceVars,
            List<(int uid, string pin)>? deferredPowerrailIns = null)
        {
            var inputs = or.inputs ?? new List<string>();
            if (inputs.Count == 0) return;
            int card = inputs.Count;

            // ── 1. 创建/复用输入变量 Access ──
            var inputVarUids = new List<int>();
            foreach (var inputVar in inputs)
            {
                int varUid;
                if (!vars.TryResolve(inputVar, out varUid))
                {
                    varUid = alloc.NextPart();
                    var scope = (allInterfaceVars != null && allInterfaceVars.Contains(inputVar))
                        ? "LocalVariable" : "GlobalVariable";
                    parts.Append(AccessHelper.BuildVariableAccess(varUid, AccessHelper.DisplayName(inputVar), scope));
                }
                inputVarUids.Add(varUid);
            }

            // ── 2. 创建/复用输出变量 Access ──
            int outputVarUid = 0;
            if (!string.IsNullOrEmpty(or.output))
            {
                if (!vars.TryResolve(or.output, out outputVarUid))
                {
                    outputVarUid = alloc.NextPart();
                    var outScope = (allInterfaceVars != null && allInterfaceVars.Contains(or.output))
                        ? "LocalVariable" : "GlobalVariable";
                    parts.Append(AccessHelper.BuildVariableAccess(outputVarUid, AccessHelper.DisplayName(or.output), outScope));
                }
            }

            // ── 3. 创建 Contact Parts ──
            var contactUids = new List<int>();
            for (int i = 0; i < card; i++)
            {
                int contactUid = alloc.NextPart();
                parts.AppendLine(AccessHelper.BuildContactPart("Contact", contactUid));
                alloc.OutputPins[contactUid] = "out";
                contactUids.Add(contactUid);
            }

            // ── 4. 创建 O 门 Part ──
            int oUid = alloc.NextPart();
            parts.AppendLine($"    <Part Name=\"O\" UId=\"{oUid}\">");
            parts.AppendLine($"      <TemplateValue Name=\"Card\" Type=\"Cardinality\">{card}</TemplateValue>");
            parts.AppendLine($"    </Part>");
            alloc.OutputPins[oUid] = "out";

            // ── 5. 创建 Coil Part（如果有输出）──
            int coilUid = 0;
            if (!string.IsNullOrEmpty(or.output))
            {
                coilUid = alloc.NextPart();
                parts.AppendLine(AccessHelper.BuildCoilPart("Coil", coilUid));
                alloc.OutputPins[coilUid] = "out";
            }

            // ── 6. Wire：共享 Powerrail → 所有 Contact.in（扇出模式，单条 Wire）──
            alloc.NextWire();
            wires.Append($"    <Wire UId=\"{alloc.WireUid}\">\n      <Powerrail />");
            foreach (var cu in contactUids)
                wires.Append($"\n      <NameCon UId=\"{cu}\" Name=\"in\" />");
            wires.Append("\n    </Wire>\n");

            // ── 7. Wire：每个变量 → Contact.operand ──
            for (int i = 0; i < card; i++)
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <IdentCon UId=\"{inputVarUids[i]}\" />");
                wires.AppendLine($"      <NameCon UId=\"{contactUids[i]}\" Name=\"operand\" />");
                wires.AppendLine("    </Wire>");
            }

            // ── 8. Wire：每个 Contact.out → O.in{i+1} ──
            for (int i = 0; i < card; i++)
            {
                int pinNum = i + 1;
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <NameCon UId=\"{contactUids[i]}\" Name=\"out\" />");
                wires.AppendLine($"      <NameCon UId=\"{oUid}\" Name=\"in{pinNum}\" />");
                wires.AppendLine("    </Wire>");
            }

            // ── 9. Wire：O.out → Coil.in（如果有输出）──
            if (coilUid != 0)
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <NameCon UId=\"{oUid}\" Name=\"out\" />");
                wires.AppendLine($"      <NameCon UId=\"{coilUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");

                // ── 10. Wire：输出变量 → Coil.operand ──
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <IdentCon UId=\"{outputVarUid}\" />");
                wires.AppendLine($"      <NameCon UId=\"{coilUid}\" Name=\"operand\" />");
                wires.AppendLine("    </Wire>");

                sourceUid = coilUid;
            }
            else
            {
                // 无输出线圈：sourceUid 指向 O 门（下游可继续串联）
                sourceUid = oUid;
            }
        }

        /// <summary>判断 rung 触点是否为常闭触点（支持标准布尔字段及历史别名）。</summary>
        private static bool IsNegatedContact(RungContact c)
        {
            if (c.negated) return true;
            if (string.IsNullOrWhiteSpace(c.type)) return false;
            return string.Equals(c.type, "negated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.type, "NegatedContact", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.type, "NotContact", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>处理触点</summary>
        private static void ProcessContact(RungContact c, StringBuilder parts, StringBuilder wires,
            VarRegistry vars, UidAllocator alloc, ref int sourceUid, List<(int uid, string pin)>? deferredPowerrailIns = null,
            bool forceIndependentAccess = false, HashSet<string>? allInterfaceVars = null)
        {
            bool isPContact = string.Equals(c.type, "pcontact", StringComparison.OrdinalIgnoreCase);
            bool isNContact = string.Equals(c.type, "ncontact", StringComparison.OrdinalIgnoreCase);
            bool isEdge = isPContact || isNContact;
            bool isNegated = IsNegatedContact(c);

            string partName = isNegated ? "NegatedContact"
                : isPContact ? "PContact"
                : isNContact ? "NContact"
                : "Contact";
            int cUid = alloc.NextPart();
            parts.AppendLine(AccessHelper.BuildContactPart(partName, cUid));
            alloc.OutputPins[cUid] = "out";

            string inputPin = isEdge ? "pre" : "in";

            // 连线：source → 触点 pre/in
            if (deferredPowerrailIns != null)
            {
                deferredPowerrailIns.Add((cUid, inputPin));
            }
            else
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (sourceUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{sourceUid}\" Name=\"{alloc.GetOutputPin(sourceUid)}\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"{inputPin}\" />");
                wires.AppendLine("    </Wire>");
            }

            // 边沿检测：bit 引脚连接到存储位
            if (isEdge && !string.IsNullOrEmpty(c.bit))
            {
                int bitUid = alloc.NextPart();
                // fix#17: 边沿存储位 scope 依据变量归属判定（FB 接口/Static 成员是 LocalVariable）。
                // 硬编码 GlobalVariable 会让 Static 边沿位编译报 "Tag not defined"。
                string bitScope = (allInterfaceVars != null && allInterfaceVars.Contains(c.bit))
                    ? "LocalVariable" : "GlobalVariable";
                parts.Append(AccessHelper.BuildVariableAccess(bitUid, AccessHelper.DisplayName(c.bit), bitScope));
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <IdentCon UId=\"{bitUid}\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"bit\" />");
                wires.AppendLine("    </Wire>");
            }

            // 变量 → operand
            // fix#13: 并联支路内同名变量的触点必须各自持有独立 Access UId，
            // 否则两条 Wire 引用同一 Access（operand 连接），V17 导入报
            // "The connection with UId 'X' at the part with UId 'Y' is used multiple times at the cables"。
            // 优先消费预注册 Access（避免其成为孤儿"defined but not used"），
            // 同一变量第二次出现时创建独立 Access。
            bool reuseResolved = false;
            if (vars.TryConsume(c.contact, out int vUid))
            {
                reuseResolved = true;
            }
            else if (!forceIndependentAccess && vars.TryResolve(c.contact, out vUid))
            {
                reuseResolved = true;
            }
            if (reuseResolved)
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                wires.AppendLine("    </Wire>");
            }
            else
            {
                vUid = alloc.NextPart();
                // fix#15: 独立 Access 的 scope 依据变量归属判定（FB 接口变量是 LocalVariable）
                string scope = (allInterfaceVars != null && allInterfaceVars.Contains(c.contact))
                    ? "LocalVariable" : "GlobalVariable";
                parts.Append(AccessHelper.BuildVariableAccess(vUid, AccessHelper.DisplayName(c.contact), scope));
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                wires.AppendLine("    </Wire>");
            }

            sourceUid = cUid;
        }

        /// <summary>处理线圈</summary>
        private static void ProcessCoil(RungCoil c, StringBuilder parts, StringBuilder wires,
            VarRegistry vars, UidAllocator alloc, ref int sourceUid, List<(int uid, string pin)>? deferredPowerrailIns = null,
            HashSet<string>? latchVars = null)
        {
            // ★ 实证：1号厂项目用 SCoil/RCoil（非 SetCoil/ResetCoil）
            string coilPartName = string.Equals(c.type, "set", StringComparison.OrdinalIgnoreCase) ? "SCoil"
                : string.Equals(c.type, "reset", StringComparison.OrdinalIgnoreCase) ? "RCoil"
                : "Coil";
            int cUid = alloc.NextPart();
            parts.AppendLine(AccessHelper.BuildCoilPart(coilPartName, cUid));
            alloc.OutputPins[cUid] = "out";

            // 连线：source → 线圈 in
            if (deferredPowerrailIns != null)
            {
                deferredPowerrailIns.Add((cUid, "in"));
            }
            else
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (sourceUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{sourceUid}\" Name=\"{alloc.GetOutputPin(sourceUid)}\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");
            }

            // 变量 → operand
            // 自锁变量使用 _write UId（对照 水塔自动控制.xml：系统启动有两个 Access）
            var key = (latchVars != null && latchVars.Contains(c.coil)) ? c.coil + "_write" : c.coil;
            if (vars.TryResolve(key, out int vUid))
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                wires.AppendLine("    </Wire>");
            }
            else
            {
                // 全局变量 / DB 变量（VarRegistry 中没有，OB1 场景）：创建 inline GlobalVariable Access
                vUid = alloc.NextPart();
                parts.Append(AccessHelper.BuildVariableAccess(vUid, AccessHelper.DisplayName(c.coil), "GlobalVariable"));
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                wires.AppendLine("    </Wire>");
            }

            sourceUid = cUid;
        }

        private static string? FindPinValue(IDictionary<string, string>? pins, string pinName)
        {
            if (pins == null) return null;
            foreach (var kv in pins)
                if (string.Equals(kv.Key, pinName, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value.Trim();
            return null;
        }

        /// <summary>
        /// 把 IEC 逻辑触发条件连接到指令盒。显式触发变量会被实现为串联触点，
        /// 从而保留上游梯形图条件；TRUE 等同直连，FALSE 使用 OpenCon 表示恒假。
        /// fix#12: 当显式触发变量与前级触点（lastContactVar）相同时，直接复用上游逻辑流，
        /// 不再新建冗余触点（否则梯形图出现 运行中→运行中→TON 的重复触点）。
        /// fix#14: 仅当前级是普通常开触点（lastContactSafe）时才消重；常闭/边沿触点
        /// 同名时逻辑不等价（NOT X AND X ≡ 恒假），必须保留独立触发触点。
        /// </summary>
        private static void ConnectTriggerCondition(
            StringBuilder parts, StringBuilder wires, VarRegistry vars, UidAllocator alloc,
            int sourceUid, int targetUid, string triggerPin, string? explicitTrigger,
            HashSet<string>? allInterfaceVars, List<(int uid, string pin)>? deferredPowerrailIns,
            string? lastContactVar = null, bool lastContactSafe = false)
        {
            void ConnectUpstream(int uid, string pin)
            {
                if (deferredPowerrailIns != null)
                {
                    deferredPowerrailIns.Add((uid, pin));
                    return;
                }
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (sourceUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{sourceUid}\" Name=\"{alloc.GetOutputPin(sourceUid)}\" />");
                wires.AppendLine($"      <NameCon UId=\"{uid}\" Name=\"{AccessHelper.Esc(pin)}\" />");
                wires.AppendLine("    </Wire>");
            }

            if (string.IsNullOrWhiteSpace(explicitTrigger)
                || explicitTrigger.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                ConnectUpstream(targetUid, triggerPin);
                return;
            }

            // fix#12/fix#14: 触发变量与前级普通常开触点变量相同 → 上游触点已保证该条件，直连流。
            if (lastContactSafe && !string.IsNullOrEmpty(lastContactVar))
            {
                string normTrigger = StripLocalRef(explicitTrigger).Trim();
                string normLast = StripLocalRef(lastContactVar).Trim();
                if (normTrigger.Equals(normLast, StringComparison.OrdinalIgnoreCase))
                {
                    ConnectUpstream(targetUid, triggerPin);
                    return;
                }
            }

            if (explicitTrigger.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                int openUid = alloc.NextPart();
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <OpenCon UId=\"{openUid}\" />");
                wires.AppendLine($"      <NameCon UId=\"{targetUid}\" Name=\"{AccessHelper.Esc(triggerPin)}\" />");
                wires.AppendLine("    </Wire>");
                return;
            }

            int contactUid = alloc.NextPart();
            parts.AppendLine(AccessHelper.BuildContactPart("Contact", contactUid));
            ConnectUpstream(contactUid, "in");

            // ★修复(fix#11)★ 不再复用 rung 元素已注册的变量 Access。
            // 当触发变量同时被前级触点/线圈引用时，复用同一个 Access UId 会被两条 Wire
            // 引用（接触点 operand + 触发触点 operand），V17 导入报
            // "The connection with UId 'X' at the part with UId 'Y' is used multiple times at the cables"。
            // 每个触点都创建独立 Access UId；TIA 允许同名符号存在多个 Access 对象。
            int variableUid = alloc.NextPart();
            string display = AccessHelper.DisplayName(explicitTrigger);
            string baseName = AccessHelper.BaseVarName(explicitTrigger);
            string scope = allInterfaceVars != null && allInterfaceVars.Contains(baseName)
                ? "LocalVariable" : "GlobalVariable";
            parts.Append(AccessHelper.BuildVariableAccess(variableUid, StripLocalRef(display), scope));

            alloc.NextWire();
            wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
            wires.AppendLine($"      <IdentCon UId=\"{variableUid}\" />");
            wires.AppendLine($"      <NameCon UId=\"{contactUid}\" Name=\"operand\" />");
            wires.AppendLine("    </Wire>");

            alloc.NextWire();
            wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
            wires.AppendLine($"      <NameCon UId=\"{contactUid}\" Name=\"out\" />");
            wires.AppendLine($"      <NameCon UId=\"{targetUid}\" Name=\"{AccessHelper.Esc(triggerPin)}\" />");
            wires.AppendLine("    </Wire>");
        }

        /// <summary>处理指令盒子</summary>
        private static void ProcessBox(RungBox box, StringBuilder parts, StringBuilder wires,
            VarRegistry vars, UidAllocator alloc, ref int sourceUid, HashSet<string> allInterfaceVars,
            List<(int uid, string pin)>? deferredPowerrailIns = null, bool inBranch = false,
            string? lastContactVar = null, bool lastContactSafe = false)
        {
            string normalizedInst = NormalizeInstName(box.box);
            var boxUpper = box.box.ToUpperInvariant();  // ★ 提前定义，供多处使用
            bool isBgDb = NeedsBackgroundDb(box.box);
            bool isBox = IsBoxInstruction(box.box);
            // TON/TOF/TP 的 time_type 必须为 Time；其他指令默认 Int
            string datatype = box.datatype ?? "Int";
            if (box.box.ToUpperInvariant() is "TON" or "TOF" or "TP" or "TONR")
                datatype = "Time";

            // CoilTON/CoilTOF/CoilTP 是定时器线圈，不需要背景 DB，可在 OB 中使用
            // 引脚: in(逻辑输入), value(PT时间值), operand(输出位), out(逻辑输出)
            bool isCoilTimer = box.box.ToUpperInvariant() is "COILTON" or "COILTOF" or "COILTP";
            if (isCoilTimer)
                datatype = "Time";

            // Part
            string versionAttr = isBgDb ? " Version=\"1.0\"" : "";
            // 非背景DB型指令也可能需要 Version（如 LIMIT=1.0, BLKMOV=1.1, S_CONV=3.4 等）
            if (!isBgDb)
            {
                var instVersion = GetInstructionVersion(box.box);
                if (!string.IsNullOrEmpty(instVersion))
                    versionAttr = $" Version=\"{instVersion}\"";
            }
            // 分支内的 box 指令 ENO 需要连接到 O 门，不能禁用
            bool needsDisabledEno = NeedsDisabledEno(box.box) && !inBranch;
            string disabledEno = needsDisabledEno ? " DisabledENO=\"true\"" : "";
            int instUid = alloc.NextPart();
            parts.AppendLine($"    <Part Name=\"{AccessHelper.Esc(normalizedInst)}\" UId=\"{instUid}\"{versionAttr}{disabledEno}>");

            // Calc 计算盒子的 Equation 元素（必须在 TemplateValue 之前，XSD: Part_T = (Equation|Instance)?, TemplateValue*）
            // 实证：流量累积.xml <Part Name="Calc"><Equation>((IN1/IN2)*(IN3/IN4))+IN5</Equation><TemplateValue...>
            if (box.box.ToUpperInvariant() == "CALC" && !string.IsNullOrEmpty(box.equation))
            {
                parts.AppendLine($"      <Equation>{AccessHelper.Esc(box.equation)}</Equation>");
            }

            // CoilTON/CoilTOF/CoilTP 需要 time_type TemplateValue，但不需要 Instance
            if (isCoilTimer)
            {
                parts.AppendLine($"      <TemplateValue Name=\"time_type\" Type=\"Type\">Time</TemplateValue>");
            }

            // 背景 DB 型 Instance。实例必须由共享 PrepareLadProgram 预先规范化；
            // Builder 禁止再猜默认名，否则多个 IEC 调用会静默共享状态。
            int instanceRefUid = 0;
            if (isBgDb && string.IsNullOrWhiteSpace(box.instance))
                throw new InvalidOperationException($"IEC 指令 {box.box} 缺少唯一 instance；请先通过共享 LAD 校验入口");
            if (isBgDb)
            {
                // ★V17修复★ v4 导入器要求 Instance UId 引用"已输出的变量 Access"：
                // 直接把实例名注册到 VarRegistry（AppendSymbolAccesses 会输出对应 Symbol Access），
                // Instance 元素引用同一 UId——否则 V17 报
                // "The reference with the UID 'N' is defined but not used" 并拒绝导入。
                if (IsV17FlgNet)
                    vars.TryRegisterSymbol(box.instance!, alloc, out instanceRefUid);
                else
                    instanceRefUid = alloc.NextPart();
                bool useGlobalInstance = string.Equals(box.instanceScope, "global", StringComparison.OrdinalIgnoreCase);
                string instanceScope = useGlobalInstance ? "GlobalVariable" : "LocalVariable";
                parts.AppendLine($"      <Instance Scope=\"{instanceScope}\" UId=\"{instanceRefUid}\">");
                // ★修复★ 支持点分实例路径（如 "DB_定时器.投入延时" → 多个 Component）。
                // 曾只生成单个 Component，DB 变量实例（GlobalVariable）无法解析导致导入失败。
                foreach (var comp in AccessHelper.DisplayName(StripLocalRef(box.instance!)).Split('.'))
                    parts.AppendLine($"        <Component Name=\"{AccessHelper.Esc(comp)}\" />");
                parts.AppendLine("      </Instance>");
                var tvName = GetTemplateValueName(box.box);
                if (tvName != null)
                    parts.AppendLine($"      <TemplateValue Name=\"{tvName}\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
            }
            // Box 型 TemplateValues
            else if (isBox)
            {
                // destType 优先从 box.destType 获取（Normalize/Scale_X 需要）
                string destType = box.destType ?? "";
                int calcCard = CountCalcCard(box.box, box.pins);
                AppendBoxTemplateValues(parts, box.box, datatype, destType, calcCard);
            }

            parts.AppendLine("    </Part>");

            // ★修复★ negateOutput 的 NOT 门必须是 Parts 的平级兄弟元素：
            // 嵌套在外层 Part 内会违反 LADFBD XSD（Part_T 不允许子 Part），
            // TIA V17 导入器报 "The program code does not correspond to the scheme" 并拒绝导入。
            int notUid = 0;
            if (box.negateOutput && !string.Equals(box.box, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                notUid = alloc.NextPart();
                parts.AppendLine($"    <Part Name=\"Not\" UId=\"{notUid}\" />");
            }

            // 触发引脚连线。显式 pins.IN/CU/CD/CLK 不能被静默忽略：
            // 它表示在当前逻辑流条件上再串联一个布尔条件。
            string triggerPin = GetIecTriggerPin(box.box);
            string? explicitTrigger = FindPinValue(box.pins, triggerPin);
            ConnectTriggerCondition(parts, wires, vars, alloc, sourceUid, instUid, triggerPin,
                explicitTrigger, allInterfaceVars, deferredPowerrailIns, lastContactVar, lastContactSafe);

            // 引脚连线
            if (box.pins != null)
            {
                foreach (var kv in box.pins)
                {
                    if (string.Equals(kv.Key, triggerPin, StringComparison.OrdinalIgnoreCase)) continue;
                    if (box.negateOutput && string.Equals(kv.Key, "in", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(kv.Value)) continue;

                    bool isOutputPin = IsIecOutputPin(box.box, kv.Key);

                    if (isOutputPin)
                    {
                        // 分支内的比较盒：out 引脚用于连接 O 门，不写变量
                        if (inBranch && IsComparisonBox(box.box) &&
                            string.Equals(kv.Key, "out", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // 分支内的算术指令（MOVE/ADD/SUB等）：eno 引脚用于连接 O 门，不写变量
                        // 数据输出引脚（out1/out）正常写入变量
                        if (inBranch && !IsComparisonBox(box.box) &&
                            string.Equals(kv.Key, "eno", StringComparison.OrdinalIgnoreCase))
                            continue;

                        int vUid;
                        // 创建独立的 inline Access（避免同一输出变量被多个 box 写入）
                        vUid = alloc.NextPart();
                        var display = StripLocalRef(AccessHelper.DisplayName(kv.Value));
                        // ★ 数组元素访问（Var[0]）需用 BaseVarName 匹配接口变量名
                        string baseName = StripLocalRef(AccessHelper.BaseVarName(kv.Value));
                        string scope = (allInterfaceVars != null && allInterfaceVars.Contains(baseName))
                            ? "LocalVariable" : "GlobalVariable";
                        parts.Append(AccessHelper.BuildVariableAccess(vUid, display, scope));
                        alloc.NextWire();
                        wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                        int outSrcUid = box.negateOutput && notUid > 0 ? notUid : instUid;
                        wires.AppendLine($"      <NameCon UId=\"{outSrcUid}\" Name=\"{AccessHelper.Esc(kv.Key)}\" />");
                        wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                        wires.AppendLine("    </Wire>");
                    }
                    else
                    {
                        // 输入引脚：每个引脚创建独立的 Access UId（避免同一 Access 被多条 Wire 引用）
                        // 同一变量被不同 box 的输入引脚引用时，每个引脚需要自己的 Access UId
                        int vUid;
                        if (IsNumericLiteral(kv.Value))
                        {
                            // 常量：先尝试从 VarRegistry 查找（可能已注册）
                            if (vars.TryResolve(kv.Value, out vUid))
                            {
                                // 已注册的常量，直接使用 UId
                            }
                            else
                            {
                                // 未注册的常量，创建 inline 常量 Access
                                vUid = alloc.NextPart();
                                parts.Append(BuildConstantAccess(vUid, kv.Value));
                            }
                        }
                        else
                        {
                            // 符号变量：创建独立的 inline Access
                            vUid = alloc.NextPart();
                            var display = StripLocalRef(AccessHelper.DisplayName(kv.Value));
                            // ★ 数组元素访问（Var[0]）需用 BaseVarName 匹配接口变量名
                            string baseName = StripLocalRef(AccessHelper.BaseVarName(kv.Value));
                            string scope = (allInterfaceVars != null && allInterfaceVars.Contains(baseName))
                                ? "LocalVariable" : "GlobalVariable";
                            parts.Append(AccessHelper.BuildVariableAccess(vUid, display, scope));
                        }
                        alloc.NextWire();
                        wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                        wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                        wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{AccessHelper.Esc(kv.Key)}\" />");
                        wires.AppendLine("    </Wire>");
                    }
                }
            }

            // 背景DB型指令：为未连接的数据输出引脚（如 TON 的 ET）添加 OpenCon 悬空
            // 对照 ref_projects/升降梯_门控制.xml:595 <NameCon UId="54" Name="ET" /><OpenCon UId="59" />
            if (isBgDb)
            {
                var unusedDataOutputs = GetUnusedDataOutputPins(box.box, box.pins);
                foreach (var pinName in unusedDataOutputs)
                {
                    int openConUid = alloc.NextPart();
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{pinName}\" />");
                    wires.AppendLine($"      <OpenCon UId=\"{openConUid}\" />");
                    wires.AppendLine("    </Wire>");
                }

                // ★修复★ CTU 的 R / CTD 的 LD / CTUD 的 R+LD 是必填输入引脚：
                // BuildSysCall（sysCall 格式）会自动 OpenCon 悬空，rung 路径原先缺这段，
                // TIA V17 导入器报 "The connection with the name 'R' is not ..." 并拒绝导入。
                var requiredInputs = GetRequiredInputPins(box.box);
                if (requiredInputs != null)
                {
                    foreach (var reqPin in requiredInputs)
                    {
                        bool declared = box.pins != null && box.pins.Keys
                            .Any(k => k.Equals(reqPin, StringComparison.OrdinalIgnoreCase));
                        if (declared) continue;
                        int openConUid = alloc.NextPart();
                        alloc.NextWire();
                        wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                        wires.AppendLine($"      <OpenCon UId=\"{openConUid}\" />");
                        wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{reqPin}\" />");
                        wires.AppendLine("    </Wire>");
                    }
                }
            }

            // negateOutput: 输出引脚 → NOT in
            // SR/RS 的输出引脚为 q，其他 box 指令为 out
            string negateOutputPin = box.box.ToUpperInvariant() is "SR" or "RS" ? "q" : "out";
            if (box.negateOutput && notUid > 0)
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{negateOutputPin}\" />");
                wires.AppendLine($"      <NameCon UId=\"{notUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");
            }

            sourceUid = isBgDb ? instUid : (notUid > 0 ? notUid : instUid);
            // 记录逻辑流输出引脚名（供下游元素引用）
            // boxUpper 已在方法开头定义
            if (notUid > 0)
            {
                alloc.OutputPins[notUid] = "out"; // NOT 的输出引脚
                alloc.OutputPins[instUid] = boxUpper is "SR" or "RS" ? "q" : "out";
            }
            else if (isBgDb)
                alloc.OutputPins[instUid] = "Q"; // 背景DB型的逻辑流输出是 Q
            else if (boxUpper is "SR" or "RS")
                alloc.OutputPins[instUid] = "q";
            else if (boxUpper is "EQ" or "NE" or "GT" or "LT" or "GE" or "LE"
                or "INRANGE" or "OUTRANGE" or "NOT")
                alloc.OutputPins[instUid] = "out";
            else if (boxUpper is "COILTON" or "COILTOF" or "COILTP"
                or "RESETIECTIMERCOIL" or "RESET_IECTIMER_COIL")
                alloc.OutputPins[instUid] = "out"; // 线圈型指令的逻辑流输出是 out
            else
                alloc.OutputPins[instUid] = "eno"; // 其他 box 指令的 ENO
        }

        /// <summary>处理并联分支
        /// 支持两种模式：
        /// 1. 从 Powerrail 开始（sourceUid==0）：所有分支共享 Powerrail Wire
        /// 2. 从上游元素扇出（sourceUid!=0）：所有分支共享上游 out Wire（如 Contact.out → 多个比较盒 pre）
        /// </summary>
        private static void ProcessBranch(List<List<RungElement>> branches, StringBuilder parts, StringBuilder wires,
            VarRegistry vars, UidAllocator alloc, ref int sourceUid, HashSet<string> allInterfaceVars,
            List<(int uid, string pin)>? deferredPowerrailIns = null, HashSet<string>? latchVars = null)
        {
            if (branches.Count == 0) return;

            // 单分支：直接串联
            if (branches.Count == 1)
            {
                ProcessRungElements(branches[0], parts, wires, vars, alloc, ref sourceUid, allInterfaceVars, deferredPowerrailIns, latchVars, inBranch: true);
                return;
            }

            // 多分支：先生成各支路，再生成 O 汇合门。
            // TIA V17 按 LAD 电流方向校验 Parts：分支元件必须位于 O 门之前，
            // 否则会报“这些元素必须根据电流进行排序”。
            // 收集所有分支第一个元素的 (uid, pin)（用于共享 Wire）
            var sharedIns = new List<(int uid, string pin)>();
            var branchLastOutUids = new int[branches.Count];
            int fanoutSourceUid = sourceUid; // 扇出源（0=Powerrail，>0=上游元素）

            // 每条支路：从扇出源开始；此处会先追加分支 Parts 和内部 Wire。
            for (int i = 0; i < branches.Count; i++)
            {
                int branchSource = fanoutSourceUid; // 从扇出源开始
                ProcessRungElements(branches[i], parts, wires, vars, alloc, ref branchSource, allInterfaceVars,
                    deferredPowerrailIns: sharedIns, latchVars: latchVars, inBranch: true);
                branchLastOutUids[i] = branchSource;
            }

            // 分支元件已生成后再追加 O 门，使 Parts 顺序与电流流向一致。
            int oUid = alloc.NextPart();
            parts.AppendLine($"    <Part Name=\"O\" UId=\"{oUid}\">");
            parts.AppendLine($"      <TemplateValue Name=\"Card\" Type=\"Cardinality\">{branches.Count}</TemplateValue>");
            parts.AppendLine("    </Part>");
            alloc.OutputPins[oUid] = "out";

            // 共享 Wire：连接所有分支第一个元素的 in/triggerPin
            if (sharedIns.Count > 0)
            {
                alloc.NextWire();
                wires.Append($"    <Wire UId=\"{alloc.WireUid}\">\n      ");
                if (fanoutSourceUid == 0)
                    wires.Append("<Powerrail />");
                else
                    wires.Append($"<NameCon UId=\"{fanoutSourceUid}\" Name=\"{alloc.GetOutputPin(fanoutSourceUid)}\" />");
                foreach (var item in sharedIns)
                    wires.Append($"\n      <NameCon UId=\"{item.uid}\" Name=\"{AccessHelper.Esc(item.pin)}\" />");
                wires.Append("\n    </Wire>\n");
            }

            // 每条支路输出 → O 门 in{N}
            for (int i = 0; i < branches.Count; i++)
            {
                // 确定支路最后一个元素的逻辑流输出引脚名
                string outPinName = "out";
                if (branches[i].Count > 0)
                {
                    var lastElem = branches[i][branches[i].Count - 1];
                    outPinName = GetLogicFlowOutputPin(lastElem);
                }
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (branchLastOutUids[i] == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{branchLastOutUids[i]}\" Name=\"{AccessHelper.Esc(outPinName)}\" />");
                wires.AppendLine($"      <NameCon UId=\"{oUid}\" Name=\"in{i + 1}\" />");
                wires.AppendLine("    </Wire>");
            }

            sourceUid = oUid;
        }

        /// <summary>处理 FB/FC 调用（Call + CallInfo + Instance? + Parameter*）
        /// 对照 Access_v5.xsd: CallInfo_T / Instance_T / Parameter_T。
        /// FB 调用含 Instance（背景 DB）；FC 调用无 Instance。
        /// 使能引脚为 EN，输出为 ENO（与 BuildCallElement 一致）。</summary>
        private static void ProcessCall(RungCall call, StringBuilder parts, StringBuilder wires,
            VarRegistry vars, UidAllocator alloc, ref int sourceUid, List<(int uid, string pin)>? deferredPowerrailIns = null,
            HashSet<string>? allInterfaceVars = null)
        {
            int callUid = alloc.NextPart();
            string bt = string.IsNullOrWhiteSpace(call.blockType) ? "FB" : call.blockType!.Trim().ToUpperInvariant();
            bool isFcCall = string.Equals(bt, "FC", StringComparison.OrdinalIgnoreCase);
            if (!isFcCall)
            {
                if (string.IsNullOrWhiteSpace(call.instance))
                    throw new InvalidOperationException($"FB 调用 {call.call} 必须显式提供 instance。");
                if (!string.Equals(call.instanceScope, "local", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(call.instanceScope, "global", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"FB 调用 {call.call} 必须显式提供 instanceScope=local/global。");
            }
            else if (!string.IsNullOrWhiteSpace(call.instance))
            {
                throw new InvalidOperationException($"FC 调用 {call.call} 不能包含 instance。");
            }

            parts.AppendLine($"    <Call UId=\"{callUid}\">");
            parts.AppendLine($"      <CallInfo Name=\"{AccessHelper.Esc(call.call)}\" BlockType=\"{AccessHelper.Esc(bt)}\">");
            if (!isFcCall)
            {
                bool instGlobal = string.Equals(call.instanceScope, "global", StringComparison.OrdinalIgnoreCase);
                string instScope = instGlobal ? "GlobalVariable" : "LocalVariable";
                int instUid = alloc.NextPart();
                parts.AppendLine($"        <Instance Scope=\"{instScope}\" UId=\"{instUid}\">");
                // ★修复★ 支持点分实例路径（如 "DB_背景.实例" → 多个 Component）。
                foreach (var comp in AccessHelper.DisplayName(call.instance).Split('.'))
                    parts.AppendLine($"          <Component Name=\"{AccessHelper.Esc(comp)}\" />");
                parts.AppendLine("        </Instance>");
            }

            var pinVarBindings = new List<(string pinName, string variable, string direction, string? scope)>();
            foreach (var p in call.pins ?? new List<RungCallPin>())
            {
                if (string.IsNullOrWhiteSpace(p.name) || string.IsNullOrWhiteSpace(p.variable))
                    throw new InvalidOperationException($"调用 {call.call} 存在空引脚名称或变量。");
                if (string.Equals(p.direction, "inout", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"调用 {call.call} 的 InOut 参数 {p.name} 尚未通过 TIA XML 回归验证，已拒绝生成。");
                if (!string.Equals(p.direction, "in", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p.direction, "out", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"调用 {call.call} 的引脚 {p.name} direction 只能是 in/out。");
                if (string.IsNullOrWhiteSpace(p.datatype))
                    throw new InvalidOperationException($"调用 {call.call} 的引脚 {p.name} 必须显式提供 datatype。");
                if (p.datatype!.IndexOfAny(new[] { '<', '>', '&', '\r', '\n', '\t' }) >= 0)
                    throw new InvalidOperationException($"调用 {call.call} 的引脚 {p.name} datatype 含非法字符。");
                if (IsLadLiteral(p.variable))
                    throw new InvalidOperationException($"调用 {call.call} 的引脚 {p.name} 必须绑定符号变量；当前不支持字面量。");

                string section = string.Equals(p.direction, "out", StringComparison.OrdinalIgnoreCase) ? "Output" : "Input";
                parts.AppendLine($"        <Parameter Name=\"{AccessHelper.Esc(p.name)}\" Section=\"{section}\" Type=\"{AccessHelper.Esc(p.datatype)}\" />");
                pinVarBindings.Add((p.name, p.variable, p.direction ?? "in", p.scope));
            }
            parts.AppendLine("      </CallInfo>");
            parts.AppendLine("    </Call>");

            if (deferredPowerrailIns != null)
            {
                deferredPowerrailIns.Add((callUid, "en"));
            }
            else
            {
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (sourceUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{sourceUid}\" Name=\"{alloc.GetOutputPin(sourceUid)}\" />");
                wires.AppendLine($"      <NameCon UId=\"{callUid}\" Name=\"en\" />");
                wires.AppendLine("    </Wire>");
            }

            foreach (var (pinName, variable, direction, explicitScope) in pinVarBindings)
            {
                int varUid;
                if (!vars.TryResolve(variable, out varUid))
                {
                    varUid = alloc.NextPart();
                    var baseName = AccessHelper.BaseVarName(variable);
                    var scope = string.Equals(explicitScope, "local", StringComparison.OrdinalIgnoreCase) ? "LocalVariable"
                        : string.Equals(explicitScope, "global", StringComparison.OrdinalIgnoreCase) ? "GlobalVariable"
                        : (allInterfaceVars != null && allInterfaceVars.Contains(baseName)) ? "LocalVariable" : "GlobalVariable";
                    parts.Append(AccessHelper.BuildVariableAccess(varUid, AccessHelper.DisplayName(variable), scope));
                }
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase))
                {
                    wires.AppendLine($"      <NameCon UId=\"{callUid}\" Name=\"{AccessHelper.Esc(pinName)}\" />");
                    wires.AppendLine($"      <IdentCon UId=\"{varUid}\" />");
                }
                else
                {
                    wires.AppendLine($"      <IdentCon UId=\"{varUid}\" />");
                    wires.AppendLine($"      <NameCon UId=\"{callUid}\" Name=\"{AccessHelper.Esc(pinName)}\" />");
                }
                wires.AppendLine("    </Wire>");
            }

            alloc.OutputPins[callUid] = "eno";
            sourceUid = callUid;
        }

        /// <summary>判断指令是否需要 DisabledENO</summary>
        private static bool NeedsDisabledEno(string inst)
        {
            var upper = inst.ToUpperInvariant();
            // NOT/INRANGE/OUTRANGE 无 ENO，不需要 DisabledENO
            if (upper is "NOT" or "INRANGE" or "OUTRANGE")
                return false;
            // 新增指令中以下指令实证需要 DisabledENO="true"
            // Inc(运行时间累计.xml)/Normalize/Scale_X(Assistant_Axis.xml)/Swap/Inv/Calc(流量累积.xml)
            // CONCAT(FB53_DTL_TO_STRING.xml)/S_CONV(10进制转ASCLL.xml)
            // MoveBlockI/FillBlockI(ROBOT_KUKA_FB.xml)/VAL_STRG/REPLACE/DELETE
            // LIMIT(模拟量输出_阀门_控制.xml: DisabledENO="true")
            if (upper is "INC" or "NORMALIZE" or "NORM_X" or "SCALE_X"
                or "SWAP" or "INV" or "CALC" or "CONCAT" or "S_CONV"
                or "MOVEBLOCKI" or "FILLBLOCKI" or "VAL_STRG" or "REPLACE" or "DELETE"
                or "LIMIT"
                // 1号厂项目新增（实证：plant1_xml）
                // S_Move 有 DisabledENO（实证：编码字符串转换.xml）
                // Chars_TO_Strg/Strg_TO_Chars 无 DisabledENO（实证：编码字符串转换.xml/Main.xml）
                or "S_MOVE")
                return true;
            // T_CONV/RD_LOC_T/RD_SYS_T/WR_SYS_T/Upper_Bound/Lower_Bound/BLKMOV/FILL/SEL 无需禁用 ENO
            // Chars_TO_Strg/Strg_TO_Chars/RBitfield 也无需禁用 ENO（实证：plant1_xml）
            if (upper is "T_CONV" or "RD_LOC_T" or "RD_SYS_T" or "WR_SYS_T"
                or "UPPER_BOUND" or "LOWER_BOUND" or "BLKMOV" or "FILL"
                or "SEL"
                or "CHARS_TO_STRG" or "STRG_TO_CHARS" or "RBITFIELD")
                return false;
            return string.Equals(upper, "MOVE", StringComparison.OrdinalIgnoreCase)
                || (IsBoxInstruction(inst)
                    && !(upper is "MUL" or "DIV" or "MOD"
                        or "EQ" or "NE" or "GT" or "LT" or "GE" or "LE"
                        or "ABS" or "SQRT" or "SQR" or "LN" or "EXP"
                        or "SIN" or "COS" or "TAN" or "ASIN" or "ACOS" or "ATAN"
                        or "SHL" or "SHR"
                        or "ROL" or "ROR"));
        }

        /// <summary>计算 Calc/CONCAT 等可变输入数指令的 Card 值</summary>
        private static int CountCalcCard(string inst, Dictionary<string, string>? pins)
        {
            var upper = inst.ToUpperInvariant();
            if (upper != "CALC" && upper != "CONCAT" && upper != "VAL_STRG")
                return 2;
            if (pins == null) return 2;
            int card = 0;
            foreach (var k in pins.Keys)
             {
                 if (k.StartsWith("in", StringComparison.OrdinalIgnoreCase)
                     && k.Length > 2
                     && int.TryParse(k.Substring(2), out _))
                     card++;
             }
            return Math.Max(card, 2); // 最少2个
        }

        /// <summary>获取指令的 Version 属性（实证来源：ref2 导出 XML）</summary>
        private static string? GetInstructionVersion(string inst)
        {
            var upper = inst.ToUpperInvariant();
            return upper switch
            {
                // 实证来源：模拟量输出_阀门_控制.xml <Part Name="LIMIT" Version="1.0" ...>
                "LIMIT" => "1.0",
                // 实证来源：FB151_3D视觉控制.xml / FB_MOVIFIT_Classic.xml <Part Name="BLKMOV" Version="1.1" ...>
                "BLKMOV" => "1.1",
                // 实证来源：FB_MOVIFIT_Classic.xml <Part Name="FILL" Version="1.1" ...>
                "FILL" => "1.1",
                // 实证来源：10进制转ASCLL.xml <Part Name="S_CONV" Version="3.4" ...>
                "S_CONV" => "3.4",
                // 实证来源：FB3034_设定闹钟.xml <Part Name="T_CONV" Version="1.2" ...>
                "T_CONV" => "1.2",
                // 实证来源：FB3034_设定闹钟.xml <Part Name="RD_LOC_T" Version="1.0" ...>
                "RD_LOC_T" => "1.0",
                // 实证来源：FB3008_系统时间读取与设置.xml <Part Name="RD_SYS_T" Version="1.0" ...>
                "RD_SYS_T" => "1.0",
                // 实证来源：FB3008_系统时间读取与设置.xml <Part Name="WR_SYS_T" Version="1.0" ...>
                "WR_SYS_T" => "1.0",
                // 1号厂项目新增（实证：plant1_xml）
                "CHARS_TO_STRG" => "1.2",  // 编码字符串转换.xml <Part Name="Chars_TO_Strg" Version="1.2">
                "STRG_TO_CHARS" => "1.2",  // Main.xml <Part Name="Strg_TO_Chars" Version="1.2">
                _ => null,
            };
        }

        private static string BuildLatch(LadNetworkDef net, HashSet<string> allInterfaceVars)
        {
            var contacts = net.contacts ?? new List<LadContactDef>();
            var coils = net.coils ?? new List<LadCoilDef>();

            var alloc = new UidAllocator();
            var vars = new VarRegistry(allInterfaceVars);
            var parts = new StringBuilder();
            var wires = new StringBuilder();

            foreach (var c in contacts) vars.TryRegisterSymbol(c.variable, alloc, out _);
            foreach (var c in coils) vars.TryRegisterSymbol(c.variable, alloc, out _);
            var latchVars = LatchVariables(contacts, coils);
            foreach (var v in latchVars) vars.TryRegisterSymbol(v + "_write", alloc, out _);

            AppendSymbolAccesses(parts, vars, interfaceVarsOnly: false, interfaceVars: allInterfaceVars);

            int orUid = 0;
            var contactUids = new List<int>();

            if (contacts.Count > 0)
            {
                orUid = alloc.NextPart();
                parts.AppendLine($"    <Part Name=\"O\" UId=\"{orUid}\">");
                parts.AppendLine($"      <TemplateValue Name=\"Card\" Type=\"Cardinality\">{contacts.Count}</TemplateValue>");
                parts.AppendLine("    </Part>");

                int inputIndex = 1;  // O 门输入从 in1 开始
                foreach (var c in contacts)
                {
                    int cUid = alloc.NextPart();
                    contactUids.Add(cUid);
                    parts.AppendLine(AccessHelper.BuildContactPart(c.name, cUid));

                    // out → O 门 in{N}
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"out\" />");
                    wires.AppendLine($"      <NameCon UId=\"{orUid}\" Name=\"in{inputIndex}\" />");
                    wires.AppendLine("    </Wire>");

                    // operand → 变量
                    if (vars.TryResolve(c.variable, out int vUid))
                    {
                        alloc.NextWire();
                        wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                        wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                        wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                        wires.AppendLine("    </Wire>");
                    }
                    inputIndex++;
                }

                // 共享 Powerrail 线：所有触点 in
                alloc.NextWire();
                wires.Append($"    <Wire UId=\"{alloc.WireUid}\">\n      <Powerrail />");
                foreach (var cu in contactUids)
                    wires.Append($"\n      <NameCon UId=\"{cu}\" Name=\"in\" />");
                wires.Append("\n    </Wire>\n");
            }

            int sourceUid = orUid;
            foreach (var c in coils)
            {
                int cUid = alloc.NextPart();
                parts.AppendLine(AccessHelper.BuildCoilPart(c.name, cUid));

                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (sourceUid != 0) wires.AppendLine($"      <NameCon UId=\"{sourceUid}\" Name=\"out\" />");
                else wires.AppendLine("      <Powerrail />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");

                var key = latchVars.Contains(c.variable) ? c.variable + "_write" : c.variable;
                if (vars.TryResolve(key, out int v2Uid))
                {
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <IdentCon UId=\"{v2Uid}\" />");
                    wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                    wires.AppendLine("    </Wire>");
                }
                sourceUid = cUid;
            }

            return WrapFlgNet(parts, wires);
        }

        // ════════════════════════════════════════════════════════════
        // 3) IEC 系统调用：CTU/TON/CTD/TOF/TP/SR/RS / box 指令
        //   严格对照 D:\mcp服务器\mcp\official_ctu.xml（博途 V19 导出，编译 0 错误）：
        //     - 接口变量（声明在 variables）        → LocalVariable
        //     - 全局 tag（Tag_4 等）                → GlobalVariable
        //     - 数字字面量（PV="10"）               → LiteralConstant（Constant/ConstantType/ConstantValue）
        //     - 背景 DB 指令 Part 含 <Instance Scope="GlobalVariable"> + <TemplateValue Name="..." Type="Type">
        //     - box 指令 Part 不含 Instance，使用 TemplateValue(s) Card/SrcType
        //     - 触点 out / 电源轨 → 触发引脚（CU/CD/IN/S1/EN...）
        //     - Wire 子元素顺序：输入引脚 OpenCon/IdentCon 在前；输出引脚 NameCon 在前
        //     - 未声明引脚（如 Q）不连线（XSD 允许）
        // ════════════════════════════════════════════════════════════
        private static string BuildSysCall(LadNetworkDef net, HashSet<string> allInterfaceVars)
        {
            var sc = net.sysCall!;
            var contacts = net.contacts ?? new List<LadContactDef>();

            // TON/TOF/TP 的 time_type 必须为 Time；CTU/CTD/CTUD 的 value_type 默认 Int
            var scInstUpper = sc.inst.ToUpperInvariant();
            string datatype = sc.datatype ?? string.Empty;
            if (scInstUpper is "TON" or "TOF" or "TP")
                datatype = "Time";
            // CoilTON/CoilTOF/CoilTP 定时器线圈也需要 Time 类型
            if (scInstUpper is "COILTON" or "COILTOF" or "COILTP")
                datatype = "Time";

            var alloc = new UidAllocator();
            var interfaceVars = allInterfaceVars;
            var vars = new VarRegistry(interfaceVars);
            var parts = new StringBuilder();
            var wires = new StringBuilder();

            // 预登记：触点变量（pins 变量不预注册——ProcessSysCall 会为每个引脚创建
            // inline Access；若同时预注册输出 Access 却无 Wire 引用，
            // V17 导入器报 "The reference with the UID 'N' is defined but not used"）
            foreach (var c in contacts) vars.TryRegisterSymbol(c.variable, alloc, out _);
            // ★V17修复★ V17(v4) 背景 DB 型实例变量必须注册到 VarRegistry：
            // Instance UId 引用已输出的变量 Access（否则 V17 报 "reference ... defined but not used"）。
            // V19(v5) 样本（DEV22QI.xml）实例可只含 Component 不注册 Access——按版本分支在 Instance 生成处处理。

            // 1) 变量 Access（Symbol）+ 常量 Access（Constant），按 official_ctu.xml 顺序
            // 收集 Static 区域变量（背景 DB 型实例变量）
            var staticVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (net.variables != null)
                foreach (var v in net.variables)
                    if (string.Equals(v.section, "Static", StringComparison.OrdinalIgnoreCase))
                        staticVars.Add(v.name);
            AppendSymbolAccesses(parts, vars, interfaceVarsOnly: false, interfaceVars: interfaceVars, staticVars: staticVars);
            AppendConstantAccesses(parts, vars, datatype);

            int leftPadUid = 0;

            // 2) 触点串联（前置）
            foreach (var c in contacts)
            {
                int cUid = alloc.NextPart();
                parts.AppendLine(AccessHelper.BuildContactPart(c.name, cUid));

                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                if (leftPadUid == 0) wires.AppendLine("      <Powerrail />");
                else wires.AppendLine($"      <NameCon UId=\"{leftPadUid}\" Name=\"out\" />");
                wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");

                if (vars.TryResolve(c.variable, out int vUid))
                {
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                    wires.AppendLine($"      <NameCon UId=\"{cUid}\" Name=\"operand\" />");
                    wires.AppendLine("    </Wire>");
                }
                leftPadUid = cUid;
            }

            int notUid = 0; // NOT 门 UId（negateOutput 时使用）
            // 3) IEC / box 指令 Part
            int instUid = alloc.NextPart();
            string version = string.IsNullOrEmpty(sc.version) ? "1.0" : sc.version;
            var normalizedInst = NormalizeInstName(sc.inst);
            // DisabledENO 规则：复用 NeedsDisabledEno（覆盖新增指令）
            bool needsDisabledEno = NeedsDisabledEno(sc.inst);
            string disabledEno = needsDisabledEno ? " DisabledENO=\"true\"" : "";
            // Version 属性：背景 DB 型用 1.0；非背景 DB 型也可能需要（如 LIMIT=1.0, BLKMOV=1.1 等）
            string versionAttr = NeedsBackgroundDb(sc.inst) ? " Version=\"1.0\"" : "";
            if (!NeedsBackgroundDb(sc.inst))
            {
                var instVersion = GetInstructionVersion(sc.inst);
                if (!string.IsNullOrEmpty(instVersion))
                    versionAttr = $" Version=\"{instVersion}\"";
            }
            parts.AppendLine($"    <Part Name=\"{AccessHelper.Esc(normalizedInst)}\" UId=\"{instUid}\"{versionAttr}{disabledEno}>");

            // Calc 计算盒子的 Equation 元素（必须在 TemplateValue 之前）
            // 实证：流量累积.xml <Part Name="Calc"><Equation>((IN1/IN2)*(IN3/IN4))+IN5</Equation>
            if (sc.inst.ToUpperInvariant() == "CALC" && !string.IsNullOrEmpty(sc.equation))
            {
                parts.AppendLine($"      <Equation>{AccessHelper.Esc(sc.equation)}</Equation>");
            }

            // CoilTON/CoilTOF/CoilTP 定时器线圈（无背景DB，可在OB中使用）
            // 实证：Fanuc_Robot_PNS_Start.xml <Part Name="CoilTON"><TemplateValue Name="time_type" Type="Type">Time</TemplateValue></Part>
            bool isCoilTimer = sc.inst.ToUpperInvariant() is "COILTON" or "COILTOF" or "COILTP";
            if (isCoilTimer)
            {
                parts.AppendLine($"      <TemplateValue Name=\"time_type\" Type=\"Type\">Time</TemplateValue>");
            }

            if (NeedsBackgroundDb(sc.inst))
            {
                if (string.IsNullOrWhiteSpace(sc.instance))
                    throw new InvalidOperationException($"IEC 指令 {sc.inst} 缺少唯一 instance；请先通过共享 LAD 校验入口");
                string instanceName = sc.instance!;
                // ★V17修复★ v4 导入器要求 Instance UId 引用已输出的变量 Access：
                // 把实例名注册到 VarRegistry（AppendSymbolAccesses 输出对应 Symbol Access），
                // Instance 元素引用同一 UId——否则 V17 报
                // "The reference with the UID 'N' is defined but not used" 并拒绝导入。
                // V19(v5) 样本（DEV22QI.xml）实例可只含 Component 不注册 Access，保持原行为。
                int instanceRefUid;
                if (IsV17FlgNet)
                    vars.TryRegisterSymbol(instanceName, alloc, out instanceRefUid);
                else
                    instanceRefUid = alloc.NextPart();
                // 背景 DB 型 Scope 选择（对照 FB232_LOG.xml）：
                //   CTU/CTD/CTUD/R_TRIG/F_TRIG → GlobalVariable（引用全局 DB，TIA Portal 自动创建）
                //   TON/TOF/TP → LocalVariable（多重实例，声明在 Static 区）
                string instanceScope = string.Equals(sc.instanceScope, "global", StringComparison.OrdinalIgnoreCase)
                    ? "GlobalVariable" : "LocalVariable";
                parts.AppendLine($"      <Instance Scope=\"{instanceScope}\" UId=\"{instanceRefUid}\">");
                // ★修复★ 支持点分实例路径（如 "DB_定时器.投入延时" → 多个 Component）。
                foreach (var comp in AccessHelper.DisplayName(StripLocalRef(instanceName)).Split('.'))
                    parts.AppendLine($"        <Component Name=\"{AccessHelper.Esc(comp)}\" />");
                parts.AppendLine("      </Instance>");
                var tvName = GetTemplateValueName(sc.inst);
                if (tvName != null)
                    parts.AppendLine($"      <TemplateValue Name=\"{tvName}\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
            }
            else if (IsBoxInstruction(sc.inst))
            {
                // destType 优先从 sc.destType 获取（Normalize/Scale_X 需要）
                // 其次从输出变量类型推断（CONVERT/ROUND 等）
                string destType = sc.destType ?? "";
                if (string.IsNullOrEmpty(destType) && sc.pins != null && net.variables != null)
                {
                    foreach (var kv in sc.pins)
                    {
                        if (IsIecOutputPin(sc.inst, kv.Key) && !string.IsNullOrEmpty(kv.Value))
                        {
                            foreach (var v in net.variables)
                            {
                                if (string.Equals(v.name, kv.Value, StringComparison.OrdinalIgnoreCase))
                                {
                                    destType = v.datatype ?? "";
                                    break;
                                }
                            }
                        }
                    }
                }
                int calcCard2 = CountCalcCard(sc.inst, sc.pins);
                AppendBoxTemplateValues(parts, sc.inst, datatype, destType, calcCard2);
            }

            parts.AppendLine("    </Part>");

            // 4) 触点逻辑流与 pins 中显式触发条件共同决定 CU/CD/IN/CLK/EN。
            // 显式触发条件会串联到已有 contacts 后面，避免在有 contacts 时被忽略。
            string triggerPin = GetIecTriggerPin(sc.inst);
            string? explicitTrigger = FindPinValue(sc.pins, triggerPin);
            int triggerSourceUid = contacts.Count > 0 ? leftPadUid : 0;
            ConnectTriggerCondition(parts, wires, vars, alloc, triggerSourceUid, instUid, triggerPin,
                explicitTrigger, allInterfaceVars, null);

            // 4.5) negateOutput: 先创建 NOT Part（需要在引脚连线之前，因为引脚需要引用 notUid）
            if (sc.negateOutput && !string.Equals(sc.inst, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                notUid = alloc.NextPart();
                parts.AppendLine($"    <Part Name=\"Not\" UId=\"{notUid}\">");
                parts.AppendLine("    </Part>");
                // SR/RS 的输出引脚为 q，其他指令为 out
                string negateOutputPin = sc.inst.ToUpperInvariant() is "SR" or "RS" ? "q" : "out";
                alloc.NextWire();
                wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{negateOutputPin}\" />");
                wires.AppendLine($"      <NameCon UId=\"{notUid}\" Name=\"in\" />");
                wires.AppendLine("    </Wire>");
            }

            // 5) 引脚连线（仅用户声明的）：
            //      空串     → OpenCon（XSD: OpenCon_T）
            //      变量/常量 → IdentCon（XSD: IdentCon_T）
            //      触发引脚  → 已被 contact/powerrail/显式值占用，跳过
            //      未声明引脚（如 Q）→ 不连线（XSD 允许）
            //    Wire 子元素顺序：输入引脚 OpenCon/IdentCon 在前；输出引脚 NameCon 在前
            if (sc.pins != null)
            {
                foreach (var kv in sc.pins)
                {
                    if (string.Equals(kv.Key, triggerPin, StringComparison.OrdinalIgnoreCase)) continue;
                    // NOT 的 in 引脚已作为触发引脚处理，跳过
                    if (sc.inst.ToUpperInvariant() == "NOT" &&
                        string.Equals(kv.Key, "in", StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool isOutputPin = IsIecOutputPin(sc.inst, kv.Key);

                    if (string.IsNullOrEmpty(kv.Value))
                    {
                        int openUid = alloc.NextPart();
                        alloc.NextWire();
                        wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                        if (isOutputPin)
                        {
                            wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{AccessHelper.Esc(kv.Key)}\" />");
                            wires.AppendLine($"      <OpenCon UId=\"{openUid}\" />");
                        }
                        else
                        {
                            wires.AppendLine($"      <OpenCon UId=\"{openUid}\" />");
                            wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{AccessHelper.Esc(kv.Key)}\" />");
                        }
                        wires.AppendLine("    </Wire>");
                        continue;
                    }

                    if (!vars.TryResolve(kv.Value, out int vUid))
                    {
                        // ★修复★ 动态注册（与 ProcessBox 一致）：sysCall 引脚不预注册，
                        // 未注册的值按类型创建 inline Access（常量/符号），否则引脚被静默跳过
                        // 导致 TON 的 PT/Q 无 Access 无 Wire。
                        if (IsNumericLiteral(kv.Value))
                        {
                            vUid = alloc.NextPart();
                            parts.Append(BuildConstantAccess(vUid, kv.Value));
                        }
                        else
                        {
                            vUid = alloc.NextPart();
                            var display = StripLocalRef(AccessHelper.DisplayName(kv.Value));
                            string baseName = StripLocalRef(AccessHelper.BaseVarName(kv.Value));
                            string scope = (interfaceVars != null && interfaceVars.Contains(baseName))
                                ? "LocalVariable" : "GlobalVariable";
                            parts.Append(AccessHelper.BuildVariableAccess(vUid, display, scope));
                        }
                    }
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    if (isOutputPin)
                    {
                        // negateOutput: 输出引脚从 NOT 门引出
                        int outSrcUid = sc.negateOutput && notUid > 0 ? notUid : instUid;
                        wires.AppendLine($"      <NameCon UId=\"{outSrcUid}\" Name=\"{AccessHelper.Esc(kv.Key)}\" />");
                        wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                    }
                    else
                    {
                        wires.AppendLine($"      <IdentCon UId=\"{vUid}\" />");
                        wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{AccessHelper.Esc(kv.Key)}\" />");
                    }
                    wires.AppendLine("    </Wire>");
                }
            }



            // 6) 自动补充未声明的输出引脚：
            //    TIA Portal 要求所有输出引脚必须有连线，即使用户没有声明。
            //    背景 DB 指令：TON/TOF/TP 的 ET，CTU/CTD/CTUD 的 CV/Q 等。
            //    box 指令：ENO 必须连 OpenCon。
            var requiredOutputPins = GetRequiredOutputPins(sc.inst);
            if (requiredOutputPins != null && sc.pins != null)
            {
                foreach (var reqPin in requiredOutputPins)
                {
                    // 跳过触发引脚和用户已声明的引脚
                    if (string.Equals(reqPin, triggerPin, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sc.pins.ContainsKey(reqPin)) continue;

                    int openUid = alloc.NextPart();
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{AccessHelper.Esc(reqPin)}\" />");
                    wires.AppendLine($"      <OpenCon UId=\"{openUid}\" />");
                    wires.AppendLine("    </Wire>");
                }
            }

            // 7) 自动补充未声明的必填输入引脚（如 CTU 的 R，CTD 的 LD，CTUD 的 R/LD）
            //    这些引脚即使用户不声明也必须有连线（OpenCon 悬空）
            var requiredInputPins = GetRequiredInputPins(sc.inst);
            if (requiredInputPins != null && sc.pins != null)
            {
                foreach (var reqPin in requiredInputPins)
                {
                    if (string.Equals(reqPin, triggerPin, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sc.pins.ContainsKey(reqPin)) continue;

                    int openUid = alloc.NextPart();
                    alloc.NextWire();
                    wires.AppendLine($"    <Wire UId=\"{alloc.WireUid}\">");
                    wires.AppendLine($"      <OpenCon UId=\"{openUid}\" />");
                    wires.AppendLine($"      <NameCon UId=\"{instUid}\" Name=\"{AccessHelper.Esc(reqPin)}\" />");
                    wires.AppendLine("    </Wire>");
                }
            }

            return WrapFlgNet(parts, wires);
        }

        // ────────────────────────────────────────────────────────────
        // Access 输出辅助
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 输出所有已登记的符号 Access（Symbol/Component）。
        /// Scope 规则（与 official_ctu.xml 一致）：
        ///   接口变量（声明在 variables）→ LocalVariable
        ///   其他（全局 tag / 绝对值地址 / 中文符号名）→ GlobalVariable
        /// </summary>
        /// <summary>去掉 TIA 局部变量前缀 '#'（XML 的 Symbol/Component 名不带 #；接口变量名也不带 #）。</summary>
        private static string StripLocalRef(string? name)
            => !string.IsNullOrEmpty(name) && name!.StartsWith("#", StringComparison.Ordinal)
                ? name!.Substring(1)
                : name ?? "";

        private static void AppendSymbolAccesses(StringBuilder parts, VarRegistry vars,
            bool interfaceVarsOnly, HashSet<string>? interfaceVars = null, HashSet<string>? staticVars = null)
        {
            foreach (var kv in vars.Symbols)
            {
                var display = AccessHelper.DisplayName(kv.Key);
                // ★V17修复★ 变量名去 '#' 前缀：用户输入 #temp（局部变量约定）时，
                // XML Symbol Component 名必须是 "temp"（TIA 接口变量不带 #），
                // 否则编译报 "Tag '#temp' not defined"。
                var clean = StripLocalRef(display);
                // 严格对照 official_ctu.xml：Tag_4 未在接口声明 → GlobalVariable。
                // 不再调用 ScopeFor（它会误判 Tag_4 为 LocalVariable）。
                // 背景 DB 型实例变量在 Static 区域声明 → LocalVariable
                string scope;
                if (interfaceVars != null && (interfaceVars.Contains(clean) || interfaceVars.Contains(display)))
                    scope = "LocalVariable";
                else if (staticVars != null && (staticVars.Contains(clean) || staticVars.Contains(display)))
                    scope = "LocalVariable";
                else
                    scope = "GlobalVariable";
                parts.Append(AccessHelper.BuildVariableAccess(kv.Value, clean, scope));
            }
        }

        /// <summary>输出所有已登记的常量 Access（LiteralConstant）。
        /// T# 开头的值强制使用 Time 类型（TypedConstant），不受 constantType 参数影响。</summary>
        private static void AppendConstantAccesses(StringBuilder parts, VarRegistry vars,
            string constantType)
        {
            foreach (var kv in vars.Constants)
            {
                // T# 开头的常量强制为 Time 类型
                string effectiveType = kv.Key.StartsWith("T#", StringComparison.OrdinalIgnoreCase)
                                            || kv.Key.StartsWith("TIME#", StringComparison.OrdinalIgnoreCase)
                    ? "Time"
                    : (kv.Key.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                       || kv.Key.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? "Bool" : constantType);
                parts.Append(AccessHelper.BuildConstantAccess(kv.Value, effectiveType, kv.Key));
            }
        }

        // ────────────────────────────────────────────────────────────
        // IEC / box 指令辅助表
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 将用户输入的指令名称规范化为 TIA Portal 期望的 Part Name。
        /// </summary>
        private static string NormalizeInstName(string inst)
        {
            var upper = inst.ToUpperInvariant();
            if (NeedsBackgroundDb(upper))
                return upper;
            // TIA Portal V19 LAD box Part Name: 首字母大写，无 Box 后缀
            // 对照 D:\mcp服务器\mcp\ref\停止.xml: <Part Name="Move" ...>
            // 对照 D:\mcp服务器\mcp\ref\自动.xml: <Part Name="Eq" ...>
            return upper switch
            {
                // 算术指令
                "ADD" => "Add",
                "SUB" => "Sub",
                "MUL" => "Mul",
                "DIV" => "Div",
                "MOD" => "Mod",
                // 比较指令
                "EQ" or "CMP" => "Eq",
                "NE" => "Ne",
                "GT" => "Gt",
                "LT" => "Lt",
                "GE" => "Ge",
                "LE" => "Le",
                // 范围判断指令
                "INRANGE" => "InRange",
                "OUTRANGE" => "OutRange",
                // 转换指令
                "CONV" or "CONVERT" => "Convert",
                "ROUND" => "Round",
                "TRUNC" => "Trunc",
                "CEIL" => "Ceil",
                "FLOOR" => "Floor",
                // 逻辑指令
                "AND" => "And",
                "OR" => "Or",
                "XOR" => "Xor",
                "NOT" => "Not",
                // 移位指令
                "SHL" => "Shl",
                "SHR" => "Shr",
                // 移位/循环指令：V17 实测 'Rol' 与 'ROL' 均报 "instruction cannot be found"，
                // 本机样本库无移位导出可校准。保留 Rol/Ror 为最接近惯例的猜测，
                // 待取得真实导出（在博途中手插 ROL 后 export）再修正。
                "ROL" => "Rol",
                "ROR" => "Ror",
                // 数学函数
                "ABS" => "Abs",
                "SQRT" => "Sqrt",
                "SQR" => "Sqr",
                "LN" => "Ln",
                "EXP" => "Exp",
                "SIN" => "Sin",
                "COS" => "Cos",
                "TAN" => "Tan",
                "ASIN" => "Asin",
                "ACOS" => "Acos",
                "ATAN" => "Atan",
                // 双稳态指令
                "SR" => "Sr",
                "RS" => "Rs",
                // 定时器线圈（无背景DB，可在OB中使用）
                // 实证：Fanuc_Robot_PNS_Start.xml <Part Name="CoilTON" UId="38">
                "COILTON" => "CoilTON",
                "COILTOF" => "CoilTOF",
                "COILTP"  => "CoilTP",
                // ── 新增指令名称映射（实证来源：示例程序_V19 / ref2 导出 XML）──
                "INC"          => "Inc",
                "SEL"          => "SEL",
                "LIMIT"        => "LIMIT",
                "NORMALIZE" or "NORM_X" => "Normalize",
                "SCALE_X"      => "Scale_X",
                "SWAP"         => "Swap",
                "INV"          => "Inv",
                "CALC"         => "Calc",
                "CONCAT"       => "CONCAT",
                "S_CONV"       => "S_CONV",
                "T_CONV"       => "T_CONV",
                "UPPER_BOUND"  => "Upper_Bound",
                "LOWER_BOUND"  => "Lower_Bound",
                "BLKMOV"       => "BLKMOV",
                "FILL"         => "FILL",
                "MOVEBLOCKI"   => "MoveBlockI",
                "FILLBLOCKI"   => "FillBlockI",
                "VAL_STRG"     => "VAL_STRG",
                "REPLACE"      => "REPLACE",
                "DELETE"       => "DELETE",
                "RD_LOC_T"     => "RD_LOC_T",
                "RD_SYS_T"     => "RD_SYS_T",
                "WR_SYS_T"     => "WR_SYS_T",
                // ── 1号厂项目新增指令名称映射 ──
                "S_MOVE"         => "S_Move",
                "CHARS_TO_STRG"  => "Chars_TO_Strg",
                "STRG_TO_CHARS"  => "Strg_TO_Chars",
                "RBITFIELD"      => "RBitfield",
                "RESET_IECTIMER_COIL" or "RESETIECTIMERCOIL" => "ResetIECTimerCoil",
                _ => inst,
            };
        }

        /// <summary>
        /// 判断指令是否需要背景 DB 实例。
        /// </summary>
        private static bool NeedsBackgroundDb(string inst)
        {
            return LadInstructionCatalog.IsBackground(inst);
        }

        /// <summary>
        /// 判断是否为 box 型指令（无背景 DB，使用 EN/ENO 及 TemplateValues）。
        /// </summary>
        private static bool IsBoxInstruction(string inst)
        {
            return BoxInstructions.Contains(inst);
        }

        private static bool IsComparisonBox(string inst)
        {
            var upper = inst.ToUpperInvariant();
            return upper is "EQ" or "NE" or "GT" or "LT" or "GE" or "LE";
        }

        /// <summary>
        /// box 指令的引脚顺序。
        /// </summary>
        private static List<string> GetBoxPins(string inst)
        {
            // 引脚名称使用小写（对照 停止.xml: en/in/out1）
            // MOVE 输出引脚为 out1（不是 out）
            return inst.ToUpperInvariant() switch
            {
                // 算术指令: 输出引脚为 out
                "ADD" or "SUB" or "MUL" or "DIV" or "MOD"
                    => new List<string> { "en", "in1", "in2", "out", "eno" },
                // 比较指令: 输出引脚为 out（对照 比较选择.xml）
                "EQ" or "NE" or "GT" or "LT" or "GE" or "LE"
                    => new List<string> { "en", "in1", "in2", "out", "eno" },
                // 范围判断指令: pre/in/min/max/out
                "INRANGE" or "OUTRANGE"
                    => new List<string> { "pre", "in", "min", "max", "out" },
                // NOT 指令: in/out（无 en/eno）
                "NOT"
                    => new List<string> { "in", "out" },
                // 字逻辑指令: 输出引脚为 out
                "AND" or "OR" or "XOR"
                    => new List<string> { "en", "in1", "in2", "out", "eno" },
                // 移位指令: 输出引脚为 out
                "SHL" or "SHR"
                    => new List<string> { "en", "in1", "in2", "out", "eno" },
                "ROL" or "ROR"
                    => new List<string> { "en", "in", "n", "out", "eno" },
                // 转换指令: 输出引脚为 out（对照 百分比计算_结果为INT_.xml）
                "CONVERT" or "ROUND" or "TRUNC" or "CEIL" or "FLOOR"
                    => new List<string> { "en", "in", "out", "eno" },
                // 数学函数: 输出引脚为 out
                "ABS" or "SQRT" or "SQR" or "LN" or "EXP"
                or "SIN" or "COS" or "TAN" or "ASIN" or "ACOS" or "ATAN"
                    => new List<string> { "en", "in", "out", "eno" },
                // 双稳态指令: 无 en/eno
                "SR"
                    => new List<string> { "s", "r1", "operand", "q" },
                "RS"
                    => new List<string> { "r", "s1", "operand", "q" },
                // ── 新增指令引脚定义（实证来源：示例程序_V19 / ref2 导出 XML）──
                // Inc 自增1: en/operand（无 out，operand 既是输入也是输出）
                "INC"
                    => new List<string> { "en", "operand" },
                // SEL 二选一: en/G(Bool选择)/IN0/IN1/OUT
                "SEL"
                    => new List<string> { "en", "G", "IN0", "IN1", "OUT" },
                // LIMIT 限幅: en/MN/IN/MX/OUT（实证：模拟量输出_阀门_控制.xml 引脚为 MN/MX 非 MIN/MAX）
                "LIMIT"
                    => new List<string> { "en", "MN", "IN", "MX", "OUT" },
                // Normalize/Scale_X 标准化/缩放: en/min/value/max/out/eno
                "NORMALIZE" or "SCALE_X"
                    => new List<string> { "en", "min", "value", "max", "out", "eno" },
                // Swap 字节交换: en/in/out/eno
                "SWAP"
                    => new List<string> { "en", "in", "out", "eno" },
                // Inv 取反: en/in/out/eno
                "INV"
                    => new List<string> { "en", "in", "out", "eno" },
                // Calc 计算盒子: en/in1..in5/out（Card 决定输入数，实证 Card=2~5）
                "CALC"
                    => new List<string> { "en", "in1", "in2", "in3", "in4", "in5", "out" },
                // CONCAT 字符串连接: en/in1..inN/out（Card 决定输入数）
                "CONCAT"
                    => new List<string> { "en", "in1", "out" },
                // S_CONV/T_CONV 转换: en/in/out
                "S_CONV" or "T_CONV"
                    => new List<string> { "en", "in", "out" },
                // Upper_Bound/Lower_Bound 数组边界: en/ARR/DIM/OUT
                "UPPER_BOUND" or "LOWER_BOUND"
                    => new List<string> { "en", "ARR", "DIM", "OUT" },
                // BLKMOV/FILL 块操作: en/SRC/DEST
                "BLKMOV" or "FILL"
                    => new List<string> { "en", "SRC", "DEST" },
                // MOVEBLOCKI/FILLBLOCKI 变长块操作: en/in/count/out/eno（实证：1号厂八桶分样.xml/Main.xml）
                "MOVEBLOCKI" or "FILLBLOCKI"
                    => new List<string> { "en", "in", "count", "out", "eno" },
                // S_Move 字符串移动: en/in/out/eno（实证：1号厂编码字符串转换.xml）
                "S_MOVE"
                    => new List<string> { "en", "in", "out", "eno" },
                // Chars_TO_Strg 字符数组转字符串: en/Strg/Cnt/Chars/pChars/eno（实证：1号厂编码字符串转换.xml）
                "CHARS_TO_STRG"
                    => new List<string> { "en", "Strg", "Cnt", "Chars", "pChars", "eno" },
                // Strg_TO_Chars 字符串转字符数组: en/Strg/pChars/Chars/Cnt/eno（实证：1号厂Main.xml）
                "STRG_TO_CHARS"
                    => new List<string> { "en", "Strg", "pChars", "Chars", "Cnt", "eno" },
                // RBitfield 位字段复位: en/n/operand（实证：1号厂自动制样动作程序.xml Wire 56-58）
                // ★ 特殊：无 out/eno 引脚，逻辑流通过 Wire 扇出绕过（NameCon UId=39 en 与下游 RCoil.in 同一 Wire）
                "RBITFIELD"
                    => new List<string> { "en", "n", "operand" },
                // VAL_STRG 数值转字符串: en/in1..inN/OUT
                "VAL_STRG"
                    => new List<string> { "en", "in1", "OUT" },
                // REPLACE 字符串替换: en/IN1/IN2/IN3/OUT
                "REPLACE"
                    => new List<string> { "en", "IN1", "IN2", "IN3", "OUT" },
                // DELETE 字符串删除: en/IN1/IN2/OUT
                "DELETE"
                    => new List<string> { "en", "IN1", "IN2", "OUT" },
                // RD_LOC_T/RD_SYS_T 读时间: en/OUT
                "RD_LOC_T" or "RD_SYS_T"
                    => new List<string> { "en", "OUT" },
                // WR_SYS_T 写时间: en/IN
                "WR_SYS_T"
                    => new List<string> { "en", "IN" },

                _ => new List<string> { "en", "in", "out1", "eno" },
            };
        }

        /// <summary>
        /// 获取 IEC 指令必须连线的输出引脚列表。
        /// TIA Portal 要求这些引脚即使未声明也必须有连线（OpenCon）。
        /// </summary>
        private static string[]? GetRequiredOutputPins(string inst)
        {
            // 双稳态指令：q 引脚必须连线
            var upper = inst.ToUpperInvariant();
            if (upper is "SR" or "RS")
                return new[] { "q" };

            // box 指令使用 DisabledENO=true 或由用户声明 out1，不需要自动补充
            if (IsBoxInstruction(inst))
                return null;

            return inst.ToUpperInvariant() switch
            {
                "TON"    => new[] { "Q", "ET" },
                "TOF"    => new[] { "Q", "ET" },
                "TP"     => new[] { "Q", "ET" },
                "CTU"    => new[] { "Q", "CV" },
                "CTD"    => new[] { "Q", "CV" },
                "CTUD"   => new[] { "QU", "QD", "CV" },
                "R_TRIG" => new[] { "Q" },
                "F_TRIG" => new[] { "Q" },
                _        => null,
            };
        }

        /// <summary>
        /// 获取 IEC 指令必须连线的输入引脚列表（用户未声明时自动 OpenCon 悬空）。
        /// CTU 的 R（复位）、CTD 的 LD（载入）、CTUD 的 R/LD 是必填引脚。
        /// </summary>
        private static string[]? GetRequiredInputPins(string inst)
        {
            return inst.ToUpperInvariant() switch
            {
                "CTU"    => new[] { "R" },
                "CTD"    => new[] { "LD" },
                "CTUD"   => new[] { "R", "LD" },
                "TONR"   => new[] { "R" },  // 保持型定时器的复位引脚同样必须连线
                _        => null,
            };
        }

        /// <summary>
        /// IEC 指令的"触发"引脚名：触点 out / 电源轨 必须连到该引脚，
        /// 用户在 sysCall.pins 里写同名键将被忽略。
        /// box 指令的触发引脚为 EN。
        /// </summary>
        private static string GetIecTriggerPin(string inst)
        {
            var catalogTrigger = LadInstructionCatalog.GetTriggerPin(inst);
            if (!string.IsNullOrEmpty(catalogTrigger)) return catalogTrigger;
            var upper = inst.ToUpperInvariant();
            // 比较指令和范围判断指令使用 pre 引脚（对照 比较选择.xml / 交通灯项目）
            if (upper is "EQ" or "NE" or "GT" or "LT" or "GE" or "LE"
                or "INRANGE" or "OUTRANGE")
                return "pre";
            // NOT 指令使用 in 引脚
            if (upper == "NOT")
                return "in";
            // 双稳态指令：SR 触发引脚为 s（set dominant），RS 触发引脚为 r（reset dominant）
            if (upper == "SR")
                return "s";
            if (upper == "RS")
                return "r";
            // Inc/SEL/LIMIT/Normalize/Scale_X/Swap/Inv/Calc/CONCAT/S_CONV/T_CONV/Upper_Bound/Lower_Bound/
            // BLKMOV/FILL/MoveBlockI/FillBlockI/VAL_STRG/REPLACE/DELETE/RD_LOC_T/RD_SYS_T/WR_SYS_T
            // 这些 box 指令的触发引脚均为 en（与 IsBoxInstruction 路径一致）
            if (IsBoxInstruction(inst))
                return "en";

            return upper switch
            {
                "CTU"    => "CU",
                "CTD"    => "CD",
                "CTUD"   => "CU",   // 主触发用 CU；CD 走用户 pins 或默认 OpenCon
                "TON"    => "IN",
                "TOF"    => "IN",
                "TP"     => "IN",
                "TONR"   => "IN",   // 保持型定时器，同 TON
                "COILTON" => "in",  // 定时器线圈：in 连逻辑流，value 是 PT，operand 是输出位
                "COILTOF" => "in",
                "COILTP"  => "in",
                "RESETIECTIMERCOIL" or "RESET_IECTIMER_COIL" => "in",  // IEC定时器复位线圈：in/operand
                "R_TRIG" => "en",   // R_TRIG: en 连 Powerrail（使能），CLK 是信号输入（pins 指定），Q 是输出
                "F_TRIG" => "en",   // F_TRIG 同理
                _        => "IN",   // 通用 fallback
            };
        }

        /// <summary>
        /// 获取元素的逻辑流输出引脚名。
        /// 触点/线圈 → "out"
        /// 比较指令/InRange/OutRange → "out"（Bool 结果即逻辑流）
        /// 背景DB型指令（TON/TOF/TP/CTU等）→ "Q"（Q 信号即逻辑流）
        /// 双稳态指令（SR/RS）→ "q"
        /// NOT → "out"
        /// 其他 box 指令 → "eno"（ENO 是逻辑流输出）
        /// FB 调用 → "ENO"
        /// </summary>
        private static string GetLogicFlowOutputPin(RungElement elem)
        {
            if (elem.contact != null) return "out";
            if (elem.coil != null) return "out";
            if (elem.box != null)
            {
                var upper = elem.box.box.ToUpperInvariant();
                // 比较指令/范围判断的 out 是 Bool 结果，即逻辑流输出
                if (upper is "EQ" or "NE" or "GT" or "LT" or "GE" or "LE"
                    or "INRANGE" or "OUTRANGE")
                    return "out";
                // NOT 的 out 是逻辑流输出
                if (upper is "NOT")
                    return "out";
                // 背景 DB 型指令（TON/TOF/CTU 等）的 Q 信号是逻辑流输出
                if (NeedsBackgroundDb(elem.box.box))
                    return "Q";
                // 双稳态指令（SR/RS）的 q 是逻辑流输出
                if (upper is "SR" or "RS")
                    return "q";
                // CoilTON/CoilTOF/CoilTP 定时器线圈的 out 是逻辑流输出
                // 实证：Fanuc_Robot_PNS_Start.xml Wire 55: CoilTON.out → Contact.in
                if (upper is "COILTON" or "COILTOF" or "COILTP")
                    return "out";
                // ResetIECTimerCoil 的 out 是逻辑流输出（非 eno）
                // 实证：1号厂报警程序.xml Wire 72: ResetIECTimerCoil.out → Contact.in
                if (upper is "RESETIECTIMERCOIL" or "RESET_IECTIMER_COIL")
                    return "out";
                // 其他 box 指令的 eno 是逻辑流输出
                return "eno";
            }
            if (elem.call != null) return "eno";  // ★ 实证：小写 eno
            return "out";
        }

        /// <summary>
        /// 获取 IEC 指令的 TemplateValue Name 属性值（仅背景 DB 指令使用）。
        /// TON/TOF/TP → "time_type"，CTU/CTD/CTUD → "value_type"，其他 → null
        /// </summary>
        private static string? GetTemplateValueName(string inst)
        {
            return inst.ToUpperInvariant() switch
            {
                "TON"    => "time_type",
                "TOF"    => "time_type",
                "TP"     => "time_type",
                "TONR"   => "time_type",  // 保持型定时器
                "CTU"    => "value_type",
                "CTD"    => "value_type",
                "CTUD"   => "value_type",
                _        => null,  // R_TRIG, F_TRIG, SR, RS etc. have no TemplateValue
            };
        }

        /// <summary>
        /// 为 box 指令追加 TemplateValue 子元素。
        /// 数学/字逻辑/移位指令需要 Card=2 及 SrcType；其它 box 指令只需 SrcType。
        /// </summary>
        private static void AppendBoxTemplateValues(StringBuilder sb, string inst, string datatype, string destType = "", int card = 2)
        {
            string upper = inst.ToUpperInvariant();
            // MOVE 指令：使用 Card=1，不使用 SrcType（对照 停止.xml）
            if (upper == "MOVE")
            {
                sb.AppendLine("      <TemplateValue Name=\"Card\" Type=\"Cardinality\">1</TemplateValue>");
                return;
            }
            // === 算术指令 ===（对照 示例程序_V19）
            // ADD: Card=2 + SrcType（★V17校准★ v4 不支持 AutomaticTyped，必须显式 SrcType）
            if (upper == "ADD")
            {
                sb.AppendLine("      <TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue>");
                if (IsV17FlgNet)
                    sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                else
                    sb.AppendLine("      <AutomaticTyped Name=\"SrcType\" />");
                return;
            }
            // SUB: SrcType（★V17校准★ 同上）
            if (upper == "SUB")
            {
                if (IsV17FlgNet)
                    sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                else
                    sb.AppendLine("      <AutomaticTyped Name=\"SrcType\" />");
                return;
            }
            // MUL: Card=2 + 显式 SrcType
            if (upper == "MUL")
            {
                sb.AppendLine("      <TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue>");
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // DIV/MOD: 显式 SrcType
            if (upper is "DIV" or "MOD")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // === 比较指令 ===
            if (upper is "EQ" or "NE" or "GT" or "LT" or "GE" or "LE")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // === 范围判断指令 === SrcType（对照交通灯项目 Main_traffic.xml）
            if (upper is "INRANGE" or "OUTRANGE")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // === NOT 指令 === 无 TemplateValue
            if (upper is "NOT")
            {
                return;
            }
            // === 转换指令 === SrcType + 显式 DestType（对照 百分比计算_结果为INT_.xml）
            if (upper is "CONVERT" or "ROUND" or "TRUNC" or "CEIL" or "FLOOR")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                if (!string.IsNullOrEmpty(destType))
                    sb.AppendLine($"      <TemplateValue Name=\"DestType\" Type=\"Type\">{AccessHelper.Esc(destType)}</TemplateValue>");
                else if (IsV17FlgNet)
                    // ★V17校准★ v4 不支持 AutomaticTyped：destType 缺失时用 datatype 兜底
                    sb.AppendLine($"      <TemplateValue Name=\"DestType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                else
                    sb.AppendLine("      <AutomaticTyped Name=\"DestType\" />");
                return;
            }
            // === 字逻辑指令 === Card=2 + SrcType
            if (upper is "AND" or "OR" or "XOR")
            {
                sb.AppendLine("      <TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue>");
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // === 移位指令 === SrcType
            if (upper is "SHL" or "SHR" or "ROL" or "ROR")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // === 数学函数 === SrcType
            // SQR/SQRT/SIN/COS/TAN/ASIN/ACOS/ATAN/LN/EXP 只支持 Real
            // ABS 支持多种类型
            if (upper is "SQR" or "SQRT"
                or "SIN" or "COS" or "TAN" or "ASIN" or "ACOS" or "ATAN"
                or "LN" or "EXP")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">Real</TemplateValue>");
                return;
            }
            if (upper is "ABS")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // NOT / SR / RS: 无模板值
            if (upper is "NOT" or "SR" or "RS")
            {
                return;
            }
            // ── 新增指令的模板值（实证来源：示例程序_V19 / ref2 导出 XML）──
            // Inc: DestType（运行时间累计.xml: <TemplateValue Name="DestType" Type="Type">Int</TemplateValue>）
            if (upper == "INC")
            {
                sb.AppendLine($"      <TemplateValue Name=\"DestType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // SEL: value_type（流量累积.xml: <TemplateValue Name="value_type" Type="Type">LReal</TemplateValue>）
            if (upper == "SEL")
            {
                sb.AppendLine($"      <TemplateValue Name=\"value_type\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // LIMIT: value_type（模拟量输出_阀门_控制.xml: <TemplateValue Name="value_type" Type="Type">Real</TemplateValue>）
            if (upper == "LIMIT")
            {
                sb.AppendLine($"      <TemplateValue Name=\"value_type\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // Normalize/Scale_X: SrcType + DestType（Assistant_Axis.xml）
            // DestType 不能用 AutomaticTyped，必须显式指定；destType 为空时用 datatype 兜底
            if (upper is "NORMALIZE" or "NORM_X" or "SCALE_X")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                string effectiveDestType = !string.IsNullOrEmpty(destType) ? destType : datatype;
                sb.AppendLine($"      <TemplateValue Name=\"DestType\" Type=\"Type\">{AccessHelper.Esc(effectiveDestType)}</TemplateValue>");
                return;
            }
            // Swap/Inv: SrcType（FB158/FB_MOVIFIT_Classic.xml）
            if (upper is "SWAP" or "INV")
            {
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // Calc 计算盒子: Equation 由调用方通过 box.equation 传入，这里只生成 Card + SrcType
            // 注意：Equation 元素必须在 TemplateValue 之前（XSD: Part_T = (Equation|Instance)?, TemplateValue*）
            // 这里不生成 Equation（由 ProcessBox/BuildSysCall 在 Part 开头插入），只生成 Card + SrcType
            if (upper == "CALC")
            {
                sb.AppendLine($"      <TemplateValue Name=\"Card\" Type=\"Cardinality\">{card}</TemplateValue>");
                sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
                return;
            }
            // CONCAT: card + str_type（FB53_DTL_TO_STRING.xml: Card=2, str_type=String）
            if (upper == "CONCAT")
            {
                sb.AppendLine($"      <TemplateValue Name=\"card\" Type=\"Cardinality\">{card}</TemplateValue>");
                sb.AppendLine($"      <TemplateValue Name=\"str_type\" Type=\"Type\">String</TemplateValue>");
                return;
            }
            // S_CONV: src_type + dest_type（10进制转ASCLL.xml）
            if (upper == "S_CONV")
            {
                sb.AppendLine($"      <TemplateValue Name=\"src_type\" Type=\"Type\">String</TemplateValue>");
                sb.AppendLine($"      <TemplateValue Name=\"dest_type\" Type=\"Type\">Char</TemplateValue>");
                return;
            }
            // T_CONV: src_type + dest_type（FB3034_设定闹钟.xml: Date_And_Time→Time_Of_Day）
            if (upper == "T_CONV")
            {
                sb.AppendLine($"      <TemplateValue Name=\"src_type\" Type=\"Type\">Date_And_Time</TemplateValue>");
                sb.AppendLine($"      <TemplateValue Name=\"dest_type\" Type=\"Type\">Time_Of_Day</TemplateValue>");
                return;
            }
            // Upper_Bound/Lower_Bound: src_type=Variant（获取数组中的最大值和最小值.xml）
            if (upper is "UPPER_BOUND" or "LOWER_BOUND")
            {
                sb.AppendLine("      <TemplateValue Name=\"src_type\" Type=\"Type\">Variant</TemplateValue>");
                return;
            }
            // BLKMOV: blk_type=Variant（FB151_3D视觉控制.xml）
            if (upper == "BLKMOV")
            {
                sb.AppendLine("      <TemplateValue Name=\"blk_type\" Type=\"Type\">Variant</TemplateValue>");
                return;
            }
            // FILL: ptr_type=Variant（FB_MOVIFIT_Classic.xml）
            if (upper == "FILL")
            {
                sb.AppendLine("      <TemplateValue Name=\"ptr_type\" Type=\"Type\">Variant</TemplateValue>");
                return;
            }
            // MoveBlockI/FillBlockI: 无 TemplateValue（ROBOT_KUKA_FB.xml: <Part Name="FillBlockI" UId="76" DisabledENO="true" />）
            if (upper is "MOVEBLOCKI" or "FILLBLOCKI")
            {
                return;
            }
            // Chars_TO_Strg: ptr_type=Variant + str_type=String（实证：1号厂编码字符串转换.xml）
            // ★ 注意：Chars_TO_Strg 用 str_type，Strg_TO_Chars 用 strg_type（不同！）
            if (upper == "CHARS_TO_STRG")
            {
                sb.AppendLine("      <TemplateValue Name=\"ptr_type\" Type=\"Type\">Variant</TemplateValue>");
                sb.AppendLine("      <TemplateValue Name=\"str_type\" Type=\"Type\">String</TemplateValue>");
                return;
            }
            // Strg_TO_Chars: ptr_type=Variant + strg_type=String（实证：1号厂Main.xml）
            if (upper == "STRG_TO_CHARS")
            {
                sb.AppendLine("      <TemplateValue Name=\"ptr_type\" Type=\"Type\">Variant</TemplateValue>");
                sb.AppendLine("      <TemplateValue Name=\"strg_type\" Type=\"Type\">String</TemplateValue>");
                return;
            }
            // S_Move: 无 TemplateValue（实证：1号厂编码字符串转换.xml: <Part Name="S_Move" DisabledENO="true" />）
            if (upper == "S_MOVE")
            {
                return;
            }
            // RBitfield: 无 TemplateValue（实证：1号厂自动制样动作程序.xml: <Part Name="RBitfield" UId="39" />）
            if (upper == "RBITFIELD")
            {
                return;
            }
            // VAL_STRG/REPLACE/DELETE: str_type=String
            if (upper is "VAL_STRG" or "REPLACE" or "DELETE")
            {
                sb.AppendLine("      <TemplateValue Name=\"str_type\" Type=\"Type\">String</TemplateValue>");
                return;
            }
            // RD_LOC_T/RD_SYS_T/WR_SYS_T: date_type=Date_And_Time
            if (upper is "RD_LOC_T" or "RD_SYS_T" or "WR_SYS_T")
            {
                sb.AppendLine("      <TemplateValue Name=\"date_type\" Type=\"Type\">Date_And_Time</TemplateValue>");
                return;
            }
            // 默认: SrcType
            sb.AppendLine($"      <TemplateValue Name=\"SrcType\" Type=\"Type\">{AccessHelper.Esc(datatype)}</TemplateValue>");
        }

        /// <summary>
        /// 判断 IEC 指令引脚是否为输出（决定 Wire 子元素顺序）。
        /// 对照 official_ctu.xml：R(Input)→OpenCon 在前，CV(Output)→NameCon 在前。
        /// box 指令的输出引脚为 OUT / ENO。
        /// </summary>
        private static bool IsIecOutputPin(string inst, string pin)
        {
            var upper = inst.ToUpperInvariant();
            // 双稳态指令：输出引脚为 q 和 operand
            if (upper is "SR" or "RS")
                return pin.Equals("q", StringComparison.OrdinalIgnoreCase)
                    || pin.Equals("operand", StringComparison.OrdinalIgnoreCase);
            // ResetIECTimerCoil: operand 是线圈型目标变量，Wire 方向为 IdentCon → NameCon（同 RCoil）
            // 实证：1号厂报警程序.xml Wire 69: <IdentCon UId="30" /><NameCon UId="45" Name="operand" />
            if (upper is "RESETIECTIMERCOIL" or "RESET_IECTIMER_COIL")
                return false;  // operand 不是输出引脚，使用输入 Wire 方向

            if (IsBoxInstruction(inst))
            {
                // SEL/LIMIT/Upper_Bound/Lower_Bound/VAL_STRG/REPLACE/DELETE/RD_LOC_T/RD_SYS_T
                // 使用大写 OUT 作为输出引脚
                if (upper is "SEL" or "LIMIT" or "UPPER_BOUND" or "LOWER_BOUND"
                    or "VAL_STRG" or "REPLACE" or "DELETE"
                    or "RD_LOC_T" or "RD_SYS_T")
                    return pin.Equals("OUT", StringComparison.OrdinalIgnoreCase);
                // Inc 的 operand 既是输入也是输出
                if (upper == "INC")
                    return pin.Equals("operand", StringComparison.OrdinalIgnoreCase);
                // Normalize/Scale_X/Swap/Inv/Calc/CONCAT/S_CONV/T_CONV
                // 使用小写 out 作为输出引脚；Normalize/Scale_X 还有 eno
                return pin.Equals("out1", StringComparison.OrdinalIgnoreCase)
                    || pin.Equals("out", StringComparison.OrdinalIgnoreCase)
                    || pin.Equals("eno", StringComparison.OrdinalIgnoreCase)
                    || pin.Equals("OUT", StringComparison.OrdinalIgnoreCase);
            }
            return inst.ToUpperInvariant() switch
            {
                "CTU" => pin.Equals("Q", StringComparison.OrdinalIgnoreCase)
                      || pin.Equals("CV", StringComparison.OrdinalIgnoreCase),
                "CTD" => pin.Equals("Q", StringComparison.OrdinalIgnoreCase)
                      || pin.Equals("CV", StringComparison.OrdinalIgnoreCase),
                "CTUD" => pin.Equals("QU", StringComparison.OrdinalIgnoreCase)
                       || pin.Equals("QD", StringComparison.OrdinalIgnoreCase)
                       || pin.Equals("CV", StringComparison.OrdinalIgnoreCase),
                "TON" => pin.Equals("Q", StringComparison.OrdinalIgnoreCase)
                      || pin.Equals("ET", StringComparison.OrdinalIgnoreCase),
                "TOF" => pin.Equals("Q", StringComparison.OrdinalIgnoreCase)
                      || pin.Equals("ET", StringComparison.OrdinalIgnoreCase),
                "TP"  => pin.Equals("Q", StringComparison.OrdinalIgnoreCase)
                      || pin.Equals("ET", StringComparison.OrdinalIgnoreCase),
                "R_TRIG" => pin.Equals("Q", StringComparison.OrdinalIgnoreCase),
                "F_TRIG" => pin.Equals("Q", StringComparison.OrdinalIgnoreCase),
                // CoilTON/CoilTOF/CoilTP 定时器线圈（无背景DB，可在OB中使用）
                // 引脚实证来源：c:\TiaCommander\ref\Fanuc_Robot_PNS_Start.xml
                //   operand → 输出位变量（Wire 54: IdentCon → CoilTON.operand）
                //   out     → 逻辑流输出（Wire 55: CoilTON.out → Contact.in）
                "COILTON" => pin.Equals("operand", StringComparison.OrdinalIgnoreCase)
                          || pin.Equals("out", StringComparison.OrdinalIgnoreCase),
                "COILTOF" => pin.Equals("operand", StringComparison.OrdinalIgnoreCase)
                          || pin.Equals("out", StringComparison.OrdinalIgnoreCase),
                "COILTP"  => pin.Equals("operand", StringComparison.OrdinalIgnoreCase)
                          || pin.Equals("out", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        /// <summary>contacts 与 coils 的同名变量集合（自锁变量）。</summary>
        private static HashSet<string> LatchVariables(
            List<LadContactDef> contacts, List<LadCoilDef> coils)
        {
            var cset = new HashSet<string>(
                contacts.Where(c => !string.IsNullOrEmpty(c.variable)).Select(c => c.variable!),
                StringComparer.OrdinalIgnoreCase);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in coils)
                if (!string.IsNullOrEmpty(c.variable) && cset.Contains(c.variable))
                    result.Add(c.variable!);
            return result;
        }

        private static bool IsLadLiteral(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                   || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                   || text.StartsWith("T#", StringComparison.OrdinalIgnoreCase)
                   || text.StartsWith("TIME#", StringComparison.OrdinalIgnoreCase)
                   || IsNumericLiteral(text);
        }

        private static bool IsNumericLiteral(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string value = s.Trim();
            if (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || value.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                return true;
            // Time 常量格式: T#30S / TIME#30S
            if (value.StartsWith("T#", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("TIME#", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value[0] == '+' || value[0] == '-')
            {
                if (value.Length == 1) return false;
                value = value.Substring(1);
            }
            bool hasDot = false;
            bool hasDigit = false;
            foreach (var ch in value)
            {
                if (ch == '.') { if (hasDot) return false; hasDot = true; continue; }
                if (!char.IsDigit(ch)) return false;
                hasDigit = true;
            }
            return hasDigit;
        }

        /// <summary>生成已验证支持的 Bool、Time、Int、Real 常量 Access。</summary>
        private static string BuildConstantAccess(int uid, string value)
        {
            string type = value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                          || value.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                ? "Bool"
                : (value.StartsWith("T#", StringComparison.OrdinalIgnoreCase)
                   || value.StartsWith("TIME#", StringComparison.OrdinalIgnoreCase)
                    ? "Time"
                    : (value.Contains('.') ? "Real" : "Int"));
            return AccessHelper.BuildConstantAccess(uid, type, value);
        }

        /// <summary>
        /// 返回背景 DB 型指令的未使用数据输出引脚列表（需要 OpenCon 悬空）。
        /// TON/TOF/TP 的 ET 是数据输出；CTU/CTD/CTUD 的 CV 是数据输出。
        /// Q 是逻辑流输出（由下游元素连接），不在此列表中。
        /// </summary>
        private static List<string> GetUnusedDataOutputPins(string inst, Dictionary<string, string>? pins)
        {
            var result = new List<string>();
            string upper = inst.ToUpperInvariant();
            // 数据输出引脚列表（不含逻辑流输出 Q）
            var dataOutputs = upper switch
            {
                "TON" or "TOF" or "TP" or "TONR" => new[] { "ET" },
                "CTU" or "CTD" or "CTUD" => new[] { "CV" },
                _ => Array.Empty<string>()
            };
            foreach (var pin in dataOutputs)
            {
                // 如果 pins 中已指定该引脚且值非空，则跳过
                bool used = pins != null && pins.Any(kv =>
                    kv.Key.Equals(pin, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(kv.Value));
                if (!used)
                    result.Add(pin);
            }
            return result;
        }

        /// <summary>SecurityElement.Escape 的本地别名，避免在 FB-call 内联 Access 时引入新 using。</summary>
        private static string SecurityElementEscape(string text) => AccessHelper.Esc(text);

        // ────────────────────────────────────────────────────────────
        // FlgNet 封装
        // ────────────────────────────────────────────────────────────

        private static string WrapFlgNet(StringBuilder parts, StringBuilder wires)
        {
            var wiresPart = wires.Length == 0
                ? ""
                : $"\n  <Wires>\n{wires}  </Wires>";
            return
                $"<FlgNet xmlns=\"{FlgNetNs}\">\n" +
                $"  <Parts>\n{parts}  </Parts>{wiresPart}\n" +
                "</FlgNet>";
        }
    }
}
