using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class NoteTests
{
    [Fact]
    public void Note_DefaultValues_AreEmpty()
    {
        var note = new Note();

        Assert.Equal("", note.Path);
        Assert.Equal("", note.Title);
        Assert.Equal("", note.Summary);
        Assert.Equal("", note.Content);
    }

    [Fact]
    public void Note_CanSetProperties()
    {
        var note = new Note
        {
            Path = "folder/note.md",
            Title = "测试笔记",
            Summary = "这是一个测试笔记的摘要",
            Content = "笔记内容..."
        };

        Assert.Equal("folder/note.md", note.Path);
        Assert.Equal("测试笔记", note.Title);
        Assert.Equal("这是一个测试笔记的摘要", note.Summary);
        Assert.Equal("笔记内容...", note.Content);
    }

    [Fact]
    public void Note_ChineseCharacters_WorksCorrectly()
    {
        var note = new Note
        {
            Path = "中医/方剂/桂枝汤.md",
            Title = "桂枝汤",
            Summary = "解肌发表，调和营卫",
            Content = "桂枝汤组成：桂枝、芍药、甘草、大枣、生姜"
        };

        Assert.Equal("中医/方剂/桂枝汤.md", note.Path);
        Assert.Equal("桂枝汤", note.Title);
    }

    [Fact]
    public void Note_LongContent_WorksCorrectly()
    {
        var longContent = new string('A', 10000);
        var note = new Note
        {
            Content = longContent
        };

        Assert.Equal(10000, note.Content.Length);
    }

    [Fact]
    public void Note_Equality_WorksCorrectly()
    {
        var note1 = new Note { Path = "test.md", Title = "Test" };
        var note2 = new Note { Path = "test.md", Title = "Test" };
        var note3 = new Note { Path = "other.md", Title = "Other" };

        // Note is a class, not a record, so equality is reference-based
        Assert.NotEqual(note1, note2);
        Assert.NotEqual(note1, note3);
    }

    [Fact]
    public void Note_CanBeModified()
    {
        var note = new Note { Title = "Original" };
        note.Title = "Modified";

        Assert.Equal("Modified", note.Title);
    }
}