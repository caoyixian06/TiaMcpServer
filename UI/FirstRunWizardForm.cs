using System;
using System.Drawing;
using System.Windows.Forms;

namespace TiaMcpServer;

internal sealed class FirstRunWizardForm : Form
{
    private readonly BeginnerUserSettings _settings;
    private readonly ListView _list = new ListView();
    private readonly Label _headline = new Label();
    private readonly CheckBox _dontShow = new CheckBox();

    public FirstRunWizardForm(BeginnerUserSettings settings)
    {
        _settings = settings;
        Text = "首次使用向导";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Size = new Size(900, 620);
        MinimumSize = new Size(820, 560);
        Font = SystemFonts.MessageBoxFont;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        root.Controls.Add(new Label
        {
            Text = "欢迎使用博途智能工程助手。第一次使用时，先完成下面的环境检查和模型客户端配置。默认已开启新手保护和沙盒保护，不会允许智能助手直接写入现场设备。",
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10F),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _headline.Dock = DockStyle.Fill;
        _headline.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12F, FontStyle.Bold);
        _headline.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_headline, 0, 1);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.Columns.Add("检查项目", 170);
        _list.Columns.Add("结果", 90);
        _list.Columns.Add("说明与处理办法", 580);
        root.Controls.Add(_list, 0, 2);

        _dontShow.Text = "环境通过后，下次启动不再自动显示首次使用向导";
        _dontShow.Checked = true;
        _dontShow.Dock = DockStyle.Fill;
        root.Controls.Add(_dontShow, 0, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var enter = new Button { Text = "进入控制中心", Width = 130, Height = 36 };
        enter.Click += (_, __) => Finish();
        var clients = new Button { Text = "配置模型客户端", Width = 140, Height = 36 };
        clients.Click += (_, __) => { using var f = new ClientConfigForm(); f.ShowDialog(this); };
        var refresh = new Button { Text = "重新检查", Width = 110, Height = 36 };
        refresh.Click += (_, __) => RefreshReport();
        buttons.Controls.Add(enter);
        buttons.Controls.Add(clients);
        buttons.Controls.Add(refresh);
        root.Controls.Add(buttons, 0, 4);

        Controls.Add(root);
        UiDpiHelper.Normalize(this);
        FormClosing += (_, __) =>
        {
            if (!_settings.FirstRunCompleted)
            {
                _settings.FirstRunCompleted = true;
                _settings.Save();
            }
        };
        Shown += (_, __) => RefreshReport();
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
        _headline.Text = report.FailedCount > 0 ? "还有必须解决的问题" : report.WarningCount > 0 ? "环境基本可用，但有提醒" : "环境检查通过";
        _headline.ForeColor = report.FailedCount > 0 ? Color.Firebrick : report.WarningCount > 0 ? Color.DarkOrange : Color.DarkGreen;
    }

    private void Finish()
    {
        var report = EnvironmentCheckService.Run();
        if (report.FailedCount > 0)
        {
            var answer = MessageBox.Show("环境检查仍有未通过项目。你可以进入控制中心查看状态，但部分功能可能无法使用。是否仍然进入？",
                "首次使用向导", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }
        // 首次向导完成后必须保存状态，否则下一次启动会再次进入向导，
        // 同时不要覆盖用户已经关闭的新手保护选择。
        if (_dontShow.Checked || report.FailedCount == 0)
        {
            _settings.FirstRunCompleted = true;
            _settings.Save();
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
