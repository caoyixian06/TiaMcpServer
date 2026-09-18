using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TiaMcpServer;

/// <summary>
/// 多 AI 客户端控制权协调器。
/// 使用独占文件句柄而不是进程内锁，确保 OpenCode / Cursor / Cherry 等分别启动服务器进程时
/// 同一台机器上始终只有一个“写控制端”。其他进程自动降级为只读。
/// </summary>
public sealed class ClientControlCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly string _dataRoot;
    private readonly string _sessionDirectory;
    private readonly string _lockPath;
    private readonly string _controllerInfoPath;
    private readonly string _sessionPath;
    private FileStream? _controllerLock;
    private bool _disposed;
    private string _clientName;

    public ClientControlCoordinator(string? clientName)
    {
        _clientName = NormalizeClientName(clientName);
        var configuredRoot = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIR");
        _dataRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "博途智能工程助手", "运行数据");
        _sessionDirectory = Path.Combine(_dataRoot, "客户端会话");
        _lockPath = Path.Combine(_sessionDirectory, "controller.lock");
        _controllerInfoPath = Path.Combine(_sessionDirectory, "controller.json");
        _sessionPath = Path.Combine(_sessionDirectory, $"session_{Process.GetCurrentProcess().Id}.json");

        try { Directory.CreateDirectory(_sessionDirectory); } catch { }
        WriteSession();
        TryAcquireController();
    }

    public string CurrentClientName
    {
        get { lock (_gate) return _clientName; }
    }

    public void UpdateClientName(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName)) return;
        lock (_gate)
        {
            var normalized = NormalizeClientName(clientName);
            if (_clientName == normalized) return;
            _clientName = normalized;
            WriteSessionUnsafe();
            if (_controllerLock != null) WriteControllerInfoUnsafe();
        }
    }

    public ClientControlSnapshot Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
                return new ClientControlSnapshot { CurrentClientName = _clientName, ControllerClientName = "无", IsController = false };

            WriteSessionUnsafe();
            if (_controllerLock == null)
                TryAcquireControllerUnsafe();

            CleanupStaleSessionsUnsafe();
            var active = ReadActiveSessionsUnsafe();
            var controller = _controllerLock != null ? _clientName : ReadControllerNameUnsafe();
            if (string.IsNullOrWhiteSpace(controller) && _controllerLock == null)
            {
                TryAcquireControllerUnsafe();
                controller = _controllerLock != null ? _clientName : ReadControllerNameUnsafe();
            }

            return new ClientControlSnapshot
            {
                CurrentClientName = _clientName,
                ControllerClientName = string.IsNullOrWhiteSpace(controller) ? "等待控制端" : controller,
                IsController = _controllerLock != null,
                ActiveClientNames = active
            };
        }
    }

    private void TryAcquireController()
    {
        lock (_gate) TryAcquireControllerUnsafe();
    }

    private void TryAcquireControllerUnsafe()
    {
        if (_disposed || _controllerLock != null) return;
        try
        {
            Directory.CreateDirectory(_sessionDirectory);
            // 当前进程要求写权限，且不允许其他进程再以写权限打开，即形成机器级唯一控制权。
            _controllerLock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            WriteControllerInfoUnsafe();
        }
        catch (IOException)
        {
            _controllerLock = null;
        }
        catch (UnauthorizedAccessException)
        {
            _controllerLock = null;
        }
    }

    private void WriteSession()
    {
        lock (_gate) WriteSessionUnsafe();
    }

    private void WriteSessionUnsafe()
    {
        try
        {
            Directory.CreateDirectory(_sessionDirectory);
            var data = new
            {
                pid = Process.GetCurrentProcess().Id,
                client = _clientName,
                heartbeat = DateTime.Now,
                startedAt = Process.GetCurrentProcess().StartTime
            };
            File.WriteAllText(_sessionPath, JsonConvert.SerializeObject(data, Formatting.Indented), new UTF8Encoding(false));
        }
        catch { }
    }

    private void WriteControllerInfoUnsafe()
    {
        try
        {
            var data = new
            {
                pid = Process.GetCurrentProcess().Id,
                client = _clientName,
                since = DateTime.Now,
                role = "controller"
            };
            File.WriteAllText(_controllerInfoPath, JsonConvert.SerializeObject(data, Formatting.Indented), new UTF8Encoding(false));
        }
        catch { }
    }

    private string ReadControllerNameUnsafe()
    {
        try
        {
            if (!File.Exists(_controllerInfoPath)) return "";
            var obj = JsonConvert.DeserializeObject<ControllerInfo>(File.ReadAllText(_controllerInfoPath));
            if (obj == null || obj.Pid <= 0 || !IsProcessAlive(obj.Pid))
            {
                try { File.Delete(_controllerInfoPath); } catch { }
                return "";
            }
            return NormalizeClientName(obj.Client);
        }
        catch { return ""; }
    }

    private List<string> ReadActiveSessionsUnsafe()
    {
        var result = new List<string>();
        try
        {
            foreach (var path in Directory.GetFiles(_sessionDirectory, "session_*.json"))
            {
                try
                {
                    var obj = JsonConvert.DeserializeObject<SessionInfo>(File.ReadAllText(path));
                    if (obj == null || !IsProcessAlive(obj.Pid)) continue;
                    var name = NormalizeClientName(obj.Client);
                    if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
                }
                catch { }
            }
        }
        catch { }
        if (!result.Contains(_clientName, StringComparer.OrdinalIgnoreCase)) result.Add(_clientName);
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void CleanupStaleSessionsUnsafe()
    {
        try
        {
            foreach (var path in Directory.GetFiles(_sessionDirectory, "session_*.json"))
            {
                try
                {
                    var obj = JsonConvert.DeserializeObject<SessionInfo>(File.ReadAllText(path));
                    if (obj == null || !IsProcessAlive(obj.Pid))
                        File.Delete(path);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static string NormalizeClientName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "未知客户端" : name!.Trim();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var wasController = _controllerLock != null;
            try { _controllerLock?.Dispose(); } catch { }
            _controllerLock = null;
            try { File.Delete(_sessionPath); } catch { }
            if (wasController)
            {
                try
                {
                    var obj = File.Exists(_controllerInfoPath)
                        ? JsonConvert.DeserializeObject<ControllerInfo>(File.ReadAllText(_controllerInfoPath))
                        : null;
                    if (obj?.Pid == Process.GetCurrentProcess().Id) File.Delete(_controllerInfoPath);
                }
                catch { }
            }
        }
    }

    private sealed class SessionInfo
    {
        [JsonProperty("pid")] public int Pid { get; set; }
        [JsonProperty("client")] public string Client { get; set; } = "";
        [JsonProperty("heartbeat")] public DateTime Heartbeat { get; set; }
    }

    private sealed class ControllerInfo
    {
        [JsonProperty("pid")] public int Pid { get; set; }
        [JsonProperty("client")] public string Client { get; set; } = "";
    }
}

public sealed class ClientControlSnapshot
{
    public string CurrentClientName { get; set; } = "";
    public string ControllerClientName { get; set; } = "";
    public bool IsController { get; set; }
    public IReadOnlyList<string> ActiveClientNames { get; set; } = Array.Empty<string>();
}
