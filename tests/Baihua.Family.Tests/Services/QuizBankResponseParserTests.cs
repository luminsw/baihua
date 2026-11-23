using System.Text.Json;
using Baihua.Contracts.Learn;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// AI 响应解析器测试：模型输出带围栏/前后文字/多对象/截断时，只有「完整且合法」的题库包才应被接受。
/// 解析失败必须返回 null（生成服务据此重试一次或回退到人工粘贴），不能返回半截对象。
/// </summary>
public class QuizBankResponseParserTests
{
    private static string SampleJson()
        => JsonSerializer.Serialize(QuizBankTestSupport.CreateSamplePackage(), JsonSerializerOptions.Web);

    [Fact]
    public void Parse_PureJson_ReturnsPackage()
    {
        var package = QuizBankResponseParser.Parse(SampleJson());

        Assert.NotNull(package);
        Assert.Equal(2, package.Items.Count);
        Assert.Equal(3, package.Questions.Count);
    }

    [Fact]
    public void Parse_MarkdownFence_ReturnsPackage()
    {
        var text = "```json\n" + SampleJson() + "\n```";

        var package = QuizBankResponseParser.Parse(text);

        Assert.NotNull(package);
        Assert.Equal(QuizBankTestSupport.BankCode, package.Bank.Code);
    }

    [Fact]
    public void Parse_SurroundingText_ReturnsPackage()
    {
        var text = "好的，下面是生成的题库包：\n" + SampleJson() + "\n希望有帮助。";

        var package = QuizBankResponseParser.Parse(text);

        Assert.NotNull(package);
        Assert.Equal(3, package.Questions.Count);
    }

    [Fact]
    public void Parse_MultipleObjects_ReturnsFirst()
    {
        var text = SampleJson() + "\n" + SampleJson();

        var package = QuizBankResponseParser.Parse(text);

        Assert.NotNull(package);
        Assert.Equal(QuizBankTestSupport.BankCode, package.Bank.Code);
    }

    [Fact]
    public void Parse_BracesAndEscapedQuotesInsideStrings_ReturnsPackage()
    {
        var sample = QuizBankTestSupport.CreateSamplePackage();
        sample.Questions[0].Stem = "患者自述「{胸闷}」，并说\"饭后加重\"。最适宜的是？";

        var package = QuizBankResponseParser.Parse(
            JsonSerializer.Serialize(sample, JsonSerializerOptions.Web));

        Assert.NotNull(package);
        Assert.Contains("{胸闷}", package.Questions[0].Stem);
    }

    [Fact]
    public void Parse_TruncatedJson_ReturnsNull()
    {
        var truncated = SampleJson()[..^10];

        Assert.Null(QuizBankResponseParser.Parse(truncated));
    }

    [Fact]
    public void Parse_NonJson_ReturnsNull()
    {
        Assert.Null(QuizBankResponseParser.Parse("抱歉，我无法完成该任务。"));
        Assert.Null(QuizBankResponseParser.Parse(""));
        Assert.Null(QuizBankResponseParser.Parse(null));
    }

    [Fact]
    public void ExtractJson_IsCaseInsensitiveForPropertyNames()
    {
        var json = SampleJson().Replace("\"formatVersion\"", "\"FormatVersion\"");

        var package = QuizBankResponseParser.Parse(json);

        Assert.NotNull(package);
        Assert.Equal(QuizBankTestSupport.IndustryCode, package.Industry.Code);
    }
}