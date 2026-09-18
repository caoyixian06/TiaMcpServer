using System;
using System.Drawing;
using System.Windows.Forms;

namespace TiaMcpServer;

internal sealed class EnvironmentCheckForm : Form
{
    private readonly ListView _list = new ListView();
    private readonly Label _summary = new Label();
    private readonly ServerRuntimeState? _state;

    public EnvironmentCheckForm(ServerRuntimeState? state = null)
    {
        _state = state;
        Text = "环境发现中心";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Size = new Size(880, 560);
        MinimumSize = new Size(760, 480);
        Font = SystemFonts.MessageBoxFont;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 3, Padding = new Padding(14) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        _summary.Dock = DockStyle.Fill;
        _summary.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12F, FontStyle.Bold);
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_summary, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.Columns.Add("检查项目", 170);
        _list.Columns.Add("结果", 90);
        _list.Columns.Add("说明与处理办法", 560);
        root.Controls.Add(_list, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "关闭", Width = 100, Height = 34 };
        close.Click += (_, __) => Close();
        var reportButton = new Button { Text = "生成一键体检报告", Width = 150, Height = 34 };
        reportButton.Click += (_, __) => GenerateHealthReport();
        var refresh = new Button { Text = "重新检查", Width = 110, Height = 34 };
        refresh.Click += (_, __) => RefreshReport();
        buttons.Controls.Add(close);
        buttons.Controls.Add(reportButton);
        buttons.Controls.Add(refresh);
        root.Controls.Add(buttons, 0, 2);

        Controls.Add(root);
        UiDpiHelper.Normalize(this);
        _list.Resize += (_, __) => ResizeDetailColumn();
        Shown += (_, __) => RefreshReport();
    }

    private void GenerateHealthReport()
    {
        try
        {
            var path = HealthReportService.GenerateHtml(EnvironmentCheckService.Run(), _state);
            var open = MessageBox.Show("体检报告已生成。是否立即打开？\r\n\r\n" + path, "一键体检报告", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (open == DialogResult.Yes) HealthReportService.OpenReport(path);
        }
        catch
        {
            MessageBox.Show("体检报告生成失败，请检查工作空间写入权限。", "一键体检报告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ResizeDetailColumn()
    {
        if (_list.Columns.Count < 3) return;
        _list.Columns[2].Width = Math.Max(240, _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 28);
    }

    private void RefreshReport()
    {
        var report = EnvironmentCheckService.Run();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in report.Items)
        {
            var result = item.Level == EnvironmentCheckLevel.Passed ? "通过" : item.Level == EnvironmentCheckLevel.Warning ? "提醒" : "未通过";
            var row = new ListViewItem(item.Name);
            row.SubItems.Add(result);
            row.SubItems.Add(item.Summary + " " + item.Guidance);
            row.ForeColor = item.Level == EnvironmentCheckLevel.Passed ? Color.DarkGreen : item.Level == EnvironmentCheckLevel.Warning ? Color.DarkOrange : Color.Firebrick;
            _list.Items.Add(row);
        }
        _list.EndUpdate();
        ResizeDetailColumn();

        _summary.Text = report.FailedCount == 0
            ? (report.WarningCount == 0 ? "环境检查通过，可以正常使用。" : "环境基本可用，但有提醒项需要关注。")
            : "发现必须处理的问题，请先解决红色项目。";
        _summary.ForeColor = report.FailedCount > 0 ? Color.Firebrick : report.WarningCount > 0 ? Color.DarkOrange : Color.DarkGreen;
    }
}
