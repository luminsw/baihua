using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class WikiLinkResolverExtendedTests
{
    #region BuildTitleToPathMap

    [Fact]
    public void BuildTitleToPathMap_EmptyNotes_ReturnsEmptyDictionary()
    {
        var result = WikiLinkResolver.BuildTitleToPathMap(new List<Note>(), new Dictionary<Note, string>());

        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_SingleNote_ReturnsOneEntry()
    {
        var note = new Note { Title = "桂枝汤", Content = "" };
        var notePathMap = new Dictionary<Note, string>
        {
            { note, "方剂/桂枝汤.md" }
        };

        var result = WikiLinkResolver.BuildTitleToPathMap(new List<Note> { note }, notePathMap);

        Assert.Single(result);
        Assert.Equal("方剂/桂枝汤.md", result["桂枝汤"]);
    }

    [Fact]
    public void BuildTitleToPathMap_DuplicateTitles_Skipped()
    {
        var n1 = new Note { Title = "桂枝汤", Content = "" };
        var n2 = new Note { Title = "桂枝汤", Content = "" };
        var notePathMap = new Dictionary<Note, string>
        {
            { n1, "方剂/桂枝汤.md" },
            { n2, "方剂/桂枝汤2.md" }
        };

        var result = WikiLinkResolver.BuildTitleToPathMap(new List<Note> { n1, n2 }, notePathMap);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_CaseInsensitiveDuplicates_Skipped()
    {
        var n1 = new Note { Title = "Target", Content = "" };
        var n2 = new Note { Title = "target", Content = "" };
        var notePathMap = new Dictionary<Note, string>
        {
            { n1, "/a.md" },
            { n2, "/b.md" }
        };

        var result = WikiLinkResolver.BuildTitleToPathMap(new List<Note> { n1, n2 }, notePathMap);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_MultipleUniqueTitles_ReturnsAll()
    {
        var n1 = new Note { Title = "桂枝汤", Content = "" };
        var n2 = new Note { Title = "麻黄汤", Content = "" };
        var n3 = new Note { Title = "太阳病", Content = "" };
        var notePathMap = new Dictionary<Note, string>
        {
            { n1, "方剂/桂枝汤.md" },
            { n2, "方剂/麻黄汤.md" },
            { n3, "病机/太阳病.md" }
        };

        var result = WikiLinkResolver.BuildTitleToPathMap(
            new List<Note> { n1, n2, n3 }, notePathMap);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void BuildTitleToPathMap_CaseInsensitiveLookup_Works()
    {
        var note = new Note { Title = "Target", Content = "" };
        var notePathMap = new Dictionary<Note, string> { { note, "/a.md" } };

        var result = WikiLinkResolver.BuildTitleToPathMap(new List<Note> { note }, notePathMap);

        Assert.Equal("/a.md", result["TARGET"]);
        Assert.Equal("/a.md", result["target"]);
    }

    #endregion

    #region ExtractWikiLinkTargets

    [Fact]
    public void ExtractWikiLinkTargets_EmptyNotes_ReturnsEmpty()
    {
        var result = WikiLinkResolver.ExtractWikiLinkTargets(new List<Note>());

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_NoWikiLinks_ReturnsEmpty()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "Plain text content" },
            new() { Title = "B", Content = "More plain text" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_SingleLink_ReturnsTarget()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "See [[Target]] for details" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
        Assert.Contains("Target", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_LinkWithHeader_StripsHeader()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "See [[Target#section]] here" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
        Assert.Contains("Target", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_LinkWithAlias_StripsAlias()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "See [[Target|alias]] here" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
        Assert.Contains("Target", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_MultipleLinks_ReturnsAll()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "[[Link1]] and [[Link2]]" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ExtractWikiLinkTargets_CaseInsensitiveDuplicates_Deduplicated()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "[[Target]]" },
            new() { Title = "B", Content = "[[target]]" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Single(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_EmptyContent_Skipped()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "" },
            new() { Title = "B", Content = null! }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_LinksFromMultipleNotes_Aggregated()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "[[Link1]]" },
            new() { Title = "B", Content = "[[Link2]]" },
            new() { Title = "C", Content = "[[Link1]] [[Link3]]" }
        };

        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);

        Assert.Equal(3, result.Count);
    }

    #endregion

    #region ResolveLinkToPath

    [Fact]
    public void ResolveLinkToPath_NullInput_ReturnsNull()
    {
        var result = WikiLinkResolver.ResolveLinkToPath(null!, new Dictionary<string, string>());
        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_EmptyInput_ReturnsNull()
    {
        var result = WikiLinkResolver.ResolveLinkToPath("", new Dictionary<string, string>());
        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_WhitespaceInput_ReturnsNull()
    {
        var result = WikiLinkResolver.ResolveLinkToPath("   ", new Dictionary<string, string>());
        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_NullDictionary_ReturnsNull()
    {
        var result = WikiLinkResolver.ResolveLinkToPath("Some Page", null!);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_LinkWithSlash_ReturnsAsIs()
    {
        var result = WikiLinkResolver.ResolveLinkToPath("方剂/桂枝汤", new Dictionary<string, string>());

        Assert.Equal("方剂/桂枝汤", result);
    }

    [Fact]
    public void ResolveLinkToPath_LinkInDictionary_ReturnsPath()
    {
        var dict = new Dictionary<string, string> { { "Target", "/path/to/target.md" } };
        var result = WikiLinkResolver.ResolveLinkToPath("Target", dict);

        Assert.Equal("/path/to/target.md", result);
    }

    [Fact]
    public void ResolveLinkToPath_LinkNotInDictionary_ReturnsNull()
    {
        var dict = new Dictionary<string, string> { { "Other", "/path/to/other.md" } };
        var result = WikiLinkResolver.ResolveLinkToPath("Target", dict);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_LinkWithHeader_TrimsAndReturnsPath()
    {
        var dict = new Dictionary<string, string> { { "Target", "/path.md" } };
        var result = WikiLinkResolver.ResolveLinkToPath("Target  ", dict);

        Assert.Equal("/path.md", result);
    }

    #endregion
}
