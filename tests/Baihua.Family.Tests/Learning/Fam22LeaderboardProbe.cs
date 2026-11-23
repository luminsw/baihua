using System.Reflection;
using Baihua.Core.Localization;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-22 契约探测：以反射方式读取"排行榜友好化"契约（FAM-20/21 Probe 同套路）。
/// 契约缺失时返回 ContractPresent=false + MissingDetail，测试据此红并给出明确指引。
/// </summary>
public static class Fam22LeaderboardProbe
{
    public sealed record CompareSnapshot(
        bool ContractPresent,
        string MissingDetail,
        int WeekTotal,
        int LastWeekTotal,
        int Delta,
        double Percent,
        string Arrow)
    {
        public static CompareSnapshot Missing(string detail) =>
            new(false, detail, 0, 0, 0, 0, "");
    }

    /// <summary>排行榜服务：LeaderboardService（或名称含 Leaderboard 的 Service）</summary>
    public static Type FindLeaderboardServiceType()
    {
        var asm = typeof(LeaderboardService).Assembly;
        return asm.GetType("Baihua.Modules.Family.Services.LeaderboardService")
            ?? asm.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                && t.Name.Contains("Leaderboard", StringComparison.OrdinalIgnoreCase)
                && t.Name.EndsWith("Service", StringComparison.OrdinalIgnoreCase))
            ?? typeof(LeaderboardService);
    }

    /// <summary>
    /// AC1/AC2 契约：存在"和自己比"（本周 vs 上周）方法——名称含 Compare/SelfCompare/WeekVsWeek，
    /// 且参数含 learner（按成员维度和自己比）。
    /// </summary>
    public static bool HasWeeklyCompareMethod()
    {
        return FindLeaderboardServiceType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => (m.Name.Contains("Compare", StringComparison.OrdinalIgnoreCase)
                       || m.Name.Contains("SelfCompare", StringComparison.OrdinalIgnoreCase)
                       || m.Name.Contains("WeekVs", StringComparison.OrdinalIgnoreCase))
                      && m.GetParameters().Any(p =>
                          p.Name != null && p.Name.Contains("learner", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// AC3 契约：存在按角色分组/过滤的排行榜机制——
    /// 方法参数含 role/adult/child/group，或返回对象含 Kids/Adults 分组结构。
    /// </summary>
    public static bool HasRoleGroupedLeaderboard()
    {
        var type = FindLeaderboardServiceType();
        var methodBased = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Leaderboard", StringComparison.OrdinalIgnoreCase)
                      && m.GetParameters().Any(p => p.Name != null && (
                          p.Name.Contains("role", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Contains("adult", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Contains("child", StringComparison.OrdinalIgnoreCase)
                          || p.Name.Contains("group", StringComparison.OrdinalIgnoreCase))));
        if (methodBased) return true;

        // 返回对象含 Kids/Adults 分组属性（如 LeaderboardBoardResult { Kids, Adults }）
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => (p.Name.Contains("Kids", StringComparison.OrdinalIgnoreCase)
                       || p.Name.Contains("Adults", StringComparison.OrdinalIgnoreCase))
                      && p.PropertyType.IsGenericType);
    }

    /// <summary>
    /// AC3 契约：角色判定机制必须存在——LearnerProfile 有 Role/IsAdult/年龄字段，
    /// 或排行榜支持角色过滤/分组（TECH-08 未完成时需兜底方案）。
    /// </summary>
    public static bool HasLearnerRoleMechanism()
    {
        var learnerProps = typeof(LearnerProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);
        var hasRoleField = learnerProps.Any(n =>
            n.Contains("Role", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Adult", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Child", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Age", StringComparison.OrdinalIgnoreCase));
        return hasRoleField || HasRoleGroupedLeaderboard();
    }

    /// <summary>
    /// AC4/AC5 契约：全家排行开关可持久化——存在设置类型（名称含 Setting/Preference/UserSettings），
    /// 且有全家排行相关读方法（AllFamily/FamilyTab/ShowAllFamily）。
    /// </summary>
    public static Type? FindAllFamilyTabSettingType()
    {
        var asm = typeof(LeaderboardService).Assembly;
        return asm.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && (t.Name.Contains("Setting", StringComparison.OrdinalIgnoreCase)
                                     || t.Name.Contains("Preference", StringComparison.OrdinalIgnoreCase)
                                     || t.Name.Contains("UserSettings", StringComparison.OrdinalIgnoreCase))
                                 && t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                     .Any(m => m.Name.Contains("AllFamily", StringComparison.OrdinalIgnoreCase)
                                               || m.Name.Contains("FamilyTab", StringComparison.OrdinalIgnoreCase)
                                               || m.Name.Contains("ShowAllFamily", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 调用"和自己比"方法并归一化为快照。
    /// </summary>
    public static CompareSnapshot GetCompareSnapshot(object service, string vaultId, int learnerId)
    {
        var type = service.GetType();
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => (m.Name.Contains("Compare", StringComparison.OrdinalIgnoreCase)
                                  || m.Name.Contains("SelfCompare", StringComparison.OrdinalIgnoreCase)
                                  || m.Name.Contains("WeekVs", StringComparison.OrdinalIgnoreCase))
                                 && m.GetParameters().Any(p =>
                                     p.Name != null && p.Name.Contains("learner", StringComparison.OrdinalIgnoreCase)));
        if (method == null)
            return CompareSnapshot.Missing(
                $"未找到'和自己比'方法（期望 {type.Name}.GetWeeklyCompareAsync(vaultId, learnerId) 或等价）（红）");

        object? result;
        try
        {
            var args = BuildArgs(method.GetParameters(), vaultId, learnerId);
            result = method.Invoke(service, args);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
        }
        catch (Exception ex)
        {
            return CompareSnapshot.Missing($"调用'和自己比'方法失败: {Unwrap(ex).Message}（红）");
        }
        if (result == null)
            return CompareSnapshot.Missing($"'和自己比'方法返回 null（红）");

        return ReadCompareSnapshot(result);
    }

    /// <summary>读取全家排行开关默认值（新用户应默认关闭）。契约缺失返回 null。</summary>
    public static bool? GetAllFamilyTabDefault(
        IDbContextFactory<FamilyDbContext> familyFactory,
        IDbContextFactory<VaultDbContext> vaultFactory,
        FakeTimeProvider clock)
    {
        var settingType = FindAllFamilyTabSettingType();
        if (settingType == null) return null;

        object? instance;
        try
        {
            var ctor = settingType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor == null) return null;
            var ps = ctor.GetParameters();
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                args[i] = ResolveDependency(ps[i].ParameterType, familyFactory, vaultFactory);
            instance = ctor.Invoke(args);
        }
        catch
        {
            return null;
        }

        var method = settingType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.Contains("AllFamily", StringComparison.OrdinalIgnoreCase)
                                 || m.Name.Contains("FamilyTab", StringComparison.OrdinalIgnoreCase)
                                 || m.Name.Contains("ShowAllFamily", StringComparison.OrdinalIgnoreCase));
        if (method == null || method.ReturnType == typeof(void)) return null;

        try
        {
            var val = method.Invoke(instance, method.GetParameters().Length == 0
                ? Array.Empty<object?>()
                : BuildSimpleArgs(method.GetParameters(), vaultFactory));
            if (val is Task task)
            {
                task.GetAwaiter().GetResult();
                val = task.GetType().GetProperty("Result")?.GetValue(task);
            }
            return val switch
            {
                bool b => b,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static CompareSnapshot ReadCompareSnapshot(object result)
    {
        var missing = new List<string>();

        var weekTotal = ReadInt(result, missing, "WeekTotal", "ThisWeekTotal", "ThisWeek");
        var lastWeekTotal = ReadInt(result, missing, "LastWeekTotal", "LastWeek", "PreviousWeekTotal");
        var delta = ReadInt(result, missing, "Delta", "Change", "Diff");
        var percent = ReadDouble(result, missing, "Percent", "ChangePercent", "Pct");

        var arrowProp = FindProp(result, "Arrow", "Trend");
        string arrow = "";
        if (arrowProp == null) missing.Add("Arrow");
        else
        {
            var v = arrowProp.GetValue(result);
            arrow = v == null ? "" : v is string s ? s.ToLowerInvariant() : v.ToString()!.ToLowerInvariant();
        }

        if (missing.Count > 0)
            return CompareSnapshot.Missing($"'和自己比'结果缺少字段: {string.Join(", ", missing)}（红）");

        return new CompareSnapshot(true, "", weekTotal, lastWeekTotal, delta, percent, arrow);
    }

    private static object?[] BuildArgs(ParameterInfo[] ps, string vaultId, int learnerId)
    {
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var name = p.Name ?? "";
            if (name.Contains("learner", StringComparison.OrdinalIgnoreCase))
                args[i] = Convert.ChangeType(learnerId, Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType);
            else if (name.Contains("vault", StringComparison.OrdinalIgnoreCase))
                args[i] = vaultId;
            else if (p.ParameterType == typeof(CancellationToken))
                args[i] = CancellationToken.None;
            else
                args[i] = p.HasDefaultValue ? p.DefaultValue : null;
        }
        return args;
    }

    private static object?[] BuildSimpleArgs(ParameterInfo[] ps, IDbContextFactory<VaultDbContext> vaultFactory)
    {
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            args[i] = ResolveDependency(ps[i].ParameterType, null, vaultFactory);
        return args;
    }

    private static object? ResolveDependency(
        Type pt,
        IDbContextFactory<FamilyDbContext>? familyFactory,
        IDbContextFactory<VaultDbContext>? vaultFactory)
    {
        if (pt == typeof(IDbContextFactory<FamilyDbContext>)) return familyFactory;
        if (pt == typeof(IDbContextFactory<VaultDbContext>)) return vaultFactory;
        if (pt == typeof(IStringLocalizer<SharedResources>)) return TestLocalizer.Create();
        if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            var loggerType = typeof(NullLogger<>).MakeGenericType(pt.GetGenericArguments()[0]);
            return loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        }
        return null;
    }

    private static int ReadInt(object o, List<string> missing, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) { missing.Add(names[0]); return 0; }
        var v = prop.GetValue(o);
        return v == null ? 0 : Convert.ToInt32(v);
    }

    private static double ReadDouble(object o, List<string> missing, params string[] names)
    {
        var prop = FindProp(o, names);
        if (prop == null) { missing.Add(names[0]); return 0; }
        var v = prop.GetValue(o);
        return v == null ? 0 : Convert.ToDouble(v);
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
