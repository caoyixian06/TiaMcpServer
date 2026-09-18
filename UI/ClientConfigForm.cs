using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TiaMcpServer;

internal sealed class ClientConfigForm : Form
{
    private readonly ListView _list = new ListView();
    private readonly Label _summary = new Label();
    private readonly TextBox _pathPreview = new TextBox();
    private readonly Dictionary<SupportedClient, ClientInstallationInfo> _states = new Dictionary<SupportedClient, ClientInstallationInfo>();

    public ClientConfigForm()
    {
        Text = "AI 客户端适配中心";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Size = new Size(1020, 680);
        MinimumSize = new Size(900, 580);
        Font = SystemFonts.MessageBoxFont;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "V4.1 会自动发现已安装的 AI 客户端。配置流程统一为：检测 → 备份原配置 → 写入 MCP → 重新读取验证。Cherry Studio 与 Trae 若未发现公开稳定配置文件，会生成安全导入配置，不直接修改其内部数据库。",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10F)
        }, 0, 0);

        _summary.Dock = DockStyle.Fill;
        _summary.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10F, FontStyle.Bold);
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_summary, 0, 1);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.MultiSelect = false;
        _list.Columns.Add("客户端", 160);
        _list.Columns.Add("安装", 80);
        _list.Columns.Add("MCP 状态", 110);
        _list.Columns.Add("方式", 100);
        _list.Columns.Add("配置位置", 500);
        root.Controls.Add(_list, 0, 2);
        _pathPreview.Dock = DockStyle.Fill;
        _pathPreview.ReadOnly = true;
        _pathPreview.Multiline = true;
        _pathPreview.WordWrap = false;
        _pathPreview.ScrollBars = ScrollBars.Horizontal;
        _pathPreview.Text = "选择客户端后在此查看完整配置路径";
        root.Controls.Add(_pathPreview, 0, 3);

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var close = new Button { Text = "完成", Width = 100, Height = 34 };
        close.Click += (_, __) => Close();
        var open = new Button { Text = "打开配置位置", Width = 130, Height = 34 };
        open.Click += (_, __) => OpenSelectedDirectory();
        var configure = new Button { Text = "配置选中客户端", Width = 140, Height = 34 };
        configure.Click += (_, __) => ConfigureSelected();
        var configureAll = new Button { Text = "配置全部已安装", Width = 140, Height = 34 };
        configureAll.Click += (_, __) => ConfigureAllInstalled();
        var refresh = new Button { Text = "重新检测", Width = 100, Height = 34 };
        refresh.Click += (_, __) => RefreshStates();
        panel.Controls.Add(close);
        panel.Controls.Add(open);
        panel.Controls.Add(configure);
        panel.Controls.Add(configureAll);
        panel.Controls.Add(refresh);
        root.Controls.Add(panel, 0, 4);

        Controls.Add(root);
        UiDpiHelper.Normalize(this);
        Shown += (_, __) => RefreshStates();
        _list.DoubleClick += (_, __) => ConfigureSelected();
        _list.SelectedIndexChanged += (_, __) => UpdatePathPreview();
        _list.Resize += (_, __) => ResizePathColumn();
    }

    private void UpdatePathPreview()
    {
        if (_list.SelectedItems.Count == 0)
        {
            _pathPreview.Text = "选择客户端后在此查看完整配置路径";
            return;
        }
        _pathPreview.Text = _list.SelectedItems[0].SubItems.Count > 4 ? _list.SelectedItems[0].SubItems[4].Text : "";
        _pathPreview.SelectionStart = 0;
        _pathPreview.SelectionLength = 0;
    }

    private void ResizePathColumn()
    {
        if (_list.Columns.Count < 5) return;
        var fixedWidth = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width + _list.Columns[3].Width + 28;
        _list.Columns[4].Width = Math.Max(220, _list.ClientSize.Width - fixedWidth);
    }

    private void RefreshStates()
    {
        var clients = ClientConfigService.DiscoverClients();
        _states.Clear();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var info in clients)
        {
            _states[info.Client] = info;
            var item = new ListViewItem(info.DisplayName) { Tag = info.Client };
            item.SubItems.Add(info.Installed ? "已发现" : "未发现");
            item.SubItems.Add(info.Configured ? "已配置" : "未配置");
            item.SubItems.Add(info.SupportsDirectWrite ? "自动写入" : "安全导入");
            item.SubItems.Add(info.ConfigPath);
            item.ForeColor = info.Configured ? Color.DarkGreen : info.Installed ? Color.DarkOrange : Color.DimGray;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        ResizePathColumn();
        UpdatePathPreview();
        var installed = clients.Count(x => x.Installed);
        var configured = clients.Count(x => x.Configured);
        _summary.Text = $"已支持 {clients.Count} 类客户端；本机发现 {installed} 个，已配置 {configured} 个。";
        _summary.ForeColor = installed == 0 ? Color.DarkOrange : Color.DarkGreen;
    }

    private SupportedClient? SelectedClient()
    {
        if (_list.SelectedItems.Count == 0 || !(_list.SelectedItems[0].Tag is SupportedClient client)) return null;
        return client;
    }

    private void ConfigureSelected()
    {
        var client = SelectedClient();
        if (client == null)
        {
            MessageBox.Show("请先选择一个客户端。", "AI 客户端适配中心", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ConfigureOne(client.Value);
    }

    private void ConfigureAllInstalled()
    {
        var installed = _states.Values.Where(x => x.Installed).ToList();
        if (installed.Count == 0)
        {
            MessageBox.Show("当前没有自动发现已安装的支持客户端。", "AI 客户端适配中心", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ask = MessageBox.Show($"将为本机发现的 {installed.Count} 个客户端逐个执行配置。所有已有配置都会先备份。是否继续？",
            "批量配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;

        var success = 0;
        var import = 0;
        var messages = new List<string>();
        foreach (var info in installed)
        {
            var result = ClientConfigService.Configure(info.Client, ClientConfigService.ResolveCurrentExecutable());
            if (result.Success) success++;
            if (result.RequiresManualImport) import++;
            messages.Add(info.DisplayName + "：" + (result.Success ? (result.RequiresManualImport ? "已生成导入配置" : "已配置") : "失败"));
        }
        RefreshStates();
        MessageBox.Show($"处理完成：成功 {success}/{installed.Count}，其中需要在客户端内导入 {import} 个。\r\n\r\n" + string.Join("\r\n", messages),
            "批量配置结果", MessageBoxButtons.OK, success == installed.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void ConfigureOne(SupportedClient client)
    {
        var name = ClientConfigService.GetDisplayName(client);
        var ask = MessageBox.Show(
            $"即将为 {name} 配置本地 TIA MCP。现有配置会先自动备份，不会删除其他 MCP 服务器。是否继续？",
            "确认配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;

        var result = ClientConfigService.Configure(client, ClientConfigService.ResolveCurrentExecutable());
        var detail = result.Message;
        if (!string.IsNullOrWhiteSpace(result.BackupPath)) detail += "\r\n\r\n备份：" + result.BackupPath;
        if (!string.IsNullOrWhiteSpace(result.Verification)) detail += "\r\n验证：" + result.Verification;
        if (result.RequiresManualImport) detail += "\r\n导入文件：" + result.ConfigPath;
        MessageBox.Show(detail, name + " 配置", MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        RefreshStates();
    }

    private void OpenSelectedDirectory()
    {
        var client = SelectedClient();
        if (client == null)
        {
            MessageBox.Show("请先选择一个客户端。", "AI 客户端适配中心", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var path = ClientConfigService.GetConfigPath(client.Value);
            var dir = Path.GetDirectoryName(path) ?? "";
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show("无法打开配置位置，请检查文件系统权限。", "AI 客户端适配中心", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
