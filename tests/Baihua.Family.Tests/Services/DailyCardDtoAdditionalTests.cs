using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class DailyCardDtoAdditionalTests
{
    [Fact]
    public void DailyCardResult_Defaults_HasCardFalseMessageEmpty()
    {
        var result = new DailyCardResult();

        Assert.False(result.HasCard);
        Assert.Equal("", result.Message);
        Assert.Null(result.Card);
        Assert.Null(result.TodayProgress);
        Assert.Equal(0, result.Remaining);
        Assert.False(result.IsReview);
    }

    [Fact]
    public void DailyCardResult_SetAllProperties_StoresValues()
    {
        var card = new CardItem { Id = "c1", Front = "F", Back = "B" };
        var progress = new DailyProgress { Completed = 3, Target = 5 };

        var result = new DailyCardResult
        {
            HasCard = true,
            Message = "OK",
            Card = card,
            TodayProgress = progress,
            Remaining = 2,
            IsReview = true
        };

        Assert.True(result.HasCard);
        Assert.Equal("OK", result.Message);
        Assert.Same(card, result.Card);
        Assert.Same(progress, result.TodayProgress);
        Assert.Equal(2, result.Remaining);
        Assert.True(result.IsReview);
    }

    [Fact]
    public void CardItem_Defaults_EmptyStringsEmptyTags()
    {
        var card = new CardItem();

        Assert.Equal("", card.Id);
        Assert.Equal("", card.Deck);
        Assert.Equal("", card.Front);
        Assert.Equal("", card.Back);
        Assert.NotNull(card.Tags);
        Assert.Empty(card.Tags);
        Assert.Equal("", card.Source);
    }

    [Fact]
    public void DailyProgress_Defaults_CompletedZeroTargetFiveStreakZero()
    {
        var p = new DailyProgress();

        Assert.Equal(0, p.Completed);
        Assert.Equal(5, p.Target);
        Assert.Equal(0, p.TotalCards);
        Assert.Equal(0, p.Streak);
    }

    [Fact]
    public void CustomCardRequest_Defaults_EmptyStringsNullCollections()
    {
        var req = new CustomCardRequest();

        Assert.Equal("", req.Front);
        Assert.Equal("", req.Back);
        Assert.Null(req.Deck);
        Assert.Null(req.Tags);
    }

    [Fact]
    public void StudiedCard_Defaults_EmptyCardEmptyResult()
    {
        var sc = new StudiedCard();

        Assert.NotNull(sc.Card);
        Assert.Equal("", sc.Result);
        Assert.Equal("", sc.Date);
    }

    [Fact]
    public void DailyRecord_Defaults_CompletedZeroEmptyAnswers()
    {
        var r = new DailyRecord();

        Assert.Equal(0, r.Completed);
        Assert.NotNull(r.Answers);
        Assert.Empty(r.Answers);
    }

    [Fact]
    public void CardItem_Tags_MutableList()
    {
        var card = new CardItem();
        card.Tags.Add("a");
        card.Tags.Add("b");

        Assert.Equal(2, card.Tags.Count);
        Assert.Contains("a", card.Tags);
        Assert.Contains("b", card.Tags);
    }
}
