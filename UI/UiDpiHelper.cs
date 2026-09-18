using System;
using System.Drawing;
using System.Windows.Forms;

namespace TiaMcpServer;

internal static class UiDpiHelper
{
    public static void Normalize(Control root)
    {
        if (root == null) return;
        NormalizeRecursive(root);
    }

    private static void NormalizeRecursive(Control control)
    {
        if (control is Button button)
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(Math.Max(88, button.Width), Math.Max(36, button.Height));
            button.Padding = new Padding(10, 4, 10, 4);
            button.Margin = new Padding(6);
            button.UseCompatibleTextRendering = true;
        }
        else if (control is FlowLayoutPanel flow)
        {
            flow.AutoScroll = true;
            flow.WrapContents = false;
            flow.Padding = new Padding(flow.Padding.Left, Math.Max(4, flow.Padding.Top), flow.Padding.Right, flow.Padding.Bottom);
        }

        foreach (Control child in control.Controls)
            NormalizeRecursive(child);
    }
}
