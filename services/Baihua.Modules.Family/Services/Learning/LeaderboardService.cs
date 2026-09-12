using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Core.Time;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 家庭赛舟榜服务
/// </summary>
public class LeaderboardService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly ITimeProvider _timeProvider;

    public LeaderboardService(IDbContextFactory<FamilyDbContext> dbFactory, ITimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>北京时间今日</summary>
    private DateTime BeijingToday => _timeProvider.Today;

    /// <summary>UTC 转北京时间日期</summary>
    private static DateTime ToBeijingDate(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SystemTimeProvider.BeijingTz).Date;
    }

    /// <summary>北京时间本周一 00:00</summary>
    private DateTime BeijingStartOfWeek()
    {
        var today = BeijingToday;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7; // Mon=0 ... Sun=6
        return today.AddDays(-daysSinceMonday);
    }

    /// <summary>
    /// 获取周排行榜
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetWeeklyLeaderboardAsync(string? vaultId = null)
    {
        var startOfWeek = BeijingStartOfWeek();
        return await GetLeaderboardAsync(startOfWeek, vaultId);
    }

    /// <summary>
    /// FAM-22 "和自己比"：本周 vs 上周（北京时间周界：周一 00:00 起）。
    /// 无上周数据 → Arrow=""（页面显示 --）；否则 up/down/flat。
    /// </summary>
    /// <param name="vaultId">知识库 ID（可空=全部）</param>
    /// <param name="learnerId">成员维度（null/0=全部成员）</param>
    public async Task<WeeklyCompareResult> GetWeeklyCompareAsync(string? vaultId = null, int? learnerId = null)
    {
        int? effectiveLearnerId = learnerId is null or <= 0 ? null : learnerId;
        using var db = await _dbFactory.CreateDbContextAsync();

        var thisWeekStart = BeijingStartOfWeek();
        var lastWeekStart = thisWeekStart.AddDays(-7);

        var query = db.StudyActivities.Where(a => a.ActivityType == "study");
        if (!string.IsNullOrEmpty(vaultId)) query = query.Where(a => a.VaultId == vaultId);
        if (effectiveLearnerId.HasValue) query = query.Where(a => a.LearnerId == effectiveLearnerId.Value);

        var activities = (await query.ToListAsync())
            .Select(a => ToBeijingDate(a.CreatedAt))
            .ToList();

        var weekTotal = activities.Count(d => d >= thisWeekStart);
        var lastWeekTotal = activities.Count(d => d >= lastWeekStart && d < thisWeekStart);
        var delta = weekTotal - lastWeekTotal;
        var percent = lastWeekTotal > 0 ? Math.Round((double)delta / lastWeekTotal * 100, 1) : 0;

        string arrow;
        if (lastWeekTotal == 0) arrow = "";
        else if (weekTotal > lastWeekTotal) arrow = "up";
        else if (weekTotal < lastWeekTotal) arrow = "down";
        else arrow = "flat";

        return new WeeklyCompareResult
        {
            WeekTotal = weekTotal,
            LastWeekTotal = lastWeekTotal,
            Delta = delta,
            Percent = percent,
            Arrow = arrow
        };
    }

    /// <summary>
    /// FAM-22 角色分组排行榜：孩子榜/大人榜（TECH-08 未完成，LearnerProfile 无 Role/年龄字段，
    /// 兜底按 IsDefault 判定：默认学习者=大人（家长），其余=孩子）。
    /// 排序维度：本周完成卡片数（北京周界），并列排名由 FAM-13 在 DTO 层处理。
    /// </summary>
    /// <param name="role">角色过滤：adults/kids（大小写不敏感，含 adult 即大人榜）</param>
    /// <param name="vaultId">知识库 ID（可空=全部）</param>
    public async Task<List<LeaderboardEntry>> GetRoleLeaderboardAsync(string role, string? vaultId = null)
    {
        var isAdults = role.Contains("adult", StringComparison.OrdinalIgnoreCase);
        var startOfWeek = BeijingStartOfWeek();

        using var db = await _dbFactory.CreateDbContextAsync();
        var learners = await db.LearnerProfiles.ToListAsync();
        var filtered = learners
            .Where(l => isAdults ? l.IsDefault : !l.IsDefault)
            .ToList();

        var result = new List<LeaderboardEntry>();
        foreach (var learner in filtered)
        {
            var query = db.StudyActivities.Where(a => a.LearnerId == learner.Id && a.ActivityType == "study");
            if (!string.IsNullOrEmpty(vaultId)) query = query.Where(a => a.VaultId == vaultId);

            var weekCount = (await query.ToListAsync())
                .Count(a => ToBeijingDate(a.CreatedAt) >= startOfWeek);

            result.Add(new LeaderboardEntry
            {
                LearnerId = learner.Id,
                LearnerName = learner.Name,
                AvatarEmoji = learner.AvatarEmoji,
                Color = learner.Color,
                CardsStudied = weekCount,
                Score = weekCount
            });
        }

        return result.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// 获取月排行榜
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetMonthlyLeaderboardAsync(string? vaultId = null)
    {
        var startOfMonth = new DateTime(BeijingToday.Year, BeijingToday.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return await GetLeaderboardAsync(startOfMonth, vaultId);
    }

    /// <summary>
    /// 获取总排行榜
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetAllTimeLeaderboardAsync(string? vaultId = null)
    {
        return await GetLeaderboardAsync(DateTime.MinValue, vaultId);
    }

    /// <summary>
    /// 获取 streak 排行榜
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetStreakLeaderboardAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var learners = await db.LearnerProfiles.ToListAsync();
        var result = new List<LeaderboardEntry>();

        foreach (var learner in learners)
        {
            var streak = await CalculateStreakAsync(db, learner.Id);
            result.Add(new LeaderboardEntry
            {
                LearnerId = learner.Id,
                LearnerName = learner.Name,
                AvatarEmoji = learner.AvatarEmoji,
                Color = learner.Color,
                Streak = streak,
                Score = streak * 10 // streak 换算成分数
            });
        }

        return result.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// 获取正确率排行榜（今日）
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetAccuracyLeaderboardAsync(string? vaultId = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var learners = await db.LearnerProfiles.ToListAsync();
        var today = BeijingToday;
        var result = new List<LeaderboardEntry>();

        foreach (var learner in learners)
        {
            var query = db.StudyActivities.Where(a => a.LearnerId == learner.Id
                && a.ActivityType == "study"
                && a.Result != null);
            if (!string.IsNullOrEmpty(vaultId))
                query = query.Where(a => a.VaultId == vaultId);

            var records = (await query.ToListAsync()).Where(r => ToBeijingDate(r.CreatedAt) == today).ToList();
            var total = records.Count;
            var remembered = records.Count(r => r.Result == "remember");
            var accuracy = total > 0 ? (double)remembered / total : 0;

            result.Add(new LeaderboardEntry
            {
                LearnerId = learner.Id,
                LearnerName = learner.Name,
                AvatarEmoji = learner.AvatarEmoji,
                Color = learner.Color,
                CardsStudied = total,
                Accuracy = accuracy * 100,
                Score = (int)(accuracy * 100)
            });
        }

        return result.OrderByDescending(r => r.Accuracy).ThenByDescending(r => r.CardsStudied).ToList();
    }

    private async Task<List<LeaderboardEntry>> GetLeaderboardAsync(DateTime since, string? vaultId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var learners = await db.LearnerProfiles.ToListAsync();
        var result = new List<LeaderboardEntry>();

        foreach (var learner in learners)
        {
            var query = db.StudyActivities.Where(a => a.LearnerId == learner.Id
                && a.ActivityType == "study");
            if (!string.IsNullOrEmpty(vaultId))
                query = query.Where(a => a.VaultId == vaultId);

            var records = (await query.ToListAsync()).Where(r => ToBeijingDate(r.CreatedAt) >= since).ToList();
            var total = records.Count;
            var remembered = records.Count(r => r.Result == "remember");
            var accuracy = total > 0 ? (double)remembered / total : 0;

            result.Add(new LeaderboardEntry
            {
                LearnerId = learner.Id,
                LearnerName = learner.Name,
                AvatarEmoji = learner.AvatarEmoji,
                Color = learner.Color,
                CardsStudied = total,
                Accuracy = accuracy * 100,
                Score = total + (int)(accuracy * 20), // 学习数量 + 正确率加成
                Streak = await CalculateStreakAsync(db, learner.Id)
            });
        }

        return result.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// 获取家长看板数据（FAM-20：家庭日报版）
    /// </summary>
    /// <param name="vaultId">知识库 ID（可空=全部）</param>
    /// <param name="learnerId">成员筛选（null/0=全部成员，非 null=单成员维度）</param>
    public async Task<DashboardData> GetDashboardAsync(string? vaultId = null, int? learnerId = null)
    {
        // 探针契约：learnerId=0 视同 null（全部成员）；Learner Id 从 1 开始
        int? effectiveLearnerId = learnerId is null or <= 0 ? null : learnerId;

        using var db = await _dbFactory.CreateDbContextAsync();
        var learners = await db.LearnerProfiles.ToListAsync();
        if (effectiveLearnerId.HasValue)
            learners = learners.Where(l => l.Id == effectiveLearnerId.Value).ToList();
        var today = BeijingToday;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-6);
        var timelineStart = today.AddDays(-29); // FAM-20-AC4：最近 30 天窗口（含今天，07-08 起，与测试断言 d >= 07-08 对齐）

        var familyStats = new List<FamilyMemberStat>();
        var weeklyTrend = new List<DailyTrend>();
        var recentAchievements = new List<RecentAchievement>();

        // 每周趋势（初始化 7 天为 0）
        for (int i = 6; i >= 0; i--)
        {
            weeklyTrend.Add(new DailyTrend { Date = today.AddDays(-i).ToString("MM-dd"), Count = 0 });
        }

        // 学习活动（按成员维度筛选）
        var activityQuery = db.StudyActivities.Where(a => a.ActivityType == "study");
        if (!string.IsNullOrEmpty(vaultId)) activityQuery = activityQuery.Where(a => a.VaultId == vaultId);
        if (effectiveLearnerId.HasValue) activityQuery = activityQuery.Where(a => a.LearnerId == effectiveLearnerId.Value);
        var allActivities = (await activityQuery.ToListAsync())
            .Select(a => new { a.LearnerId, a.Result, Date = ToBeijingDate(a.CreatedAt), a.CreatedAt })
            .ToList();

        foreach (var learner in learners)
        {
            var activities = allActivities.Where(a => a.LearnerId == learner.Id).ToList();
            var weekActivities = activities.Where(a => a.Date >= weekAgo).ToList();

            var total = activities.Count;
            var weekTotal = weekActivities.Count;
            var remembered = weekActivities.Count(r => r.Result == "remember");
            var accuracy = weekTotal > 0 ? (double)remembered / weekTotal * 100 : 0;
            var streak = await CalculateStreakAsync(db, learner.Id);

            familyStats.Add(new FamilyMemberStat
            {
                LearnerId = learner.Id,
                Name = learner.Name,
                AvatarEmoji = learner.AvatarEmoji,
                Color = learner.Color,
                WeekTotal = weekTotal,
                Accuracy = accuracy,
                Streak = streak,
                TotalCards = total
            });

            // 累加每周趋势
            foreach (var act in weekActivities)
            {
                var dateStr = act.Date.ToString("MM-dd");
                var day = weeklyTrend.FirstOrDefault(d => d.Date == dateStr);
                if (day != null) day.Count++;
            }
        }

        // 最近解锁的成就（按成员维度筛选）
        var achievementQuery = db.Achievements.AsQueryable();
        if (effectiveLearnerId.HasValue) achievementQuery = achievementQuery.Where(a => a.LearnerId == effectiveLearnerId.Value);
        var achievements = await achievementQuery
            .OrderByDescending(a => a.UnlockedAt)
            .Take(10)
            .ToListAsync();

        foreach (var ach in achievements)
        {
            var learner = learners.FirstOrDefault(l => l.Id == ach.LearnerId);
            recentAchievements.Add(new RecentAchievement
            {
                LearnerName = learner?.Name ?? "",
                AvatarEmoji = learner?.AvatarEmoji ?? "",
                Title = ach.Title,
                Icon = ach.Icon,
                Tier = ach.Tier,
                UnlockedAt = ach.UnlockedAt
            });
        }

        // 答题结果分布（本周，按成员维度筛选）
        var weekResults = allActivities.Where(a => a.Date >= weekAgo).ToList();

        // ===== FAM-20：家庭日报字段 =====

        // AC1：今日/昨日完成卡片数（北京自然日）
        var todayCompleted = allActivities.Count(a => a.Date == today);
        var yesterdayCompleted = allActivities.Count(a => a.Date == yesterday);

        // AC1：趋势箭头（今天>昨天→up；今天<昨天→down；持平→flat；无数据→""）
        string trendArrow;
        if (todayCompleted == 0 && yesterdayCompleted == 0) trendArrow = "";
        else if (todayCompleted > yesterdayCompleted) trendArrow = "up";
        else if (todayCompleted < yesterdayCompleted) trendArrow = "down";
        else trendArrow = "flat";

        // AC3：家庭维度连续打卡——任意成员有学习行为即算当天（北京时间自然日）
        var familyStreak = CalculateFamilyStreak(allActivities.Select(a => a.Date).ToList(), today);

        // AC1：今日三件事（谁 + 做了什么）——今日有学习行为的成员
        var todayActivities = learners
            .Select(l => new
            {
                Learner = l,
                Count = allActivities.Count(a => a.LearnerId == l.Id && a.Date == today)
            })
            .Where(x => x.Count > 0)
            .Select(x => new TodayActivityItem
            {
                LearnerName = x.Learner.Name,
                Description = $"完成了 {x.Count} 张卡片"
            })
            .ToList();

        // AC1：最新成就（最多 3 个，按解锁时间倒序）
        var latestAchievements = achievements
            .OrderByDescending(a => a.UnlockedAt)
            .Take(3)
            .Select(a => new RecentAchievement
            {
                LearnerName = learners.FirstOrDefault(l => l.Id == a.LearnerId)?.Name ?? "",
                AvatarEmoji = learners.FirstOrDefault(l => l.Id == a.LearnerId)?.AvatarEmoji ?? "",
                Title = a.Title,
                Icon = a.Icon,
                Tier = a.Tier,
                UnlockedAt = a.UnlockedAt
            })
            .ToList();

        // AC4：成长时间线（最近 30 天，时间倒序）——学习事件 + 成就解锁事件
        var timeline = new List<GrowthTimelineItem>();
        foreach (var grp in allActivities
                     .Where(a => a.Date >= timelineStart)
                     .GroupBy(a => new { a.LearnerId, a.Date }))
        {
            var learner = learners.FirstOrDefault(l => l.Id == grp.Key.LearnerId);
            timeline.Add(new GrowthTimelineItem
            {
                Date = grp.Min(a => a.CreatedAt),
                LearnerName = learner?.Name ?? "",
                Description = $"完成了 {grp.Count()} 张卡片"
            });
        }
        foreach (var ach in achievements.Where(a => ToBeijingDate(a.UnlockedAt) >= timelineStart))
        {
            var learner = learners.FirstOrDefault(l => l.Id == ach.LearnerId);
            timeline.Add(new GrowthTimelineItem
            {
                Date = ach.UnlockedAt,
                LearnerName = learner?.Name ?? "",
                Description = $"解锁了 {ach.Title} 成就"
            });
        }
        timeline = timeline.OrderByDescending(t => t.Date).ToList();

        return new DashboardData
        {
            FamilyStats = familyStats,
            WeeklyTrend = weeklyTrend,
            RecentAchievements = recentAchievements,
            ResultDistribution = new ResultDistribution
            {
                Remember = weekResults.Count(r => r.Result == "remember"),
                Hard = weekResults.Count(r => r.Result == "hard"),
                Forgot = weekResults.Count(r => r.Result == "forgot")
            },
            // FAM-20 新增
            FamilyStreak = familyStreak,
            TodayCompleted = todayCompleted,
            YesterdayCompleted = yesterdayCompleted,
            TrendArrow = trendArrow,
            TodayActivities = todayActivities,
            LatestAchievements = latestAchievements,
            GrowthTimeline = timeline,
            PageSize = DashboardData.TimelinePageSize
        };
    }

    /// <summary>
    /// 家庭维度连续打卡：任意成员有学习行为即算当天（不是取各 Learner streak 最大值）。
    /// 共享锚点算法见 <see cref="StreakCalculator"/>。
    /// </summary>
    private static int CalculateFamilyStreak(List<DateTime> beijingDates, DateTime today)
        => StreakCalculator.Calculate(beijingDates, today);

    private async Task<int> CalculateStreakAsync(FamilyDbContext db, int learnerId)
    {
        var dates = (await db.StudyActivities
            .Where(a => a.LearnerId == learnerId && a.ActivityType == "study")
            .ToListAsync())
            .Select(a => ToBeijingDate(a.CreatedAt))
            .ToList();

        return StreakCalculator.Calculate(dates, BeijingToday);
    }
}
