using System;
using System.Threading;

namespace TiaMcpServer;

/// <summary>
/// TIA 操作读写锁。读取可并行，任何修改、下载和在线操作严格串行；
/// Lease 使用 using 自动释放，避免异常路径残留控制锁。
/// </summary>
public sealed class SafeTiaOperationCoordinator : IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

    public IDisposable EnterRead(CancellationToken cancellationToken = default) => Enter(false, cancellationToken);
    public IDisposable EnterWrite(CancellationToken cancellationToken = default) => Enter(true, cancellationToken);

    private IDisposable Enter(bool write, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entered = write ? _gate.TryEnterWriteLock(200) : _gate.TryEnterReadLock(200);
            if (entered) return new Lease(_gate, write);
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private sealed class Lease : IDisposable
    {
        private ReaderWriterLockSlim? _owner;
        private readonly bool _write;

        public Lease(ReaderWriterLockSlim owner, bool write)
        {
            _owner = owner;
            _write = write;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;
            if (_write) owner.ExitWriteLock();
            else owner.ExitReadLock();
        }
    }
}
