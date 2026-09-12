using Baihua.Core.Models;
using Baihua.Core.Services;
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
/// FAM-21 契约探测：以反射方式读取"学习打卡页"契约（FAM-20 DashboardProbe 同套路）。
/// 契约缺失时返回 ContractPresent=false + MissingDetail，测试据此红并给出明确指引。
/// </summary>
public static class Fam21CheckinProbe
{
    public sealed record CheckinRecordItem(
        string LearnerName,
        string Content,
        DateTime? Time,
        bool IsCompleted,
        string Source,
        DateTime? StartTime,
        DateTime? EndTime,
        int CardCount,
        double Accuracy);

    public sealed record CalendarDayItem(DateTime? Date, bool IsChecked, bool IsToday);

    public sealed record CheckinSnapshot(
        bool ContractPresent,
        string MissingDetail,
        int FamilyStreak,
        IReadOnlyList<CheckinRecordItem> TodayRecords,
        IReadOnlyList<CalendarDayItem> Last7Days)
    {
        public static CheckinSnapshot Missing(string detail) =>
            new(false, detail, 0, Array.Empty<CheckinRecordItem>(), Array.Empty<CalendarDayItem>());
    }

    /// <summary>
    /// 打卡服务类型：优先新建的 CheckinService，兜底扫描名称含 Checkin 的 Service。
    /// </summary>
    public static Type? FindCheckinServiceType()
    {
        var asm = typeof(LeaderboardService).Assembly;
        var direct = asm.GetType("Baihua.Modules.Family.Services.CheckinService")
                    ?? asm.GetType("Baihua.Modules.Family.Services.Learning.CheckinService");
        if (direct != null) return direct;
        return asm.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && t.Name.Contains("Checkin", StringComparison.OrdinalIgnoreCase)
                                 && t.Name.EndsWith("Service", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 契约：存在返回打卡数据的服务方法（名称含 Checkin/CheckinData/GetCheckin）。
    /// </summary>
    public static bool HasCheckinDataMethod()
    {
        var type = FindCheckinServiceType();
        if (type == null) return false;
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Checkin", StringComparison.OrdinalIgnoreCase)
                      && m.ReturnType != typeof(void)
                      && (m.ReturnType.Name.Contains("Task") || m.ReturnType.IsGenericType));
    }

    /// <summary>
    /// 契约（端点层）：存在提供打卡数据的 GET action（名称含 Checkin）。
    /// </summary>
    public static bool HasCheckinEndpoint()
    {
        var controllers = typeof(LeaderboardService).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic
                        && t.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var controller in controllers)
        {
            foreach (var m in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!m.Name.Contains("Checkin", StringComparison.OrdinalIgnoreCase)) continue;
                var hasGet = m.GetCustomAttributes().Any(a => a.GetType().Name == "HttpGetAttribute");
                if (hasGet || m.GetParameters().All(p => p.GetCustomAttributes().Any(a => a.GetType().Name == "FromQueryAttribute")))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 构造打卡服务实例（时间参数注入固定时钟，其余由解析器提供）。
    /// </summary>
    public static object? CreateService(
        IDbContextFactory<FamilyDbContext> familyFactory,
        IDbContextFactory<VaultDbContext> vaultFactory,
        FakeTimeProvider clock,
        out string? error)
    {
        error = null;
        var type = FindCheckinServiceType();
        if (type == null)
        {
            error = "未找到 CheckinService（或名称含 Checkin 的服务类）（红）";
            return null;
        }

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
            var mi = typeof(TimeSourceProbe)
                .GetMethod(nameof(TimeSourceProbe.ConstructWithClock), BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(type);
            return mi.Invoke(null, new object?[] { clock, (Func<Type, object?>)(pt => ResolveDependency(pt, familyFactory, vaultFactory)) });
        }
        catch (Exception ex)
        {
            error = $"无法构造 {type.Name}: {Unwrap(ex).Message}（请对齐测试 ResolveDependency 或调整服务构造）（红）\n{ex}";
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
            // NullLogger<T>.Instance 在 10.x Abstractions 由属性改为字段，属性优先、字段兜底
            var loggerType = typeof(NullLogger<>).MakeGenericType(pt.GetGenericArguments()[0]);
            var instance = loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return instance ?? throw new InvalidOperationException($"NullLogger<{pt.GetGenericArguments()[0].Name}>.Instance 不可用");
        }
        if (pt == typeof(CardRepository))
        {
            return new CardRepository(
                new VaultSettingsService(vaultFactory, NullLogger<VaultSettingsService>.Instance),
                familyFactory,
                new LearnerService(familyFactory, NullLogger<LearnerService>.Instance),
                NullLogger<CardRepository>.Instance);
        }
        return null;
    }

    /// <summary>
    /// 调用打卡数据方法并归一化为快照。
    /// </summary>
    public static CheckinSnapshot GetSnapshot(object service, string vaultId)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.Contains("Checkin", StringComparison.OrdinalIgnoreCase)
                                 && m.ReturnType != typeof(void));
        if (method == null)
            return CheckinSnapshot.Missing(
                $"未找到打卡数据方法（期望 {type.Name}.GetCheckinDataAsync(vaultId) 或等价方法）（红）");

        object? result;
        try
        {
            var args = BuildArgs(method.GetParameters(), vaultId);
            result = method.Invoke(service, args);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                result = GetTaskResult(task);
            }
        }
        catch (Exception ex)
        {
            return CheckinSnapshot.Missing($"调用打卡方法失败: {Unwrap(ex).Message}（红）");
        }

        if (result == null)
            return CheckinSnapshot.Missing($"打卡方法返回 null（红）");

        return ReadSnapshot(result);
    }

    private static object?[] BuildArgs(ParameterInfo[] ps, string vaultId)
    {
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var name = p.Name ?? "";
            if (name.Contains("vault", StringComparison.OrdinalIgnoreCase))
                args[i] = vaultId;
            else if (p.ParameterType == typeof(CancellationToken))
                args[i] = CancellationToken.None;
            else
                args[i] = p.HasDefaultValue ? p.DefaultValue : null;
        }
        return args;
    }

    private static object? GetTaskResult(Task task)
        => task.GetType().GetProperty("Result")?.GetValue(task);

    private static CheckinSnapshot ReadSnapshot(object result)
    {
        var missing = new List<string>();

        var familyStreak = ReadInt(result, missing, "FamilyStreak", "Streak");

        var records = ReadList(result, missing, item => new CheckinRecordItem(
                ReadString(item, "LearnerName", "Name", "Learner") ?? "",
                ReadString(item, "Content", "Title", "CardTitle", "Name") ?? "",
                ReadDateTime(item, "Time", "CompletedAt", "CreatedAt"),
                ReadBool(item, "IsCompleted", "Completed", "StatusDone"),
                ReadString(item, "Source", "SourceType", "SourceLabel") ?? "",
                ReadDateTime(item, "StartTime", "StartedAt"),
                ReadDateTime(item, "EndTime", "FinishedAt"),
                ReadInt(item, null, "CardCount", "CardTotal"),
                ReadDouble(item, null, "Accuracy", "AccuracyRate")),
            "TodayRecords", "CheckinItems", "TodayItems");

        var calendar = ReadList(result, missing, item => new CalendarDayItem(
                ReadDateTime(item, "Date", "Day"),
                ReadBool(item, "IsChecked", "Checked", "HasActivity"),
                ReadBool(item, "IsToday", "Today")),
            "Last7Days", "Calendar");

        if (missing.Count > 0)
            return CheckinSnapshot.Missing($"打卡结果缺少字段: {string.Join(", ", missing)}（红）");

        return new CheckinSnapshot(true, "", familyStreak, records, calendar);
    }

    private static int ReadInt(object o, List<string>? missing, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) { missing?.Add(names[0]); return 0; }
        var v = prop.GetValue(o);
        return v == null ? 0 : Convert.ToInt32(v);
    }

    private static double ReadDouble(object o, List<string>? missing, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) { missing?.Add(names[0]); return 0; }
        var v = prop.GetValue(o);
        return v == null ? 0 : Convert.ToDouble(v);
    }

    private static bool ReadBool(object o, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) return false;
        var v = prop.GetValue(o);
        return v switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var b) && b,
            _ => false
        };
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

    private static List<T> ReadList<T>(object o, List<string>? missing, Func<object, T> map, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = FindProp(o, name);
            if (prop == null) continue;
            var v = prop.GetValue(o);
            if (v is not System.Collections.IEnumerable items) { missing?.Add(name); return new List<T>(); }
            var list = new List<T>();
            foreach (var item in items)
            {
                if (item != null) list.Add(map(item));
            }
            return list;
        }
        missing?.Add(names[0]);
        return new List<T>();
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
