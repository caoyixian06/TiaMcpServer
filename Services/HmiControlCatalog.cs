using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// HMI 编排器支持目录。所有 compile/validate/apply/create 入口必须使用本目录，
    /// 禁止在各入口维护独立的控件白名单。
    /// </summary>
    internal static class HmiControlCatalog
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rectangle"] = "rectangle", ["rect"] = "rectangle", ["panel"] = "rectangle",
            ["text"] = "text", ["label"] = "text", ["textfield"] = "text",
            ["indicator"] = "indicator", ["circle"] = "indicator", ["lamp"] = "indicator", ["status"] = "indicator",
            ["button"] = "button", ["btn"] = "button", ["commandbutton"] = "button",
            ["navigation"] = "navigation",
            ["switch"] = "switch", ["modeselector"] = "switch",
            ["iofield"] = "iofield", ["io"] = "iofield", ["display"] = "iofield",
            ["line"] = "line", ["separator"] = "line",
            ["symboliciofield"] = "symboliciofield", ["symbolic_io_field"] = "symboliciofield", ["symbolic"] = "symboliciofield",
            ["graphicview"] = "graphicview", ["graphic_view"] = "graphicview", ["picture"] = "graphicview", ["image"] = "graphicview",
            ["group"] = "group"
        };

        internal static string Canonicalize(string? type)
        {
            var key = (type ?? "").Trim();
            return Aliases.TryGetValue(key, out var canonical) ? canonical : "";
        }

        internal static bool IsSupported(string? type) => Canonicalize(type).Length > 0;
        internal static bool RequiresTag(string canonical) => canonical is "indicator" or "switch" or "iofield" or "symboliciofield";
        internal static bool RequiresTextList(string canonical) => canonical == "symboliciofield";
        internal static bool RequiresPicture(string canonical) => canonical == "graphicview";
        internal static bool IsGroup(string canonical) => canonical == "group";
        internal static bool IsDecorative(string canonical) => canonical is "rectangle" or "line";
        internal static bool IsTextual(string canonical) => canonical is "text" or "button" or "navigation" or "iofield" or "symboliciofield";

        internal static IReadOnlyList<string> CanonicalTypes { get; } = new[]
        {
            "rectangle", "text", "indicator", "button", "navigation", "switch", "iofield",
            "line", "symboliciofield", "graphicview", "group"
        };

        internal static IReadOnlyDictionary<string, string[]> AliasMap => CanonicalTypes.ToDictionary(
            canonical => canonical,
            canonical => Aliases.Where(x => x.Value.Equals(canonical, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).OrderBy(x => x).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
