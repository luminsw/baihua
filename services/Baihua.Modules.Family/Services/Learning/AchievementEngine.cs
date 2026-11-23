using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Core.Time;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 成就引擎：检查并颁发成就
/// </summary>
public class AchievementEngine
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly ILogger<AchievementEngine> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;
    private readonly ITimeProvider _timeProvider;

    public AchievementEngine(IDbContextFactory<FamilyDbContext> dbFactory, ILogger<AchievementEngine> logger, IStringLocalizer<SharedResources> loc, ITimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _loc = loc;
        _timeProvider = timeProvider;
    }

    /// <summary>UTC → 北京时间日期（SQLite 读出 Kind=Unspecified，先补为 UTC）</summary>
    private static DateTime ToBeijingDate(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SystemTimeProvider.BeijingTz).Date;
    }

    /// <summary>UTC → 北京时间（判断时段用，如早鸟成就）</summary>
    private static DateTime ToBeijingLocal(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SystemTimeProvider.BeijingTz);
    }

    private List<AchievementDef>? _definitions;

    /// <summary>
    /// 成就定义列表
    /// </summary>
    public List<AchievementDef> Definitions => _definitions ??= new()
    {
        new("first_step", "👶", _loc["Achievement_FirstStep_Title"], _loc["Achievement_FirstStep_Desc"], "bronze", "study"),
        new("streak_3", "🔥", _loc["Achievement_Streak3_Title"], _loc["Achievement_Streak3_Desc"], "bronze", "study"),
        new("streak_7", "🔥", _loc["Achievement_Streak7_Title"], _loc["Achievement_Streak7_Desc"], "silver", "study"),
        new("streak_30", "🔥", _loc["Achievement_Streak30_Title"], _loc["Achievement_Streak30_Desc"], "gold", "study"),
        new("cards_10", "📚", _loc["Achievement_Cards10_Title"], _loc["Achievement_Cards10_Desc"], "bronze", "study"),
        new("cards_50", "📚", _loc["Achievement_Cards50_Title"], _loc["Achievement_Cards50_Desc"], "silver", "study"),
        new("cards_100", "📚", _loc["Achievement_Cards100_Title"], _loc["Achievement_Cards100_Desc"], "gold", "study"),
        new("cards_500", "📚", _loc["Achievement_Cards500_Title"], _loc["Achievement_Cards500_Desc"], "diamond", "study"),
        new("creator_1", "✏️", _loc["Achievement_Creator1_Title"], _loc["Achievement_Creator1_Desc"], "bronze", "creation"),
        new("creator_10", "✏️", _loc["Achievement_Creator10_Title"], _loc["Achievement_Creator10_Desc"], "silver", "creation"),
        new("explorer_1", "🤖", _loc["Achievement_Explorer1_Title"], _loc["Achievement_Explorer1_Desc"], "bronze", "exploration"),
        new("explorer_10", "🤖", _loc["Achievement_Explorer10_Title"], _loc["Achievement_Explorer10_Desc"], "silver", "exploration"),
        new("accuracy_80", "🎯", _loc["Achievement_Accuracy80_Title"], _loc["Achievement_Accuracy80_Desc"], "gold", "study"),
        new("early_bird", "🌅", _loc["Achievement_EarlyBird_Title"], _loc["Achievement_EarlyBird_Desc"], "bronze", "study"),
    };

    /// <summary>
    /// 记录学习活动并检查成就
    /// </summary>
    public async Task RecordActivityAsync(int learnerId, string vaultId, string activityType, string? cardId = null, string? result = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.StudyActivities.Add(new StudyActivity
        {
            LearnerId = learnerId,
            VaultId = vaultId,
            ActivityType = activityType,
            CardId = cardId,
            Result = result,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // 异步检查成就（不阻塞主流程）
        _ = Task.Run(async () => await CheckAndUnlockAsync(learnerId));
    }

    /// <summary>
    /// 检查并解锁成就
    /// </summary>
    public async Task<List<AchievementDef>> CheckAndUnlockAsync(int learnerId)
    {
        var newlyUnlocked = new List<AchievementDef>();
        using var db = await _dbFactory.CreateDbContextAsync();

        // 使用事务包裹读取-判断-写入，防止并发重复解锁
        await using var transaction = await db.Database.BeginTransactionAsync();

        try
        {
            // 已解锁的成就 Key（在事务内重新查询，确保一致性）
            var unlockedKeys = await db.Achievements
                .Where(a => a.LearnerId == learnerId)
                .Select(a => a.Key)
                .ToHashSetAsync();

            // 统计指标
            var totalStudy = await db.StudyActivities
                .CountAsync(a => a.LearnerId == learnerId && a.ActivityType == "study");
            var totalCreate = await db.StudyActivities
                .CountAsync(a => a.LearnerId == learnerId && a.ActivityType == "create_card");
            var totalChat = await db.StudyActivities
                .CountAsync(a => a.LearnerId == learnerId && a.ActivityType == "chat");

            // streak 计算
            var streak = await CalculateStreakAsync(db, learnerId);

            // 今日正确率
            var todayAccuracy = await CalculateTodayAccuracyAsync(db, learnerId);

            // 是否早鸟（北京时间 0-6 点完成学习）
            var earlyBirdTimes = await db.StudyActivities
                .Where(a => a.LearnerId == learnerId && a.ActivityType == "study")
                .Select(a => a.CreatedAt)
                .ToListAsync();
            var isEarlyBird = earlyBirdTimes.Any(t => ToBeijingLocal(t).Hour < 6);

            // 检查每个成就
            foreach (var def in Definitions)
            {
                if (unlockedKeys.Contains(def.Key)) continue;

                bool shouldUnlock = def.Key switch
                {
                    "first_step" => totalStudy >= 1,
                    "streak_3" => streak >= 3,
                    "streak_7" => streak >= 7,
                    "streak_30" => streak >= 30,
                    "cards_10" => totalStudy >= 10,
                    "cards_50" => totalStudy >= 50,
                    "cards_100" => totalStudy >= 100,
                    "cards_500" => totalStudy >= 500,
                    "creator_1" => totalCreate >= 1,
                    "creator_10" => totalCreate >= 10,
                    "explorer_1" => totalChat >= 1,
                    "explorer_10" => totalChat >= 10,
                    "accuracy_80" => todayAccuracy >= 0.8,
                    "early_bird" => isEarlyBird,
                    _ => false
                };

                if (shouldUnlock)
                {
                    db.Achievements.Add(new Achievement
                    {
                        LearnerId = learnerId,
                        Key = def.Key,
                        Title = def.Title,
                        Description = def.Description,
                        Icon = def.Icon,
                        Tier = def.Tier,
                        Category = def.Category,
                        UnlockedAt = DateTime.UtcNow
                    });
                    newlyUnlocked.Add(def);
                    _logger.LogInformation("学习者 {LearnerId} 解锁成就: {Title}", learnerId, def.Title);
                }
            }

            if (newlyUnlocked.Count > 0)
            {
                await db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            return newlyUnlocked;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "成就检查事务失败: LearnerId={LearnerId}", learnerId);
            throw;
        }
    }

    /// <summary>
    /// 获取学习者成就列表
    /// </summary>
    public async Task<List<AchievementViewModel>> GetAchievementsAsync(int learnerId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var unlocked = await db.Achievements
            .Where(a => a.LearnerId == learnerId)
            .ToListAsync();

        var unlockedKeys = unlocked.Select(a => a.Key).ToHashSet();

        return Definitions.Select(def => new AchievementViewModel
        {
            Key = def.Key,
            Title = def.Title,
            Description = def.Description,
            Icon = def.Icon,
            Tier = def.Tier,
            Category = def.Category,
            IsUnlocked = unlockedKeys.Contains(def.Key),
            UnlockedAt = unlocked.FirstOrDefault(a => a.Key == def.Key)?.UnlockedAt
        }).ToList();
    }

    private async Task<int> CalculateStreakAsync(FamilyDbContext db, int learnerId)
    {
        // 按北京时间天统计学习次数（存储为 UTC，需转换后再按天去重）
        var createdAts = await db.StudyActivities
            .Where(a => a.LearnerId == learnerId && a.ActivityType == "study")
            .Select(a => a.CreatedAt)
            .ToListAsync();
        var dates = createdAts
            .Select(ToBeijingDate)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        int streak = 0;
        var today = _timeProvider.Today;
        for (int i = 0; i < dates.Count; i++)
        {
            var expected = today.AddDays(-i);
            if (dates[i] == expected || (i == 0 && dates[i] == expected.AddDays(-1)))
            {
                streak++;
            }
            else
            {
                break;
            }
        }
        return streak;
    }

    private async Task<double> CalculateTodayAccuracyAsync(FamilyDbContext db, int learnerId)
    {
        // 按北京时间“今天”统计（存储为 UTC，日期边界需转换）
        var records = await db.StudyActivities
            .Where(a => a.LearnerId == learnerId && a.ActivityType == "study" && a.Result != null)
            .Select(a => new { a.CreatedAt, a.Result })
            .ToListAsync();

        var today = _timeProvider.Today;
        var todayRecords = records.Where(r => ToBeijingDate(r.CreatedAt) == today).ToList();

        if (todayRecords.Count == 0) return 0;
        var rememberCount = todayRecords.Count(r => r.Result == "remember");
        return (double)rememberCount / todayRecords.Count;
    }
}
