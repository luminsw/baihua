using System.Collections.Concurrent;
using System.Threading.Channels;
using Baihua.Contracts.Learn;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 后台题库生成任务表 + 队列。
///
/// 设计：
/// - 任务结果与前端连接解耦——页面关闭后任务继续执行；
/// - 单消费者（<see cref="QuizGenerateJobWorker"/>）+ 单并发，避免同时打爆 AI provider；
/// - 结果仍是「题库包 + 校验」，导入必须由用户另行确认（绝不自动入库）；
/// - 内存存储，保留 2 小时 / 最多 50 条，进程重启即丢失（预览本就需用户确认，可接受）。
/// </summary>
public sealed class QuizGenerateJobService
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    private const int MaxJobs = 50;

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly IQuizBankGenerator _generator;
    private readonly ILogger<QuizGenerateJobService> _logger;

    public QuizGenerateJobService(IQuizBankGenerator generator, ILogger<QuizGenerateJobService> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    /// <summary>创建任务并立即返回 jobId（不等待生成）</summary>
    public string Enqueue(QuizGenerateRequestDto request)
    {
        CleanupExpired();
        var id = Guid.NewGuid().ToString("N")[..12];
        var entry = new JobEntry(id, request);
        _jobs[id] = entry;
        if (!_queue.Writer.TryWrite(id))
        {
            entry.Status = "failed";
            entry.Error = "任务队列不可用";
            entry.CompletedAt = DateTime.UtcNow;
        }
        return id;
    }

    public QuizGenerateJobDto? Get(string jobId)
        => _jobs.TryGetValue(jobId, out var entry) ? ToDto(entry) : null;

    public List<QuizGenerateJobSummaryDto> ListRecent(int limit = 10)
    {
        CleanupExpired();
        return _jobs.Values
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(ToSummary)
            .ToList();
    }

    /// <summary>取消任务（已结束的任务返回 false）</summary>
    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) return false;
        if (entry.Status is "succeeded" or "failed" or "canceled") return false;
        entry.Cts.Cancel();
        return true;
    }

    public async Task<string> DequeueAsync(CancellationToken ct)
        => await _queue.Reader.ReadAsync(ct);

    /// <summary>执行一个任务（由 Worker 调用；公开以便回归测试）</summary>
    public async Task ExecuteAsync(string jobId, CancellationToken stoppingToken)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) return;
        if (entry.Status != "queued") return;

        entry.Status = "running";
        entry.StartedAt = DateTime.UtcNow;
        entry.Message = "生成中…";

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token, stoppingToken);
        try
        {
            var progress = new SyncProgress<QuizGenerateProgress>(p =>
            {
                entry.CompletedBatches = p.CompletedBatches;
                entry.TotalBatches = p.TotalBatches;
                entry.ProgressPercent = p.TotalBatches <= 0
                    ? 0
                    : (int)Math.Round(p.CompletedBatches * 100.0 / p.TotalBatches);
                entry.Message = p.Message;
            });

            var result = await _generator.PreviewAsync(entry.Request, progress, linked.Token);
            if (result.Success)
            {
                entry.Result = result;
                entry.Status = "succeeded";
                entry.Message = "生成完成，待预览确认";
                entry.ProgressPercent = 100;
            }
            else
            {
                entry.Error = result.Error;
                entry.Status = "failed";
                entry.Message = "生成失败";
            }
        }
        catch (OperationCanceledException)
        {
            entry.Status = "canceled";
            entry.Message = "已取消";
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            entry.Status = "failed";
            entry.Message = "生成失败";
            _logger.LogError(ex, "后台题库生成任务失败 job={JobId}", jobId);
        }
        finally
        {
            entry.CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>清理过期/超量任务（运行中的任务不清理）</summary>
    internal void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, entry) in _jobs)
        {
            if (entry.CompletedAt is { } completed && now - completed > Retention)
            {
                _jobs.TryRemove(id, out _);
            }
        }

        if (_jobs.Count <= MaxJobs) return;

        var removable = _jobs.Values
            .Where(e => e.CompletedAt is not null && e.Status is "succeeded" or "failed" or "canceled")
            .OrderBy(e => e.CreatedAt)
            .Take(_jobs.Count - MaxJobs)
            .Select(e => e.Id);
        foreach (var id in removable) _jobs.TryRemove(id, out _);
    }

    private static QuizGenerateJobDto ToDto(JobEntry e) => new()
    {
        JobId = e.Id,
        Status = e.Status,
        ProgressPercent = e.ProgressPercent,
        CompletedBatches = e.CompletedBatches,
        TotalBatches = e.TotalBatches,
        Message = e.Message,
        BankName = e.Request.BankName,
        Topic = e.Request.Topic,
        CreatedAt = e.CreatedAt,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        Result = e.Result,
        Error = e.Error,
    };

    private static QuizGenerateJobSummaryDto ToSummary(JobEntry e) => new()
    {
        JobId = e.Id,
        Status = e.Status,
        ProgressPercent = e.ProgressPercent,
        Message = e.Message,
        BankName = e.Request.BankName,
        Topic = e.Request.Topic,
        CreatedAt = e.CreatedAt,
        CompletedAt = e.CompletedAt,
        Error = e.Error,
    };

    private sealed class JobEntry
    {
        public JobEntry(string id, QuizGenerateRequestDto request)
        {
            Id = id;
            Request = request;
        }

        public string Id { get; }
        public QuizGenerateRequestDto Request { get; }
        public CancellationTokenSource Cts { get; } = new();
        public string Status = "queued";
        public int ProgressPercent;
        public int CompletedBatches;
        public int TotalBatches;
        public string Message = "排队中";
        public QuizGeneratePreviewDto? Result;
        public string? Error;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime? StartedAt;
        public DateTime? CompletedAt;
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}