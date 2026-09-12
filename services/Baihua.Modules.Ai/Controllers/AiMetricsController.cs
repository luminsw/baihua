using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Baihua.Contracts.Metrics;
using Baihua.Data;

namespace Baihua.Modules.Ai.Controllers;

/// <summary>
/// AI 调用性能指标 API
/// </summary>
[ApiController]
[Route("api/ai/metrics")]
public class AiMetricsController : ControllerBase
{
    private readonly IDbContextFactory<AIDbContext> _dbFactory;

    public AiMetricsController(IDbContextFactory<AIDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private DateTime GetCutoffDate(int days) => DateTime.UtcNow.AddDays(-days);

    /// <summary>
    /// 获取指标总览
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<AiMetricsSummaryDto>> GetSummary([FromQuery] int days = 7)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = GetCutoffDate(days);
        var query = db.AiUsageMetrics.Where(m => m.CalledAt >= cutoff);

        var totalCalls = await query.CountAsync();
        if (totalCalls == 0)
        {
            return Ok(new AiMetricsSummaryDto());
        }

        var avgLatency = (long)await query.AverageAsync(m => m.LatencyMs);
        var successCount = await query.CountAsync(m => m.IsSuccess);
        var totalTokens = await query.SumAsync(m => (long?)m.TotalTokens) ?? 0;

        var bestProvider = await query
            .Where(m => m.TokensPerSecond.HasValue && m.IsSuccess)
            .GroupBy(m => new { m.ProviderId, m.ProviderName })
            .Select(g => new { g.Key.ProviderName, AvgTps = g.Average(m => m.TokensPerSecond!.Value) })
            .OrderByDescending(x => x.AvgTps)
            .FirstOrDefaultAsync();

        return Ok(new AiMetricsSummaryDto
        {
            TotalCalls = totalCalls,
            AvgLatencyMs = avgLatency,
            SuccessRate = Math.Round((double)successCount / totalCalls * 100, 1),
            TotalTokens = totalTokens,
            BestProvider = bestProvider?.ProviderName,
            BestProviderTps = bestProvider != null ? Math.Round(bestProvider.AvgTps, 1) : null,
        });
    }

    /// <summary>
    /// Provider 排行榜
    /// </summary>
    [HttpGet("providers")]
    public async Task<ActionResult<List<AiProviderMetricsDto>>> GetProviderMetrics([FromQuery] int days = 7)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = GetCutoffDate(days);

        var results = await db.AiUsageMetrics
            .Where(m => m.CalledAt >= cutoff)
            .GroupBy(m => new { m.ProviderId, m.ProviderName })
            .Select(g => new AiProviderMetricsDto
            {
                ProviderId = g.Key.ProviderId,
                ProviderName = g.Key.ProviderName,
                CallCount = g.Count(),
                AvgLatencyMs = (long)g.Average(m => m.LatencyMs),
                AvgTokensPerSecond = g.Any(m => m.TokensPerSecond.HasValue)
                    ? g.Where(m => m.TokensPerSecond.HasValue).Average(m => m.TokensPerSecond!.Value)
                    : null,
                SuccessRate = Math.Round((double)g.Count(m => m.IsSuccess) / g.Count() * 100, 1),
                TotalTokens = g.Sum(m => (long?)m.TotalTokens) ?? 0,
            })
            .OrderByDescending(x => x.CallCount)
            .ToListAsync();

        return Ok(results);
    }

    /// <summary>
    /// 模型排行榜
    /// </summary>
    [HttpGet("models")]
    public async Task<ActionResult<List<AiModelMetricsDto>>> GetModelMetrics([FromQuery] int days = 7)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = GetCutoffDate(days);

        var results = await db.AiUsageMetrics
            .Where(m => m.CalledAt >= cutoff)
            .GroupBy(m => new { m.ModelId, m.ProviderId, m.ProviderName })
            .Select(g => new AiModelMetricsDto
            {
                ModelId = g.Key.ModelId,
                ProviderId = g.Key.ProviderId,
                ProviderName = g.Key.ProviderName,
                CallCount = g.Count(),
                AvgLatencyMs = (long)g.Average(m => m.LatencyMs),
                AvgTokensPerSecond = g.Any(m => m.TokensPerSecond.HasValue)
                    ? g.Where(m => m.TokensPerSecond.HasValue).Average(m => m.TokensPerSecond!.Value)
                    : null,
                SuccessRate = Math.Round((double)g.Count(m => m.IsSuccess) / g.Count() * 100, 1),
            })
            .OrderByDescending(x => x.CallCount)
            .ToListAsync();

        return Ok(results);
    }

    /// <summary>
    /// 每日趋势
    /// </summary>
    [HttpGet("trends")]
    public async Task<ActionResult<List<AiMetricsTrendDto>>> GetTrends([FromQuery] int days = 7)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = GetCutoffDate(days);

        var raw = await db.AiUsageMetrics
            .Where(m => m.CalledAt >= cutoff)
            .GroupBy(m => m.CalledAt.Date)
            .Select(g => new
            {
                Date = g.Key,
                CallCount = g.Count(),
                AvgLatencyMs = (long)g.Average(m => m.LatencyMs),
                TotalTokens = g.Sum(m => (long?)m.TotalTokens) ?? 0,
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        var results = raw.Select(r => new AiMetricsTrendDto
        {
            Date = r.Date.ToString("yyyy-MM-dd"),
            CallCount = r.CallCount,
            AvgLatencyMs = r.AvgLatencyMs,
            TotalTokens = r.TotalTokens,
        }).ToList();

        return Ok(results);
    }

    /// <summary>
    /// 最近调用记录
    /// </summary>
    [HttpGet("recent")]
    public async Task<ActionResult<List<AiUsageMetricDto>>> GetRecent([FromQuery] int limit = 50, [FromQuery] int days = 7)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = GetCutoffDate(days);

        var results = await db.AiUsageMetrics
            .Where(m => m.CalledAt >= cutoff)
            .OrderByDescending(m => m.CalledAt)
            .Take(limit)
            .Select(m => new AiUsageMetricDto
            {
                Id = m.Id,
                CalledAt = m.CalledAt,
                ProviderId = m.ProviderId,
                ProviderName = m.ProviderName,
                ModelId = m.ModelId,
                Operation = m.Operation,
                LatencyMs = m.LatencyMs,
                InputTokens = m.InputTokens,
                OutputTokens = m.OutputTokens,
                TotalTokens = m.TotalTokens,
                TokensPerSecond = m.TokensPerSecond,
                IsSuccess = m.IsSuccess,
                ErrorMessage = m.ErrorMessage,
            })
            .ToListAsync();

        return Ok(results);
    }
}
