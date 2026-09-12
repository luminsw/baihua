using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class WikiLinkRewriterTests
{
    [Fact]
    public void RewriteMissingCategoryLinks_EmptyContent_ReturnsContent()
    {
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks("", titleToPath);

        Assert.Equal("", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_NullContent_ReturnsNull()
    {
        var titleToPath = new Dictionary<string, string>();

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(null!, titleToPath);

        Assert.Null(result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_EmptyMap_ReturnsContent()
    {
        var content = "这是一个 [[桂枝汤]] 链接";
        var titleToPath = new Dictionary<string, string>();

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_KnownTarget_AddsCategory()
    {
        var content = "这是一个 [[桂枝汤]] 链接";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("这是一个 [[方剂/桂枝汤]] 链接", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_WithHeader_PreservesHeader()
    {
        var content = "跳转到 [[桂枝汤#组成]]";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("跳转到 [[方剂/桂枝汤#组成]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_WithAlias_PreservesAlias()
    {
        var content = "跳转到 [[桂枝汤|桂枝汤方剂]]";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("跳转到 [[方剂/桂枝汤|桂枝汤方剂]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_WithHeaderAndAlias_PreservesBoth()
    {
        var content = "跳转到 [[桂枝汤#功效|桂枝汤功效]]";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("跳转到 [[方剂/桂枝汤#功效|桂枝汤功效]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_AlreadyHasCategory_Skips()
    {
        var content = "这是一个 [[方剂/桂枝汤]] 链接";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_UnknownTarget_Skips()
    {
        var content = "这是一个 [[未知方剂]] 链接";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_MultipleLinks_RewritesAll()
    {
        var content = "[[桂枝汤]] 和 [[麻黄汤]] 都是经方";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" },
            { "麻黄汤", "方剂/麻黄汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("[[方剂/桂枝汤]] 和 [[方剂/麻黄汤]] 都是经方", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_MixedLinks_RewritesSome()
    {
        var content = "[[桂枝汤]] 和 [[方剂/麻黄汤]]";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" },
            { "麻黄汤", "方剂/麻黄汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("[[方剂/桂枝汤]] 和 [[方剂/麻黄汤]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_CaseInsensitiveKey_Matches()
    {
        var content = "这是一个 [[桂枝汤]] 链接";
        var titleToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("这是一个 [[方剂/桂枝汤]] 链接", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_BackslashPreserved()
    {
        var content = "这是一个 [[桂枝汤]] 链接";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂\\桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("这是一个 [[方剂\\桂枝汤]] 链接", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_TrailingSlashPreserved()
    {
        var content = "这是一个 [[桂枝汤]] 链接";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "/方剂/桂枝汤/" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("这是一个 [[/方剂/桂枝汤/]] 链接", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_EmptyTarget_Skips()
    {
        var content = "这是一个 [[]] 链接";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_NestedLinks_HandlesCorrectly()
    {
        var content = "[[桂枝汤]] 中的 [[麻黄]] 成分";
        var titleToPath = new Dictionary<string, string>
        {
            { "桂枝汤", "方剂/桂枝汤" },
            { "麻黄", "药材/麻黄" }
        };

        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, titleToPath);

        Assert.Equal("[[方剂/桂枝汤]] 中的 [[药材/麻黄]] 成分", result);
    }
}