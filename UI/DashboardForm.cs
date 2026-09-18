using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

/// <summary>
/// 中文图形控制中心。所有固定界面文字均使用中文；外部技术标识默认不直接展示。
/// </summary>
internal sealed class DashboardForm : Form
{
    private readonly McpServer _server;
    private readonly ServerRuntimeState _state;
    private readonly Timer _uiTimer;
    private DateTime _lastTiaRefresh = DateTime.MinValue;
    private int _lastLogCount;
    private string _lastChangeFingerprint = "";
    private bool _updatingSettings;
    private bool _changingExecutionMode;

    private readonly Label _serverState = NewValueLabel();
    private readonly Label _serverDetails = NewValueLabel();
    private readonly Label _clientState = NewValueLabel();
    private readonly Label _clientDetails = NewValueLabel();
    private readonly Label _tiaState = NewValueLabel();
    private readonly Label _tiaDetails = NewValueLabel();
    private readonly Label _sandboxState = NewValueLabel();
    private readonly Label _sandboxDetails = NewValueLabel();
    private readonly Label _lastError = NewValueLabel();
    private readonly Label _beginnerStatus = NewValueLabel();

    private readonly ComboBox _mode = new ComboBox();
    private readonly GroupBox _approvalBox = new GroupBox();
    private readonly Label _approvalText = NewValueLabel();
    private readonly ListBox _logs = new ListBox();
    private readonly ListView _changes = new ListView();
    private readonly Button _createSandbox = new Button();
    private readonly Button _leaveSandbox = new Button();

    private sealed class ModeChoice
    {
        public ExecutionMode Mode { get; set; }
        public string Text { get; set; } = "";
        public override string ToString() => Text;
    }

    public DashboardForm(McpServer server, ServerRuntimeState state)
    {
        _server = server;
        _state = state;

        Text = "博途智能工程助手控制中心";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(1040, 760);
        Size = new Size(1180, 840);
        Font = SystemFonts.MessageBoxFont;

        BuildLayout();

        _uiTimer = new Timer { Interval = 500 };
        _uiTimer.Tick += (_, __) => RefreshUi();
        _uiTimer.Start();
        Shown += (_, __) => _ = RefreshTiaAsync();
        FormClosed += (_, __) =>
        {
            _state.ResolveApproval(false);
            _state.HasInteractiveUi = false;
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);
        UiDpiHelper.Normalize(this);

        var titlePanel = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = "博途智能工程助手",
            Dock = DockStyle.Left,
            Width = 520,
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 19F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var subtitle = new Label
        {
            Text = "工程状态、安全审批与变更审计控制中心",
            Dock = DockStyle.Right,
            Width = 350,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleRight
        };
        _beginnerStatus.Dock = DockStyle.Right;
        _beginnerStatus.Width = 150;
        _beginnerStatus.TextAlign = ContentAlignment.MiddleCenter;
        _beginnerStatus.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10F, FontStyle.Bold);
        titlePanel.Controls.Add(subtitle);
        titlePanel.Controls.Add(_beginnerStatus);
        titlePanel.Controls.Add(title);
        root.Controls.Add(titlePanel, 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildBeginnerHomePage());
        tabs.TabPages.Add(BuildOverviewPage());
        tabs.TabPages.Add(BuildApprovalPage());
        tabs.TabPages.Add(BuildChangesPage());
        tabs.TabPages.Add(BuildRecoveryPage());
        tabs.TabPages.Add(BuildLogsPage());
        tabs.TabPages.Add(BuildSettingsPage());
        root.Controls.Add(tabs, 0, 1);
    }


    private TabPage BuildBeginnerHomePage()
    {
        var page = new TabPage("新手首页");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "第一次使用不用理解工具、接口或工程内部结构。按下面顺序完成：先检查环境，再配置模型客户端，打开或创建工程，最后创建沙盒副本。遇到问题时直接运行一键故障诊断。",
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11F),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(4) };
        for (var i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.Controls.Add(NewActionButton("环境发现中心", "扫描多版本博途、Openness、安装盘符、权限和工作空间", () => { using var f = new EnvironmentCheckForm(_state); f.ShowDialog(this); }), 0, 0);
        actions.Controls.Add(NewActionButton("配置模型客户端", "自动备份并写入本地服务器连接配置", () => { using var f = new ClientConfigForm(); f.ShowDialog(this); }), 1, 0);
        actions.Controls.Add(NewActionButton("创建或打开工程", "新建空白工程，或选择现有多版本博途工程", () => { using var f = new ProjectWizardForm(_server); f.ShowDialog(this); _ = RefreshTiaAsync(); }), 2, 0);
        actions.Controls.Add(NewActionButton("创建沙盒副本", "在独立副本中让智能助手修改，不直接碰原工程", () => _ = CreateSandboxAsync()), 0, 1);
        actions.Controls.Add(NewActionButton("一键故障诊断", "自动判断环境、模型客户端、博途和沙盒哪里没准备好", () => { _server.TryRefreshTiaStatus(); using var f = new BeginnerDiagnosisForm(_state); f.ShowDialog(this); }), 1, 1);
        actions.Controls.Add(NewActionButton("恢复最近安全版本", "把最近安全快照恢复成独立工程副本，不覆盖原工程", () => _ = RestoreLatestSnapshotAsync()), 2, 1);
        root.Controls.Add(actions, 0, 1);

        var safety = new GroupBox { Text = "新手保护", Dock = DockStyle.Fill, Padding = new Padding(14) };
        safety.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "默认禁止下载到现场设备、在线写值、处理器启停和地址修改。工程修改优先在沙盒副本中进行。即使误点到高权限设置，新手保护仍会在服务器内部阻止关键操作。",
            TextAlign = ContentAlignment.MiddleLeft
        });
        root.Controls.Add(safety, 0, 2);
        page.Controls.Add(root);
        return page;
    }

    private static Button NewActionButton(string title, string description, Action action)
    {
        var button = new Button
        {
            Dock = DockStyle.Fill,
            Text = title + "\r\n" + description,
            Margin = new Padding(8),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10F),
            TextAlign = ContentAlignment.MiddleCenter
        };
        button.Click += (_, __) => action();
        return button;
    }

    private TabPage BuildRecoveryPage()
    {
        var page = new TabPage("恢复中心");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "如果智能修改后工程不符合预期，不要继续下载或在线操作。恢复中心会把最近的安全快照恢复成一个新的独立工程副本，不会覆盖你原来的工程文件。",
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11F),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10) };
        var restore = new Button { Text = "恢复最近安全版本", Width = 190, Height = 46 };
        restore.Click += async (_, __) => await RestoreLatestSnapshotAsync();
        var open = new Button { Text = "打开安全快照目录", Width = 170, Height = 46 };
        open.Click += (_, __) => OpenDirectory(_server.GetSafetySnapshotsDirectory());
        buttons.Controls.Add(restore);
        buttons.Controls.Add(open);
        root.Controls.Add(buttons, 0, 1);

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "恢复原则：不覆盖原工程；恢复失败时尽量重新打开恢复前工程；正在使用沙盒时必须先退出沙盒。"
        }, 0, 2);
        page.Controls.Add(root);
        return page;
    }

    private async Task RestoreLatestSnapshotAsync()
    {
        var ask = MessageBox.Show("将把最近一次安全快照恢复为独立工程副本。当前工程会先保存并关闭，但不会被覆盖。是否继续？",
            "恢复安全版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;
        var result = await Task.Run(() => _server.RestoreLatestSafetySnapshot());
        ShowOperationResult(result, "恢复安全版本");
        await RefreshTiaAsync();
    }

    private TabPage BuildOverviewPage()
    {
        var page = new TabPage("状态总览");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        cards.Controls.Add(MakeCard("模型上下文协议服务器状态", _serverState, _serverDetails), 0, 0);
        cards.Controls.Add(MakeCard("智能模型客户端状态", _clientState, _clientDetails), 1, 0);
        cards.Controls.Add(MakeCard("博途状态", _tiaState, _tiaDetails, true), 2, 0);
        layout.Controls.Add(cards, 0, 0);

        var sandboxBox = new GroupBox { Text = "沙盒保护", Dock = DockStyle.Fill, Padding = new Padding(14) };
        var sandboxLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        sandboxLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sandboxLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        var sandboxInfo = new Panel { Dock = DockStyle.Fill };
        _sandboxState.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 15F, FontStyle.Bold);
        _sandboxState.Location = new Point(4, 8);
        _sandboxState.AutoSize = true;
        _sandboxDetails.Location = new Point(4, 48);
        _sandboxDetails.AutoSize = false;
        _sandboxDetails.Size = new Size(650, 70);
        sandboxInfo.Controls.Add(_sandboxState);
        sandboxInfo.Controls.Add(_sandboxDetails);
        sandboxLayout.Controls.Add(sandboxInfo, 0, 0);

        var sandboxButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        _createSandbox.Text = "创建沙盒副本";
        _createSandbox.Width = 300;
        _createSandbox.Height = 38;
        _createSandbox.Click += async (_, __) => await CreateSandboxAsync();
        _leaveSandbox.Text = "退出沙盒并返回原工程";
        _leaveSandbox.Width = 300;
        _leaveSandbox.Height = 38;
        _leaveSandbox.Click += async (_, __) => await LeaveSandboxAsync();
        sandboxButtons.Controls.Add(_createSandbox);
        sandboxButtons.Controls.Add(_leaveSandbox);
        sandboxLayout.Controls.Add(sandboxButtons, 1, 0);
        sandboxBox.Controls.Add(sandboxLayout);
        layout.Controls.Add(sandboxBox, 0, 1);

        var note = new GroupBox { Text = "状态说明", Dock = DockStyle.Fill, Padding = new Padding(14) };
        note.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "智能客户端状态仅表示接入本服务器的模型客户端通信状态，不代表云端模型服务本身一定可用。\r\n沙盒保护会把当前工程归档并恢复到独立目录后再允许离线写入；沙盒模式禁止在线下载、在线写变量、控制运行状态和修改设备地址。",
            TextAlign = ContentAlignment.TopLeft,
            AutoSize = false
        });
        layout.Controls.Add(note, 0, 2);
        return page;
    }

    private TabPage BuildApprovalPage()
    {
        var page = new TabPage("审批中心");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        page.Controls.Add(layout);

        _approvalBox.Text = "待人工批准的危险操作";
        _approvalBox.Dock = DockStyle.Fill;
        _approvalBox.Padding = new Padding(16);
        _approvalText.Dock = DockStyle.Fill;
        _approvalText.AutoSize = false;
        _approvalText.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11F);
        _approvalText.TextAlign = ContentAlignment.TopLeft;
        _approvalBox.Controls.Add(_approvalText);
        layout.Controls.Add(_approvalBox, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var approve = new Button { Text = "批准执行", Width = 130, Height = 40 };
        approve.Click += (_, __) => _state.ResolveApproval(true);
        var reject = new Button { Text = "拒绝操作", Width = 130, Height = 40 };
        reject.Click += (_, __) => _state.ResolveApproval(false);
        buttons.Controls.Add(approve);
        buttons.Controls.Add(reject);
        layout.Controls.Add(buttons, 0, 1);
        return page;
    }

    private TabPage BuildChangesPage()
    {
        var page = new TabPage("变更记录");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        page.Controls.Add(layout);

        _changes.Dock = DockStyle.Fill;
        _changes.View = View.Details;
        _changes.FullRowSelect = true;
        _changes.GridLines = true;
        _changes.Columns.Add("时间", 150);
        _changes.Columns.Add("操作", 420);
        _changes.Columns.Add("风险", 80);
        _changes.Columns.Add("结果", 90);
        _changes.Columns.Add("工程", 260);
        layout.Controls.Add(_changes, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        var openSelected = new Button { Text = "打开选中报告", Width = 140, Height = 38 };
        openSelected.Click += (_, __) => OpenSelectedReport();
        var openReports = new Button { Text = "打开报告目录", Width = 140, Height = 38 };
        openReports.Click += (_, __) => OpenDirectory(_server.GetChangeReportsDirectory());
        var openSnapshots = new Button { Text = "打开安全快照目录", Width = 160, Height = 38 };
        openSnapshots.Click += (_, __) => OpenDirectory(_server.GetSafetySnapshotsDirectory());
        buttons.Controls.Add(openSelected);
        buttons.Controls.Add(openReports);
        buttons.Controls.Add(openSnapshots);
        layout.Controls.Add(buttons, 0, 1);
        return page;
    }

    private TabPage BuildLogsPage()
    {
        var page = new TabPage("运行日志");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        page.Controls.Add(layout);

        _logs.Dock = DockStyle.Fill;
        _logs.HorizontalScrollbar = true;
        _logs.Font = SystemFonts.MessageBoxFont;
        layout.Controls.Add(_logs, 0, 0);

        var errorPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        errorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        errorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        errorPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        errorPanel.Controls.Add(new Label { Text = "最近错误：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _lastError.AutoSize = false;
        _lastError.Dock = DockStyle.Fill;
        _lastError.TextAlign = ContentAlignment.MiddleLeft;
        errorPanel.Controls.Add(_lastError, 1, 0);
        var openTechLogs = new Button { Text = "打开技术日志目录", AutoSize = true, Dock = DockStyle.Fill };
        openTechLogs.Click += (_, __) => OpenDirectory(_server.GetTechnicalLogsDirectory());
        errorPanel.Controls.Add(openTechLogs, 2, 0);
        layout.Controls.Add(errorPanel, 0, 1);
        return page;
    }

    private TabPage BuildSettingsPage()
    {
        var page = new TabPage("执行设置") { AutoScroll = true };
        var box = new GroupBox { Text = "统一 ExecutionPolicy", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(18) };
        page.Controls.Add(box);

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.Controls.Add(layout);

        layout.Controls.Add(new Label { Text = "当前执行模式", AutoSize = true, Padding = new Padding(0, 9, 0, 0) }, 0, 0);
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Dock = DockStyle.Top;
        _mode.Items.Add(new ModeChoice { Mode = ExecutionMode.Safe, Text = "安全模式" });
        _mode.Items.Add(new ModeChoice { Mode = ExecutionMode.Assist, Text = "辅助模式" });
        _mode.Items.Add(new ModeChoice { Mode = ExecutionMode.Auto, Text = "自动模式" });
        _mode.SelectedIndexChanged += (_, __) =>
        {
            if (_updatingSettings || _changingExecutionMode || !(_mode.SelectedItem is ModeChoice choice)) return;

            var previousMode = _state.Snapshot().ExecutionMode;
            _changingExecutionMode = true;
            _uiTimer.Stop();

            try
            {
                if (choice.Mode == ExecutionMode.Auto)
                {
                    var answer = MessageBox.Show(
                        "自动模式会自动放行已明确分级的高风险操作。删除/重置类操作仍需显式二次确认。只有确认现场设备和工程状态安全时才应启用。是否继续？",
                        "切换自动模式",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (answer != DialogResult.Yes)
                    {
                        _updatingSettings = true;
                        try
                        {
                            _mode.SelectedItem = _mode.Items.Cast<ModeChoice>()
                                .First(x => x.Mode == previousMode);
                        }
                        finally
                        {
                            _updatingSettings = false;
                        }
                        return;
                    }
                }

                // 先更新唯一事实来源，再持久化兼容字段，避免刷新线程或旧配置回写模式。
                _state.SetExecutionMode(choice.Mode);

                var settings = BeginnerUserSettings.Load();
                settings.ConfigVersion = 3;
                settings.ExecutionMode = choice.Mode.ToString();
                settings.BeginnerMode = choice.Mode == ExecutionMode.Safe;
                settings.Save();

                _updatingSettings = true;
                try
                {
                    _mode.SelectedItem = _mode.Items.Cast<ModeChoice>()
                        .First(x => x.Mode == choice.Mode);
                }
                finally
                {
                    _updatingSettings = false;
                }
            }
            finally
            {
                _changingExecutionMode = false;
                _uiTimer.Start();
                RefreshUi();
            }
        };
        layout.Controls.Add(_mode, 1, 0);

        layout.Controls.Add(new Label { Text = "模式边界", AutoSize = true, Padding = new Padding(0, 9, 0, 0) }, 0, 1);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            Text = "安全：只读/会话可直接执行，工程写入必须在沙盒中并审批，禁止在线写入。辅助：低/中风险直接执行，高/关键风险审批。自动：已知风险可自动执行。"
        }, 1, 1);

        layout.Controls.Add(new Label { Text = "删除保护", AutoSize = true, Padding = new Padding(0, 9, 0, 0) }, 0, 2);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            Text = "删除、重置等破坏性 Tool 支持 dryRun=true；正式执行必须额外传入 confirm=true，并用 confirmation 指定工具名。此保护在自动模式下也不会关闭。"
        }, 1, 2);

        layout.Controls.Add(new Label { Text = "安全链", AutoSize = true, Padding = new Padding(0, 9, 0, 0) }, 0, 3);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            Text = "所有写操作统一经过 RiskCheck → Permission → ControlLock → Audit → Execute；在线写、Force、下载和在线修改还必须进入 SafeOnlineExecutor。"
        }, 1, 3);
        return page;
    }

    private GroupBox MakeCard(string title, Label stateLabel, Label detailsLabel, bool addRefresh = false)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            Margin = new Padding(5)
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(2)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = addRefresh ? 2 : 1,
            RowCount = 1,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (addRefresh)
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        stateLabel.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 15F, FontStyle.Bold);
        stateLabel.AutoSize = false;
        stateLabel.Dock = DockStyle.Fill;
        stateLabel.TextAlign = ContentAlignment.MiddleLeft;
        stateLabel.Margin = Padding.Empty;
        header.Controls.Add(stateLabel, 0, 0);

        if (addRefresh)
        {
            var refresh = new Button
            {
                Text = "刷新状态",
                AutoSize = true,
                MinimumSize = new Size(86, 30),
                Anchor = AnchorStyles.Right,
                Margin = new Padding(6, 3, 0, 3)
            };
            refresh.Click += (_, __) => _ = RefreshTiaAsync();
            header.Controls.Add(refresh, 1, 0);
        }

        detailsLabel.AutoSize = false;
        detailsLabel.Dock = DockStyle.Fill;
        detailsLabel.TextAlign = ContentAlignment.TopLeft;
        detailsLabel.Margin = new Padding(0, 6, 0, 0);

        content.Controls.Add(header, 0, 0);
        content.Controls.Add(detailsLabel, 0, 1);
        box.Controls.Add(content);
        return box;
    }
    private async Task CreateSandboxAsync()
    {
        _createSandbox.Enabled = false;
        _leaveSandbox.Enabled = false;
        try
        {
            var result = await Task.Run(() => _server.CreateSafeSandbox());
            ShowOperationResult(result, "沙盒操作");
            await RefreshTiaAsync();
        }
        finally
        {
            _createSandbox.Enabled = true;
            _leaveSandbox.Enabled = true;
        }
    }

    private async Task LeaveSandboxAsync()
    {
        _createSandbox.Enabled = false;
        _leaveSandbox.Enabled = false;
        try
        {
            var result = await Task.Run(() => _server.LeaveSafeSandbox(true));
            ShowOperationResult(result, "沙盒操作");
            await RefreshTiaAsync();
        }
        finally
        {
            _createSandbox.Enabled = true;
            _leaveSandbox.Enabled = true;
        }
    }

    private static void ShowOperationResult(string result, string title)
    {
        try
        {
            var token = JObject.Parse(result ?? "{}");
            var ok = token["success"]?.Value<bool>() == true;
            var msg = ok ? token["message"]?.ToString() : token["error"]?.ToString();
            MessageBox.Show(ToChineseUiText(msg ?? (ok ? "操作已完成。" : "操作失败。")), title,
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch
        {
            MessageBox.Show("操作已结束，请查看运行日志。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task RefreshTiaAsync()
    {
        _lastTiaRefresh = DateTime.Now;
        await Task.Run(() => _server.TryRefreshTiaStatus());
    }

    private void RefreshUi()
    {
        var s = _state.Snapshot();

        _beginnerStatus.Text = s.ExecutionMode == ExecutionMode.Safe ? "安全模式" : s.ExecutionMode == ExecutionMode.Assist ? "辅助模式" : "自动模式";
        _beginnerStatus.ForeColor = s.ExecutionMode == ExecutionMode.Safe ? Color.ForestGreen : s.ExecutionMode == ExecutionMode.Assist ? Color.DarkOrange : Color.Firebrick;

        _serverState.Text = s.ServerRunning ? "运行中" : "已停止";
        _serverState.ForeColor = s.ServerRunning ? Color.ForestGreen : Color.Firebrick;
        var uptime = s.ServerRunning ? DateTime.Now - s.StartedAt : TimeSpan.Zero;
        _serverDetails.Text = $"版本：{s.Version}\r\n协议版本：{s.ProtocolVersion}\r\n工具集：{ProfileText(s.Profile)}　可用工具：{s.ToolCount}\r\n请求次数：{s.RequestCount}　工具调用：{s.ToolCallCount}\r\n运行时长：{uptime:hh\\:mm\\:ss}";

        _clientState.Text = ClientStateText(s.AiState);
        _clientState.ForeColor = s.AiState == AiClientState.Busy ? Color.RoyalBlue :
            (s.AiState == AiClientState.Idle || s.AiState == AiClientState.Initialized ? Color.ForestGreen : Color.DimGray);
        var currentClient = string.IsNullOrWhiteSpace(s.CurrentClientName) ? (string.IsNullOrWhiteSpace(s.ClientName) ? "等待连接" : s.ClientName) : s.CurrentClientName;
        var others = s.ActiveClientNames == null ? Array.Empty<string>() : s.ActiveClientNames.Where(x => !string.Equals(x, currentClient, StringComparison.OrdinalIgnoreCase)).ToArray();
        var otherClients = others.Length == 0 ? "无" : string.Join("、", others.Select(x => x + "（只读）"));
        var role = s.IsController ? "控制端（可按安全策略写入）" : "只读端";
        _clientDetails.Text = $"通信状态：{ClientStateText(s.AiState)}\r\n当前客户端：{currentClient}\r\n当前控制端：{(string.IsNullOrWhiteSpace(s.ControllerClientName) ? "等待控制端" : s.ControllerClientName)}\r\n本端角色：{role}\r\n其他客户端：{otherClients}\r\n当前任务：{(string.IsNullOrWhiteSpace(s.ActiveToolDescription) ? "无" : ToChineseUiText(s.ActiveToolDescription))}\r\n最近任务：{(string.IsNullOrWhiteSpace(s.LastToolDescription) ? "无" : ToChineseUiText(s.LastToolDescription))}";

        _tiaState.Text = s.TiaConnected ? "已连接" : (s.TiaRunningProcessCount > 0 ? "已检测到运行实例" : "未连接");
        _tiaState.ForeColor = s.TiaConnected ? Color.ForestGreen : (s.TiaRunningProcessCount > 0 ? Color.DarkOrange : Color.Firebrick);
        var projectText = string.IsNullOrWhiteSpace(s.TiaProjectName) ? "未打开工程" : "已打开工程";
        _tiaDetails.Text = $"运行实例：{s.TiaRunningProcessCount}\r\n进程编号：{(s.TiaProcessId?.ToString() ?? "无")}\r\n工程状态：{projectText}\r\n工程路径：{(string.IsNullOrWhiteSpace(s.TiaProjectPath) ? "未读取" : "已读取")}";

        _sandboxState.Text = s.SandboxActive ? "沙盒已启用" : "沙盒未启用";
        _sandboxState.ForeColor = s.SandboxActive ? Color.ForestGreen : Color.DimGray;
        _sandboxDetails.Text = s.SandboxActive
            ? "当前操作对象是独立工程副本；原工程保持不变。退出沙盒后会重新打开原工程。"
            : "当前没有进入沙盒工程。沙盒保护模式下，离线写操作必须先创建沙盒副本。";
        _createSandbox.Enabled = !s.SandboxActive && s.TiaConnected && !string.IsNullOrWhiteSpace(s.TiaProjectName);
        _leaveSandbox.Enabled = s.SandboxActive;

        _lastError.Text = string.IsNullOrWhiteSpace(s.LastError) ? "无" : "存在错误，请查看对应变更记录或技术日志。";

        if (!_changingExecutionMode)
        {
            _updatingSettings = true;
            try
            {
                var selected = _mode.Items.Cast<ModeChoice>().FirstOrDefault(x => x.Mode == s.ExecutionMode);
                if (selected != null && !ReferenceEquals(_mode.SelectedItem, selected))
                    _mode.SelectedItem = selected;
            }
            finally
            {
                _updatingSettings = false;
            }
        }

        _approvalBox.Enabled = s.PendingApproval != null;
        _approvalBox.BackColor = s.PendingApproval == null ? SystemColors.Control : Color.LemonChiffon;
        _approvalText.Text = s.PendingApproval == null
            ? "当前没有等待批准的危险操作。"
            : $"风险等级：{RiskText(s.PendingApproval.RiskLevel)}\r\n影响范围：{ScopeText(s.PendingApproval.Scope)}\r\n申请时间：{s.PendingApproval.CreatedAt:yyyy-MM-dd HH:mm:ss}\r\n\r\n{s.PendingApproval.Summary}\r\n\r\n请确认当前工程和设备状态后再决定是否批准。";

        if (s.Logs.Count != _lastLogCount)
        {
            _logs.BeginUpdate();
            _logs.Items.Clear();
            foreach (var line in s.Logs) _logs.Items.Add(ToChineseUiText(line));
            if (_logs.Items.Count > 0) _logs.TopIndex = _logs.Items.Count - 1;
            _logs.EndUpdate();
            _lastLogCount = s.Logs.Count;
        }

        var fingerprint = string.Join("|", s.ChangeRecords.Select(x => x.Id));
        if (!string.Equals(fingerprint, _lastChangeFingerprint, StringComparison.Ordinal))
        {
            _changes.BeginUpdate();
            _changes.Items.Clear();
            foreach (var change in s.ChangeRecords)
            {
                var item = new ListViewItem(change.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(ToChineseUiText(change.Operation));
                item.SubItems.Add(RiskText(change.RiskLevel));
                item.SubItems.Add(change.Result == "成功" ? "成功" : "失败");
                item.SubItems.Add(string.IsNullOrWhiteSpace(change.ProjectName) ? "未记录" : ToChineseUiText(change.ProjectName));
                item.Tag = change.ReportPath;
                _changes.Items.Add(item);
            }
            _changes.EndUpdate();
            _lastChangeFingerprint = fingerprint;
        }

        if ((DateTime.Now - _lastTiaRefresh).TotalSeconds >= 3 && string.IsNullOrEmpty(s.ActiveTool))
            _ = RefreshTiaAsync();
    }

    private void OpenSelectedReport()
    {
        if (_changes.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选择一条变更记录。", "变更记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var path = _changes.SelectedItems[0].Tag as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show("对应报告文件不存在。", "变更记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        OpenPath(path);
    }

    private static void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            OpenPath(path);
        }
        catch
        {
            MessageBox.Show("无法打开目录，请检查文件系统权限。", "文件目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show("无法打开目标文件或目录。", "文件访问", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string ClientStateText(AiClientState state)
    {
        switch (state)
        {
            case AiClientState.Waiting: return "等待连接";
            case AiClientState.Initialized: return "已建立连接";
            case AiClientState.Busy: return "正在执行";
            case AiClientState.Idle: return "空闲";
            case AiClientState.Disconnected: return "已断开";
            default: return "未知";
        }
    }

    private static string ProfileText(string profile)
    {
        switch ((profile ?? "").ToLowerInvariant())
        {
            case "all": return "全部功能";
            case "project": return "工程管理";
            case "plc": return "可编程控制器";
            case "hmi": return "人机界面";
            case "network": return "网络与通信";
            case "online": return "在线操作";
            case "advanced": return "高级功能";
            default: return "自定义";
        }
    }

    private static string RiskText(ToolRiskLevel risk)
    {
        switch (risk)
        {
            case ToolRiskLevel.Low: return "低";
            case ToolRiskLevel.Medium: return "中";
            case ToolRiskLevel.High: return "高";
            case ToolRiskLevel.Critical: return "关键";
            default: return "未知";
        }
    }

    private static string ScopeText(ToolOperationScope scope)
    {
        switch (scope)
        {
            case ToolOperationScope.ReadOnly: return "只读查询";
            case ToolOperationScope.Session: return "工程会话";
            case ToolOperationScope.Project: return "离线工程";
            case ToolOperationScope.OnlineDevice: return "在线设备";
            case ToolOperationScope.FileSystem: return "本机文件";
            case ToolOperationScope.Security: return "安全与权限";
            default: return "未知";
        }
    }

    /// <summary>
    /// 图形界面默认不展示英文技术标识。路径、工具内部名称和第三方异常文本保留在报告文件中。
    /// </summary>
    private static string ToChineseUiText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text;
        value = Regex.Replace(value, @"[A-Za-z][A-Za-z0-9_./:\\-]*", "技术标识");
        value = Regex.Replace(value, @"技术标识(?:\s+技术标识)+", "技术信息");
        return value;
    }

    private static Label NewValueLabel() => new Label
    {
        AutoSize = true,
        Text = "无",
        UseMnemonic = false
    };
}

