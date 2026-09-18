using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

internal sealed class ProjectWizardForm : Form
{
    private readonly McpServer _server;
    private readonly TextBox _name = new TextBox();
    private readonly TextBox _parent = new TextBox();
    private readonly Label _hint = new Label();

    public ProjectWizardForm(McpServer server)
    {
        _server = server;
        Text = "新手工程向导";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Size = new Size(720, 450);
        MinimumSize = new Size(640, 400);
        Font = SystemFonts.MessageBoxFont;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "你可以创建一个空白博途工程，也可以打开现有工程。新手建议先创建空白工程或打开练习工程，然后在沙盒副本中让智能助手修改。",
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var group = new GroupBox { Text = "创建空白工程", Dock = DockStyle.Fill, Padding = new Padding(16) };
        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        form.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        form.Controls.Add(new Label { Text = "工程名称", AutoSize = true, Padding = new Padding(0, 10, 0, 0) }, 0, 0);
        _name.Dock = DockStyle.Fill;
        _name.Text = "我的练习工程";
        form.Controls.Add(_name, 1, 0);

        form.Controls.Add(new Label { Text = "保存位置", AutoSize = true, Padding = new Padding(0, 10, 0, 0) }, 0, 1);
        _parent.Dock = DockStyle.Fill;
        _parent.ReadOnly = true;
        _parent.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "博途智能助手", "用户工程");
        form.Controls.Add(_parent, 1, 1);
        var browse = new Button { Text = "选择位置", Dock = DockStyle.Fill };
        browse.Click += (_, __) => ChooseParent();
        form.Controls.Add(browse, 2, 1);

        _hint.Dock = DockStyle.Fill;
        _hint.Text = "创建后会自动打开博途工程。为了安全，创建完成后请继续点击控制中心里的“创建沙盒副本”。";
        _hint.ForeColor = Color.DimGray;
        form.Controls.Add(_hint, 0, 2);
        form.SetColumnSpan(_hint, 3);
        group.Controls.Add(form);
        root.Controls.Add(group, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "取消", Width = 90, Height = 34 };
        cancel.Click += (_, __) => Close();
        var create = new Button { Text = "创建工程", Width = 110, Height = 34 };
        create.Click += async (_, __) => await CreateAsync(create);
        var open = new Button { Text = "打开已有工程", Width = 130, Height = 34 };
        open.Click += async (_, __) => await OpenExistingAsync(open);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(create);
        buttons.Controls.Add(open);
        root.Controls.Add(buttons, 0, 2);

        Controls.Add(root);
        UiDpiHelper.Normalize(this);
    }

    private void ChooseParent()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择工程保存位置", SelectedPath = _parent.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _parent.Text = dialog.SelectedPath;
    }

    private async Task CreateAsync(Button button)
    {
        var name = (_name.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("请输入工程名称。", "新手工程向导", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        name = Regex.Replace(name, "[\\\\/:*?\"<>|]", "_");
        var parent = _parent.Text.Trim();
        if (string.IsNullOrWhiteSpace(parent)) return;
        var target = Path.Combine(parent, name);
        if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0)
        {
            MessageBox.Show("目标位置已经有同名非空文件夹，请换一个工程名称。", "新手工程向导", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        button.Enabled = false;
        try
        {
            Directory.CreateDirectory(target);
            var result = await Task.Run(() => _server.CreateBeginnerProject(target, name));
            ShowResult(result, "创建工程");
            if (IsSuccess(result)) Close();
        }
        finally { button.Enabled = true; }
    }

    private async Task OpenExistingAsync(Button button)
    {
        var discovery = EnvironmentDiscoveryService.Discover();
        var selected = discovery.SelectedTia;
        var versionText = selected?.Version ?? "已安装版本";
        var ext = selected?.ProjectExtension ?? ".ap19";
        using var dialog = new OpenFileDialog
        {
            Title = $"选择博途工程（当前环境 {versionText}）",
            Filter = $"当前博途工程|*{ext}|所有博途工程|*.ap*|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var risk = EnvironmentDiscoveryService.DescribePathRisk(dialog.FileName);
        if (EnvironmentDiscoveryService.ContainsNonAscii(dialog.FileName) || dialog.FileName.Length >= 220)
        {
            var choice = MessageBox.Show(risk + "\r\n\r\n是否仍然继续打开？", "路径兼容性提醒", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes) return;
        }
        var fileVersion = EnvironmentDiscoveryService.ParseVersionNumber(Path.GetExtension(dialog.FileName));
        if (selected != null && fileVersion > 0 && selected.VersionNumber != fileVersion)
        {
            var match = discovery.TiaInstallations.FirstOrDefault(x => x.VersionNumber == fileVersion && x.HasOpenness);
            var msg = match != null
                ? $"该工程是 V{fileVersion}，当前服务器绑定的是 {selected.Version}。已检测到匹配的 {match.Version}：{match.RootPath}。\r\n请从‘环境发现中心’切换/重启到匹配版本后再修改；当前继续打开可能失败。"
                : $"该工程是 V{fileVersion}，当前服务器绑定的是 {selected.Version}，且未检测到带 Openness 的 V{fileVersion}。建议安装/启用对应版本 Openness。";
            MessageBox.Show(msg, "博途版本匹配提醒", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        button.Enabled = false;
        try
        {
            var result = await Task.Run(() => _server.OpenProjectFromUi(dialog.FileName));
            ShowResult(result, "打开工程");
            if (IsSuccess(result)) Close();
        }
        finally { button.Enabled = true; }
    }

    private static bool IsSuccess(string result)
    {
        try { return JObject.Parse(result ?? "{}")["success"]?.Value<bool>() == true; }
        catch { return false; }
    }

    private static void ShowResult(string result, string title)
    {
        try
        {
            var obj = JObject.Parse(result ?? "{}");
            var ok = obj["success"]?.Value<bool>() == true;
            var msg = ok ? obj["message"]?.ToString() : obj["error"]?.ToString();
            MessageBox.Show(string.IsNullOrWhiteSpace(msg) ? (ok ? "操作已完成。" : "操作失败。") : SafeMessage(msg), title,
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch
        {
            MessageBox.Show("操作已结束，请返回控制中心查看状态。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static string SafeMessage(string message)
    {
        // 小白界面不直接暴露第三方英文异常；详细内容保留在技术日志中。
        if (Regex.IsMatch(message ?? "", "[A-Za-z]{4,}")) return "操作没有成功。请运行“一键故障诊断”查看处理建议。";
        return message;
    }
}
