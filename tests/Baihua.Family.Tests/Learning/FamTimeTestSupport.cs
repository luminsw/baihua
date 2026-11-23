using System.Reflection;
using Baihua.Core.Time;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-01 契约：可注入时间源接口（产品侧定义于 Baihua.Core.Time.ITimeProvider）。
/// 测试通过反射按"类型名含 Time/Clock 且 FakeTimeProvider 可赋值"识别注入点。
/// </summary>
/// <remarks>
/// 产品接口与 FakeTimeProvider 的兼容性由 TimeSourceProbe.Compatible 锁定：
/// 若产品接口形状变更（如改名/加方法），本测试会明确失败提示。
/// </remarks>

/// <summary>
/// 固定时钟：锁定"北京时间 2026-08-07 07:30"（周五）。
/// 注意 07:30 北京 = 2026-08-06T23:30Z —— 北京日期与 UTC 日期不同，
/// 恰好用于暴露"用 UTC 算今天"的时区 bug。
/// </summary>
public sealed class FakeTimeProvider : ITimeProvider
{
    private static readonly TimeZoneInfo BeijingTz = ResolveBeijingTz();

    private static TimeZoneInfo ResolveBeijingTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
    }

    private readonly DateTime _utcNow;

    public FakeTimeProvider(DateTime beijingLocalNow)
    {
        _utcNow = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(beijingLocalNow, DateTimeKind.Unspecified), BeijingTz);
    }

    public DateTime UtcNow => _utcNow;
    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(_utcNow, BeijingTz);
    public DateTime Today => Now.Date;

    /// <summary>固定"现在"：北京时间 2026-08-07（周五）07:30</summary>
    public static FakeTimeProvider Beijing20260807_0730() => new(new DateTime(2026, 8, 7, 7, 30, 0));
}

public static class TimeSourceProbe
{
    /// <summary>
    /// 探测服务是否具备可注入时间源。
    /// </summary>
    /// <returns>(是否找到注入点, 注入点类型是否与 FakeTimeProvider 兼容)</returns>
    public static (bool Injectable, bool Compatible) Probe(Type serviceType)
    {
        var ctor = serviceType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor == null) return (false, false);

        var timeParam = ctor.GetParameters().FirstOrDefault(p =>
            p.ParameterType.Name.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
            p.ParameterType.Name.Contains("Clock", StringComparison.OrdinalIgnoreCase));
        if (timeParam == null) return (false, false);

        return (true, timeParam.ParameterType.IsAssignableFrom(typeof(FakeTimeProvider)));
    }

    /// <summary>
    /// 反射构造服务实例：时间参数注入 FakeTimeProvider，其余参数由 resolver 提供。
    /// </summary>
    public static T ConstructWithClock<T>(FakeTimeProvider clock, Func<Type, object?> resolver)
    {
        var ctor = typeof(T).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            if (pt.IsAssignableFrom(typeof(FakeTimeProvider)))
                args[i] = clock;
            else
                args[i] = resolver(pt);
        }
        return (T)ctor.Invoke(args);
    }
}

/// <summary>
/// 仓库路径解析：从测试输出目录向上逐层查找仓库内文件。
/// 兼容默认输出（bin/Debug/net10.0）与隔离输出（bin/famtest/net10.0）等不同层级。
/// </summary>
public static class RepoPath
{
    public static string FindUp(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"仓库内找不到文件: {relativePath}");
    }
}
