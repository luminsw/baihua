using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 知识点规划解析测试（自适应题量第一阶段）：模型输出带围栏/前后文字仍能解析，
/// 非法 JSON 返回 null 让调用方回退到固定分批。
/// </summary>
public class QuizTopicPlanParserTests
{
    private const string SampleJson = """
    {"topics":[{"title":"经络腧穴基础","keyPoints":["十二经脉走向","常用腧穴定位"]},{"title":"配穴方法","keyPoints":["俞募配穴","原络配穴"]}]}
    """;

    [Fact]
    public void Parse_PureJson_ReturnsTopics()
    {
        var plan = QuizTopicPlanParser.Parse(SampleJson);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Topics.Count);
        Assert.Equal("经络腧穴基础", plan.Topics[0].Title);
        Assert.Equal(2, plan.Topics[0].KeyPoints.Count);
    }

    [Fact]
    public void Parse_MarkdownFence_ReturnsTopics()
    {
        var plan = QuizTopicPlanParser.Parse("```json\n" + SampleJson + "\n```");

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Topics.Count);
    }

    [Fact]
    public void Parse_SurroundingText_ReturnsTopics()
    {
        var plan = QuizTopicPlanParser.Parse("好的，知识点如下：\n" + SampleJson + "\n请确认。");

        Assert.NotNull(plan);
        Assert.Equal("配穴方法", plan.Topics[1].Title);
    }

    [Fact]
    public void Parse_FiltersBlankTitles()
    {
        const string json = """
        {"topics":[{"title":"  ","keyPoints":[]},{"title":"有效知识点","keyPoints":["要点"]}]}
        """;

        var plan = QuizTopicPlanParser.Parse(json);

        Assert.NotNull(plan);
        Assert.Single(plan.Topics);
        Assert.Equal("有效知识点", plan.Topics[0].Title);
    }

    [Fact]
    public void Parse_InvalidOrEmpty_ReturnsNull()
    {
        Assert.Null(QuizTopicPlanParser.Parse(null));
        Assert.Null(QuizTopicPlanParser.Parse(""));
        Assert.Null(QuizTopicPlanParser.Parse("抱歉，我无法完成。"));
        Assert.Null(QuizTopicPlanParser.Parse("{\"topics\":[{\"title\":\"未闭合"));
    }
}