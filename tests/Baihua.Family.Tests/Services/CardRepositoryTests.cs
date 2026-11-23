using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class CardRepositoryTests
{
    #region ComputeCardId

    [Fact]
    public void ComputeCardId_SameContent_ReturnsSameId()
    {
        var id1 = CardRepository.ComputeCardId("问题", "答案");
        var id2 = CardRepository.ComputeCardId("问题", "答案");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeCardId_DifferentContent_ReturnsDifferentId()
    {
        var id1 = CardRepository.ComputeCardId("问题A", "答案");
        var id2 = CardRepository.ComputeCardId("问题B", "答案");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeCardId_DifferentBack_ReturnsDifferentId()
    {
        var id1 = CardRepository.ComputeCardId("问题", "答案A");
        var id2 = CardRepository.ComputeCardId("问题", "答案B");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeCardId_Returns16CharacterString()
    {
        var id = CardRepository.ComputeCardId("问题", "答案");

        Assert.Equal(16, id.Length);
    }

    [Fact]
    public void ComputeCardId_ReturnsHexString()
    {
        var id = CardRepository.ComputeCardId("问题", "答案");

        // Should be uppercase hex characters
        Assert.True(id.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F'));
    }

    [Fact]
    public void ComputeCardId_EmptyFront_Works()
    {
        var id = CardRepository.ComputeCardId("", "答案");

        Assert.Equal(16, id.Length);
    }

    [Fact]
    public void ComputeCardId_EmptyBack_Works()
    {
        var id = CardRepository.ComputeCardId("问题", "");

        Assert.Equal(16, id.Length);
    }

    [Fact]
    public void ComputeCardId_BothEmpty_ReturnsConsistentId()
    {
        var id1 = CardRepository.ComputeCardId("", "");
        var id2 = CardRepository.ComputeCardId("", "");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeCardId_NullFront_TreatsAsEmpty()
    {
        var id1 = CardRepository.ComputeCardId(null!, "答案");
        var id2 = CardRepository.ComputeCardId("", "答案");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeCardId_NullBack_TreatsAsEmpty()
    {
        var id1 = CardRepository.ComputeCardId("问题", null!);
        var id2 = CardRepository.ComputeCardId("问题", "");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeCardId_ChineseCharacters_Works()
    {
        var id = CardRepository.ComputeCardId("桂枝汤的组成是什么？", "桂枝、芍药、甘草、大枣、生姜");

        Assert.Equal(16, id.Length);
    }

    [Fact]
    public void ComputeCardId_LongContent_Works()
    {
        var longFront = new string('A', 1000);
        var longBack = new string('B', 1000);
        var id = CardRepository.ComputeCardId(longFront, longBack);

        Assert.Equal(16, id.Length);
    }

    [Fact]
    public void ComputeCardId_NewlineInContent_AffectsHash()
    {
        var id1 = CardRepository.ComputeCardId("问题\n", "答案");
        var id2 = CardRepository.ComputeCardId("问题", "答案");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeCardId_SpecialCharacters_Works()
    {
        var id = CardRepository.ComputeCardId("What is 1+1?", "2! 😊");

        Assert.Equal(16, id.Length);
    }

    #endregion
}