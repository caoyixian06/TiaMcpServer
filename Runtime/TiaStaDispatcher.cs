using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace TiaMcpServer;

/// <summary>
/// TIA Portal Openness COM 调用调度器：把全部 Openness COM 调用收敛到单一 MTA 线程执行。
/// ★修复★ 曾用 STA 线程：TIA 对象在 STA 线程创建后，其他 MTA 线程（Job 线程）调用
/// Compile 等会被 COM 封送回 STA 线程执行并挂死（TIA 编译完成但调用不返回），
/// 导致 STA 线程永久占用、所有经调度器的工具（含 get_job_status）全部超时。
/// 改为 MTA 后：同一进程内所有线程同属 MTA 公寓，COM 调用直接执行不再跨公寓封送，
/// 编译正常返回；串行安全仍由内部队列 + 外层读写锁保证。
/// 全局唯一实例；Post 非阻塞投递，Run 同步等待并原样重抛异常（保留原始异常类型与栈）。
/// </summary>
public sealed class TiaStaDispatcher : IDisposable
{
    public static TiaStaDispatcher Instance { get; } = new TiaStaDispatcher();

    private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
    private readonly Thread _thread;
    private volatile bool _disposed;

    private TiaStaDispatcher()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TiaStaDispatcher"
        };
        // ★修复★ STA→MTA：见类注释。MTA 下 COM 调用不跨公寓封送，compile 不再挂死。
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[tia-mcp] STA 调度器执行异常: " + ex);
                }
            }
        }
        catch { }
    }

    /// <summary>当前调用线程是否为调度器 STA 线程。</summary>
    public bool IsStaThread => Thread.CurrentThread.ManagedThreadId == _thread.ManagedThreadId;

    /// <summary>非阻塞投递到 STA 线程；不等待完成、不等待结果。</summary>
    public void Post(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (_disposed) throw new ObjectDisposedException(nameof(TiaStaDispatcher));
        _queue.Add(action);
    }

    /// <summary>同步执行：投递到 STA 线程并阻塞等待完成，异常原样重抛。</summary>
    public void Run(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (_disposed) throw new ObjectDisposedException(nameof(TiaStaDispatcher));
        if (IsStaThread) { action(); return; }

        Exception? error = null;
        using (var done = new ManualResetEventSlim(false))
        {
            _queue.Add(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
        }
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>同步执行并返回结果，异常原样重抛。</summary>
    public T Run<T>(Func<T> func)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));
        if (_disposed) throw new ObjectDisposedException(nameof(TiaStaDispatcher));
        if (IsStaThread) return func();

        T result = default!;
        Exception? error = null;
        using (var done = new ManualResetEventSlim(false))
        {
            _queue.Add(() =>
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
        }
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.CompleteAdding();
        try { _thread.Join(TimeSpan.FromSeconds(5)); } catch { }
        _queue.Dispose();
    }
}
