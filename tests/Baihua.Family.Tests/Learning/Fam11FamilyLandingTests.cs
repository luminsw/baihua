using System.Text.RegularExpressions;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-11 红测试：FamilyLanding.razor（家庭聚合首页）。
///
/// 验收标准：
///   - /family 路由可达（Blazor 声明式路由 = 页面文件存在 + @page "/family"）
///   - 页面包含四区域：汇总卡片 / 成员快照 / 每日卡片捷径 / 本周排行预览
///   - 无 Learner 时显示引导提示（不空白）
///   - 菜单注册表：Scene=1 第一个菜单项 href="family"（替代原 tasks 位置）
///
/// 红测试方式：Blazor 路由不存在时 HTTP 仍返回 200（组件级 NotFound），
/// 路由可达性无法用 HTTP 状态码断言 → 采用源码级契约（pm 已允许）。
/// 当前 FamilyLanding.razor 不存在、Scene 1 第一项为 daily-card → 红。
/// </summary>
public class Fam11FamilyLandingTests
{
    private static readonly string LandingPagePath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Pages", "FamilyLanding.razor"));

    private static readonly string NavMenuPath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Shared", "FamilyNavMenu.razor"));

    private static string ReadLandingSource()
    {
        // 契约红：页面文件必须存在
        Assert.True(File.Exists(LandingPagePath),
            "FAM-11 契约：Pages/FamilyLanding.razor 不存在（红）——需要新建家庭聚合首页");
        return File.ReadAllText(LandingPagePath);
    }

    // ============ 路由可达 ============

    [Fact]
    public void FamilyLanding_Exists_WithFamilyRoute()
    {
        var source = ReadLandingSource();
        // 验收：/family 路由可达 —— Blazor 声明式路由由 @page 指令声明
        Assert.Contains("@page \"/family\"", source);
    }

    // ============ 页面结构契约（四区域） ============

    [Fact]
    public void FamilyLanding_HasFamilySummaryCard()
    {
        var source = ReadLandingSource();
        // 区域 1：家庭学习汇总卡片（今日全家学了 X 张卡、连续打卡 Y 天）
        Assert.True(
            source.Contains("summary", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("汇总", StringComparison.OrdinalIgnoreCase),
            "FAM-11：缺少家庭学习汇总卡片区域（summary/汇总）（红）");
    }

    [Fact]
    public void FamilyLanding_HasMemberSnapshotSection()
    {
        var source = ReadLandingSource();
        // 区域 2：成员学习快照（头像 + 今日进度 + 最新成就）
        Assert.True(
            source.Contains("snapshot", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("member", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("成员", StringComparison.OrdinalIgnoreCase),
            "FAM-11：缺少成员学习快照区域（snapshot/member）（红）");
    }

    [Fact]
    public void FamilyLanding_HasDailyCardShortcut()
    {
        var source = ReadLandingSource();
        // 区域 3：每日卡片捷径（一键跳转今日每日卡片）
        Assert.True(
            source.Contains("daily-card", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("DailyCard", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("每日卡片", StringComparison.OrdinalIgnoreCase),
            "FAM-11：缺少每日卡片捷径（daily-card 链接）（红）");
    }

    [Fact]
    public void FamilyLanding_HasWeeklyLeaderboardPreview()
    {
        var source = ReadLandingSource();
        // 区域 4：本周排行预览（和自己比版本）
        Assert.True(
            source.Contains("leaderboard", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("排行", StringComparison.OrdinalIgnoreCase),
            "FAM-11：缺少本周排行预览区域（leaderboard）（红）");
    }

    // ============ 无 Learner 引导 ============

    [Fact]
    public void FamilyLanding_ShowsGuide_WhenNoLearners()
    {
        var source = ReadLandingSource();
        // 无 Learner 时显示引导提示（不空白）：必须有学习者判断 + 空态引导
        var hasLearnerCheck =
            Regex.IsMatch(source, @"learners\.(Count|Length)\s*==\s*0", RegexOptions.IgnoreCase) ||
            source.Contains("!learners.Any", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("learners.length === 0", StringComparison.OrdinalIgnoreCase);
        var hasGuide =
            source.Contains("guide", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("引导", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("NoLearner", StringComparison.OrdinalIgnoreCase);

        Assert.True(hasLearnerCheck && hasGuide,
            "FAM-11：无 Learner 时缺少引导提示（需学习者空判断 + 引导/空态 UI）（红）");
    }

    // ============ 菜单注册表：Scene 1 第一个菜单项 = "family" ============

    [Fact]
    public void FamilyNavMenu_Scene1_FirstItem_IsFamilyHome()
    {
        Assert.True(File.Exists(NavMenuPath), $"找不到 FamilyNavMenu.razor（路径: {NavMenuPath}）");
        var lines = File.ReadAllLines(NavMenuPath);

        // 提取 Scene 参数为 1 的菜单注册行（第 4 个参数），取第一个
        var scene1Item = lines
            .Select(l => Regex.Match(l.Trim(), @"new\(""([^""]+)""[^)]*,\s*1\s*[,)]"))
            .FirstOrDefault(m => m.Success);

        Assert.True(scene1Item != null, "FAM-11：Family 场景（Scene=1）没有任何菜单项（红）");

        // 验收：Family 场景第一个菜单 href 必须是 "family"（替代原 tasks 位置）
        Assert.Equal("family", scene1Item!.Groups[1].Value);
    }
}
