using System;
using System.Threading;

namespace TiaMcpServer;

/// <summary>
/// 在线写入统一授权上下文。所有由 MCP 触发的在线写、Force、下载、CPU 控制等操作
/// 必须在安全执行链建立的授权范围内运行。
/// </summary>
public static class SafeOnlineExecutor
{
    private static readonly AsyncLocal<int> AuthorizedDepth = new AsyncLocal<int>();

    public static IDisposable EnterAuthorizedScope(ToolMetadata metadata)
    {
        if (metadata == null || !metadata.TouchesOnlineDevice)
            throw new InvalidOperationException("非在线工具不能创建在线写入授权范围。");
        AuthorizedDepth.Value = AuthorizedDepth.Value + 1;
        return new Scope();
    }

    public static bool IsAuthorized => AuthorizedDepth.Value > 0;

    public static void DemandAuthorized(string operationName)
    {
        if (!IsAuthorized)
            throw new InvalidOperationException($"在线操作 {operationName} 被拒绝：必须通过 SafeOnlineExecutor 统一安全执行链。");
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            AuthorizedDepth.Value = Math.Max(0, AuthorizedDepth.Value - 1);
        }
    }
}
