using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer
{
    /// <summary>工具 JSON Schema 构造辅助。V4.3.0 保留长度、数量、枚举和未知属性约束。</summary>
    public partial class McpServer
    {
        private static object EmptySchema() => Props(new Dictionary<string, object>(), Array.Empty<string>());

        private static object Props(Dictionary<string, object> properties, IEnumerable<string>? required = null)
        {
            var normalized = new Dictionary<string, object>(properties ?? new Dictionary<string, object>(), StringComparer.Ordinal);
            // 全局安全参数；破坏性 Tool 由调度器强制校验。
            if (!normalized.ContainsKey("dryRun")) normalized["dryRun"] = BoolProp("仅预检，不执行写入/删除");
            if (!normalized.ContainsKey("confirm")) normalized["confirm"] = BoolProp("破坏性操作二次确认");
            if (!normalized.ContainsKey("confirmation")) normalized["confirmation"] = StrProp("确认目标工具名");

            var requiredArray = required as string[] ?? (required != null ? required.ToArray() : Array.Empty<string>());
            return new
            {
                type = "object",
                properties = normalized,
                required = requiredArray,
                additionalProperties = false,
                maxProperties = 128
            };
        }

        private static object StrProp(string description)
        {
            var pathLike = (description ?? "").IndexOf("路径", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (description ?? "").IndexOf("目录", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (description ?? "").IndexOf("文件", StringComparison.OrdinalIgnoreCase) >= 0;
            // ★修复★ JSON/XML/IR 类大字符串参数（画面规格、LAD IR、HMI IR 等）放宽到 1MB，
            // 否则 800x480 全画面规格（3.5万+字符）会被 8192/32767 上限拒绝。
            var jsonLike = (description ?? "").IndexOf("JSON", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (description ?? "").IndexOf("XML", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (description ?? "").IndexOf("IR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (description ?? "").IndexOf("spec", StringComparison.OrdinalIgnoreCase) >= 0;
            return new
            {
                type = "string",
                description,
                maxLength = pathLike ? 32767 : (jsonLike ? 1048576 : 8192)
            };
        }

        private static object EnumStrProp(string description, params string[] values) => new
        {
            type = "string",
            description,
            @enum = values ?? Array.Empty<string>(),
            maxLength = 256
        };

        private static object IntProp(string description) => new
        {
            type = "integer",
            description,
            minimum = int.MinValue,
            maximum = int.MaxValue
        };

        private static object NumProp(string description) => new
        {
            type = "number",
            description,
            minimum = -1.0E+308,
            maximum = 1.0E+308
        };

        private static object BoolProp(string description) => new
        {
            type = "boolean",
            description
        };

        private static object ArrProp(string description, string? itemType = "string") => new
        {
            type = "array",
            description,
            minItems = 0,
            maxItems = 10000,
            items = new { type = itemType ?? "string", maxLength = 8192 }
        };
    }
}
