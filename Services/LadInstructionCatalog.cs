using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// LAD 指令的单一事实来源。IR 编译、validate/apply、实例声明和 XML Builder
    /// 必须使用这里的必填引脚、触发引脚、值类型与本地实例类型映射。
    /// </summary>
    internal static class LadInstructionCatalog
    {
        private static readonly HashSet<string> Background = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TON", "TOF", "TP", "TONR", "CTU", "CTD", "CTUD", "R_TRIG", "F_TRIG"
        };

        private static readonly Dictionary<string, string[]> RequiredPins =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["TON"] = new[] { "IN", "PT" },
                ["TOF"] = new[] { "IN", "PT" },
                ["TP"] = new[] { "IN", "PT" },
                ["TONR"] = new[] { "IN", "PT" },
                ["CTU"] = new[] { "CU", "PV" },
                ["CTD"] = new[] { "CD", "PV" },
                ["CTUD"] = new[] { "CU", "CD", "PV" },
                ["R_TRIG"] = new[] { "CLK" },
                ["F_TRIG"] = new[] { "CLK" },
                ["MOVE"] = new[] { "IN", "OUT" },
                ["ADD"] = new[] { "IN1", "IN2", "OUT" },
                ["SUB"] = new[] { "IN1", "IN2", "OUT" },
                ["MUL"] = new[] { "IN1", "IN2", "OUT" },
                ["DIV"] = new[] { "IN1", "IN2", "OUT" },
                ["MOD"] = new[] { "IN1", "IN2", "OUT" },
                ["CONVERT"] = new[] { "IN", "OUT" },
                ["NORM_X"] = new[] { "MIN", "VALUE", "MAX", "OUT" },
                ["SCALE_X"] = new[] { "MIN", "VALUE", "MAX", "OUT" }
            };

        internal static bool IsBackground(string? instruction) =>
            !string.IsNullOrWhiteSpace(instruction) && Background.Contains(instruction.Trim());

        internal static string[] GetRequiredPins(string? instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction)) return Array.Empty<string>();
            return RequiredPins.TryGetValue(instruction.Trim(), out var pins) ? pins : Array.Empty<string>();
        }

        internal static string GetTriggerPin(string? instruction)
        {
            var upper = (instruction ?? "").Trim().ToUpperInvariant();
            switch (upper)
            {
                case "TON": case "TOF": case "TP": case "TONR": return "IN";
                case "CTU": case "CTUD": return "CU";
                case "CTD": return "CD";
                case "R_TRIG": case "F_TRIG": return "CLK";
                default: return "";
            }
        }

        internal static string NormalizeValueDatatype(string? instruction, string? datatype)
        {
            var upper = (instruction ?? "").Trim().ToUpperInvariant();
            var value = string.IsNullOrWhiteSpace(datatype) ? "" : datatype!.Trim();
            if (upper == "TON" || upper == "TOF" || upper == "TP" || upper == "TONR") return "Time";
            if (upper == "R_TRIG" || upper == "F_TRIG") return "Bool";
            if (upper == "CTU" || upper == "CTD" || upper == "CTUD")
            {
                var normalized = value.ToUpperInvariant();
                foreach (var prefix in new[] { "CTU_", "CTD_", "CTUD_" })
                    if (normalized.StartsWith(prefix, StringComparison.Ordinal)) normalized = normalized.Substring(prefix.Length);
                if (normalized == "IEC_COUNTER" || normalized.Length == 0) normalized = "INT";
                return ToCanonicalScalar(normalized);
            }
            return value.Length == 0 ? "Int" : value;
        }

        internal static string GetLocalInstanceDatatype(string? instruction, string? datatype)
        {
            var upper = (instruction ?? "").Trim().ToUpperInvariant();
            switch (upper)
            {
                case "TON": case "TONR": return "TON_TIME";
                case "TOF": return "TOF_TIME";
                case "TP": return "TP_TIME";
                case "R_TRIG": return "R_TRIG";
                case "F_TRIG": return "F_TRIG";
                case "CTU": return "CTU_" + NormalizeValueDatatype(upper, datatype).ToUpperInvariant();
                case "CTD": return "CTD_" + NormalizeValueDatatype(upper, datatype).ToUpperInvariant();
                case "CTUD": return "CTUD_" + NormalizeValueDatatype(upper, datatype).ToUpperInvariant();
                default: throw new ArgumentException("不是 IEC 背景实例指令: " + instruction, nameof(instruction));
            }
        }

        internal static string GetInstancePrefix(string? instruction)
        {
            var upper = (instruction ?? "").Trim().ToUpperInvariant();
            return upper.Length == 0 ? "Inst_IEC" : "Inst_" + upper.Replace("_", "");
        }

        internal static IEnumerable<string> GetOutputPins(string? instruction)
        {
            var upper = (instruction ?? "").Trim().ToUpperInvariant();
            switch (upper)
            {
                case "TON": case "TOF": case "TP": case "TONR": return new[] { "Q", "ET" };
                case "CTU": case "CTD": return new[] { "Q", "CV" };
                case "CTUD": return new[] { "QU", "QD", "CV" };
                case "R_TRIG": case "F_TRIG": return new[] { "Q" };
                default: return new[] { "OUT", "OUT1", "RET_VAL" };
            }
        }

        private static string ToCanonicalScalar(string upper)
        {
            switch (upper)
            {
                case "SINT": return "SInt";
                case "USINT": return "USInt";
                case "INT": return "Int";
                case "UINT": return "UInt";
                case "DINT": return "DInt";
                case "UDINT": return "UDInt";
                case "LINT": return "LInt";
                case "ULINT": return "ULInt";
                default: throw new ArgumentException("计数器 datatype 仅支持整数标量类型，收到: " + upper);
            }
        }
    }
}
