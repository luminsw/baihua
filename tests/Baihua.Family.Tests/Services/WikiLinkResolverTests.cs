using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class WikiLinkResolverTests
{
    [Fact]
    public void BuildTitleToPathMap_EmptyNotes_ReturnsEmpty()
    {
        var notes = new List<Note>();
        var notePathMap = new Dictionary<Note, string>();

        var result = WikiLinkResolver.BuildTitleToPathMap(notes, notePathMap);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_SingleNote_ReturnsMap()
    {
        var note = new Note { Title = "Test Title", Content = "content" };
        var notes = new List<Note> { note };
        var notePathMap = new Dictionary<Note, string> { { note, "/path/to/test.md" } };

        var result = WikiLinkResolver.BuildTitleToPathMap(notes, notePathMap);

        Assert.Single(result);
        Assert.Contains("Test Title", result);
        Assert.Equal("/path/to/test.md", result["Test Title"]);
    }

    [Fact]
    public void BuildTitleToPathMap_DuplicateTitles_ExcludesDuplicates()
    {
        var note1 = new Note { Title = "Same Title", Content = "content1" };
        var note2 = new Note { Title = "Same Title", Content = "content2" };
        var notes = new List<Note> { note1, note2 };
        var notePathMap = new Dictionary<Note, string>
        {
            { note1, "/path/to/note1.md" },
            { note2, "/path/to/note2.md" }
        };

        var result = WikiLinkResolver.BuildTitleToPathMap(notes, notePathMap);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_CaseInsensitive_UsesFirstMatch()
    {
        var note = new Note { Title = "My Title", Content = "content" };
        var notes = new List<Note> { note };
        var notePathMap = new Dictionary<Note, string> { { note, "/path/my-title.md" } };

        var result = WikiLinkResolver.BuildTitleToPathMap(notes, notePathMap);

        Assert.True(result.ContainsKey("my title"));
        Assert.True(result.ContainsKey("MY TITLE"));
        Assert.Equal("/path/my-title.md", result["my title"]);
    }

    [Fact]
    public void ExtractWikiLinkTargets_EmptyNotes_ReturnsEmpty()
    {
        var notes = new List<Note>();

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_NoWikiLinks_ReturnsEmpty()
    {
        var notes = new List<Note> { new Note { Title = "Title", Content = "No links here" } };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_SingleWikiLink_ReturnsTarget()
    {
        var notes = new List<Note> { new Note { Title = "Title", Content = "See [[Target Page]] for details" } };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
        Assert.Equal("Target Page", result[0]);
    }

    [Fact]
    public void ExtractWikiLinkTargets_MultipleWikiLinks_ReturnsUniqueTargets()
    {
        var notes = new List<Note>
        {
            new Note { Title = "Title1", Content = "See [[Page A]] and [[Page B]]" },
            new Note { Title = "Title2", Content = "Also [[Page A]] and [[Page C]]" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Equal(3, result.Count);
        Assert.Contains("Page A", result);
        Assert.Contains("Page B", result);
        Assert.Contains("Page C", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_WikiLinksWithPipes_ExtractsBeforePipe()
    {
        var notes = new List<Note> { new Note { Title = "Title", Content = "[[Target|Display Text]]" } };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
        Assert.Equal("Target", result[0]);
    }

    [Fact]
    public void ExtractWikiLinkTargets_WikiLinksWithAnchors_ExtractsBeforeAnchor()
    {
        var notes = new List<Note> { new Note { Title = "Title", Content = "[[Target#Section]]" } };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
        Assert.Equal("Target", result[0]);
    }

    [Fact]
    public void ExtractWikiLinkTargets_CaseInsensitive_ReturnsNormalized()
    {
        var notes = new List<Note> { new Note { Title = "Title", Content = "[[page a]] [[Page A]]" } };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_EmptyWikiLink_Ignores()
    {
        var notes = new List<Note> { new Note { Title = "Title", Content = "[[]]" } };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveLinkToPath_NullLink_ReturnsNull()
    {
        var titleToPath = new Dictionary<string, string>();

        var result = WikiLinkResolver.ResolveLinkToPath(null, titleToPath);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_EmptyLink_ReturnsNull()
    {
        var titleToPath = new Dictionary<string, string>();

        var result = WikiLinkResolver.ResolveLinkToPath("", titleToPath);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_LinkWithSlash_ReturnsAsIs()
    {
        var titleToPath = new Dictionary<string, string>();

        var result = WikiLinkResolver.ResolveLinkToPath("subdir/target.md", titleToPath);

        Assert.Equal("subdir/target.md", result);
    }

    [Fact]
    public void ResolveLinkToPath_LinkInMap_ReturnsPath()
    {
        var titleToPath = new Dictionary<string, string>
        {
            { "Target Page", "/path/to/target.md" }
        };

        var result = WikiLinkResolver.ResolveLinkToPath("Target Page", titleToPath);

        Assert.Equal("/path/to/target.md", result);
    }

    [Fact]
    public void ResolveLinkToPath_LinkNotInMap_ReturnsNull()
    {
        var titleToPath = new Dictionary<string, string>
        {
            { "Other Page", "/path/to/other.md" }
        };

        var result = WikiLinkResolver.ResolveLinkToPath("Target Page", titleToPath);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_CaseInsensitiveMatch_ReturnsPath()
    {
        var titleToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Target Page", "/path/to/target.md" }
        };

        var result = WikiLinkResolver.ResolveLinkToPath("target page", titleToPath);

        Assert.Equal("/path/to/target.md", result);
    }

    [Fact]
    public void ResolveLinkToPath_WhitespaceTrimmed_ReturnsPath()
    {
        var titleToPath = new Dictionary<string, string>
        {
            { "Target Page", "/path/to/target.md" }
        };

        var result = WikiLinkResolver.ResolveLinkToPath("  Target Page  ", titleToPath);

        Assert.Equal("/path/to/target.md", result);
    }
}