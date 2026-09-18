using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TiaMcpServer;

public enum AiClientState
{
    Waiting,
    Initialized,
    Busy,
    Idle,
    Disconnected
}

public enum ToolRiskLevel
{
    Unknown = -1,
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum ToolOperationScope
{
    ReadOnly,
    Session,
    Project,
    OnlineDevice,
    FileSystem,
    Security
}

public sealed class ToolMetadata
{
    public string OriginalName { get; set; } = "";
    public ToolRiskLevel RiskLevel { get; set; } = ToolRiskLevel.Unknown;
    public bool ReadOnly { get; set; }
    public bool Destructive { get; set; }
    public bool Idempotent { get; set; }
    public bool OpenWorld { get; set; }
    public bool MutatesProject { get; set; }
    public bool TouchesOnlineDevice { get; set; }
    public bool RequiresSnapshot { get; set; }
    public bool LongRunning { get; set; }
    public ToolOperationScope Scope { get; set; } = ToolOperationScope.Project;
}

public sealed class PendingApprovalInfo
{
    public Guid Id { get; set; }
    public string ToolName { get; set; } = "";
    public string ToolDescription { get; set; } = "";
    public ToolRiskLevel RiskLevel { get; set; }
    public ToolOperationScope Scope { get; set; }
    public string Summary { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class ChangeRecordSummary
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; }
    public string Operation { get; set; } = "";
    public ToolRiskLevel RiskLevel { get; set; }
    public string Result { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
}

public sealed class RuntimeStatusSnapshot
{
    public bool ServerRunning { get; set; }
    public DateTime StartedAt { get; set; }
    public string Version { get; set; } = "";
    public string ProtocolVersion { get; set; } = "";
    public string Profile { get; set; } = "";
    public int ToolCount { get; set; }
    public long RequestCount { get; set; }
    public long ToolCallCount { get; set; }
    public string LastError { get; set; } = "";
    public string ActiveTool { get; set; } = "";
    public string ActiveToolDescription { get; set; } = "";
    public DateTime LastActivityAt { get; set; }

    public AiClientState AiState { get; set; }
    public string ClientName { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public string CurrentClientName { get; set; } = "";
    public string ControllerClientName { get; set; } = "";
    public bool IsController { get; set; }
    public IReadOnlyList<string> ActiveClientNames { get; set; } = Array.Empty<string>();
    public string LastTool { get; set; } = "";
    public string LastToolDescription { get; set; } = "";

    public bool TiaConnected { get; set; }
    public int? TiaProcessId { get; set; }
    public int TiaRunningProcessCount { get; set; }
    public string TiaProjectName { get; set; } = "";
    public string TiaProjectPath { get; set; } = "";
    public string TiaStatusError { get; set; } = "";

    public bool SandboxActive { get; set; }
    public string SandboxProjectPath { get; set; } = "";
    public string SandboxOriginalProjectPath { get; set; } = "";

    public ExecutionMode ExecutionMode { get; set; }
    public bool AutoApproveDangerous { get; set; }
    public bool BeginnerMode { get; set; }
    public PendingApprovalInfo? PendingApproval { get; set; }
    public IReadOnlyList<string> Logs { get; set; } = Array.Empty<string>();
    public IReadOnlyList<ChangeRecordSummary> ChangeRecords { get; set; } = Array.Empty<ChangeRecordSummary>();
}

/// <summary>
/// 线程安全的运行时状态中心。界面只读取这里，不直接访问标准输入输出。
/// </summary>
public sealed class ServerRuntimeState
{
    private readonly object _gate = new();
    private readonly Queue<string> _logs = new();
    private readonly Queue<ChangeRecordSummary> _changeRecords = new();
    private readonly int _maxLogs;
    private readonly int _maxChanges;
    private readonly string _technicalLogDirectory;
    private readonly string _technicalLogPath;
    private ManualResetEventSlim? _approvalWaiter;
    private bool? _approvalDecision;

    private bool _serverRunning;
    private DateTime _startedAt;
    private string _version = "";
    private string _protocolVersion = "";
    private string _profile = "";
    private int _toolCount;
    private long _requestCount;
    private long _toolCallCount;
    private string _lastError = "";
    private string _activeTool = "";
    private string _activeToolDescription = "";
    private DateTime _lastActivityAt;

    private AiClientState _aiState = AiClientState.Waiting;
    private string _clientName = "";
    private string _clientVersion = "";
    private string _currentClientName = "";
    private string _controllerClientName = "";
    private bool _isController;
    private IReadOnlyList<string> _activeClientNames = Array.Empty<string>();
    private string _lastTool = "";
    private string _lastToolDescription = "";

    private bool _tiaConnected;
    private int? _tiaProcessId;
    private int _tiaRunningProcessCount;
    private string _tiaProjectName = "";
    private string _tiaProjectPath = "";
    private string _tiaStatusError = "";

    private bool _sandboxActive;
    private string _sandboxProjectPath = "";
    private string _sandboxOriginalProjectPath = "";

    private readonly ExecutionPolicy _executionPolicy = new ExecutionPolicy();
    private PendingApprovalInfo? _pendingApproval;

    public bool HasInteractiveUi { get; set; }

    public ServerRuntimeState(int maxLogs = 800, int maxChanges = 200)
    {
        _maxLogs = Math.Max(50, maxLogs);
        _maxChanges = Math.Max(20, maxChanges);
        var configuredRoot = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIR");
        var dataRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "博途智能工程助手", "运行数据");
        _technicalLogDirectory = Path.Combine(dataRoot, "技术日志");
        _technicalLogPath = Path.Combine(_technicalLogDirectory, DateTime.Now.ToString("yyyyMMdd") + ".log");
        try { Directory.CreateDirectory(_technicalLogDirectory); } catch { }
    }

    public string TechnicalLogDirectory => _technicalLogDirectory;

    public void MarkServerStarted(string version, string protocolVersion, string profile, int toolCount)
    {
        lock (_gate)
        {
            _serverRunning = true;
            _startedAt = DateTime.Now;
            _version = version;
            _protocolVersion = protocolVersion;
            _profile = profile;
            _toolCount = toolCount;
            _lastActivityAt = DateTime.Now;
        }
        AddLog($"通信服务器已启动，版本 {version}，可用工具 {toolCount} 个");
    }

    public void MarkServerStopped()
    {
        lock (_gate)
        {
            _serverRunning = false;
            _aiState = AiClientState.Disconnected;
            _activeTool = "";
            _activeToolDescription = "";
        }
        AddLog("通信服务器已停止");
    }

    public void SetProfile(string profile, int toolCount)
    {
        lock (_gate)
        {
            _profile = profile;
            _toolCount = toolCount;
            _lastActivityAt = DateTime.Now;
        }
        AddLog($"工具集已切换，当前工具数量 {toolCount}");
    }

    public void MarkRequest()
    {
        lock (_gate)
        {
            _requestCount++;
            _lastActivityAt = DateTime.Now;
        }
    }

    public void MarkClientInitialized(string name, string version, string protocolVersion)
    {
        lock (_gate)
        {
            _clientName = name ?? "";
            _clientVersion = version ?? "";
            _protocolVersion = protocolVersion ?? _protocolVersion;
            _aiState = AiClientState.Initialized;
            _lastActivityAt = DateTime.Now;
        }
        AddLog("智能客户端已建立通信连接");
    }

    public void UpdateClientControlState(ClientControlSnapshot control)
    {
        if (control == null) return;
        lock (_gate)
        {
            _currentClientName = control.CurrentClientName ?? "";
            _controllerClientName = control.ControllerClientName ?? "";
            _isController = control.IsController;
            _activeClientNames = control.ActiveClientNames?.ToArray() ?? Array.Empty<string>();
        }
    }

    public void MarkToolStarted(string toolName, string description = "")
    {
        lock (_gate)
        {
            _toolCallCount++;
            _activeTool = toolName ?? "";
            _activeToolDescription = description ?? "";
            _lastTool = toolName ?? "";
            _lastToolDescription = description ?? "";
            _aiState = AiClientState.Busy;
            _lastActivityAt = DateTime.Now;
        }
        AddLog(string.IsNullOrWhiteSpace(description) ? "智能客户端开始执行工程操作" : "开始执行：" + description);
    }

    public void MarkToolCompleted(string toolName, string description = "")
    {
        lock (_gate)
        {
            _activeTool = "";
            _activeToolDescription = "";
            _lastTool = toolName ?? "";
            _lastToolDescription = description ?? _lastToolDescription;
            _aiState = AiClientState.Idle;
            _lastActivityAt = DateTime.Now;
        }
        AddLog(string.IsNullOrWhiteSpace(description) ? "工程操作执行完成" : "执行完成：" + description);
    }

    public void MarkToolFailed(string toolName, string error, string description = "")
    {
        lock (_gate)
        {
            _activeTool = "";
            _activeToolDescription = "";
            _lastTool = toolName ?? "";
            _lastToolDescription = description ?? _lastToolDescription;
            _lastError = error ?? "";
            _aiState = AiClientState.Idle;
            _lastActivityAt = DateTime.Now;
        }
        AddLog("操作失败：" + (string.IsNullOrWhiteSpace(description) ? "工程操作" : description) + "；" + error);
    }

    public void SetError(string error)
    {
        lock (_gate) _lastError = error ?? "";
        if (!string.IsNullOrWhiteSpace(error)) AddLog("错误：" + error);
    }

    public void UpdateTiaStatus(bool connected, int? processId, int runningCount, string projectName, string projectPath, string error = "")
    {
        lock (_gate)
        {
            _tiaConnected = connected;
            _tiaProcessId = processId;
            _tiaRunningProcessCount = runningCount;
            _tiaProjectName = projectName ?? "";
            _tiaProjectPath = projectPath ?? "";
            _tiaStatusError = error ?? "";
        }
    }

    public void SetSandboxStatus(bool active, string sandboxProjectPath, string originalProjectPath)
    {
        bool changed;
        lock (_gate)
        {
            changed = _sandboxActive != active ||
                      !string.Equals(_sandboxProjectPath, sandboxProjectPath ?? "", StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(_sandboxOriginalProjectPath, originalProjectPath ?? "", StringComparison.OrdinalIgnoreCase);
            _sandboxActive = active;
            _sandboxProjectPath = sandboxProjectPath ?? "";
            _sandboxOriginalProjectPath = originalProjectPath ?? "";
        }
        if (changed) AddLog(active ? "已进入沙盒工程，原工程保持不变" : "已退出沙盒工程");
    }

    public void SetExecutionMode(ExecutionMode mode)
    {
        lock (_gate) _executionPolicy.SetMode(mode);
        AddLog("执行模式已切换为" + mode);
    }

    /// <summary>旧配置兼容入口：开启等价于安全模式，关闭时从安全模式切换到辅助模式。</summary>
    public void SetBeginnerMode(bool enabled)
    {
        lock (_gate)
        {
            if (enabled) _executionPolicy.SetMode(ExecutionMode.Safe);
            else if (_executionPolicy.Mode == ExecutionMode.Safe) _executionPolicy.SetMode(ExecutionMode.Assist);
        }
        AddLog(enabled ? "执行模式已切换为安全模式" : "执行模式已切换为辅助模式");
    }

    /// <summary>旧配置兼容入口：开启等价于自动模式，关闭时从自动模式回退到辅助模式。</summary>
    public void SetAutoApproveDangerous(bool enabled)
    {
        lock (_gate)
        {
            if (enabled) _executionPolicy.SetMode(ExecutionMode.Auto);
            else if (_executionPolicy.Mode == ExecutionMode.Auto) _executionPolicy.SetMode(ExecutionMode.Assist);
        }
        AddLog(enabled ? "执行模式已切换为自动模式" : "自动模式已关闭");
    }

    public bool RequestApproval(string toolName, string toolDescription, ToolRiskLevel risk, ToolOperationScope scope, string summary, TimeSpan timeout)
    {
        if (!HasInteractiveUi) return false;

        ManualResetEventSlim waiter;
        lock (_gate)
        {
            if (_pendingApproval != null) return false;
            _approvalDecision = null;
            waiter = new ManualResetEventSlim(false);
            _approvalWaiter = waiter;
            _pendingApproval = new PendingApprovalInfo
            {
                Id = Guid.NewGuid(),
                ToolName = toolName,
                ToolDescription = toolDescription ?? "",
                RiskLevel = risk,
                Scope = scope,
                Summary = summary ?? "",
                CreatedAt = DateTime.Now
            };
        }
        AddLog("存在一项高风险操作，正在等待人工审批");

        var signaled = waiter.Wait(timeout);
        bool approved;
        lock (_gate)
        {
            approved = signaled && _approvalDecision == true;
            _pendingApproval = null;
            _approvalWaiter?.Dispose();
            _approvalWaiter = null;
            _approvalDecision = null;
        }
        AddLog(approved ? "人工审批已通过" : "人工审批已拒绝或超时");
        return approved;
    }

    public void ResolveApproval(bool approved)
    {
        lock (_gate)
        {
            if (_pendingApproval == null || _approvalWaiter == null) return;
            _approvalDecision = approved;
            _approvalWaiter.Set();
        }
    }

    public void AddChangeRecord(ChangeRecordSummary record)
    {
        if (record == null) return;
        lock (_gate)
        {
            _changeRecords.Enqueue(record);
            while (_changeRecords.Count > _maxChanges) _changeRecords.Dequeue();
        }
    }

    public void AddLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (_gate)
        {
            _logs.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
            while (_logs.Count > _maxLogs) _logs.Dequeue();
        }
        try
        {
            Directory.CreateDirectory(_technicalLogDirectory);
            File.AppendAllText(_technicalLogPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }


    public void RecoverStaleRuntime(TimeSpan staleAfter)
    {
        bool recovered = false;
        lock (_gate)
        {
            if (_aiState == AiClientState.Busy && _lastActivityAt != default && DateTime.Now - _lastActivityAt > staleAfter)
            {
                _aiState = AiClientState.Idle;
                _activeTool = "";
                _activeToolDescription = "";
                _lastError = "检测到异常残留的 Busy 状态，运行时已自动恢复。";
                recovered = true;
            }
        }
        if (recovered) AddLog("运行时健康检测已清理异常 Busy 状态");
    }

    public RuntimeStatusSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RuntimeStatusSnapshot
            {
                ServerRunning = _serverRunning,
                StartedAt = _startedAt,
                Version = _version,
                ProtocolVersion = _protocolVersion,
                Profile = _profile,
                ToolCount = _toolCount,
                RequestCount = _requestCount,
                ToolCallCount = _toolCallCount,
                LastError = _lastError,
                ActiveTool = _activeTool,
                ActiveToolDescription = _activeToolDescription,
                LastActivityAt = _lastActivityAt,
                AiState = _aiState,
                ClientName = _clientName,
                ClientVersion = _clientVersion,
                CurrentClientName = _currentClientName,
                ControllerClientName = _controllerClientName,
                IsController = _isController,
                ActiveClientNames = _activeClientNames.ToArray(),
                LastTool = _lastTool,
                LastToolDescription = _lastToolDescription,
                TiaConnected = _tiaConnected,
                TiaProcessId = _tiaProcessId,
                TiaRunningProcessCount = _tiaRunningProcessCount,
                TiaProjectName = _tiaProjectName,
                TiaProjectPath = _tiaProjectPath,
                TiaStatusError = _tiaStatusError,
                SandboxActive = _sandboxActive,
                SandboxProjectPath = _sandboxProjectPath,
                SandboxOriginalProjectPath = _sandboxOriginalProjectPath,
                ExecutionMode = _executionPolicy.Mode,
                AutoApproveDangerous = _executionPolicy.AutoApproveDangerous,
                BeginnerMode = _executionPolicy.BeginnerMode,
                PendingApproval = _pendingApproval == null ? null : new PendingApprovalInfo
                {
                    Id = _pendingApproval.Id,
                    ToolName = _pendingApproval.ToolName,
                    ToolDescription = _pendingApproval.ToolDescription,
                    RiskLevel = _pendingApproval.RiskLevel,
                    Scope = _pendingApproval.Scope,
                    Summary = _pendingApproval.Summary,
                    CreatedAt = _pendingApproval.CreatedAt
                },
                Logs = _logs.ToArray(),
                ChangeRecords = _changeRecords.Reverse().ToArray()
            };
        }
    }
}
