using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Baihua.Contracts.Metrics;

/// <summary>
/// 服务业务指标 — 所有关键操作的观测点。
/// 同时用于 Cloud 和 Family 版，确保 OpenObserve 仪表板通用。
/// </summary>
public class ServiceMetrics : IDisposable
{
    // 指标名沿用合并前的 "baihua.family"：OpenObserve 既有看板按此过滤，改名会让历史与现网指标断档
    public const string MeterName = "baihua.family";

    private readonly Meter _meter;

    // ---- API 请求 ----
    public Histogram<double> HttpRequestDuration { get; }
    public Counter<long> HttpRequestCount { get; }
    public Counter<long> HttpErrorCount { get; }

    // ---- AI 调用 ----
    public Histogram<double> AiLatencyMs { get; }
    public Counter<long> AiCallCount { get; }
    public Counter<long> AiErrorCount { get; }
    public Counter<long> AiEmptyResponseCount { get; }

    // ---- 同步操作 ----
    public Histogram<double> SyncDurationMs { get; }
    public Counter<long> SyncOperationCount { get; }
    public Counter<long> SyncErrorCount { get; }
    public Counter<long> SyncFilesTransferred { get; }

    // ---- 搜索 ----
    public Histogram<double> SearchLatencyMs { get; }
    public Counter<long> SearchQueryCount { get; }

    // ---- 任务 ----
    public Counter<long> TaskCreatedCount { get; }
    public Counter<long> TaskCompletedCount { get; }
    public Counter<long> TaskFailedCount { get; }

    // ---- 备份 ----
    public Counter<long> BackupCreatedCount { get; }
    public Counter<long> BackupRestoredCount { get; }
    public Counter<long> BackupFailedCount { get; }

    // ---- 知识库索引 ----
    public Counter<long> VaultIndexedCount { get; }
    public Histogram<double> VaultIndexDurationMs { get; }
    public Counter<long> VaultIndexErrorCount { get; }

    // ---- 数据库 ----
    public Histogram<double> DbQueryDurationMs { get; }
    public Counter<long> DbErrorCount { get; }

    // ---- 设备/配对 ----
    public Counter<long> DevicePairCount { get; }
    public Counter<long> DeviceSyncCount { get; }

    public ServiceMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        HttpRequestDuration = _meter.CreateHistogram<double>(
            "http.request.duration_ms", "ms", "HTTP 请求延迟（毫秒）");
        HttpRequestCount = _meter.CreateCounter<long>(
            "http.request.count", description: "HTTP 请求总数");
        HttpErrorCount = _meter.CreateCounter<long>(
            "http.error.count", description: "HTTP 错误数（5xx/4xx）");

        AiLatencyMs = _meter.CreateHistogram<double>(
            "ai.latency_ms", "ms", "AI 调用延迟（毫秒）");
        AiCallCount = _meter.CreateCounter<long>(
            "ai.call.count", description: "AI 调用总数");
        AiErrorCount = _meter.CreateCounter<long>(
            "ai.error.count", description: "AI 调用失败数");
        AiEmptyResponseCount = _meter.CreateCounter<long>(
            "ai.empty_response.count", description: "AI 返回空响应次数");

        SyncDurationMs = _meter.CreateHistogram<double>(
            "sync.operation_duration_ms", "ms", "同步操作延迟（毫秒）");
        SyncOperationCount = _meter.CreateCounter<long>(
            "sync.operation.count", description: "同步操作总数");
        SyncErrorCount = _meter.CreateCounter<long>(
            "sync.error.count", description: "同步失败数");
        SyncFilesTransferred = _meter.CreateCounter<long>(
            "sync.files_transferred", "files", "同步传输文件数");

        SearchLatencyMs = _meter.CreateHistogram<double>(
            "search.latency_ms", "ms", "搜索延迟（毫秒）");
        SearchQueryCount = _meter.CreateCounter<long>(
            "search.query.count", description: "搜索查询总数");

        TaskCreatedCount = _meter.CreateCounter<long>(
            "task.created.count", description: "创建的任务数");
        TaskCompletedCount = _meter.CreateCounter<long>(
            "task.completed.count", description: "完成的任务数");
        TaskFailedCount = _meter.CreateCounter<long>(
            "task.failed.count", description: "失败的任务数");

        BackupCreatedCount = _meter.CreateCounter<long>(
            "backup.created.count", description: "创建的备份数");
        BackupRestoredCount = _meter.CreateCounter<long>(
            "backup.restored.count", description: "恢复的备份数");
        BackupFailedCount = _meter.CreateCounter<long>(
            "backup.failed.count", description: "备份失败数");

        VaultIndexedCount = _meter.CreateCounter<long>(
            "vault.indexed.count", description: "已索引的知识库数");
        VaultIndexDurationMs = _meter.CreateHistogram<double>(
            "vault.index_duration_ms", "ms", "知识库索引耗时（毫秒）");
        VaultIndexErrorCount = _meter.CreateCounter<long>(
            "vault.index_error.count", description: "知识库索引失败数");

        DbQueryDurationMs = _meter.CreateHistogram<double>(
            "db.query_duration_ms", "ms", "数据库查询延迟（毫秒）");
        DbErrorCount = _meter.CreateCounter<long>(
            "db.error.count", description: "数据库错误数");

        DevicePairCount = _meter.CreateCounter<long>(
            "device.pair.count", description: "设备配对次数");
        DeviceSyncCount = _meter.CreateCounter<long>(
            "device.sync.count", description: "设备同步次数");
    }

    /// <summary>
    /// 记录 AI 调用结果（延迟 + 成功/失败/空响应）
    /// </summary>
    public void RecordAiCall(double latencyMs, bool isSuccess, bool isEmptyResponse)
    {
        AiLatencyMs.Record(latencyMs);
        AiCallCount.Add(1);
        if (!isSuccess) AiErrorCount.Add(1);
        if (isEmptyResponse) AiEmptyResponseCount.Add(1);
    }

    /// <summary>
    /// 记录 HTTP 请求指标
    /// </summary>
    public void RecordHttpRequest(double latencyMs, bool isError)
    {
        HttpRequestDuration.Record(latencyMs);
        HttpRequestCount.Add(1);
        if (isError) HttpErrorCount.Add(1);
    }

    /// <summary>
    /// 记录同步操作
    /// </summary>
    public void RecordSync(double latencyMs, int filesTransferred, bool isError)
    {
        SyncDurationMs.Record(latencyMs);
        SyncOperationCount.Add(1);
        SyncFilesTransferred.Add(filesTransferred);
        if (isError) SyncErrorCount.Add(1);
    }

    /// <summary>
    /// 记录搜索操作
    /// </summary>
    public void RecordSearch(double latencyMs)
    {
        SearchLatencyMs.Record(latencyMs);
        SearchQueryCount.Add(1);
    }

    /// <summary>
    /// 记录任务状态变更
    /// </summary>
    public void RecordTaskCreated() => TaskCreatedCount.Add(1);
    public void RecordTaskCompleted() => TaskCompletedCount.Add(1);
    public void RecordTaskFailed() => TaskFailedCount.Add(1);

    /// <summary>
    /// 记录备份操作
    /// </summary>
    public void RecordBackupCreated() => BackupCreatedCount.Add(1);
    public void RecordBackupRestored() => BackupRestoredCount.Add(1);
    public void RecordBackupFailed() => BackupFailedCount.Add(1);

    /// <summary>
    /// 记录知识库索引操作
    /// </summary>
    public void RecordVaultIndex(double latencyMs, bool isError)
    {
        VaultIndexedCount.Add(1);
        VaultIndexDurationMs.Record(latencyMs);
        if (isError) VaultIndexErrorCount.Add(1);
    }

    /// <summary>
    /// 记录数据库操作
    /// </summary>
    public void RecordDbQuery(double latencyMs, bool isError)
    {
        DbQueryDurationMs.Record(latencyMs);
        if (isError) DbErrorCount.Add(1);
    }

    /// <summary>
    /// 记录设备活动
    /// </summary>
    public void RecordDevicePair() => DevicePairCount.Add(1);
    public void RecordDeviceSync() => DeviceSyncCount.Add(1);

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// 用于测量操作耗时的辅助结构
/// </summary>
public readonly struct OperationTimer : IDisposable
{
    private readonly long _start;
    private readonly Action<double>? _onComplete;

    public OperationTimer(Action<double> onComplete)
    {
        _onComplete = onComplete;
        _start = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_start);
        _onComplete?.Invoke(elapsed.TotalMilliseconds);
    }
}
