using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TiaMcpServer;


public sealed class TiaOperationRecoveryRequiredException : Exception
{
    public TiaOperationRecoveryRequiredException(string message) : base(message) { }
}

public enum TiaJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    CancelRequested,
    Canceled,
    RecoveryRequired
}

public sealed class TiaJobSnapshot
{
    public string JobId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public TiaJobState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Result { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>长任务 Job 管理：提交、查询和取消。</summary>
public sealed class JobManager : IDisposable
{
    private sealed class JobRecord
    {
        public readonly object Gate = new object();
        public string JobId = "";
        public string ToolName = "";
        public TiaJobState State;
        public DateTime CreatedAt;
        public DateTime? StartedAt;
        public DateTime? FinishedAt;
        public string Result = "";
        public string Error = "";
        public CancellationTokenSource Cancellation = new CancellationTokenSource();
    }

    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new ConcurrentDictionary<string, JobRecord>(StringComparer.OrdinalIgnoreCase);
    private readonly ServerRuntimeState _state;
    private bool _disposed;

    public JobManager(ServerRuntimeState state)
    {
        _state = state;
    }

    public string Submit(string toolName, Func<CancellationToken, string> operation)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JobManager));
        var record = new JobRecord
        {
            JobId = Guid.NewGuid().ToString("N"),
            ToolName = toolName ?? "",
            State = TiaJobState.Queued,
            CreatedAt = DateTime.Now
        };
        _jobs[record.JobId] = record;
        _state.AddLog($"长任务已进入队列：{record.ToolName}，job_id={record.JobId}");

        Task.Run(() => Run(record, operation));
        return record.JobId;
    }

    private void Run(JobRecord record, Func<CancellationToken, string> operation)
    {
        lock (record.Gate)
        {
            if (record.Cancellation.IsCancellationRequested)
            {
                record.State = TiaJobState.Canceled;
                record.FinishedAt = DateTime.Now;
                return;
            }
            record.State = TiaJobState.Running;
            record.StartedAt = DateTime.Now;
        }

        try
        {
            record.Cancellation.Token.ThrowIfCancellationRequested();
            var result = operation(record.Cancellation.Token);
            lock (record.Gate)
            {
                record.Result = result ?? "";
                record.FinishedAt = DateTime.Now;
                record.State = record.Cancellation.IsCancellationRequested
                    ? TiaJobState.RecoveryRequired
                    : TiaJobState.Succeeded;
            }
        }
        catch (TiaOperationRecoveryRequiredException ex)
        {
            lock (record.Gate)
            {
                record.State = TiaJobState.RecoveryRequired;
                record.Error = ex.Message;
                record.FinishedAt = DateTime.Now;
            }
        }
        catch (OperationCanceledException)
        {
            lock (record.Gate)
            {
                record.State = TiaJobState.Canceled;
                record.FinishedAt = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            lock (record.Gate)
            {
                record.State = record.Cancellation.IsCancellationRequested ? TiaJobState.RecoveryRequired : TiaJobState.Failed;
                record.Error = ex.Message;
                record.FinishedAt = DateTime.Now;
            }
        }
        finally
        {
            _state.AddLog($"长任务状态更新：{record.ToolName}，job_id={record.JobId}，state={record.State}");
        }
    }

    public bool Cancel(string jobId, out string message)
    {
        message = "";
        if (!_jobs.TryGetValue(jobId ?? "", out var record))
        {
            message = "未找到指定 job_id。";
            return false;
        }
        lock (record.Gate)
        {
            if (record.State == TiaJobState.Succeeded || record.State == TiaJobState.Failed || record.State == TiaJobState.Canceled)
            {
                message = "任务已经结束。";
                return false;
            }
            record.State = TiaJobState.CancelRequested;
            record.Cancellation.Cancel();
            message = "已提交取消请求；若底层 TIA API 无法立即中止，任务会进入恢复状态。";
            return true;
        }
    }

    public TiaJobSnapshot? Get(string jobId)
    {
        if (!_jobs.TryGetValue(jobId ?? "", out var record)) return null;
        return Snapshot(record);
    }

    public IReadOnlyList<TiaJobSnapshot> List(int limit = 50)
    {
        return _jobs.Values.Select(Snapshot).OrderByDescending(x => x.CreatedAt).Take(Math.Max(1, Math.Min(limit, 200))).ToArray();
    }

    private static TiaJobSnapshot Snapshot(JobRecord record)
    {
        lock (record.Gate)
        {
            return new TiaJobSnapshot
            {
                JobId = record.JobId,
                ToolName = record.ToolName,
                State = record.State,
                CreatedAt = record.CreatedAt,
                StartedAt = record.StartedAt,
                FinishedAt = record.FinishedAt,
                Result = record.Result,
                Error = record.Error
            };
        }
    }

    public string GetJson(string jobId)
    {
        var job = Get(jobId);
        return job == null
            ? JsonConvert.SerializeObject(new { success = false, error = "未找到指定 job_id。" })
            : JsonConvert.SerializeObject(new { success = true, job }, Formatting.Indented);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var record in _jobs.Values)
        {
            try { record.Cancellation.Cancel(); } catch { }
            record.Cancellation.Dispose();
        }
    }
}
