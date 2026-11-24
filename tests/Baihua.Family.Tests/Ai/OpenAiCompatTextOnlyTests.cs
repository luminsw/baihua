using System.Text.Json;
using Baihua.Modules.Ai.Controllers;
using Xunit;

namespace Baihua.Family.Tests.Ai;

public class OpenAiCompatTextOnlyTests
{
    [Fact]
    public void TryParseContent_AcceptsPlainText()
    {
        using var document = JsonDocument.Parse("\"hello\"");

        var accepted = OpenAiCompatController.TryParseContent(document.RootElement, out var contents);

        Assert.True(accepted);
        Assert.Single(contents);
    }

    [Fact]
    public void TryParseContent_AcceptsTextParts()
    {
        using var document = JsonDocument.Parse("""[{"type":"text","text":"hello"}]""");

        var accepted = OpenAiCompatController.TryParseContent(document.RootElement, out var contents);

        Assert.True(accepted);
        Assert.Single(contents);
    }

    [Theory]
    [InlineData("""[{"type":"image_url","image_url":{"url":"data:image/png;base64,AA=="}}]""")]
    [InlineData("""[{"type":"video_url","video_url":{"url":"data:video/mp4;base64,AA=="}}]""")]
    [InlineData("""[{"type":"input_audio","input_audio":{"data":"AA==","format":"wav"}}]""")]
    [InlineData("""[{"type":"file","file":{"file_id":"file_123"}}]""")]
    public void TryParseContent_RejectsNonTextParts(string json)
    {
        using var document = JsonDocument.Parse(json);

        var accepted = OpenAiCompatController.TryParseContent(document.RootElement, out var contents);

        Assert.False(accepted);
        Assert.Empty(contents);
    }

    [Theory]
    [InlineData("""{"modalities":["text"]}""", false)]
    [InlineData("""{"modalities":["text","audio"]}""", true)]
    [InlineData("""{"audio":{"voice":"alloy"}}""", true)]
    public void RequestsNonTextOutput_RecognizesTextOnlyRequests(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, OpenAiCompatController.RequestsNonTextOutput(document.RootElement));
    }
}
