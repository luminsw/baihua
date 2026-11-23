using System.Reflection;
using Baihua.Core.Localization;
using Baihua.Data;
using Baihua.Modules.Family.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-20 契约探测：以反射方式读取"家长看板 v2"契约，
/// 避免测试代码直接引用尚未实现的成员导致编译失败（那样全项目一起红，信息量差）。
///
/// 契约缺失时返回 ContractPresent=false + MissingDetail，测试据此红并给出明确指引。
/// 契约就绪后，本探测负责把后端返回对象归一化为测试侧快照，供时区边界语义断言使用。
/// </summary>
public static class Fam20DashboardProbe
{
    // 趋势箭头契约值（AC1）：今天>昨天→up；今天<昨天→down；持平→flat；无数据→""（页面显示"--"）
    public const string TrendUp = "up";
    public const string TrendDown = "down";
    public const string TrendFlat = "flat";
    public const string TrendNone = "";

    /// <summary>时间线每页条数契约（AC4 分页，每页 20 条）</summary>
    public const int TimelinePageSize = 20;

    public sealed record TodayActivityItem(string LearnerName, string Description);
    public sealed record LatestAchievementItem(string Title, string Icon, DateTime? UnlockedAt);
    public sealed record TimelineItem(DateTime? Date, string LearnerName, string Description);

    public sealed record DashboardSnapshot(
        bool ContractPresent,
        string MissingDetail,
        int FamilyStreak,
        int TodayCompleted,
        int YesterdayCompleted,
        string TrendArrow,
        IReadOnlyList<TodayActivityItem> TodayActivities,
        IReadOnlyList<LatestAchievementItem> LatestAchievements,
        IReadOnlyList<TimelineItem> GrowthTimeline,
        int PageSize)
    {
        public static DashboardSnapshot Missing(string detail) =>
            new(false, detail, 0, 0, 0, "",
                Array.Empty<TodayActivityItem>(),
                Array.Empty<LatestAchievementItem>(),
                Array.Empty<TimelineItem>(),
                0);
    }

    /// <summary>
    /// 看板服务类型：优先新建的 DashboardService，兜底扩展现有 LeaderboardService。
    /// </summary>
    public static Type FindDashboardServiceType()
    {
        var asm = typeof(LeaderboardService).Assembly;
        return asm.GetType("Baihua.Modules.Family.Services.DashboardService")
            ?? asm.GetType("Baihua.Modules.Family.Services.Learning.DashboardService")
            ?? typeof(LeaderboardService);
    }

    /// <summary>
    /// AC5 契约：看板数据方法必须支持成员筛选（参数名含 learner）。
    /// 期望形状：GetDashboardAsync(string? vaultId, int? learnerId, ...) 或 GetGrowthTimelineAsync(..., int? learnerId, ...)。
    /// </summary>
    public static bool HasLearnerFilteredDashboardMethod()
    {
        return FindDashboardServiceType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase)
                      && m.GetParameters().Any(p =>
                          p.Name != null && p.Name.Contains("learner", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// AC5 契约（端点层）：/api/achievements/dashboard（或新 Dashboard 端点）必须接受 learnerId 查询参数。
    /// </summary>
    public static bool HasDashboardEndpointWithLearnerFilter()
    {
        var controllers = typeof(LeaderboardService).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                        && (t.Name == "AchievementsController"
                            || t.Name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var controller in controllers)
        {
            foreach (var m in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!m.Name.Equals("GetDashboard", StringComparison.OrdinalIgnoreCase)) continue;
                if (m.GetParameters().Any(p =>
                        p.Name != null && p.Name.Contains("learner", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 构造看板服务实例。现有 LeaderboardService 直接 new；未来 DashboardService 用反射按需装配。
    /// </summary>
    public static object? CreateService(
        IDbContextFactory<FamilyDbContext> familyFactory,
        IDbContextFactory<VaultDbContext> vaultFactory,
        FakeTimeProvider clock,
        out string? error)
    {
        error = null;
        var type = FindDashboardServiceType();
        if (type == typeof(LeaderboardService))
            return new LeaderboardService(familyFactory, clock);

        // 其他 Dashboard*Service：时间参数注入固定时钟，其余参数由解析器提供
        var (injectable, compatible) = TimeSourceProbe.Probe(type);
        if (!injectable)
        {
            error = $"{type.Name} 缺少可注入时间源（ITimeProvider 参数）（红）";
            return null;
        }
        if (!compatible)
        {
            error = $"{type.Name} 时间注入点与 ITimeProvider 不兼容（红）";
            return null;
        }

        try
        {
            // ConstructWithClock<T> 是泛型方法，Type 变量需反射调用
            var mi = typeof(TimeSourceProbe)
                .GetMethod(nameof(TimeSourceProbe.ConstructWithClock), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type);
            return mi.Invoke(null, new object?[] { clock, (Func<Type, object?>)(pt => ResolveDependency(pt, familyFactory, vaultFactory)) });
        }
        catch (Exception ex)
        {
            error = $"无法构造 {type.Name}: {ex.Message}（请对齐测试 ResolveDependency 或调整服务构造）（红）";
            return null;
        }
    }

    private static object? ResolveDependency(
        Type pt,
        IDbContextFactory<FamilyDbContext> familyFactory,
        IDbContextFactory<VaultDbContext> vaultFactory)
    {
        if (pt == typeof(IDbContextFactory<FamilyDbContext>)) return familyFactory;
        if (pt == typeof(IDbContextFactory<VaultDbContext>)) return vaultFactory;
        if (pt == typeof(IStringLocalizer<SharedResources>)) return TestLocalizer.Create();
        if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            var loggerType = typeof(NullLogger<>).MakeGenericType(pt.GetGenericArguments()[0]);
            return loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        }
        return null; // 未知依赖交给构造函数（可能为 null，dev 需对齐）
    }

    /// <summary>
    /// 调用带成员筛选的看板方法并归一化为快照。
    /// 契约缺失（方法不存在 / 字段缺失）→ Missing 快照（红）。
    /// </summary>
    public static DashboardSnapshot GetSnapshot(object service, string vaultId, int? learnerId)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(m => m.GetParameters().Any(p =>
                p.Name != null && p.Name.Contains("learner", StringComparison.OrdinalIgnoreCase)));

        if (method == null)
            return DashboardSnapshot.Missing(
                $"未找到支持成员筛选的看板方法（期望 {type.Name}.GetDashboardAsync(vaultId, learnerId) 或等价方法）（红）");

        object? result;
        try
        {
            var args = BuildArgs(method.GetParameters(), vaultId, learnerId);
            result = method.Invoke(service, args);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                result = GetTaskResult(task);
            }
        }
        catch (Exception ex)
        {
            return DashboardSnapshot.Missing($"调用看板方法失败: {Unwrap(ex).Message}（红）");
        }

        if (result == null)
            return DashboardSnapshot.Missing($"看板方法返回 null（红）");

        return ReadSnapshot(result);
    }

    private static object?[] BuildArgs(ParameterInfo[] ps, string vaultId, int? learnerId)
    {
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var name = p.Name ?? "";
            if (name.Contains("learner", StringComparison.OrdinalIgnoreCase))
                args[i] = Convert.ChangeType((object?)learnerId ?? 0, Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType);
            else if (name.Contains("vault", StringComparison.OrdinalIgnoreCase))
                args[i] = vaultId;
            else if (name.Contains("page", StringComparison.OrdinalIgnoreCase))
                args[i] = 1;
            else if (name.Contains("size", StringComparison.OrdinalIgnoreCase) || name.Contains("count", StringComparison.OrdinalIgnoreCase))
                args[i] = TimelinePageSize;
            else if (p.ParameterType == typeof(CancellationToken))
                args[i] = CancellationToken.None;
            else
                args[i] = p.HasDefaultValue ? p.DefaultValue : null;
        }
        return args;
    }

    private static object? GetTaskResult(Task task)
    {
        var prop = task.GetType().GetProperty("Result");
        return prop?.GetValue(task);
    }

    private static DashboardSnapshot ReadSnapshot(object result)
    {
        var missing = new List<string>();

        var familyStreak = ReadInt(result, "FamilyStreak", missing);
        var todayCompleted = ReadInt(result, "TodayCompleted", missing);
        var yesterdayCompleted = ReadInt(result, "YesterdayCompleted", missing);
        var trend = ReadTrend(result, missing);

        var todayActivities = ReadList(result, "TodayActivities", missing,
            item => new TodayActivityItem(
                ReadString(item, "LearnerName", "Name", "Learner") ?? "",
                ReadString(item, "Description", "Content", "Text", "Summary") ?? ""));
        var latestAchievements = ReadList(result, "LatestAchievements", missing,
            item => new LatestAchievementItem(
                ReadString(item, "Title", "Name") ?? "",
                ReadString(item, "Icon") ?? "",
                ReadDateTime(item, "UnlockedAt")));
        var timeline = ReadList(result, "GrowthTimeline", missing,
            item => new TimelineItem(
                ReadDateTime(item, "Date", "CreatedAt", "UnlockedAt"),
                ReadString(item, "LearnerName", "Name", "Learner") ?? "",
                ReadString(item, "Description", "Content", "Text") ?? ""));

        var pageSize = ReadInt(result, "PageSize", new List<string>()); // 分页：可来自结果对象；缺失不致命（另有方法参数契约）

        if (missing.Count > 0)
            return DashboardSnapshot.Missing($"看板结果缺少字段: {string.Join(", ", missing)}（红）");

        return new DashboardSnapshot(
            true, "",
            familyStreak, todayCompleted, yesterdayCompleted, trend,
            todayActivities, latestAchievements, timeline, pageSize);
    }

    private static string ReadTrend(object o, List<string> missing)
    {
        var prop = FindProp(o, "TrendArrow", "Trend");
        if (prop == null) { missing.Add("TrendArrow"); return ""; }
        var v = prop.GetValue(o);
        if (v == null) { missing.Add("TrendArrow"); return ""; }
        return v is string s ? s.ToLowerInvariant() : v.ToString()!.ToLowerInvariant();
    }

    private static int ReadInt(object o, string name, List<string> missing)
    {
        var prop = FindProp(o, name);
        if (prop == null) { missing.Add(name); return 0; }
        var v = prop.GetValue(o);
        return v == null ? 0 : Convert.ToInt32(v);
    }

    private static DateTime? ReadDateTime(object o, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) return null;
        var v = prop.GetValue(o);
        return v switch
        {
            null => null,
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            string str when DateTime.TryParse(str, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(object o, params string[] names)
    {
        var prop = FindProp(o, names);
        return prop?.GetValue(o)?.ToString();
    }

    private static List<T> ReadList<T>(object o, string name, List<string> missing, Func<object, T> map)
    {
        var prop = FindProp(o, name);
        if (prop == null) { missing.Add(name); return new List<T>(); }
        var v = prop.GetValue(o);
        if (v is not System.Collections.IEnumerable items) { missing.Add(name); return new List<T>(); }
        var list = new List<T>();
        foreach (var item in items)
        {
            if (item != null) list.Add(map(item));
        }
        return list;
    }

    private static PropertyInfo? FindProp(object o, params string[] names)
    {
        var t = o.GetType();
        foreach (var name in names)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p;
        }
        return null;
    }

    private static Exception Unwrap(Exception ex)
        => ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
}
