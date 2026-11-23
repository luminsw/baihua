using Microsoft.Extensions.AI;
using Baihua.Contracts.Ai;
using Baihua.Contracts.Master;
using Baihua.Modules.Family.Services;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// MasterPromptBuilder 单元测试：师父名称解析、阶段系统、安全过滤、大纲匹配
/// </summary>
public class MasterPromptBuilderTests
{
    private static readonly IStringLocalizer<SharedResources> _loc = TestLocalizer.Instance;
    private readonly MasterPromptBuilder _builder = new(_loc);

    #region ResolveMasterName

    [Theory]
    [InlineData("中医", "岐伯")]
    [InlineData("医学", "岐伯")]
    [InlineData("计算机", "图灵")]
    [InlineData("IT", "图灵")]
    [InlineData("会计", "算圣")]
    [InlineData("财务", "算圣")]
    [InlineData("教资", "夫子")]
    [InlineData("教育", "夫子")]
    [InlineData("法律", "廷尉")]
    [InlineData("建筑", "鲁班")]
    public void ResolveMasterName_KnownIndustry_ReturnsCorrectName(string industry, string expectedName)
    {
        var result = _builder.ResolveMasterName(industry);
        Assert.Equal(expectedName, result);
    }

    [Fact]
    public void ResolveMasterName_UnknownIndustry_ReturnsDefault()
    {
        var result = _builder.ResolveMasterName("未知行业ABC");
        Assert.Equal("先生", result);
    }

    [Fact]
    public void ResolveMasterName_EmptyIndustry_ReturnsDefault()
    {
        var result = _builder.ResolveMasterName("");
        Assert.Equal("先生", result);
    }

    [Fact]
    public void ResolveMasterName_CaseInsensitive()
    {
        var result = _builder.ResolveMasterName("中医理论");
        Assert.Equal("岐伯", result); // 包含"中医"关键词即匹配
    }

    #endregion

    #region GetDefaultStages

    [Fact]
    public void GetDefaultStages_ReturnsFiveStages()
    {
        var stages = _builder.GetDefaultStages();
        Assert.Equal(5, stages.Count);
    }

    [Fact]
    public void GetDefaultStages_HasCorrectOrder()
    {
        var stages = _builder.GetDefaultStages();
        Assert.Equal("入道", stages[0].Name);
        Assert.Equal("筑基", stages[1].Name);
        Assert.Equal("精进", stages[2].Name);
        Assert.Equal("磨砺", stages[3].Name);
        Assert.Equal("出师", stages[4].Name);

        for (int i = 0; i < stages.Count; i++)
            Assert.Equal(i + 1, stages[i].Order);
    }

    #endregion

    #region GetStagesForOutline

    [Fact]
    public void GetStagesForOutline_NullOutline_ReturnsDefaultStages()
    {
        var result = _builder.GetStagesForOutline(null);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void GetStagesForOutline_OutlineWithoutStages_ReturnsDefaultStages()
    {
        var outline = new ExamOutline { Id = "test", Name = "测试", Stages = null };
        var result = _builder.GetStagesForOutline(outline);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void GetStagesForOutline_OutlineWithEmptyStages_ReturnsDefaultStages()
    {
        var outline = new ExamOutline { Id = "test", Name = "测试", Stages = new List<ExamStageOutline>() };
        var result = _builder.GetStagesForOutline(outline);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void GetStagesForOutline_WithCustomStages_ReturnsOutlineStages()
    {
        var outline = new ExamOutline
        {
            Id = "custom",
            Name = "自定义考试",
            Stages = new List<ExamStageOutline>
            {
                new() { Name = "基础", Description = "基础阶段" },
                new() { Name = "进阶", Description = "进阶阶段" },
                new() { Name = "冲刺", Description = "冲刺阶段" },
            }
        };
        var result = _builder.GetStagesForOutline(outline);
        Assert.Equal(3, result.Count);
        Assert.Equal("基础", result[0].Name);
        Assert.Equal("进阶", result[1].Name);
        Assert.Equal("冲刺", result[2].Name);
        Assert.Equal(1, result[0].Order);
        Assert.Equal(3, result[2].Order);
    }

    #endregion

    #region GetOutlineContext

    [Fact]
    public void GetOutlineContext_NullOutline_ReturnsNull()
    {
        var result = _builder.GetOutlineContext(null, "入道");
        Assert.Null(result);
    }

    [Fact]
    public void GetOutlineContext_ValidOutline_ContainsStageDetails()
    {
        var outline = new ExamOutline
        {
            Id = "test",
            Name = "测试考试",
            Stages = new List<ExamStageOutline>
            {
                new()
                {
                    Name = "入道",
                    KeyPoints = new List<string> { "基础概念", "学习方法" },
                    TransitionCriteria = "掌握基础知识",
                    Milestones = new List<string> { "完成入门测试" }
                }
            }
        };
        var result = _builder.GetOutlineContext(outline, "入道");
        Assert.NotNull(result);
        Assert.Contains("测试考试", result);
        Assert.Contains("基础概念", result);
        Assert.Contains("学习方法", result);
        Assert.Contains("掌握基础知识", result);
        Assert.Contains("完成入门测试", result);
    }

    [Fact]
    public void GetOutlineContext_StageNotFound_ReturnsNull()
    {
        var outline = new ExamOutline
        {
            Id = "test",
            Name = "测试",
            Stages = new List<ExamStageOutline>
            {
                new() { Name = "入道" }
            }
        };
        var result = _builder.GetOutlineContext(outline, "精进");
        Assert.Null(result);
    }

    #endregion

    #region ContainsBlockedContent

    [Theory]
    [InlineData("帮我真实诊断一下病情")]
    [InlineData("给我开处方")]
    [InlineData("给我开药方")]
    [InlineData("给我真实法律建议")]
    [InlineData("帮我代理诉讼")]
    [InlineData("医疗诊断这个病")]
    [InlineData("推荐一个处方药")]
    [InlineData("我需要手术方案")]
    [InlineData("帮我做法律代理")]
    public void ContainsBlockedContent_BlockedKeywords_ReturnsTrue(string input)
    {
        Assert.True(_builder.ContainsBlockedContent(input));
    }

    [Theory]
    [InlineData("如何学习中医基础理论？")]
    [InlineData("考试的重点是什么？")]
    [InlineData("请给我一些学习建议")]
    [InlineData("如何准备执业医师考试？")]
    public void ContainsBlockedContent_SafeQuestions_ReturnsFalse(string input)
    {
        Assert.False(_builder.ContainsBlockedContent(input));
    }

    #endregion

    #region BuildSafetyRefusal

    [Fact]
    public void BuildSafetyRefusal_ReturnsNonEmpty()
    {
        var refusal = _builder.BuildSafetyRefusal();
        Assert.False(string.IsNullOrWhiteSpace(refusal));
        Assert.Contains("抱歉", refusal);
    }

    #endregion

    #region BuildSystemPrompt

    [Fact]
    public void BuildSystemPrompt_ContainsKeyElements()
    {
        var prompt = _builder.BuildSystemPrompt(
            goal: "通过执业医师考试",
            industry: "中医",
            masterName: "岐伯",
            currentStage: "入道",
            coreProfile: null,
            stageSummary: null);

        Assert.Contains("岐伯", prompt);
        Assert.Contains("中医", prompt);
        Assert.Contains("执业医师考试", prompt);
        Assert.Contains("入道", prompt);
        Assert.Contains("教学原则", prompt);
        Assert.Contains("安全边界", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithProfile_IncludesProfile()
    {
        var prompt = _builder.BuildSystemPrompt(
            goal: "学编程",
            industry: "计算机",
            masterName: "图灵",
            currentStage: "筑基",
            coreProfile: "有Python基础",
            stageSummary: null);

        Assert.Contains("学徒核心画像", prompt);
        Assert.Contains("有Python基础", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithSummary_IncludesSummary()
    {
        var prompt = _builder.BuildSystemPrompt(
            goal: "学编程",
            industry: "计算机",
            masterName: "图灵",
            currentStage: "精进",
            coreProfile: null,
            stageSummary: "已掌握基础知识");

        Assert.Contains("当前阶段学习摘要", prompt);
        Assert.Contains("已掌握基础知识", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_UnknownStage_FallsBackTo入道()
    {
        var prompt = _builder.BuildSystemPrompt(
            goal: "测试",
            industry: "测试",
            masterName: "先生",
            currentStage: "未知阶段",
            coreProfile: null,
            stageSummary: null);

        // 应该回退到"入道"的 persona（引路人）
        Assert.Contains("引路人", prompt);
    }

    #endregion

    #region BuildMessages

    [Fact]
    public void BuildMessages_ReturnsSystemPlusUserMessage()
    {
        var messages = _builder.BuildMessages(
            goal: "学医",
            industry: "医学",
            masterName: "岐伯",
            currentStage: "入道",
            coreProfile: null,
            stageSummary: null,
            recentHistory: null,
            userMessage: "你好");

        Assert.True(messages.Count >= 2);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[^1].Role);
        Assert.Equal("你好", messages[^1].Text);
    }

    [Fact]
    public void BuildMessages_WithHistory_IncludesRecentHistory()
    {
        var history = new List<ChatHistoryItem>
        {
            new() { Role = "user", Content = "什么是中医？" },
            new() { Role = "assistant", Content = "中医是..." },
        };

        var messages = _builder.BuildMessages(
            goal: "学医",
            industry: "医学",
            masterName: "岐伯",
            currentStage: "入道",
            coreProfile: null,
            stageSummary: null,
            recentHistory: history,
            userMessage: "继续说");

        // System + 2 history + 1 user = 4 messages
        Assert.Equal(4, messages.Count);
        Assert.Equal("什么是中医？", messages[1].Text);
        Assert.Equal("中医是...", messages[2].Text);
        Assert.Equal("继续说", messages[3].Text);
    }

    [Fact]
    public void BuildMessages_TruncatesHistoryToLast20()
    {
        var history = Enumerable.Range(1, 30).Select(i =>
            new ChatHistoryItem { Role = "user", Content = $"msg{i}" }).ToList();

        var messages = _builder.BuildMessages(
            goal: "test", industry: "test", masterName: "X",
            currentStage: "入道", coreProfile: null, stageSummary: null,
            recentHistory: history, userMessage: "hello");

        // System + 20 history + 1 user = 22 messages
        Assert.Equal(22, messages.Count);
        Assert.Equal("msg11", messages[1].Text); // First kept history item
        Assert.Equal("msg30", messages[20].Text); // Last history item
    }

    #endregion
}
