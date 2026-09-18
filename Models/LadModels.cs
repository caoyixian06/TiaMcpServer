using System.Collections.Generic;
using Newtonsoft.Json;

// ════════════════════════════════════════════════════════════════
// LAD 输入模型（JSON 反序列化）。所有 LAD 网络统一 JSON 格式见任务 5.2。
// ════════════════════════════════════════════════════════════════

namespace TiaMcpServer
{
    /// <summary>梯形图逻辑网络（外部 JSON 入口）。</summary>
    public class LadLogicNetwork
    {
        public List<LadLogicVariable>? variables { get; set; }
        public List<LadLogicContact>? contacts { get; set; }
        public List<LadLogicCoil>? coils { get; set; }
        public List<LadLogicBranch>? branches { get; set; }
        public List<LadLogicContact>? tail { get; set; }
        public List<LadLogicCall>? calls { get; set; }
        public string? type { get; set; }
        public LadLogicSysCall? sysCall { get; set; }
        public List<LadLogicNetwork>? networks { get; set; }
        public List<RungElement>? rung { get; set; }  // P1: rung 格式
    }

    /// <summary>IEC 系统调用（CTU/TON 等）的外部 JSON 入口（任务 5.2 sysCall）。</summary>
    public class LadLogicSysCall
    {
        public string inst { get; set; } = "TON";
        public string? version { get; set; }
        public string? datatype { get; set; }
        public string? instance { get; set; }
        public Dictionary<string, string>? pins { get; set; }
        public bool negateOutput { get; set; } // 输出取反（插入 NOT 门）
    }

    public class LadLogicVariable
    {
        public string name { get; set; } = "";
        public string? datatype { get; set; }
        public string? section { get; set; }
    }

    public class LadLogicContact
    {
        public string variable { get; set; } = "";
        public bool negated { get; set; }
        public string? name { get; set; } // 显式触点名：Contact/NegatedContact/PosEdgeContact/NegEdgeContact
    }

    public class LadLogicCoil
    {
        public string variable { get; set; } = "";
        public string? name { get; set; } // Coil/SetCoil/ResetCoil
    }

    public class LadLogicBranch
    {
        public List<LadLogicContact>? contacts { get; set; }
        public List<LadLogicContact>? tail { get; set; }
        public List<LadLogicCoil>? coils { get; set; }
        public List<LadLogicCall>? calls { get; set; }
    }

    public class LadLogicCall
    {
        public string blockName { get; set; } = "";
        public string? instanceName { get; set; }
        public string blockType { get; set; } = "FB";
        public List<LadLogicPin>? pins { get; set; }
    }

    public class LadLogicPin
    {
        public string name { get; set; } = "";
        public string variable { get; set; } = "";
        public string direction { get; set; } = "in";
    }

    // ════════════════════════════════════════════════════════════════
    // 内部模型（XML 生成用）
    // ════════════════════════════════════════════════════════════════

    /// <summary>IEC 系统调用（CTU/TON 等）。</summary>
    public class SysCallDef
    {
        public string inst { get; set; } = "TON";       // 指令名：CTU/TON/CTD/TOF/TP ...
        public string version { get; set; } = "1.0";
        public string datatype { get; set; } = "Int";   // value_type
        public string? destType { get; set; }           // 目标类型（Normalize/Scale_X 的 DestType）
        public string? instance { get; set; }           // 背景 DB 名
        public string? instanceScope { get; set; }      // "global"=全局实例 DB；默认 FB Static 多重实例
        public Dictionary<string, string>? pins { get; set; } // 引脚名 → 变量/常量
        public bool negateOutput { get; set; }          // 输出取反（插入 NOT 门）
        public string? equation { get; set; }           // Calc 计算盒子的公式（如 "((IN1/IN2)*(IN3/IN4))+IN5"）
    }

    public class LadNetworkDef
    {
        public string? title { get; set; }               // 网络标题（支持中文）
        public List<LadContactDef>? contacts { get; set; }
        public List<LadCoilDef>? coils { get; set; }
        public List<LadCallDef>? calls { get; set; }
        public List<LadVariableDef>? variables { get; set; }
        public SysCallDef? sysCall { get; set; }
        public string? type { get; set; }                // "latch" → 并联自锁
        public List<LadNetworkDef>? branches { get; set; }
        public List<LadContactDef>? tail { get; set; }
        public List<LadNetworkDef>? networks { get; set; }
        public List<RungElement>? rung { get; set; }  // P1: rung 格式
        // ★ 直通格式: read_lad_network 返回的原始结构（parts/accesses/calls/wires），
        // 与 FlgNetBuilder 简写格式（contacts/coils）不兼容；非空时直接保真重建 FlgNet
        public List<LadPartDef>? parts { get; set; }
        public List<LadAccessDef>? accesses { get; set; }
        public List<LadRawCallDef>? rawCalls { get; set; }
        public List<LadRawWireDef>? wires { get; set; }
    }

    /// <summary>read_lad_network 返回的 Part 元素（触点/线圈/指令盒）。</summary>
    public class LadPartDef
    {
        public int uid { get; set; }
        public string name { get; set; } = "";
        public string version { get; set; } = "";
        public string disabledEno { get; set; } = "";
        public List<string>? negated { get; set; }
        public string instance { get; set; } = "";
        public string instanceScope { get; set; } = "";
        public Dictionary<string, string>? templates { get; set; }
    }

    /// <summary>read_lad_network 返回的 Access 元素（变量/常量引用）。</summary>
    public class LadAccessDef
    {
        public int uid { get; set; }
        public string scope { get; set; } = "";
        public string type { get; set; } = "";
        public string symbol { get; set; } = "";
    }

    /// <summary>read_lad_network 返回的 Call 元素（FB/FC 调用）。</summary>
    public class LadRawCallDef
    {
        public int uid { get; set; }
        public string calleeName { get; set; } = "";
        public string blockType { get; set; } = "";
        public string instance { get; set; } = "";
        public string instanceScope { get; set; } = "";
        public List<LadRawParamDef>? parameters { get; set; }
    }

    public class LadRawParamDef
    {
        public string name { get; set; } = "";
        public string section { get; set; } = "";
        public string type { get; set; } = "";
        public string value { get; set; } = "";
    }

    /// <summary>read_lad_network 返回的 Wire 元素（端点列表）。</summary>
    public class LadRawWireDef
    {
        public List<string>? endpoints { get; set; }
    }

    public class LadContactDef
    {
        public string name { get; set; } = "Contact";
        public string variable { get; set; } = "";
    }

    public class LadCoilDef
    {
        public string name { get; set; } = "Coil";
        public string variable { get; set; } = "";
    }

    public class LadCallDef
    {
        public string blockName { get; set; } = "";
        public string instanceName { get; set; } = "";
        public string blockType { get; set; } = "FB";
        /// <summary>FB 实例作用域：local/global。不得再通过变量名猜测。</summary>
        public string? instanceScope { get; set; }
        public List<LadPinDef>? pins { get; set; }
    }

    public class LadPinDef
    {
        public string name { get; set; } = "";
        public string variable { get; set; } = "";
        public string direction { get; set; } = "in";
        /// <summary>TIA Parameter Type 必填；必须显式提供，禁止按变量名猜测。</summary>
        public string? datatype { get; set; }
        /// <summary>可选 local/global；为空时仅根据已知块接口变量判定变量访问作用域。</summary>
        public string? scope { get; set; }
    }

    public class LadVariableDef
    {
        public string name { get; set; } = "";
        public string datatype { get; set; } = "Bool";
        public string section { get; set; } = "Input";
    }

    // ════════════════════════════════════════════════════════════════
    // Rung 模型（P1: 高级 LAD 逻辑描述层）
    // ════════════════════════════════════════════════════════════════

    public class RungContact
    {
        public string contact { get; set; } = "";
        public bool negated { get; set; }
        public string? type { get; set; }  // "pcontact"/"ncontact" → PContact/NContact
        public string? bit { get; set; }   // 边沿存储位（如 "p状态.p[0]"）
    }

    public class RungCoil
    {
        public string coil { get; set; } = "";
        public string? type { get; set; }  // "set"/"reset"
    }

    public class RungBox
    {
        public string box { get; set; } = "";
        public Dictionary<string, string>? pins { get; set; }
        public string? datatype { get; set; }
        public string? destType { get; set; }  // 目标类型（Normalize/Scale_X 的 DestType）
        public string? instance { get; set; }
        public string? instanceScope { get; set; }  // "global" → TON全局实例 DB
        public bool negateOutput { get; set; }
        public string? equation { get; set; }  // Calc 计算盒子的公式
    }

    public class RungCall
    {
        /// <summary>调用的块名。JSON 接受 "call" 或 "blockName" 两种键。</summary>
        [JsonProperty("call")]
        public string call { get; set; } = "";

        /// <summary>JSON 别名，与 call 互通（AI 通常用 blockName）。</summary>
        [JsonProperty("blockName")]
        public string? blockName
        {
            get => call;
            set => call = value ?? "";
        }

        /// <summary>背景 DB 实例名。JSON 接受 "instance" 或 "instanceName"。</summary>
        [JsonProperty("instance")]
        public string? instance { get; set; }

        [JsonProperty("instanceName")]
        public string? instanceName
        {
            get => instance;
            set => instance = value;
        }

        public string? blockType { get; set; }
        public string? instanceScope { get; set; }
        public List<RungCallPin>? pins { get; set; }
    }

    public class RungCallPin
    {
        public string name { get; set; } = "";
        public string variable { get; set; } = "";
        public string direction { get; set; } = "in";
        public string? scope { get; set; }
        public string? datatype { get; set; }
    }

    public class RungBranch
    {
        public List<List<RungElement>>? branch { get; set; }
    }

    public class RungOr
    {
        public List<string>? inputs { get; set; }
        public string? output { get; set; }
    }

    public class RungElement
    {
        public RungContact? contact { get; set; }
        public RungCoil? coil { get; set; }
        public RungBox? box { get; set; }
        public RungCall? call { get; set; }
        public RungBranch? branch { get; set; }
        public RungOr? or { get; set; }
    }

    // ════════════════════════════════════════════════════════════════
    // 指令盒子批量定义（AddLadBoxBatch 使用）
    // ════════════════════════════════════════════════════════════════

    /// <summary>单个指令盒子定义（批量添加使用）。</summary>
    public class LadBoxDef
    {
        /// <summary>指令类型：TON/TOF/TP/TONR/CTU/CTD/CTUD/R_TRIG/F_TRIG/SR/RS/ADD/SUB/MUL/DIV/MOVE/GT/LT/GE/LE/EQ/NE 等</summary>
        public string boxType { get; set; } = "";

        /// <summary>IEC 实例名；local 可省略并由共享校验器分配唯一名称，global 时必填。</summary>
        public string? instanceName { get; set; }

        /// <summary>实例作用域 local/global；默认 local（FB Static），OB/FC 中应使用显式 global 实例。</summary>
        public string? instanceScope { get; set; }

        /// <summary>引脚参数映射（如 {"IN": "Tag1", "PT": "T#5s", "Q": "Tag2"}）</summary>
        public Dictionary<string, string>? parameters { get; set; }

        /// <summary>数据类型（如 "Int"/"Real"/"Time"/"Word"）</summary>
        public string? datatype { get; set; }

        /// <summary>目标类型（CONVERT/NORM_X/SCALE_X 等需要 DestType 的指令）</summary>
        public string? destType { get; set; }

        /// <summary>网络标题（可选，支持中文）</summary>
        public string? title { get; set; }

        /// <summary>输出取反（在 box 输出端插入 NOT 门）</summary>
        public bool negateOutput { get; set; }
    }
}
