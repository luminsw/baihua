namespace Baihua.Modules.Family.Services;

/// <summary>
/// FAM-20/21 共享连续打卡计算（arch P2 建议：消除 LeaderboardService / CheckinService 重复拷贝）。
///
/// 锚点算法：今天有行为 → 锚点=today；今天无但昨天有 → 锚点=yesterday；否则 → 0。
/// 从锚点往回数连续自然日（断一天即停）。
/// 注意：日期必须已是北京时间自然日（调用方负责时区转换）。
/// </summary>
public static class StreakCalculator
{
    /// <summary>
    /// 计算连续打卡天数（任意成员/单成员共用同一算法，日期集合由调用方按需聚合）。
    /// </summary>
    public static int Calculate(List<DateTime> beijingDates, DateTime today)
    {
        var dateSet = new HashSet<DateTime>(beijingDates);
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
