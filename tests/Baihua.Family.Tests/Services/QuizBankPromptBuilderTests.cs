using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 题库生成提示词构建器测试：保证 MCP 与 UI 两个入口拿到的模板结构一致且包含全部硬性约束。
/// 模板是「生成质量」的核心资产，参数替换/转义/边界值必须稳定。
/// </summary>
public class QuizBankPromptBuilderTests
{
    private static QuizBankPromptBuilder.Options Options() => new()
    {
        IndustryCode = "tcm",
        IndustryName = "中医",
        BankCode = "jingfang",
        BankName = "经方",
        Topic = "太阳病桂枝汤类方，重点考兼证与禁忌",
        Target = "formula",
        QuestionCount = 10,
        Difficulty = 3,
        ReferenceScope = "伤寒论·太阳篇",
        DistractorStrategy = "same_scope",
        CodePrefix = "ai20261109",
    };

    [Fact]
    public void Build_IncludesAllTaskParameters()
    {
        var prompt = QuizBankPromptBuilder.Build(Options());

        Assert.Contains("中医（代码 tcm）", prompt);
        Assert.Contains("经方（代码 jingfang）", prompt);
        Assert.Contains("太阳病桂枝汤类方", prompt);
        Assert.Contains("题目数量：10", prompt);
        Assert.Contains("难度：3", prompt);
        Assert.Contains("伤寒论·太阳篇", prompt);
        Assert.Contains("ai20261109_item_1", prompt);
        Assert.Contains("ai20261109_q_1", prompt);
    }

    [Fact]
    public void Build_ContainsJsonSchemaAndHardRules()
    {
        var prompt = QuizBankPromptBuilder.Build(Options());

        Assert.Contains("\"formatVersion\": 1", prompt);
        Assert.Contains("\"questions\"", prompt);
        Assert.Contains("`isCorrect` 为 true 的恰好一个", prompt);
        Assert.Contains("不得编造不存在的出处", prompt);
        Assert.Contains("只输出一个 JSON 对象", prompt);
    }

[Fact]
    public void Build_UnknownTargetWithoutDescription_UsesGenericCustomRule()
    {
        var options = Options();
        options.Target = "something-else";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("自定义题型，按该字段字面含义出题", prompt);
    }

    [Fact]

    public void Build_WithoutReferenceScope_AddsNoFabricationInstruction()
    {
        var options = Options();
        options.ReferenceScope = null;

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("参考范围：未指定", prompt);
        Assert.DoesNotContain("伤寒论·太阳篇", prompt);
    }

    [Fact]
    public void Build_EmptyTopic_UsesFallback()
    {
        var options = Options();
        options.Topic = "   ";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("（未指定，按该行业的基础知识生成）", prompt);
    }

    [Fact]
    public void Build_ClampsQuestionCountAndDifficulty()
    {
        var options = Options();
        options.QuestionCount = 999;
        options.Difficulty = 0;

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("题目数量：50", prompt);
        Assert.Contains("难度：1", prompt);
    }

    [Fact]
    public void Build_SanitizesCodePrefix()
    {
        var options = Options();
        options.CodePrefix = "AI 2026/11!";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("ai202611_item_1", prompt);
        Assert.DoesNotContain("AI 2026/11!", prompt);
    }

    [Fact]
    public void Build_BlankCodePrefix_FallsBackToAi()
    {
        var options = Options();
        options.CodePrefix = "   ";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("ai_item_1", prompt);
    }

    [Fact]
    public void Build_RandomDistractor_ChangesDescription()
    {
        var options = Options();
        options.DistractorStrategy = "random";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("随机选取的其他对象", prompt);
    }

    // ============ 全行业适配 ============

    [Fact]
    public void Build_ProgrammingIndustry_UsesProgrammingTermsOnly()
    {
        var options = Options();
        options.IndustryCode = "programming";
        options.IndustryName = "编程";
        options.Target = "output";
        options.Topic = "C# 异步与并发";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("选输出结果", prompt);
        Assert.Contains("options 的 content 必须是输出结果", prompt);
        Assert.DoesNotContain("方剂", prompt);
        Assert.DoesNotContain("证型", prompt);
    }

    [Fact]
    public void Build_DrivingIndustry_UsesDrivingTerms()
    {
        var options = Options();
        options.IndustryCode = "driving";
        options.IndustryName = "驾照";
        options.Target = "rule";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("选法规条款", prompt);
        Assert.DoesNotContain("方剂", prompt);
        Assert.DoesNotContain("病机", prompt);
    }

    [Fact]
    public void Build_UnregisteredIndustry_FallsBackToGeneralProfile()
    {
        var options = Options();
        options.IndustryCode = "cooking";
        options.IndustryName = "烹饪";
        options.Target = "";

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("烹饪（代码 cooking）", prompt);
        Assert.Contains("选正确答案", prompt);
        Assert.DoesNotContain("方剂", prompt);
        Assert.DoesNotContain("证型", prompt);
    }

    [Fact]
    public void Build_ExplicitProfile_OverridesIndustryCode()
    {
        var options = Options();
        options.IndustryCode = "whatever";
        options.Target = "";
        options.Profile = QuizIndustryProfileCatalog.Resolve("programming", "编程");

        var prompt = QuizBankPromptBuilder.Build(options);

        Assert.Contains("选概念", prompt);
        Assert.Contains("\"itemType\": \"concept\"", prompt);
        Assert.DoesNotContain("composition", prompt);
    }

    [Fact]
    public void Build_TcmProfile_KeepsCompositionInSchema()
    {
        var prompt = QuizBankPromptBuilder.Build(Options());

        Assert.Contains("\"composition\"", prompt);
        Assert.Contains("\"decoction\"", prompt);
        Assert.Contains("\"itemType\": \"formula\"", prompt);
    }
}