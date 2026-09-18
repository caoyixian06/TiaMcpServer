namespace TiaMcpServer
{
    /// <summary>
    /// 经正式画面 XML 样本验证的 HMI 动画规格。
    /// Type: visibility | singleBitVisibility | enabling。
    /// </summary>
    public sealed class HmiAnimationSpec
    {
        public string Type { get; set; } = "visibility";
        public string Tag { get; set; } = "";
        public int RangeStart { get; set; } = 1;
        public int RangeEnd { get; set; } = 1;
        public int BitPosition { get; set; } = 0;
        public bool Visible { get; set; } = true;
        public bool ObjectEnabled { get; set; } = true;
    }
}
