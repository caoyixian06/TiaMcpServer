using System;
using System.Collections.Generic;
using System.Security;
using System.Text;

namespace TiaMcpServer
{
    internal static class AccessHelper
    {
        private static readonly Dictionary<string, string> PartNameMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "Contact", "Contact" },
                { "NegatedContact", "Contact" },
                { "NotContact", "Contact" },
                { "Coil", "Coil" },
                { "SetCoil", "SCoil" },
                { "SCoil", "SCoil" },
                { "ResetCoil", "RCoil" },
                { "RCoil", "RCoil" },
                { "PosEdgeContact", "PContact" },
                { "NegEdgeContact", "NContact" },
                { "PContact", "PContact" },
                { "NContact", "NContact" },
            };

        public static string MapPartName(string name)
            => PartNameMap.TryGetValue(name, out var mapped) ? mapped : name;

        public static bool IsNegatedContact(string name)
            => name.Equals("NegatedContact", StringComparison.OrdinalIgnoreCase)
               || name.Equals("NotContact", StringComparison.OrdinalIgnoreCase);

        public static bool IsEdgeContact(string name)
            => name.Equals("PosEdgeContact", StringComparison.OrdinalIgnoreCase)
               || name.Equals("NegEdgeContact", StringComparison.OrdinalIgnoreCase);

        public static bool IsGlobalVariable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // % 开头为绝对地址（如 %I0.0、%M0.0），一定是全局变量
            if (name.StartsWith("%", StringComparison.Ordinal))
                return true;
            if (name.Length >= 2)
            {
                char first = name[0];
                // I/Q/M/T/C/D + 数字/W/D/B 为绝对地址（如 I0.0、M0.0、T1、C5）
                // 注意：这与局部短名变量（如 T1、M0）存在歧义，但绝对地址更常见。
                // 主代码路径（FlgNetBuilder.AppendSymbolAccesses）已不依赖本方法，
                // 改为通过 interfaceVars/staticVars 集合精确判断 scope。
                if ((first == 'I' || first == 'Q' || first == 'M' ||
                     first == 'T' || first == 'C' || first == 'D') &&
                    (char.IsDigit(name[1]) || name[1] == 'W' || name[1] == 'D' || name[1] == 'B'))
                    return true;
            }
            // DB 开头为全局数据块引用（如 DB1.Var）
            if (name.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                return true;
            // 不再使用 name.Contains(".") 启发式判断：
            // 局部结构体变量访问（如 MyStruct.Field）会被误判为全局变量。
            // DB 名称引用（如 p状态.p[3]）与局部结构体字段在语法上无法区分，
            // 调用方应通过 interfaceVars/staticVars 集合显式指定 scope。
            return false;
        }

        public static string ScopeFor(string variable)
            => IsGlobalVariable(variable) ? "GlobalVariable" : "LocalVariable";

        public static string BuildVariableAccess(int uid, string displayName, string scope)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"    <Access Scope=\"{scope}\" UId=\"{uid}\">");
            sb.AppendLine("      <Symbol>");
            // 支持点分变量名（如 DB_TrafficData.Mode_Select）→ 多个 Component
            // 支持数组元素访问（如 p状态.p[0] → 最后一个 Component 带 AccessModifier="Array"）
            var components = displayName.Split('.');
            for (int i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (string.IsNullOrEmpty(comp)) continue;
                bool isLast = (i == components.Length - 1);
                // 检测数组索引后缀 [N]
                int bracketIdx = comp.IndexOf('[');
                if (bracketIdx > 0 && comp.EndsWith("]") && isLast)
                {
                    string varName = comp.Substring(0, bracketIdx);
                    string indexStr = comp.Substring(bracketIdx + 1, comp.Length - bracketIdx - 2);
                    sb.AppendLine($"        <Component Name=\"{SecurityElement.Escape(varName)}\" AccessModifier=\"Array\">");
                    sb.AppendLine($"          <Access Scope=\"LiteralConstant\">");
                    sb.AppendLine($"            <Constant>");
                    sb.AppendLine($"              <ConstantType>DInt</ConstantType>");
                    sb.AppendLine($"              <ConstantValue>{SecurityElement.Escape(indexStr)}</ConstantValue>");
                    sb.AppendLine($"            </Constant>");
                    sb.AppendLine($"          </Access>");
                    sb.AppendLine($"        </Component>");
                }
                else
                {
                    sb.AppendLine($"        <Component Name=\"{SecurityElement.Escape(comp)}\" />");
                }
            }
            sb.AppendLine("      </Symbol>");
            sb.AppendLine("    </Access>");
            return sb.ToString();
        }

        public static string BuildConstantAccess(int uid, string constantType, string constantValue)
        {
            var sb = new StringBuilder();

            // 识别特殊常量值模式（根据值内容自动判定类型）
            // Bool: TRUE/FALSE（不区分大小写）
            bool isBoolValue = constantValue.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                            || constantValue.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
            // Hex: 16#...（十六进制常量）
            bool isHexValue = constantValue.StartsWith("16#", StringComparison.OrdinalIgnoreCase);
            // Char: 'x'（单引号包裹单字符）
            bool isCharValue = constantValue.Length >= 3
                            && constantValue.StartsWith("'") && constantValue.EndsWith("'");
            // String: "..."（双引号包裹）
            bool isStringValue = constantValue.Length >= 2
                            && constantValue.StartsWith("\"") && constantValue.EndsWith("\"");

            // Time/Date/DateAndTime/DTL/String 等类型使用 TypedConstant（只有 ConstantValue，没有 ConstantType）
            // 数字类型使用 LiteralConstant（有 ConstantType 和 ConstantValue）
            bool isTyped = constantType.Equals("Time", StringComparison.OrdinalIgnoreCase)
                        || constantType.Equals("Date", StringComparison.OrdinalIgnoreCase)
                        || constantType.Equals("DateAndTime", StringComparison.OrdinalIgnoreCase)
                        || constantType.Equals("DTL", StringComparison.OrdinalIgnoreCase)
                        || constantType.Equals("String", StringComparison.OrdinalIgnoreCase)
                        || isStringValue;
            if (isTyped)
            {
                sb.AppendLine($"    <Access Scope=\"TypedConstant\" UId=\"{uid}\">");
                sb.AppendLine("      <Constant>");
                sb.AppendLine($"        <ConstantValue>{SecurityElement.Escape(constantValue)}</ConstantValue>");
                sb.AppendLine("      </Constant>");
            }
            else
            {
                // 根据值模式确定实际类型（覆盖调用方传入的泛型类型）
                string actualType = constantType;
                string actualValue = constantValue;
                if (isBoolValue)
                {
                    actualType = "Bool";
                    actualValue = constantValue.ToUpperInvariant();
                }
                else if (isHexValue)
                {
                    actualType = "DInt";
                }
                else if (isCharValue)
                {
                    actualType = "Char";
                }
                sb.AppendLine($"    <Access Scope=\"LiteralConstant\" UId=\"{uid}\">");
                sb.AppendLine("      <Constant>");
                sb.AppendLine($"        <ConstantType>{SecurityElement.Escape(actualType)}</ConstantType>");
                sb.AppendLine($"        <ConstantValue>{SecurityElement.Escape(actualValue)}</ConstantValue>");
                sb.AppendLine("      </Constant>");
            }
            sb.AppendLine("    </Access>");
            return sb.ToString();
        }

        public static string BuildContactPart(string partName, int uid)
        {
            var mapped = MapPartName(partName);
            if (IsNegatedContact(partName))
                return $"    <Part Name=\"{mapped}\" UId=\"{uid}\"><Negated Name=\"operand\" /></Part>";
            // PContact/NContact 不需要 AutomaticTyped（对照峨胜项目实证 XML）
            return $"    <Part Name=\"{mapped}\" UId=\"{uid}\" />";
        }

        public static string BuildCoilPart(string partName, int uid)
            => $"    <Part Name=\"{MapPartName(partName)}\" UId=\"{uid}\" />";

        public static string DisplayName(string? key)
        {
            key ??= string.Empty;
            return key.EndsWith("_write") ? key.Substring(0, key.Length - 6) : key;
        }

        /// <summary>获取变量的基础名（去掉 _write 后缀和 [N] 数组索引）。
        /// 用于 allInterfaceVars 匹配（接口区变量名不含数组索引）。</summary>
        public static string BaseVarName(string key)
        {
            var name = DisplayName(key);
            int bracketIdx = name.IndexOf('[');
            if (bracketIdx > 0 && name.EndsWith("]"))
                return name.Substring(0, bracketIdx);
            return name;
        }

        public static string Esc(string? text) => SecurityElement.Escape(text ?? string.Empty) ?? "";
    }
}
