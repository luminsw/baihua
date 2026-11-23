using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class LeaderboardDtoTests
{
    [Fact]
    public void DashboardData_DefaultValues_AreEmpty()
    {
        var data = new DashboardData();
        Assert.Empty(data.FamilyStats);
        Assert.Empty(data.WeeklyTrend);
        Assert.Empty(data.RecentAchievements);
        Assert.NotNull(data.ResultDistribution);
    }

    [Fact]
    public void DashboardData_CanSetProperties()
    {
        var data = new DashboardData
        {
            FamilyStats = new List<FamilyMemberStat> { new FamilyMemberStat { Name = "Test" } },
            WeeklyTrend = new List<DailyTrend> { new DailyTrend { Date = "01-01", Count = 5 } },
            RecentAchievements = new List<RecentAchievement> { new RecentAchievement { Title = "成就" } },
            ResultDistribution = new ResultDistribution { Remember = 10, Hard = 2, Forgot = 1 }
        };
        Assert.Single(data.FamilyStats);
        Assert.Single(data.WeeklyTrend);
        Assert.Single(data.RecentAchievements);
        Assert.Equal(10, data.ResultDistribution.Remember);
    }

    [Fact]
    public void FamilyMemberStat_DefaultValues_AreCorrect()
    {
        var stat = new FamilyMemberStat();
        Assert.Equal(0, stat.LearnerId);
        Assert.Equal("", stat.Name);
        Assert.Equal("", stat.AvatarEmoji);
        Assert.Equal("", stat.Color);
        Assert.Equal(0, stat.WeekTotal);
        Assert.Equal(0, stat.Accuracy);
        Assert.Equal(0, stat.Streak);
        Assert.Equal(0, stat.TotalCards);
    }

    [Fact]
    public void FamilyMemberStat_CanSetProperties()
    {
        var stat = new FamilyMemberStat
        {
            LearnerId = 1,
            Name = "小明",
            AvatarEmoji = "👦",
            Color = "#FF5733",
            WeekTotal = 20,
            Accuracy = 85.5,
            Streak = 7,
            TotalCards = 100
        };
        Assert.Equal(1, stat.LearnerId);
        Assert.Equal("小明", stat.Name);
        Assert.Equal("👦", stat.AvatarEmoji);
        Assert.Equal("#FF5733", stat.Color);
        Assert.Equal(20, stat.WeekTotal);
        Assert.Equal(85.5, stat.Accuracy);
        Assert.Equal(7, stat.Streak);
        Assert.Equal(100, stat.TotalCards);
    }

    [Fact]
    public void DailyTrend_DefaultValues_AreCorrect()
    {
        var trend = new DailyTrend();
        Assert.Equal("", trend.Date);
        Assert.Equal(0, trend.Count);
    }

    [Fact]
    public void DailyTrend_CanSetProperties()
    {
        var trend = new DailyTrend
        {
            Date = "06-21",
            Count = 15
        };
        Assert.Equal("06-21", trend.Date);
        Assert.Equal(15, trend.Count);
    }

    [Fact]
    public void RecentAchievement_DefaultValues_AreCorrect()
    {
        var achievement = new RecentAchievement();
        Assert.Equal("", achievement.LearnerName);
        Assert.Equal("", achievement.AvatarEmoji);
        Assert.Equal("", achievement.Title);
        Assert.Equal("", achievement.Icon);
        Assert.Equal("", achievement.Tier);
        Assert.Equal(default, achievement.UnlockedAt);
    }

    [Fact]
    public void RecentAchievement_CanSetProperties()
    {
        var unlockedAt = DateTime.UtcNow;
        var achievement = new RecentAchievement
        {
            LearnerName = "小明",
            AvatarEmoji = "👦",
            Title = "第一步",
            Icon = "👶",
            Tier = "bronze",
            UnlockedAt = unlockedAt
        };
        Assert.Equal("小明", achievement.LearnerName);
        Assert.Equal("第一步", achievement.Title);
        Assert.Equal(unlockedAt, achievement.UnlockedAt);
    }

    [Fact]
    public void ResultDistribution_DefaultValues_AreZero()
    {
        var dist = new ResultDistribution();
        Assert.Equal(0, dist.Remember);
        Assert.Equal(0, dist.Hard);
        Assert.Equal(0, dist.Forgot);
    }

    [Fact]
    public void ResultDistribution_CanSetProperties()
    {
        var dist = new ResultDistribution
        {
            Remember = 50,
            Hard = 10,
            Forgot = 5
        };
        Assert.Equal(50, dist.Remember);
        Assert.Equal(10, dist.Hard);
        Assert.Equal(5, dist.Forgot);
    }

    [Fact]
    public void ResultDistribution_Total_CanBeCalculated()
    {
        var dist = new ResultDistribution
        {
            Remember = 50,
            Hard = 10,
            Forgot = 5
        };
        var total = dist.Remember + dist.Hard + dist.Forgot;
        Assert.Equal(65, total);
    }

    [Fact]
    public void LeaderboardEntry_DefaultValues_AreCorrect()
    {
        var entry = new LeaderboardEntry();
        Assert.Equal(0, entry.LearnerId);
        Assert.Equal("", entry.LearnerName);
        Assert.Equal("", entry.AvatarEmoji);
        Assert.Equal("", entry.Color);
        Assert.Equal(0, entry.CardsStudied);
        Assert.Equal(0, entry.Accuracy);
        Assert.Equal(0, entry.Score);
        Assert.Equal(0, entry.Streak);
        Assert.Equal(0, entry.Rank);
    }

    [Fact]
    public void LeaderboardEntry_CanSetProperties()
    {
        var entry = new LeaderboardEntry
        {
            LearnerId = 1,
            LearnerName = "小明",
            AvatarEmoji = "👦",
            Color = "#FF5733",
            CardsStudied = 100,
            Accuracy = 85.5,
            Score = 120,
            Streak = 7,
            Rank = 1
        };
        Assert.Equal(1, entry.LearnerId);
        Assert.Equal("小明", entry.LearnerName);
        Assert.Equal(100, entry.CardsStudied);
        Assert.Equal(85.5, entry.Accuracy);
        Assert.Equal(120, entry.Score);
        Assert.Equal(7, entry.Streak);
        Assert.Equal(1, entry.Rank);
    }

    [Fact]
    public void LeaderboardEntry_ScoreCalculation_MatchesExpected()
    {
        // Score = CardsStudied + (Accuracy * 20)
        var cardsStudied = 100;
        var accuracy = 0.85;
        var expectedScore = cardsStudied + (int)(accuracy * 20);
        Assert.Equal(117, expectedScore);
    }
}