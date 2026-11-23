using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Baihua.Contracts.Benchmark;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// Benchmark 历史记录仓库：持久化与查询。
/// 一服务一数据库：测速历史归 Family 自有库（family.db 的 BenchmarkSessions 表），
/// 不再读写 AI 服务 ai.db（旧数据仍在 ai.db 中，不影响）。
/// </summary>
public class BenchmarkRepository(
    IDbContextFactory<FamilyDbContext> dbFactory,
    ILogger<BenchmarkRepository> logger)
{
    /// <summary>
    /// 获取测试历史
    /// </summary>
    public List<BenchmarkSession> GetHistory(string? category = null)
    {
        var sessions = LoadSessionsFromDb();
        if (!string.IsNullOrEmpty(category))
            sessions = sessions.Where(s => s.Category == category).ToList();
        return sessions.OrderByDescending(s => s.TestedAt).ToList();
    }

    /// <summary>
    /// 获取排行榜
    /// </summary>
    public List<BenchmarkLeaderboardEntry> GetLeaderboard(string? category = null)
    {
        var sessions = LoadSessionsFromDb();
        if (!string.IsNullOrEmpty(category))
            sessions = sessions.Where(s => s.Category == category).ToList();

        var grouped = sessions
            .GroupBy(s => new { s.ModelName, s.Category, s.ProviderId, s.ModelId })
            .Select(g => new BenchmarkLeaderboardEntry
            {
                ModelName = g.Key.ModelName,
                Category = g.Key.Category,
                ProviderId = g.Key.ProviderId,
                ModelId = g.Key.ModelId,
                AvgTokensPerSecond = g.Average(s => s.AvgTokensPerSecond),
                AvgLatencyMs = g.Average(s => s.AvgLatencyMs),
                AvgQualityScore = g.Average(s => s.AvgQualityScore),
                TestCount = g.Count(),
                LastTestedAt = g.Max(s => s.TestedAt)
            })
            .OrderByDescending(e => e.AvgQualityScore)
            .ThenByDescending(e => e.AvgTokensPerSecond)
            .ToList();

        return grouped;
    }

    /// <summary>
    /// 删除某条历史记录
    /// </summary>
    public async Task<bool> DeleteSessionAsync(string sessionId)
    {
        try
        {
            using var db = await dbFactory.CreateDbContextAsync();
            var entity = db.BenchmarkSessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (entity == null) return false;
            db.BenchmarkSessions.Remove(entity);
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除 Benchmark 记录失败");
            return false;
        }
    }

    /// <summary>
    /// 清空所有历史
    /// </summary>
    public async Task ClearHistoryAsync()
    {
        try
        {
            using var db = await dbFactory.CreateDbContextAsync();
            db.BenchmarkSessions.ExecuteDelete();
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清空 Benchmark 历史失败");
        }
    }

    public async Task SaveSessionAsync(BenchmarkSession session)
    {
        try
        {
            using var db = await dbFactory.CreateDbContextAsync();
            db.BenchmarkSessions.Add(new BenchmarkSessionEntity
            {
                SessionId = session.Id,
                TestedAt = session.TestedAt,
                ModelName = session.ModelName,
                Category = session.Category,
                ProviderId = session.ProviderId,
                ModelId = session.ModelId,
                ResultsJson = JsonSerializer.Serialize(session.Results),
                AvgTokensPerSecond = session.AvgTokensPerSecond,
                AvgLatencyMs = session.AvgLatencyMs,
                AvgQualityScore = session.AvgQualityScore,
                CompletionRate = session.CompletionRate,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存 Benchmark 结果到数据库失败");
        }
    }

    private List<BenchmarkSession> LoadSessionsFromDb()
    {
        try
        {
            using var db = dbFactory.CreateDbContext();
            var entities = db.BenchmarkSessions.OrderByDescending(s => s.TestedAt).ToList();
            return entities.Select(e => new BenchmarkSession
            {
                Id = e.SessionId,
                TestedAt = e.TestedAt,
                ModelName = e.ModelName,
                Category = e.Category,
                ProviderId = e.ProviderId,
                ModelId = e.ModelId,
                Results = JsonSerializer.Deserialize<List<BenchmarkPromptResult>>(e.ResultsJson) ?? new List<BenchmarkPromptResult>(),
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "从数据库加载 Benchmark 历史失败");
            return new List<BenchmarkSession>();
        }
    }
}
