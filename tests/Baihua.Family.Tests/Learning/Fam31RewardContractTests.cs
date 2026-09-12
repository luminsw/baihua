using System.Reflection;
using System.Text.RegularExpressions;
using Baihua.Data;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-31 静态契约红测试：成就贴纸墙 + 家庭奖励。
///
/// 验收标准覆盖（本轮：契约层）：
///   - AC1  成就展示：贴纸墙页面（大图标+成就名+解锁日期；未解锁灰显+锁定+条件提示）
///   - AC2  成员筛选（复用 FAM-20 选择器）
///   - AC3  家庭奖励配置：Reward 服务/持久化 + 进度条（当前值/目标值）
///   - AC4  奖励达成：庆祝动画 + 达成不重复触发（每条件一次）
///
/// 红测试方式：当前无 Reward 服务/成就墙页面 → 红。
/// </summary>
public class Fam31RewardContractTests
{
    // ============ 后端契约（反射探测） ============

    [Fact]
    public void RewardService_MustExist()
    {
        // AC3：家庭奖励服务存在（名称含 Reward）
        var rewardType = typeof(LeaderboardService).Assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && t.Name.Contains("Reward", StringComparison.OrdinalIgnoreCase)
                                 && t.Name.EndsWith("Service", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rewardType);
    }

    [Fact]
    public void RewardData_MustExposeProgress()
    {
        // AC3：孩子视角进度条（当前值/目标值，如"连续打卡 5/7 天"）
        var rewardType = typeof(LeaderboardService).Assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && t.Name.Contains("Reward", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rewardType);

        var hasProgress = rewardType!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase)
                      || m.Name.Contains("Current", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasProgress,
            "FAM-31-AC3 契约：Reward 服务缺少进度查询方法（Progress/Current）（红）");
    }

    [Fact]
    public void RewardAchievement_MustNotRepeat()
    {
        // AC4：达成记录每条件仅触发一次（去重契约）
        var rewardType = typeof(LeaderboardService).Assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && t.Name.Contains("Reward", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(rewardType);

        var hasClaimOnce = rewardType!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Claim", StringComparison.OrdinalIgnoreCase)
                      || m.Name.Contains("CheckAnd", StringComparison.OrdinalIgnoreCase)
                      || m.Name.Contains("Trigger", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasClaimOnce,
            "FAM-31-AC4 契约：Reward 服务缺少达成触发方法（Claim/Trigger，需含去重语义）（红）");
    }

    // ============ 前端源码级契约 ============

    private static readonly string WebRoot = Path.GetDirectoryName(
        Path.GetDirectoryName(RepoPath.FindUp(Path.Combine("services", "Baihua.Web", "Pages", "FamilyLanding.razor"))))!;

    private static string ReadSource(string relative)
    {
        var path = Path.Combine(WebRoot, relative);
        Assert.True(File.Exists(path), $"FAM-31 契约：{relative} 不存在（红）");
        return File.ReadAllText(path);
    }

    private static string ReadAchievementsSource()
    {
        // 成就墙页面（新页面或改造现有 Achievements.razor）
        var sb = new System.Text.StringBuilder();
        foreach (var p in new[] { "Pages/Achievements.razor", "Pages/FamilyAchievements.razor", "Components/AchievementWall.razor" })
        {
            var path = Path.Combine(WebRoot, p);
            if (File.Exists(path)) sb.AppendLine(File.ReadAllText(path));
        }
        return sb.ToString();
    }

    [Fact]
    public void AchievementWall_ShowsUnlockedAndLocked()
    {
        // AC1：已解锁彩色大图标 + 未解锁灰显/锁定 + 条件提示
        var source = ReadAchievementsSource();
        Assert.True(
            source.Contains("解锁", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Unlocked", StringComparison.OrdinalIgnoreCase),
            "FAM-31-AC1：成就墙缺少解锁状态展示（红）");
        Assert.True(
            source.Contains("lock", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("锁定", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("灰", StringComparison.OrdinalIgnoreCase),
            "FAM-31-AC1：成就墙缺少未解锁灰显/锁定状态（红）");
    }

    [Fact]
    public void AchievementWall_HasMemberFilter()
    {
        // AC2：成员筛选（复用 FAM-20 选择器）
        var source = ReadAchievementsSource();
        Assert.True(
            source.Contains("learner", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("成员", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("selector", StringComparison.OrdinalIgnoreCase),
            "FAM-31-AC2：成就墙缺少成员筛选（红）");
    }

    [Fact]
    public void RewardProgress_HasProgressBar()
    {
        // AC3：孩子视角进度条（如"连续打卡 5/7 天，还差 2 天就能获得🍦冰淇淋！"）
        var source = ReadSource("Pages/Checkin.razor") + ReadAchievementsSource();
        Assert.True(
            source.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("进度", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("还差", StringComparison.OrdinalIgnoreCase),
            "FAM-31-AC3：缺少奖励进度条（红）");
    }

    [Fact]
    public void RewardCelebration_Exists()
    {
        // AC4：达成时庆祝动画/提示
        var source = ReadSource("Pages/Checkin.razor") + ReadAchievementsSource();
        Assert.True(
            source.Contains("庆祝", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("达成", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("celebrate", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("🎉", StringComparison.OrdinalIgnoreCase),
            "FAM-31-AC4：缺少奖励达成庆祝提示（红）");
    }
}
