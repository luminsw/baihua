using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Core.Time;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// FAM-21/33 学习打卡服务：今日学习清单 + 家庭连续打卡 + 最近 7 天日历 + 补签 + 连击保护。
/// 打卡 = 当天（北京时间自然日）有 StudyActivity 记录，自动判定；FAM-33 增加：
///   - 补签：3 天窗口内、有学习记录但未打卡的日期可补签，月限 3 次
///   - 连击保护：今天还没学不归零（宽限状态），中断 2 天归零
/// </summary>
public class CheckinService
{
    private const int MakeupWindowDays = 3;
    private const int MakeupMonthlyLimit = 3;

    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly ITimeProvider _timeProvider;
    private readonly CardRepository _cardRepository;
    private readonly ILogger<CheckinService> _logger;

    public CheckinService(IDbContextFactory<FamilyDbContext> dbFactory, ITimeProvider timeProvider, CardRepository cardRepository, ILogger<CheckinService> logger)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _cardRepository = cardRepository;
        _logger = logger;
    }

    /// <summary>北京时间今日</summary>
    private DateTime BeijingToday => _timeProvider.Today;

    /// <summary>UTC 转北京时间日期</summary>
    private static DateTime ToBeijingDate(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SystemTimeProvider.BeijingTz).Date;
    }

    /// <summary>
    /// 获取学习打卡数据（FAM-21 + FAM-33 补签/保护扩展）
    /// </summary>
    /// <param name="vaultId">知识库 ID（可空=全部）</param>
    public async Task<CheckinData> GetCheckinDataAsync(string? vaultId = null)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var today = BeijingToday;

        var activityQuery = db.StudyActivities.AsQueryable();
        if (!string.IsNullOrEmpty(vaultId))
            activityQuery = activityQuery.Where(a => a.VaultId == vaultId);

        var activities = (await activityQuery.ToListAsync())
            .Select(a => new
            {
                a.LearnerId,
                a.ActivityType,
                a.CardId,
                a.VaultId,
                a.Result,
                Date = ToBeijingDate(a.CreatedAt),
                a.CreatedAt
            })
            .ToList();

        var learners = await db.LearnerProfiles.ToListAsync();
        var learnerMap = learners.ToDictionary(l => l.Id);

        // SQLite 读出的 DateTime.Kind=Unspecified，统一转北京时间存储（测试与展示口径一致）
        DateTime ToBeijingLocal(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, SystemTimeProvider.BeijingTz);
        }

        // ===== AC1：今日学习清单（按 Learner 分组）=====
        var todayActivities = activities.Where(a => a.Date == today).ToList();
        // 预加载今日涉及知识库的卡片标题（避免每条记录重复解析卡片文件）
        var cardFrontByKey = new Dictionary<string, string>();
        var todayVaultIds = todayActivities.Select(a => a.VaultId).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct();
        foreach (var vid in todayVaultIds)
        {
            try
            {
                var cardsPath = _cardRepository.ResolveCardsPath(vid!);
                if (string.IsNullOrEmpty(cardsPath) || !Directory.Exists(cardsPath)) continue;
                foreach (var c in _cardRepository.LoadAllCards(cardsPath))
                {
                    cardFrontByKey[$"{vid}:{c.Id}"] = c.Front;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "加载知识库卡片失败: {VaultId}", vid);
            }
        }
        var records = new List<CheckinRecord>();
        foreach (var act in todayActivities.OrderBy(a => a.CreatedAt))
        {
            var learner = learnerMap.GetValueOrDefault(act.LearnerId);
            var beijingTime = ToBeijingLocal(act.CreatedAt);
            records.Add(new CheckinRecord
            {
                LearnerName = learner?.Name ?? "",
                Content = ResolveContent(act.CardId, act.ActivityType, act.VaultId, cardFrontByKey),
                Time = beijingTime,
                IsCompleted = true, // 有学习记录 = 自动已打卡（AC2）
                Source = ResolveSource(act.ActivityType),
                StartTime = beijingTime,
                EndTime = beijingTime,
                CardCount = 1,
                Accuracy = ResolveAccuracy(act.Result)
            });
        }

        // 按 Learner 分组排序：Learner 名升序 + 时间倒序
        records = records
            .OrderBy(r => r.LearnerName)
            .ThenByDescending(r => r.Time)
            .ToList();

        // ===== AC2/AC4：家庭连续打卡（带 FAM-33 连击保护）=====
        var beijingDates = activities.Select(a => a.Date).ToList();
        var makeupDates = (await db.CheckinMakeupRecords.ToListAsync())
            .Select(m => m.MakeupDate.Date)
            .ToList();
        var allCheckedDates = beijingDates.Concat(makeupDates).ToList();

        var familyStreak = CalculateFamilyStreak(allCheckedDates, today);
        var streakStatus = BuildStreakStatus(beijingDates, today, familyStreak);

        // ===== 补签剩余次数（AC2：月限 3 次，家庭维度）=====
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var makeupThisMonth = makeupDates.Count(d => d >= monthStart && d <= today);
        var makeupRemaining = Math.Max(0, MakeupMonthlyLimit - makeupThisMonth);

        // ===== AC4：最近 7 天打卡日历（7 格，恰好 1 格 IsToday）=====
        var last7Days = new List<CheckinCalendarDay>();
        var checkedSet = new HashSet<DateTime>(allCheckedDates);
        var hasActivitySet = new HashSet<DateTime>(beijingDates);
        for (int i = 6; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var hasActivity = hasActivitySet.Contains(date);
            var isChecked = checkedSet.Contains(date);
            last7Days.Add(new CheckinCalendarDay
            {
                Date = date,
                IsChecked = isChecked,
                IsToday = date == today,
                // AC3：可补签 = 3 天窗口内 + 该日无学习记录（⬜ 格才显示补签入口，🔥 格不可补签）
                IsMakeupable = !isChecked && !hasActivity
                               && date < today
                               && (today - date).Days <= MakeupWindowDays
            });
        }

        return new CheckinData
        {
            FamilyStreak = familyStreak,
            StreakStatus = streakStatus,
            MakeupRemaining = makeupRemaining,
            TodayRecords = records,
            Last7Days = last7Days
        };
    }

    /// <summary>
    /// FAM-33 补签（pm 拍板语义）：对最近 3 天内**无 StudyActivity** 的 ⬜ 日期打补签标记，
    /// 填补连击缺口。不创建虚假 StudyActivity。月限 3 次（家庭维度）。
    /// </summary>
    /// <param name="beijingDate">补签日期（北京时间自然日）</param>
    /// <param name="vaultId">知识库 ID（可空=全部）</param>
    /// <returns>补签结果（Success/Message/Remaining）</returns>
    public async Task<CheckinMakeupResult> MakeupCheckinAsync(DateTime beijingDate, string? vaultId = null)
    {
        var today = BeijingToday;
        var date = beijingDate.Date;

        using var db = await _dbFactory.CreateDbContextAsync();

        // AC2：月限 3 次（家庭维度，按补签日期所在月）——优先于其他检查
        var monthStart = new DateTime(date.Year, date.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var makeupCountThisMonth = await db.CheckinMakeupRecords
            .CountAsync(m => m.MakeupDate >= monthStart && m.MakeupDate < monthEnd);
        if (makeupCountThisMonth >= MakeupMonthlyLimit)
        {
            return new CheckinMakeupResult
            {
                Success = false,
                Message = "本月补签次数已用完",
                Remaining = 0
            };
        }

        // 窗口校验：最近 3 天内（不含今天/未来）
        var daysAgo = (today - date).Days;
        if (date >= today || daysAgo > MakeupWindowDays)
        {
            return new CheckinMakeupResult
            {
                Success = false,
                Message = "仅可补签最近 3 天内的日期",
                Remaining = 0
            };
        }

        // pm 拍板语义：该日已有学习记录 → 已是 🔥，无需补签（不显示入口）；
        // 无 StudyActivity 的 ⬜ 日期才允许补签（填补连击缺口，不创建虚假记录）
        var activityQuery = db.StudyActivities.AsQueryable();
        if (!string.IsNullOrEmpty(vaultId))
            activityQuery = activityQuery.Where(a => a.VaultId == vaultId);
        var activities = await activityQuery.ToListAsync();
        var hasActivity = activities.Any(a => ToBeijingDate(a.CreatedAt) == date);
        if (hasActivity)
        {
            return new CheckinMakeupResult
            {
                Success = false,
                Message = "该日已有学习记录，无需补签",
                Remaining = 0
            };
        }

        // 幂等：同一天已补签过则直接成功（不重复计数）
        var alreadyMadeUp = await db.CheckinMakeupRecords
            .AnyAsync(m => m.MakeupDate == date
                           && (vaultId == null || m.VaultId == vaultId));
        if (!alreadyMadeUp)
        {
            db.CheckinMakeupRecords.Add(new CheckinMakeupRecord
            {
                MakeupDate = date,
                VaultId = vaultId,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var remaining = Math.Max(0, MakeupMonthlyLimit - makeupCountThisMonth - (alreadyMadeUp ? 0 : 1));
        return new CheckinMakeupResult
        {
            Success = true,
            Message = "补签成功",
            Remaining = remaining
        };
    }

    /// <summary>
    /// 家庭维度连续打卡（FAM-33 连击保护）：
    /// 今天有 → 锚点 today；今天无但昨天有 → 锚点 yesterday（保护：今天还没学不归零）；
    /// 昨天也无 → 中断 2 天 → 归零。
    /// </summary>
    private static int CalculateFamilyStreak(List<DateTime> checkedDates, DateTime today)
    {
        var dateSet = new HashSet<DateTime>(checkedDates);
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

    /// <summary>
    /// FAM-33 连击保护状态文案：
    ///   - 今天还没学（昨天有记录）→ "今天还没学"
    ///   - 已中断 1 天（前天有记录，昨天/今天无）→ "已中断 1 天，明天前补学可恢复"
    ///   - 其余 → ""
    /// </summary>
    private static string BuildStreakStatus(List<DateTime> activityDates, DateTime today, int familyStreak)
    {
        if (familyStreak <= 0) return "";
        var activitySet = new HashSet<DateTime>(activityDates);
        if (activitySet.Contains(today)) return "";
        if (activitySet.Contains(today.AddDays(-1)))
            return "今天还没学";
        return "已中断 1 天，明天前补学可恢复";
    }

    /// <summary>学习内容名称（AC1）：优先卡片 ID，缺失时按活动类型描述</summary>
    /// <summary>解析打卡清单内容：优先显示卡片标题（家长能看懂孩子学了什么），否则用活动类型文案</summary>
    private string ResolveContent(string? cardId, string activityType, string? vaultId, Dictionary<string, string> cardFrontByKey)
    {
        if (!string.IsNullOrWhiteSpace(cardId) && !string.IsNullOrWhiteSpace(vaultId))
        {
            if (cardFrontByKey.TryGetValue($"{vaultId}:{cardId}", out var front))
            {
                var title = front.Trim().Replace("\n", " ");
                return title.Length > 30 ? title[..30] + "…" : title;
            }
            return "卡片学习";
        }
        return activityType switch
        {
            "study" => "每日卡片学习",
            "create_card" => "创作学习卡片",
            "generate_cards" => "批量生成卡片",
            "chat" => "AI 对话学习",
            _ => "学习活动"
        };
    }

    /// <summary>来源标签（AC3 可追溯）：按 ActivityType 映射（StudyActivity 无 session 级字段）</summary>
    private static string ResolveSource(string activityType)
    {
        return activityType switch
        {
            "study" => "每日卡片",
            "review" => "复习模式",
            "create_card" or "generate_cards" => "自由学习",
            _ => "自由学习"
        };
    }

    /// <summary>正确率推断（AC3）：remember=100 / hard=50 / forgot=0</summary>
    private static double ResolveAccuracy(string? result)
    {
        return result switch
        {
            "remember" => 100,
            "hard" => 50,
            _ => 0
        };
    }
}
