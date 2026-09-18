using System.Collections.Generic;

namespace TiaMcpServer
{
    public sealed class HmiFunctionParameterSpec
    {
        public string Name { get; set; } = "Tag";
        public string Value { get; set; } = "";
        /// <summary>链接参数写入 LinkList；字面量参数写入 AttributeList/Value。</summary>
        public bool IsLink { get; set; } = true;
        /// <summary>字面量 CLR 类型，例如 System.Int32 / System.Double。</summary>
        public string Type { get; set; } = "System.String";
    }

    public sealed class HmiButtonActionSpec
    {
        public string Trigger { get; set; } = "Click";
        public string Function { get; set; } = "InvertBit";

        // 兼容旧调用方；新代码优先使用 Parameters。
        public string Target { get; set; } = "";
        public string ParameterName { get; set; } = "Tag";
        public List<HmiFunctionParameterSpec> Parameters { get; set; } = new List<HmiFunctionParameterSpec>();
    }
}
