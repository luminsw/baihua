using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class DailyCardDtoTests
{
    #region DailyCardResult

    [Fact]
    public void DailyCardResult_DefaultValues_AreCorrect()
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
    public void DailyCardResult_CanSetProperties()
    {
        var result = new DailyCardResult
        {
            HasCard = true,
            Message = "今日卡片",
            Card = new CardItem { Front = "Question" },
            TodayProgress = new DailyProgress { Completed = 3 },
            Remaining = 2,
            IsReview = true
        };

        Assert.True(result.HasCard);
        Assert.Equal("今日卡片", result.Message);
        Assert.NotNull(result.Card);
        Assert.NotNull(result.TodayProgress);
        Assert.Equal(2, result.Remaining);
        Assert.True(result.IsReview);
    }

    #endregion

    #region CardItem

    [Fact]
    public void CardItem_DefaultValues_AreCorrect()
    {
        var card = new CardItem();

        Assert.Equal("", card.Id);
        Assert.Equal("", card.Deck);
        Assert.Equal("", card.Front);
        Assert.Equal("", card.Back);
        Assert.Empty(card.Tags);
        Assert.Equal("", card.Source);
    }

    [Fact]
    public void CardItem_CanSetProperties()
    {
        var card = new CardItem
        {
            Id = "card-001",
            Deck = "数学",
            Front = "1+1=?",
            Back = "2",
            Tags = new List<string> { "基础", "加法" },
            Source = "家长出题"
        };

        Assert.Equal("card-001", card.Id);
        Assert.Equal("数学", card.Deck);
        Assert.Equal("1+1=?", card.Front);
        Assert.Equal("2", card.Back);
        Assert.Equal(2, card.Tags.Count);
        Assert.Contains("基础", card.Tags);
        Assert.Equal("家长出题", card.Source);
    }

    #endregion

    #region DailyProgress

    [Fact]
    public void DailyProgress_DefaultValues_AreCorrect()
    {
        var progress = new DailyProgress();

        Assert.Equal(0, progress.Completed);
        Assert.Equal(5, progress.Target); // 默认目标 5
        Assert.Equal(0, progress.TotalCards);
        Assert.Equal(0, progress.Streak);
    }

    [Fact]
    public void DailyProgress_CanSetProperties()
    {
        var progress = new DailyProgress
        {
            Completed = 3,
            Target = 10,
            TotalCards = 50,
            Streak = 7
        };

        Assert.Equal(3, progress.Completed);
        Assert.Equal(10, progress.Target);
        Assert.Equal(50, progress.TotalCards);
        Assert.Equal(7, progress.Streak);
    }

    [Fact]
    public void DailyProgress_CanCalculateRemaining()
    {
        var progress = new DailyProgress { Completed = 3, Target = 10 };
        var remaining = progress.Target - progress.Completed;
        Assert.Equal(7, remaining);
    }

    #endregion

    #region CustomCardRequest

    [Fact]
    public void CustomCardRequest_DefaultValues_AreCorrect()
    {
        var request = new CustomCardRequest();

        Assert.Equal("", request.Front);
        Assert.Equal("", request.Back);
        Assert.Null(request.Deck);
        Assert.Null(request.Tags);
    }

    [Fact]
    public void CustomCardRequest_CanSetProperties()
    {
        var request = new CustomCardRequest
        {
            Front = "问题",
            Back = "答案",
            Deck = "数学",
            Tags = new List<string> { "基础" }
        };

        Assert.Equal("问题", request.Front);
        Assert.Equal("答案", request.Back);
        Assert.Equal("数学", request.Deck);
        Assert.Single(request.Tags!);
    }

    #endregion

    #region StudiedCard

    [Fact]
    public void StudiedCard_DefaultValues_AreCorrect()
    {
        var studied = new StudiedCard();

        Assert.NotNull(studied.Card);
        Assert.Equal("", studied.Result);
        Assert.Equal("", studied.Date);
    }

    [Fact]
    public void StudiedCard_CanSetProperties()
    {
        var studied = new StudiedCard
        {
            Card = new CardItem { Front = "Q" },
            Result = "remember",
            Date = "2024-06-21"
        };

        Assert.NotNull(studied.Card);
        Assert.Equal("remember", studied.Result);
        Assert.Equal("2024-06-21", studied.Date);
    }

    #endregion

    #region DailyRecord

    [Fact]
    public void DailyRecord_DefaultValues_AreCorrect()
    {
        var record = new DailyRecord();

        Assert.Equal(0, record.Completed);
        Assert.Empty(record.Answers);
    }

    [Fact]
    public void DailyRecord_CanSetProperties()
    {
        var record = new DailyRecord
        {
            Completed = 5,
            Answers = new Dictionary<string, string>
            {
                ["card-1"] = "remember",
                ["card-2"] = "hard"
            }
        };

        Assert.Equal(5, record.Completed);
        Assert.Equal(2, record.Answers.Count);
        Assert.Equal("remember", record.Answers["card-1"]);
    }

    #endregion
}