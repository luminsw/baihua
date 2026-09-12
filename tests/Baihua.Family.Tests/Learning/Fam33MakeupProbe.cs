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
/// FAM-33 契约探测：补签 + 连击保护（FAM-21 CheckinProbe 同套路）。
/// 契约缺失时返回 ContractPresent=false + MissingDetail，测试据此红。
/// </summary>
public static class Fam33MakeupProbe
{
    public sealed record CalendarCell(DateTime? Date, bool IsChecked, bool IsToday, bool IsMakeupable);

    public sealed record StreakState(int Days, string Status);

    public sealed record CheckinSnapshot(
        bool ContractPresent,
        string MissingDetail,
        int FamilyStreak,
        string StreakStatus,
        int MakeupRemaining,
        IReadOnlyList<CalendarCell> Last7Days)
    {
        public static CheckinSnapshot Missing(string detail) =>
            new(false, detail, 0, "", 0, Array.Empty<CalendarCell>());
    }

    public sealed record MakeupResult(
        bool ContractPresent,
        string MissingDetail,
        bool Success,
        string Message,
        int RemainingAfter);

    /// <summary>打卡服务类型（CheckinService 或名称含 Checkin 的 Service）</summary>
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
    /// AC1 契约：存在补签方法（名称含 Makeup/补签，参数含日期）。
    /// 期望形状：Task&lt;MakeupResult&gt; MakeupCheckinAsync(DateTime beijingDate, string? vaultId) 或等价。
    /// </summary>
    public static bool HasMakeupMethod()
    {
        var type = FindCheckinServiceType();
        if (type == null) return false;
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Makeup", StringComparison.OrdinalIgnoreCase)
                      && m.GetParameters().Any(p =>
                          p.ParameterType == typeof(DateTime) || p.ParameterType == typeof(DateTime?)));
    }

    /// <summary>
    /// AC4 契约：打卡数据含连击保护状态（今天还没学/已中断 N 天）。
    /// 期望：CheckinData.StreakStatus 或等价格式（如 "grace"/"protected" + 文案）。
    /// </summary>
    public static bool HasStreakProtectionField()
    {
        var type = FindCheckinServiceType();
        if (type == null) return false;
        // 返回类型 CheckinData（或等价）含 Streak 状态字段
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.Contains("Checkin", StringComparison.OrdinalIgnoreCase)
                                 && m.ReturnType != typeof(void));
        if (method == null) return false;
        var ret = method.ReturnType;
        if (ret.IsGenericType) ret = ret.GetGenericArguments()[0];
        return ret.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.Name.Contains("Streak", StringComparison.OrdinalIgnoreCase)
                      && (p.Name.Contains("Status", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Contains("Protect", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Contains("Grace", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 构造打卡服务实例（时间参数注入固定时钟）。
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
            error = "未找到 CheckinService（红）";
            return null;
        }
        var (injectable, compatible) = TimeSourceProbe.Probe(type);
        if (!injectable)
        {
            error = $"{type.Name} 缺少可注入时间源（红）";
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
            error = $"无法构造 {type.Name}: {Unwrap(ex).Message}（红）\n{ex}";
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

    /// <summary>调用打卡数据方法并归一化为快照（含补签/保护字段）。</summary>
    public static CheckinSnapshot GetSnapshot(object service, string vaultId)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.Contains("Checkin", StringComparison.OrdinalIgnoreCase)
                                 && m.ReturnType != typeof(void));
        if (method == null)
            return CheckinSnapshot.Missing($"未找到打卡数据方法（红）");

        object? result;
        try
        {
            var args = BuildArgs(method.GetParameters(), vaultId);
            result = method.Invoke(service, args);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
        }
        catch (Exception ex)
        {
            return CheckinSnapshot.Missing($"调用打卡方法失败: {Unwrap(ex).Message}（红）");
        }
        if (result == null)
            return CheckinSnapshot.Missing($"打卡方法返回 null（红）");

        var missing = new List<string>();
        var streak = ReadInt(result, missing, "FamilyStreak", "Streak");
        var streakStatus = ReadString(result, "StreakStatus", "StreakState", "ProtectionStatus") ?? "";
        if (streakStatus == "" && missing.Contains("StreakStatus") == false)
        {
            // StreakStatus 缺失不单独报红（有 FamilyStreak 即可），保护状态由 HasStreakProtectionField 契约锁
        }
        var makeupRemaining = ReadInt(result, missing, "MakeupRemaining", "MonthlyMakeupRemaining", "MakeupQuotaLeft");

        var cells = ReadList(result, missing, item => new CalendarCell(
                ReadDateTime(item, "Date", "Day"),
                ReadBool(item, "IsChecked", "Checked", "HasActivity"),
                ReadBool(item, "IsToday", "Today"),
                ReadBoolStrict(item, missing, "IsMakeupable", "CanMakeup", "Makeupable")),
            "Last7Days", "Calendar");

        if (missing.Count > 0)
            return CheckinSnapshot.Missing($"打卡结果缺少字段: {string.Join(", ", missing)}（红）");

        return new CheckinSnapshot(true, "", streak, streakStatus, makeupRemaining, cells);
    }

    /// <summary>调用补签方法并归一化为结果。</summary>
    public static MakeupResult InvokeMakeup(object service, string vaultId, DateTime beijingDate)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.Contains("Makeup", StringComparison.OrdinalIgnoreCase)
                                 && m.GetParameters().Any(p =>
                                     p.ParameterType == typeof(DateTime) || p.ParameterType == typeof(DateTime?)));
        if (method == null)
            return new MakeupResult(false, "未找到补签方法（红）", false, "", 0);

        object? result;
        try
        {
            var ps = method.GetParameters();
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                var name = p.Name ?? "";
                if (p.ParameterType == typeof(DateTime) || p.ParameterType == typeof(DateTime?))
                    args[i] = beijingDate;
                else if (name.Contains("vault", StringComparison.OrdinalIgnoreCase))
                    args[i] = vaultId;
                else if (p.ParameterType == typeof(CancellationToken))
                    args[i] = CancellationToken.None;
                else
                    args[i] = p.HasDefaultValue ? p.DefaultValue : null;
            }
            result = method.Invoke(service, args);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
        }
        catch (Exception ex)
        {
            return new MakeupResult(false, $"调用补签方法失败: {Unwrap(ex).Message}（红）", false, "", 0);
        }
        if (result == null)
            return new MakeupResult(false, "补签方法返回 null（红）", false, "", 0);

        var success = ReadBool(result, "Success", "IsSuccess", "Ok");
        var message = ReadString(result, "Message", "Error", "Reason") ?? "";
        var remaining = ReadInt(result, new List<string>(), "Remaining", "RemainingAfter", "MakeupRemaining");
        return new MakeupResult(true, "", success, message, remaining);
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

    private static int ReadInt(object o, List<string> missing, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) { missing.Add(names[0]); return 0; }
        var v = prop.GetValue(o);
        return v == null ? 0 : Convert.ToInt32(v);
    }

    /// <summary>严格读 bool：属性不存在时报告缺失（防止字段缺失被归一化为 false 造成假绿）</summary>
    private static bool ReadBoolStrict(object o, List<string> missing, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) { missing.Add(names[0]); return false; }
        var v = prop.GetValue(o);
        return v switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var b) && b,
            null => false,
            _ => false
        };
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

    private static List<T> ReadList<T>(object o, List<string> missing, Func<object, T> map, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = FindProp(o, name);
            if (prop == null) continue;
            var v = prop.GetValue(o);
            if (v is not System.Collections.IEnumerable items) { missing.Add(name); return new List<T>(); }
            var list = new List<T>();
            foreach (var item in items)
            {
                if (item != null) list.Add(map(item));
            }
            return list;
        }
        missing.Add(names[0]);
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
