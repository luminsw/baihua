using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class ChatMemoryServiceTests
{
    #region EstimateTokens

    [Fact]
    public void EstimateTokens_NullText_ReturnsZero()
    {
        var result = ChatMemoryService.EstimateTokens(null!);
        Assert.Equal(0, result);
    }

    [Fact]
    public void EstimateTokens_EmptyText_ReturnsZero()
    {
        var result = ChatMemoryService.EstimateTokens("");
        Assert.Equal(0, result);
    }

    [Fact]
    public void EstimateTokens_ShortText_ReturnsAtLeastOne()
    {
        var result = ChatMemoryService.EstimateTokens("a");
        Assert.True(result >= 1);
    }

    [Fact]
    public void EstimateTokens_ChineseText_CountsAsFewerCharsPerToken()
    {
        // 6 Chinese chars: ~6/1.5 = 4 tokens
        var result = ChatMemoryService.EstimateTokens("桂枝汤方");
        Assert.True(result >= 1);
    }

    [Fact]
    public void EstimateTokens_EnglishText_CountsAsFourCharsPerToken()
    {
        // 4 English chars: ~4/4 = 1 token
        var result = ChatMemoryService.EstimateTokens("test");
        Assert.Equal(1, result);
    }

    [Fact]
    public void EstimateTokens_LongEnglishText_Estimates()
    {
        // 40 chars / 4 = 10 tokens
        var text = new string('a', 40);
        var result = ChatMemoryService.EstimateTokens(text);
        Assert.Equal(10, result);
    }

    [Fact]
    public void EstimateTokens_LongChineseText_Estimates()
    {
        // 100 CJK chars / 1.5 ≈ 67 tokens
        var text = new string('中', 100);
        var result = ChatMemoryService.EstimateTokens(text);
        Assert.True(result >= 60 && result <= 75);
    }

    [Fact]
    public void EstimateTokens_MixedText_CombinesEstimates()
    {
        // 6 CJK + 12 non-CJK = ceil(6/1.5) + ceil(12/4) = 4 + 3 = 7, or ceil(4) + ceil(3) = 7
        // Actually Math.Ceiling is applied to the sum, so this is approximate
        var text = "桂枝汤方" + new string('a', 12);
        var result = ChatMemoryService.EstimateTokens(text);
        Assert.True(result >= 6 && result <= 8, $"Expected 6-8, got {result}");
    }

    [Fact]
    public void EstimateTokens_NumbersAndSymbols_CountsAsNonCjk()
    {
        var result = ChatMemoryService.EstimateTokens("12345");
        // 5 non-CJK chars / 4 = ~2 tokens
        Assert.Equal(2, result);
    }

    #endregion
}
