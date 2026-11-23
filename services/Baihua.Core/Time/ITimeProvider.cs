namespace Baihua.Core.Time;

/// <summary>
/// 可注入时间源（FAM-01 统一时区）。
///
/// 家庭场景所有"今天/本周/本月"一律按北京时间（Asia/Shanghai）计算，
/// 避免 UTC 与本地混用导致每日 0:00–8:00 期间进度/排行计算错误。
/// </summary>
public interface ITimeProvider
{
    /// <summary>当前 UTC 时间</summary>
    DateTime UtcNow { get; }

    /// <summary>当前北京时间（Asia/Shanghai）</summary>
    DateTime Now { get; }

    /// <summary>当前北京时间日期（今日 00:00）</summary>
    DateTime Today { get; }
}

/// <summary>
/// 系统时钟实现：北京时间（Asia/Shanghai）。
/// </summary>
public sealed class SystemTimeProvider : ITimeProvider
{
    /// <summary>北京时间时区（优先 IANA 名，回退 Windows 名）</summary>
    public static readonly TimeZoneInfo BeijingTz = ResolveBeijingTz();

    private static TimeZoneInfo ResolveBeijingTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }

    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BeijingTz);
    public DateTime Today => Now.Date;
}
