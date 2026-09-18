using System;
using System.Threading;

namespace TiaMcpServer;

/// <summary>运行时健康检测：清理异常 Busy 状态，并周期刷新连接/锁状态。</summary>
public sealed class RuntimeHealthMonitor : IDisposable
{
    private readonly ServerRuntimeState _state;
    private readonly Action _refresh;
    private readonly Timer _timer;
    private int _running;

    public RuntimeHealthMonitor(ServerRuntimeState state, Action refresh)
    {
        _state = state;
        _refresh = refresh;
        _timer = new Timer(Check, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
    }

    private void Check(object? _)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            _state.RecoverStaleRuntime(TimeSpan.FromMinutes(10));
            _refresh();
        }
        catch (Exception ex)
        {
            _state.AddLog("运行时健康检测失败：" + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
