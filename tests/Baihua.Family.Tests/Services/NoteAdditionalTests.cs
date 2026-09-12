using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class NoteAdditionalTests
{
    [Fact]
    public void Note_Defaults_AllEmptyStrings()
    {
        var note = new Note();

        Assert.Equal("", note.Path);
        Assert.Equal("", note.Title);
        Assert.Equal("", note.Summary);
        Assert.Equal("", note.Content);
    }

    [Fact]
    public void Note_SetAllProperties_StoresValues()
    {
        var note = new Note
        {
            Path = "test/atomic.md",
            Title = "Test Note",
            Summary = "A summary",
            Content = "The content"
        };

        Assert.Equal("test/atomic.md", note.Path);
        Assert.Equal("Test Note", note.Title);
        Assert.Equal("A summary", note.Summary);
        Assert.Equal("The content", note.Content);
    }

    [Fact]
    public void Note_ChineseContent_StoredAsIs()
    {
        var note = new Note
        {
            Path = "中医/桂枝汤.md",
            Title = "桂枝汤",
            Summary = "解表剂",
            Content = "桂枝、芍药、生姜、大枣、甘草"
        };

        Assert.Equal("中医/桂枝汤.md", note.Path);
        Assert.Equal("桂枝汤", note.Title);
        Assert.Equal("解表剂", note.Summary);
        Assert.Contains("桂枝", note.Content);
    }
}
