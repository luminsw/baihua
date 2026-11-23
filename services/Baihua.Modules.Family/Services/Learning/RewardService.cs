using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Core.Time;
using Baihua.Contracts.Achievements;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// FAM-31 家庭奖励服务：成就贴纸墙 + 家长自定义奖励。
/// 奖励触发条件：连续打卡天数（streak_days）/ 成就数（achievement_count）/ 学习卡片数（card_count）。
/// 达成记录每条件仅触发一次（去重），家庭维度。
/// </summary>
public class RewardService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly ITimeProvider _timeProvider;

    public RewardService(IDbContextFactory<FamilyDbContext> dbFactory, ITimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    private DateTime BeijingToday => _timeProvider.Today;

    /// <summary>UTC 转北京时间日期</summary>
    private static DateTime ToBeijingDate(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SystemTimeProvider.BeijingTz).Date;
    }

    /// <summary>
    /// FAM-31-AC3：奖励进度查询（孩子视角：当前值/目标值）
    /// </summary>
    /// <param name="vaultId">知识库 ID（可空=全部）</param>
    public async Task<List<RewardProgressDto>> GetRewardProgressAsync(string? vaultId = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var rewards = await db.FamilyRewards.OrderBy(r => r.Id).ToListAsync();
        var (streakDays, achievementCount, cardCount) = await ComputeMetricsAsync(db, vaultId);

        var result = new List<RewardProgressDto>();
        foreach (var r in rewards)
        {
            var current = r.ConditionType switch
            {
                "streak_days" => streakDays,
                "achievement_count" => achievementCount,
                "card_count" => cardCount,
                _ => 0
            };
            result.Add(new RewardProgressDto
            {
                RewardId = r.Id,
                RewardName = r.RewardName,
                RewardIcon = r.RewardIcon,
                ConditionType = r.ConditionType,
                TargetValue = r.TargetValue,
                CurrentValue = current,
                IsAchieved = current >= r.TargetValue,
                Remaining = Math.Max(0, r.TargetValue - current)
            });
        }
        return result;
    }

    /// <summary>
    /// FAM-31-AC4：检查并触发达成奖励（每条件仅触发一次，去重）。
    /// 返回本次新达成的奖励。
    /// </summary>
    /// <param name="vaultId">知识库 ID（可空=全部）</param>
    public async Task<List<RewardClaimDto>> CheckAndTriggerAsync(string? vaultId = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var rewards = await db.FamilyRewards.ToListAsync();
        var (streakDays, achievementCount, cardCount) = await ComputeMetricsAsync(db, vaultId);

        var newlyClaimed = new List<RewardClaimDto>();
        foreach (var r in rewards)
        {
            var current = r.ConditionType switch
            {
                "streak_days" => streakDays,
                "achievement_count" => achievementCount,
                "card_count" => cardCount,
                _ => 0
            };
            if (current < r.TargetValue) continue;

            // 去重：同奖励已达成过则不重复触发（每条件一次）
            var alreadyClaimed = await db.RewardClaims.AnyAsync(c => c.RewardId == r.Id);
            if (alreadyClaimed) continue;

            db.RewardClaims.Add(new RewardClaim
            {
                RewardId = r.Id,
                LearnerId = 0, // 家庭维度
                ClaimedAt = DateTime.UtcNow
            });
            newlyClaimed.Add(new RewardClaimDto
            {
                RewardId = r.Id,
                RewardName = r.RewardName,
                RewardIcon = r.RewardIcon,
                ClaimedAt = DateTime.UtcNow
            });
        }

        if (newlyClaimed.Count > 0)
            await db.SaveChangesAsync();

        return newlyClaimed;
    }

    /// <summary>计算家庭维度指标：连续打卡天数 / 解锁成就数 / 学习卡片数</summary>
    private async Task<(int StreakDays, int AchievementCount, int CardCount)> ComputeMetricsAsync(
        FamilyDbContext db, string? vaultId)
    {
        // 连续打卡天数：任意成员有学习行为即算当天（北京时间）
        var activityQuery = db.StudyActivities.AsQueryable();
        if (!string.IsNullOrEmpty(vaultId))
            activityQuery = activityQuery.Where(a => a.VaultId == vaultId);
        var activityDates = (await activityQuery.Select(a => a.CreatedAt).ToListAsync())
            .Select(ToBeijingDate)
            .ToList();
        var streakDays = CalculateStreak(activityDates, BeijingToday);

        // 解锁成就数（家庭维度：所有 Learner 的去重成就 Key）
        var achievementCount = await db.Achievements
            .Select(a => a.Key)
            .Distinct()
            .CountAsync();

        // 学习卡片数（有 StudyActivity 的去重卡片数）
        var cardCount = await activityQuery
            .Select(a => a.CardId)
            .Distinct()
            .CountAsync(a => a != null);

        return (streakDays, achievementCount, cardCount);
    }

    /// <summary>带 FAM-33 连击保护的连续打卡天数</summary>
    private static int CalculateStreak(List<DateTime> activityDates, DateTime today)
    {
        var dateSet = new HashSet<DateTime>(activityDates);
        if (dateSet.Count == 0) return 0;

        DateTime anchor;
        if (dateSet.Contains(today)) anchor = today;
        else if (dateSet.Contains(today.AddDays(-1))) anchor = today.AddDays(-1);
        else return 0;

        int streak = 0;
        while (dateSet.Contains(anchor.AddDays(-streak)))
            streak++;
        return streak;
    }
}
