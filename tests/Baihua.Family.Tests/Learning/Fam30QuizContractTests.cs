using System.Reflection;
using System.Text.RegularExpressions;
using Baihua.Data;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Learning;

/// <summary>
/// FAM-30 静态契约红测试：亲子共学（互考模式）。
///
/// 验收标准覆盖（本轮：契约层）：
///   - AC1  创建对战：/family/quiz 页面 + 双 Learner 选择 + 回合制
///   - AC2  答题判定：对错判定 + 分数实时显示
///   - AC3  结算：胜者 + 得分 + 正确率 + "再来一局"
///   - AC4  计入打卡：互考记录写入 StudyActivity（ActivityType=quiz/互考）
///
/// 红测试方式：当前无 Quiz 服务/页面 → 红。
/// </summary>
public class Fam30QuizContractTests
{
    // ============ 后端契约（反射探测） ============

    [Fact]
    public void QuizService_MustExist()
    {
        // AC1/AC2：互考服务存在（名称含 Quiz）
        var quizType = typeof(LeaderboardService).Assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && t.Name.Contains("Quiz", StringComparison.OrdinalIgnoreCase)
                                 && t.Name.EndsWith("Service", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(quizType);
    }

    [Fact]
    public void QuizResult_MustRecordScore()
    {
        // AC2/AC3：对战结果含双方得分/正确率
        var quizType = typeof(LeaderboardService).Assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && t.Name.Contains("Quiz", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(quizType);
        var hasScore = quizType!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Score", StringComparison.OrdinalIgnoreCase)
                      || m.Name.Contains("Result", StringComparison.OrdinalIgnoreCase)
                      || m.Name.Contains("Finish", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasScore,
            "FAM-30-AC2/AC3 契约：Quiz 服务缺少计分/结算方法（Score/Result/Finish）（红）");
    }

    [Fact]
    public void QuizResult_MustPersistToStudyActivity()
    {
        // AC4：互考记录写入 StudyActivity（ActivityType=quiz，计入打卡）
        var quizType = typeof(LeaderboardService).Assembly.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.IsPublic
                                 && t.Name.Contains("Quiz", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(quizType);

        // 反射检查 Quiz 服务是否引用 StudyActivity 写入（互考类型）
        var writesActivity = quizType!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Record", StringComparison.OrdinalIgnoreCase)
                      || m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase)
                      || m.Name.Contains("Checkin", StringComparison.OrdinalIgnoreCase));
        Assert.True(writesActivity,
            "FAM-30-AC4 契约：Quiz 服务缺少记录落库方法（Record/Save，需写 StudyActivity 类型=quiz）（红）");
    }

    // ============ 前端源码级契约 ============

    private static readonly string WebRoot = Path.GetDirectoryName(
        Path.GetDirectoryName(RepoPath.FindUp(Path.Combine("services", "Baihua.Web", "Pages", "FamilyLanding.razor"))))!;

    private static string ReadQuizSource()
    {
        var path = Path.Combine(WebRoot, "Pages", "Quiz.razor");
        Assert.True(File.Exists(path),
            "FAM-30 契约：Pages/Quiz.razor 不存在（红）——需要新建互考页面");
        return File.ReadAllText(path);
    }

    [Fact]
    public void QuizPage_Exists_WithRoute()
    {
        // AC1：/family/quiz 路由
        var source = ReadQuizSource();
        Assert.True(
            source.Contains("@page \"/family/quiz\"", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("@page \"/quiz\"", StringComparison.OrdinalIgnoreCase),
            "FAM-30-AC1：互考页缺少路由（@page \"/family/quiz\"）（红）");
    }

    [Fact]
    public void QuizPage_HasScoreAndSettlement()
    {
        // AC2/AC3：分数实时显示 + 结算面板（胜者/得分/正确率/再来一局）
        var source = ReadQuizSource();
        Assert.True(
            source.Contains("score", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("分数", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("得分", StringComparison.OrdinalIgnoreCase),
            "FAM-30-AC2：互考页缺少分数显示（红）");
        Assert.True(
            source.Contains("再来一局", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("结算", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("胜者", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("settle", StringComparison.OrdinalIgnoreCase),
            "FAM-30-AC3：互考页缺少结算面板（胜者/再来一局）（红）");
    }
}
