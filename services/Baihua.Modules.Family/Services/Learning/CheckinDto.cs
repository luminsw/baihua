namespace Baihua.Modules.Family.Services;

/// <summary>
/// FAM-21 学习打卡数据（家庭维度，北京时间自然日）
/// </summary>
public class CheckinData
{
    /// <summary>家庭维度连续打卡天数（从今天往前连续有记录的天数；任意成员有学习行为即算当天）</summary>
    public int FamilyStreak { get; set; }

    /// <summary>
    /// FAM-33 连击保护状态：""（正常）/ "今天还没学" / "已中断 1 天，明天前补学可恢复"
    /// </summary>
    public string StreakStatus { get; set; } = "";

    /// <summary>FAM-33 本月剩余补签次数（月限 3 次，家庭维度）</summary>
    public int MakeupRemaining { get; set; }

    /// <summary>今日学习清单（按 Learner 分组）</summary>
    public List<CheckinRecord> TodayRecords { get; set; } = new();

    /// <summary>最近 7 天打卡日历（7 格：日期 + 是否打卡 + 是否今天）</summary>
    public List<CheckinCalendarDay> Last7Days { get; set; } = new();
}

/// <summary>FAM-33 补签结果</summary>
public class CheckinMakeupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int Remaining { get; set; }
}

/// <summary>今日学习记录条目（AC1/AC3：内容名称 + 学习时间 + 完成状态 + 可追溯详情）</summary>
public class CheckinRecord
{
    public string LearnerName { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime? Time { get; set; }
    public bool IsCompleted { get; set; }

    /// <summary>来源标签（每日卡片/自由学习/复习模式）</summary>
    public string Source { get; set; } = "";

    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int CardCount { get; set; }
    public double Accuracy { get; set; }
}

/// <summary>打卡日历格子（AC4：日期 + 是否打卡 + 是否今天）</summary>
public class CheckinCalendarDay
{
    public DateTime? Date { get; set; }
    public bool IsChecked { get; set; }
    public bool IsToday { get; set; }

    /// <summary>FAM-33：是否可补签（⬜ 格、3 天窗口内；🔥 格不可补签）</summary>
    public bool IsMakeupable { get; set; }
}
