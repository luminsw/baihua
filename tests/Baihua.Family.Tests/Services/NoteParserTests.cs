using Microsoft.Extensions.Logging;
using Baihua.Modules.Family.Services;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Baihua.Family.Tests.Services;

public class NoteParserTests(ITestOutputHelper output)
{
    private readonly ILogger<NoteParser> _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<NoteParser>();
    private static readonly IStringLocalizer<SharedResources> _loc = TestLocalizer.Instance;

    #region ParseResult - Basic

    [Fact]
    public void ParseResult_ValidJsonArray_ReturnsNotes()
    {
        var parser = new NoteParser(_logger, _loc);
        var json = """[{"Path":"test.md","Title":"Test","Summary":"Summary","Content":"Content"}]""";
        var result = parser.ParseResult(json);
        Assert.Single(result);
        Assert.Equal("test.md", result[0].Path);
        Assert.Equal("Test", result[0].Title);
    }

    [Fact]
    public void ParseResult_MultipleNotes_ReturnsAll()
    {
        var parser = new NoteParser(_logger, _loc);
        var json = """[{"Path":"a.md","Title":"A","Summary":"","Content":""},{"Path":"b.md","Title":"B","Summary":"","Content":""}]""";
        var result = parser.ParseResult(json);
        Assert.Equal(2, result.Count);
        Assert.Equal("a.md", result[0].Path);
        Assert.Equal("b.md", result[1].Path);
    }

    [Fact]
    public void ParseResult_EmptyArray_ReturnsEmptyList()
    {
        var parser = new NoteParser(_logger, _loc);
        var result = parser.ParseResult("[]");
        Assert.Empty(result);
    }

    #endregion

    #region ParseResult - Markdown Code Block

    [Fact]
    public void ParseResult_MarkdownCodeBlock_ExtractsJson()
    {
        var parser = new NoteParser(_logger, _loc);
        var input = """
            ```json
            [{"Path":"test.md","Title":"Test","Summary":"","Content":""}]
            ```
            """;
        var result = parser.ParseResult(input);
        Assert.Single(result);
        Assert.Equal("test.md", result[0].Path);
    }

    [Fact]
    public void ParseResult_CodeBlockWithoutLanguage_ExtractsJson()
    {
        var parser = new NoteParser(_logger, _loc);
        var input = """
            ```
            [{"Path":"test.md","Title":"Test","Summary":"","Content":""}]
            ```
            """;
        var result = parser.ParseResult(input);
        Assert.Single(result);
        Assert.Equal("test.md", result[0].Path);
    }

    [Fact]
    public void ParseResult_TextBeforeJson_ExtractsJson()
    {
        var parser = new NoteParser(_logger, _loc);
        var input = """
            Here are the notes:
            [{"Path":"test.md","Title":"Test","Summary":"","Content":""}]
            """;
        var result = parser.ParseResult(input);
        Assert.Single(result);
        Assert.Equal("test.md", result[0].Path);
    }

    [Fact]
    public void ParseResult_TextAfterJson_ExtractsJson()
    {
        var parser = new NoteParser(_logger, _loc);
        var input = """
            [{"Path":"test.md","Title":"Test","Summary":"","Content":""}]
            That's all!
            """;
        var result = parser.ParseResult(input);
        Assert.Single(result);
        Assert.Equal("test.md", result[0].Path);
    }

    #endregion

    #region ParseResult - Error Cases

    [Fact]
    public void ParseResult_EmptyString_Throws()
    {
        var parser = new NoteParser(_logger, _loc);
        Assert.Throws<Exception>(() => parser.ParseResult(""));
    }

    [Fact]
    public void ParseResult_Null_ThrowsNullReference()
    {
        var parser = new NoteParser(_logger, _loc);
        Assert.Throws<NullReferenceException>(() => parser.ParseResult(null!));
    }

    [Fact]
    public void ParseResult_NoJsonArray_Throws()
    {
        var parser = new NoteParser(_logger, _loc);
        Assert.Throws<Exception>(() => parser.ParseResult("No array here"));
    }

    [Fact]
    public void ParseResult_UnclosedArray_Throws()
    {
        var parser = new NoteParser(_logger, _loc);
        Assert.Throws<Exception>(() => parser.ParseResult("[{\"Path\":\"test.md\"}"));
    }

    [Fact]
    public void ParseResult_InvalidJson_Throws()
    {
        var parser = new NoteParser(_logger, _loc);
        Assert.Throws<Exception>(() => parser.ParseResult("[{invalid}]"));
    }

    #endregion

    #region ParseResult - Special Characters

    [Fact]
    public void ParseResult_NewlinesInContent_EscapesCorrectly()
    {
        var parser = new NoteParser(_logger, _loc);
        // JSON with literal newlines in string (unescaped)
        var json = "[{\"Path\":\"test.md\",\"Title\":\"Test\",\"Summary\":\"\",\"Content\":\"Line1\nLine2\"}]";
        var result = parser.ParseResult(json);
        Assert.Single(result);
        Assert.Contains("Line1", result[0].Content);
        Assert.Contains("Line2", result[0].Content);
    }

    [Fact]
    public void ParseResult_TabInContent_EscapesCorrectly()
    {
        var parser = new NoteParser(_logger, _loc);
        var json = "[{\"Path\":\"test.md\",\"Title\":\"Test\",\"Summary\":\"\",\"Content\":\"Col1\tCol2\"}]";
        var result = parser.ParseResult(json);
        Assert.Single(result);
        Assert.Contains("\t", result[0].Content);
    }

    [Fact]
    public void ParseResult_ChineseCharacters_ParsesCorrectly()
    {
        var parser = new NoteParser(_logger, _loc);
        var json = "[{\"Path\":\"测试.md\",\"Title\":\"中文标题\",\"Summary\":\"摘要\",\"Content\":\"内容\"}]";
        var result = parser.ParseResult(json);
        Assert.Single(result);
        Assert.Equal("测试.md", result[0].Path);
        Assert.Equal("中文标题", result[0].Title);
        Assert.Equal("摘要", result[0].Summary);
        Assert.Equal("内容", result[0].Content);
    }

    [Fact]
    public void ParseResult_Emojis_ParsesCorrectly()
    {
        var parser = new NoteParser(_logger, _loc);
        var json = "[{\"Path\":\"test.md\",\"Title\":\"Test 📝\",\"Summary\":\"\",\"Content\":\"Hello 👋\"}]";
        var result = parser.ParseResult(json);
        Assert.Single(result);
        Assert.Contains("📝", result[0].Title);
        Assert.Contains("👋", result[0].Content);
    }

    #endregion

    #region ParseResult - Nested Structures

    [Fact]
    public void ParseResult_NestedArraysInContent_ParsesCorrectly()
    {
        var parser = new NoteParser(_logger, _loc);
        var json = "[{\"Path\":\"test.md\",\"Title\":\"Test\",\"Summary\":\"\",\"Content\":\"Items: [1, 2, 3]\"}]";
        var result = parser.ParseResult(json);
        Assert.Single(result);
        Assert.Contains("[1, 2, 3]", result[0].Content);
    }

    [Fact]
    public void ParseResult_NestedBracketsInContent_ParsesCorrectly()
    {
        var parser = new NoteParser(_logger, _loc);
        var json = "[{\"Path\":\"test.md\",\"Title\":\"Test\",\"Summary\":\"\",\"Content\":\"Array [[link]] here\"}]";
        var result = parser.ParseResult(json);
        Assert.Single(result);
        Assert.Contains("[[link]]", result[0].Content);
    }

    #endregion
}
