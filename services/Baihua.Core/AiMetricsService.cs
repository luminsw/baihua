using System.Diagnostics;
using System.Diagnostics.Metrics;
using Baihua.Core.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Services;

/// <summary>
/// AI 与 Benchmark 指标服务：基于 System.Diagnostics.Metrics，
/// 通过 OpenTelemetry OTLP 导出到 OpenObserve 等可观测平台。
/// </summary>
public class AiMetricsService : IDisposable
{
    // 指标名沿用合并前的 "Baihua.AI"：OpenObserve 既有看板按此过滤，改名会让历史与现网指标断档
    public static readonly string MeterName = "Baihua.AI";

    private readonly Meter _meter;

    // AI 通用调用指标
    private readonly Histogram<double> _aiLatency;
    private readonly Histogram<double> _aiTps;
    private readonly Counter<long> _aiRequests;
    private readonly Counter<long> _aiTokens;

    // Benchmark 专属指标
    private readonly Histogram<double> _benchLatency;
    private readonly Histogram<double> _benchTps;
    private readonly Histogram<int> _benchQuality;
    private readonly Counter<long> _benchTimeouts;
    private readonly Counter<long> _benchErrors;
    private readonly Counter<long> _benchRuns;

    // 健康检查指标
    private readonly Histogram<double> _healthCheckDuration;
    private readonly Histogram<double> _healthWallClock;
    private readonly Histogram<int> _healthScore;
    private readonly Counter<long> _healthComponents;

    private readonly IStringLocalizer<SharedResources> _loc;

    public AiMetricsService(IStringLocalizer<SharedResources> loc)
    {
        _loc = loc;
        _meter = new Meter(MeterName, "1.0.0");

        _aiLatency = _meter.CreateHistogram<double>("ai.latency_ms", unit: "ms", description: _loc["AiMetrics_AiLatency"]);
        _aiTps = _meter.CreateHistogram<double>("ai.tokens_per_second", unit: "tokens/s", description: _loc["AiMetrics_AiTps"]);
        _aiRequests = _meter.CreateCounter<long>("ai.requests.total", unit: "{request}", description: _loc["AiMetrics_AiRequests"]);
        _aiTokens = _meter.CreateCounter<long>("ai.tokens.total", unit: "{token}", description: _loc["AiMetrics_AiTokens"]);

        _benchLatency = _meter.CreateHistogram<double>("benchmark.latency_ms", unit: "ms", description: _loc["AiMetrics_BenchLatency"]);
        _benchTps = _meter.CreateHistogram<double>("benchmark.tokens_per_second", unit: "tokens/s", description: _loc["AiMetrics_BenchTps"]);
        _benchQuality = _meter.CreateHistogram<int>("benchmark.quality_score", unit: "1", description: _loc["AiMetrics_BenchQuality"]);
        _benchTimeouts = _meter.CreateCounter<long>("benchmark.timeouts.total", unit: "{timeout}", description: _loc["AiMetrics_BenchTimeouts"]);
        _benchErrors = _meter.CreateCounter<long>("benchmark.errors.total", unit: "{error}", description: _loc["AiMetrics_BenchErrors"]);
        _benchRuns = _meter.CreateCounter<long>("benchmark.runs.total", unit: "{run}", description: _loc["AiMetrics_BenchRuns"]);

        _healthCheckDuration = _meter.CreateHistogram<double>("health.check.duration_ms", unit: "ms", description: _loc["AiMetrics_HealthCheckDuration"]);
        _healthWallClock = _meter.CreateHistogram<double>("health.check.wallclock_ms", unit: "ms", description: _loc["AiMetrics_HealthWallClock"]);
        _healthScore = _meter.CreateHistogram<int>("health.score", unit: "1", description: _loc["AiMetrics_HealthScore"]);
        _healthComponents = _meter.CreateCounter<long>("health.components.total", unit: "{component}", description: _loc["AiMetrics_HealthComponents"]);
    }

    /// <summary>
    /// 记录通用 AI 调用指标
    /// </summary>
    public void RecordAiRequest(
        string provider, string model, string operation,
        double latencyMs, bool success,
        long? inputTokens = null, long? outputTokens = null, double? tokensPerSecond = null)
    {
        var tags = new TagList();
        tags.Add("provider", provider);
        tags.Add("model", model);
        tags.Add("operation", operation);
        tags.Add("success", success);

        _aiRequests.Add(1, tags);
        _aiLatency.Record(latencyMs, tags);

        if (inputTokens.HasValue && outputTokens.HasValue)
        {
            _aiTokens.Add(inputTokens.Value + outputTokens.Value, tags);
        }

        if (tokensPerSecond.HasValue)
        {
            _aiTps.Record(tokensPerSecond.Value, tags);
        }
    }

    /// <summary>
    /// 记录 Benchmark 单条提示词指标
    /// </summary>
    public void RecordBenchmark(
        string provider, string model, string category, string promptId,
        double latencyMs, int qualityScore, bool isTimeout, bool isError,
        long? outputTokens = null, double? tokensPerSecond = null, bool isEstimatedTokens = false)
    {
        var tags = new TagList();
        tags.Add("provider", provider);
        tags.Add("model", model);
        tags.Add("category", category);
        tags.Add("prompt_id", promptId);
        tags.Add("is_estimated", isEstimatedTokens);

        _benchRuns.Add(1, tags);
        _benchLatency.Record(latencyMs, tags);
        _benchQuality.Record(qualityScore, tags);

        if (tokensPerSecond.HasValue)
        {
            _benchTps.Record(tokensPerSecond.Value, tags);
        }

        if (isTimeout)
        {
            _benchTimeouts.Add(1, tags);
        }

        if (isError)
        {
            _benchErrors.Add(1, tags);
        }
    }

    /// <summary>
    /// 记录健康检查结果
    /// </summary>
    public void RecordHealthCheck(
        string componentName, string status,
        double durationMs, double? wallClockMs = null, int? score = null)
    {
        var tags = new TagList();
        tags.Add("component", componentName);
        tags.Add("status", status);

        _healthCheckDuration.Record(durationMs, tags);
        _healthComponents.Add(1, tags);

        if (wallClockMs.HasValue)
        {
            _healthWallClock.Record(wallClockMs.Value, new TagList());
        }

        if (score.HasValue)
        {
            _healthScore.Record(score.Value, new TagList());
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}
