using System.Text.RegularExpressions;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-34 静态契约红测试：新用户 Onboarding 向导。
///
/// 验收标准覆盖（本轮：源码级契约层，WebUI）：
///   - AC1  首次进入（无 Learner）触发 Onboarding Step 1（欢迎 + 名字输入）
///   - AC2  四步流程（创建学习者→完成第一次学习→看到成果→Done）
///   - AC3  跳过不重复弹出（持久化标记 OnboardingCompleted）
///   - AC4  已有数据不触发（无 Learner 且无学习记录才触发）
///
/// 红测试方式（FAM-11 先例，源码级）：当前无 Onboarding 组件/逻辑 → 红。
/// </summary>
public class Fam34OnboardingContractTests
{
    private static readonly string FamilyLandingPath = RepoPath.FindUp(Path.Combine(
        "services", "Baihua.Web", "Pages", "FamilyLanding.razor"));

    private static readonly string ComponentsRoot = Path.GetDirectoryName(
        Path.GetDirectoryName(RepoPath.FindUp(Path.Combine("services", "Baihua.Web", "Pages", "FamilyLanding.razor"))))!;

    /// <summary>读取家庭聚合页源码 + 可选的 Onboarding 组件（拆分或内联均覆盖）</summary>
    private static string ReadFamilySource()
    {
        Assert.True(File.Exists(FamilyLandingPath),
            "FAM-34 契约：Pages/FamilyLanding.razor 不存在（红）");
        var sb = new System.Text.StringBuilder(File.ReadAllText(FamilyLandingPath));

        var onboardingComponent = Directory.EnumerateFiles(
            ComponentsRoot, "Onboarding*.razor", SearchOption.AllDirectories).FirstOrDefault();
        if (onboardingComponent != null)
        {
            sb.AppendLine();
            sb.AppendLine(File.ReadAllText(onboardingComponent));
        }
        return sb.ToString();
    }

    // ============ AC1：无 Learner 触发 Onboarding ============

    [Fact]
    public void Onboarding_MustExist_WithStep1()
    {
        // Onboarding 组件/逻辑存在，Step 1 有欢迎文案 + 名字输入框
        var source = ReadFamilySource();
        Assert.True(
            source.Contains("Onboarding", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("onboarding", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("向导", StringComparison.OrdinalIgnoreCase),
            "FAM-34-AC1：缺少 Onboarding 向导（红）");
        Assert.True(
            source.Contains("创建", StringComparison.OrdinalIgnoreCase) &&
            (source.Contains("学习者", StringComparison.OrdinalIgnoreCase) || source.Contains("Learner", StringComparison.OrdinalIgnoreCase)),
            "FAM-34-AC1：Step 1 缺少'创建学习者'引导（红）");
    }

    [Fact]
    public void Onboarding_Step1_HasNameInput()
    {
        // Step 1：名字输入框 + 开始按钮（复用 FAM-12 Learner 校验）——必须在 Onboarding 上下文内
        var source = ReadFamilySource();
        var hasOnboarding = source.Contains("Onboarding", StringComparison.OrdinalIgnoreCase);
        var hasInput = source.Contains("input", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("TextBox", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("名字", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasOnboarding && hasInput,
            "FAM-34-AC1：Onboarding Step 1 缺少名字输入框（红）");
    }

    // ============ AC2：四步流程 ============

    [Fact]
    public void Onboarding_HasFourSteps()
    {
        // 四步：创建学习者 → 完成第一次学习 → 看到成果 → Done（step 标记在 Onboarding 上下文内）
        var source = ReadFamilySource();
        var hasOnboarding = source.Contains("Onboarding", StringComparison.OrdinalIgnoreCase);
        var stepMarkers =
            (source.Contains("Step 1", StringComparison.OrdinalIgnoreCase) || source.Contains("Step1", StringComparison.OrdinalIgnoreCase) || source.Contains("step1", StringComparison.OrdinalIgnoreCase)) &&
            (source.Contains("Step 2", StringComparison.OrdinalIgnoreCase) || source.Contains("Step2", StringComparison.OrdinalIgnoreCase) || source.Contains("step2", StringComparison.OrdinalIgnoreCase)) &&
            (source.Contains("Step 3", StringComparison.OrdinalIgnoreCase) || source.Contains("Step3", StringComparison.OrdinalIgnoreCase) || source.Contains("step3", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasOnboarding && stepMarkers,
            "FAM-34-AC2：Onboarding 缺少四步结构（Step 1/2/3）（红）");
        Assert.True(
            source.Contains("FamilyLanding_FirstStudyTitle", StringComparison.OrdinalIgnoreCase),
            "FAM-34-AC2：缺少 Step 2 引导 key（FamilyLanding_FirstStudyTitle）（红）");
    }

    // ============ AC3：跳过持久化 ============

    [Fact]
    public void Onboarding_Skip_MustPersist()
    {
        // 跳过后不再次弹出：持久化标记 OnboardingCompleted
        var source = ReadFamilySource();
        Assert.True(
            source.Contains("OnboardingCompleted", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("OnboardingComplete", StringComparison.OrdinalIgnoreCase),
            "FAM-34-AC3：缺少 OnboardingCompleted 持久化标记（红）");
        Assert.True(
            source.Contains("跳过", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Skip", StringComparison.OrdinalIgnoreCase),
            "FAM-34-AC3：缺少'跳过'入口（红）");
    }

    // ============ AC4：已有数据不触发 ============

    [Fact]
    public void Onboarding_MustNotTrigger_WhenDataExists()
    {
        // 触发条件必须包含 Onboarding 判断（未完成标记 && 无 Learner），已有数据不弹
        var source = ReadFamilySource();
        var hasOnboardingGuard =
            source.Contains("OnboardingCompleted", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("ShowOnboarding", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("!Onboarding", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(source, @"Onboarding\w*\s*(==|!=)\s*(false|true)", RegexOptions.IgnoreCase);
        var hasLearnerCheck =
            Regex.IsMatch(source, @"learners\.(Count|Length)\s*==\s*0", RegexOptions.IgnoreCase) ||
            source.Contains("!learners.Any", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("NoLearner", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasOnboardingGuard && hasLearnerCheck,
            "FAM-34-AC4：Onboarding 触发缺少'未完成标记 && 无 Learner'条件（红）");
    }
}
