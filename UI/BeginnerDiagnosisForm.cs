using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TiaMcpServer;

internal sealed class BeginnerDiagnosisForm : Form
{
    private readonly ServerRuntimeState _state;
    private readonly Label _summary = new Label();
    private readonly ListBox _problems = new ListBox();
    private readonly ListBox _actions = new ListBox();

    public BeginnerDiagnosisForm(ServerRuntimeState state)
    {
        _state = state;
        Text = "一键故障诊断";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Size = new Size(820, 600);
        Font = SystemFonts.MessageBoxFont;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        _summary.Dock = DockStyle.Fill;
        _summary.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12F, FontStyle.Bold);
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_summary, 0, 0);
        root.Controls.Add(new Label { Text = "发现的问题", Dock = DockStyle.Fill, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10F, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft }, 0, 1);
        _problems.Dock = DockStyle.Fill;
        root.Controls.Add(_problems, 0, 2);
        _actions.Dock = DockStyle.Fill;
        root.Controls.Add(_actions, 0, 3);

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "关闭", Width = 100, Height = 34 };
        close.Click += (_, __) => Close();
        var refresh = new Button { Text = "重新诊断", Width = 110, Height = 34 };
        refresh.Click += (_, __) => RefreshDiagnosis();
        panel.Controls.Add(close);
        panel.Controls.Add(refresh);
        root.Controls.Add(panel, 0, 4);

        Controls.Add(root);
        UiDpiHelper.Normalize(this);
        Shown += (_, __) => RefreshDiagnosis();
    }

    private void RefreshDiagnosis()
    {
        var d = BeginnerDiagnosticService.Diagnose(_state);
        _summary.Text = d.Overall;
        _summary.ForeColor = d.Problems.Count == 0 ? Color.DarkGreen : Color.DarkOrange;
        _problems.Items.Clear();
        if (d.Problems.Count == 0) _problems.Items.Add("没有发现明显问题。 ");
        foreach (var p in d.Problems) _problems.Items.Add("• " + p.Trim());
        _actions.Items.Clear();
        _actions.Items.Add("建议处理顺序：");
        foreach (var a in d.Actions.Select((x, i) => (x, i))) _actions.Items.Add((a.i + 1) + "．" + a.x.Trim());
    }
}
