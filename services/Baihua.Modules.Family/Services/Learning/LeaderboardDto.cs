namespace Baihua.Modules.Family.Services;

public class DashboardData
{
    public List<FamilyMemberStat> FamilyStats { get; set; } = new();
    public List<DailyTrend> WeeklyTrend { get; set; } = new();
    public List<RecentAchievement> RecentAchievements { get; set; } = new();
    public ResultDistribution ResultDistribution { get; set; } = new();

    // ===== FAM-20 家长看板 v2（家庭日报）=====
    /// <summary>家庭维度连续打卡天数（任意成员有学习行为即算当天，北京时间自然日）</summary>
    public int FamilyStreak { get; set; }

    /// <summary>今日完成的卡片/任务数（北京时间）</summary>
    public int TodayCompleted { get; set; }

    /// <summary>昨日完成的卡片/任务数（北京时间）</summary>
    public int YesterdayCompleted { get; set; }

    /// <summary>趋势箭头：up=今天&gt;昨天 / down=今天&lt;昨天 / flat=持平 / ""=无数据（页面显示 --）</summary>
    public string TrendArrow { get; set; } = "";

    /// <summary>今日三件事条目（谁 + 做了什么）</summary>
    public List<TodayActivityItem> TodayActivities { get; set; } = new();

    /// <summary>最新成就（最多 3 个，按解锁时间倒序）</summary>
    public List<RecentAchievement> LatestAchievements { get; set; } = new();

    /// <summary>成长时间线（最近 30 天，时间倒序）</summary>
    public List<GrowthTimelineItem> GrowthTimeline { get; set; } = new();

    /// <summary>时间线分页大小（FAM-20-AC4 契约：每页 20 条）</summary>
    public int PageSize { get; set; } = TimelinePageSize;

    /// <summary>时间线每页条数契约（AC4）</summary>
    public const int TimelinePageSize = 20;
}

/// <summary>今日三件事条目：Learner 名 + 学习内容描述</summary>
public class TodayActivityItem
{
    public string LearnerName { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>成长时间线条目：日期 + Learner 名 + 事件描述</summary>
public class GrowthTimelineItem
{
    public DateTime? Date { get; set; }
    public string LearnerName { get; set; } = "";
    public string Description { get; set; } = "";
}

public class FamilyMemberStat
{
    public int LearnerId { get; set; }
    public string Name { get; set; } = "";
    public string AvatarEmoji { get; set; } = "";
    public string Color { get; set; } = "";
    public int WeekTotal { get; set; }
    public double Accuracy { get; set; }
    public int Streak { get; set; }
    public int TotalCards { get; set; }
}

public class DailyTrend
{
    public string Date { get; set; } = "";
    public int Count { get; set; }
}

public class RecentAchievement
{
    public string LearnerName { get; set; } = "";
    public string AvatarEmoji { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Tier { get; set; } = "";
    public DateTime UnlockedAt { get; set; }
}

public class ResultDistribution
{
    public int Remember { get; set; }
    public int Hard { get; set; }
    public int Forgot { get; set; }
}

public class LeaderboardEntry
{
    public int LearnerId { get; set; }
    public string LearnerName { get; set; } = "";
    public string AvatarEmoji { get; set; } = "";
    public string Color { get; set; } = "";
    public int CardsStudied { get; set; }
    public double Accuracy { get; set; }
    public int Score { get; set; }
    public int Streak { get; set; }
    public int Rank { get; set; }
}

/// <summary>FAM-22 "和自己比"结果：本周 vs 上周</summary>
public class WeeklyCompareResult
{
    /// <summary>本周完成卡片数（北京周界：周一 00:00 起）</summary>
    public int WeekTotal { get; set; }

    /// <summary>上周完成卡片数</summary>
    public int LastWeekTotal { get; set; }

    /// <summary>变化量 = 本周 - 上周</summary>
    public int Delta { get; set; }

    /// <summary>变化百分比（上周为 0 时无意义返回 0）</summary>
    public double Percent { get; set; }

    /// <summary>趋势箭头：up / down / flat / ""（无上周数据，页面显示 --）</summary>
    public string Arrow { get; set; } = "";
}
