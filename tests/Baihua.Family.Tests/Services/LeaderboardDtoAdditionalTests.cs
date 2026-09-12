using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class LeaderboardDtoAdditionalTests
{
    [Fact]
    public void DashboardData_Defaults_EmptyLists()
    {
        var data = new DashboardData();

        Assert.NotNull(data.FamilyStats);
        Assert.Empty(data.FamilyStats);
        Assert.NotNull(data.WeeklyTrend);
        Assert.Empty(data.WeeklyTrend);
        Assert.NotNull(data.RecentAchievements);
        Assert.Empty(data.RecentAchievements);
        Assert.NotNull(data.ResultDistribution);
    }

    [Fact]
    public void ResultDistribution_Defaults_AllZero()
    {
        var dist = new ResultDistribution();

        Assert.Equal(0, dist.Remember);
        Assert.Equal(0, dist.Hard);
        Assert.Equal(0, dist.Forgot);
    }

    [Fact]
    public void FamilyMemberStat_Defaults_ZeroValues()
    {
        var stat = new FamilyMemberStat();

        Assert.Equal(0, stat.LearnerId);
        Assert.Equal("", stat.Name);
        Assert.Equal("", stat.AvatarEmoji);
        Assert.Equal("", stat.Color);
        Assert.Equal(0, stat.WeekTotal);
        Assert.Equal(0.0, stat.Accuracy);
        Assert.Equal(0, stat.Streak);
        Assert.Equal(0, stat.TotalCards);
    }

    [Fact]
    public void DailyTrend_Defaults_EmptyDateZeroCount()
    {
        var trend = new DailyTrend();

        Assert.Equal("", trend.Date);
        Assert.Equal(0, trend.Count);
    }

    [Fact]
    public void RecentAchievement_Defaults_EmptyStringsDefaultDate()
    {
        var recent = new RecentAchievement();

        Assert.Equal("", recent.LearnerName);
        Assert.Equal("", recent.AvatarEmoji);
        Assert.Equal("", recent.Title);
        Assert.Equal("", recent.Icon);
        Assert.Equal("", recent.Tier);
        Assert.Equal(default(DateTime), recent.UnlockedAt);
    }

    [Fact]
    public void LeaderboardEntry_Defaults_ZeroValues()
    {
        var entry = new LeaderboardEntry();

        Assert.Equal(0, entry.LearnerId);
        Assert.Equal("", entry.LearnerName);
        Assert.Equal("", entry.AvatarEmoji);
        Assert.Equal("", entry.Color);
        Assert.Equal(0, entry.CardsStudied);
        Assert.Equal(0.0, entry.Accuracy);
        Assert.Equal(0, entry.Score);
        Assert.Equal(0, entry.Streak);
        Assert.Equal(0, entry.Rank);
    }

    [Fact]
    public void LeaderboardEntry_SetAllProperties_StoresValues()
    {
        var entry = new LeaderboardEntry
        {
            LearnerId = 5,
            LearnerName = "Alice",
            AvatarEmoji = "👧",
            Color = "#ff6b6b",
            CardsStudied = 100,
            Accuracy = 0.85,
            Score = 850,
            Streak = 7,
            Rank = 1
        };

        Assert.Equal(5, entry.LearnerId);
        Assert.Equal("Alice", entry.LearnerName);
        Assert.Equal("👧", entry.AvatarEmoji);
        Assert.Equal("#ff6b6b", entry.Color);
        Assert.Equal(100, entry.CardsStudied);
        Assert.Equal(0.85, entry.Accuracy);
        Assert.Equal(850, entry.Score);
        Assert.Equal(7, entry.Streak);
        Assert.Equal(1, entry.Rank);
    }

    [Fact]
    public void DashboardData_CanAssignAllCollections()
    {
        var stat = new FamilyMemberStat { Name = "Bob" };
        var trend = new DailyTrend { Date = "2024-01-15", Count = 10 };
        var recent = new RecentAchievement { Title = "First Lesson" };
        var dist = new ResultDistribution { Remember = 5, Hard = 2, Forgot = 1 };

        var data = new DashboardData
        {
            FamilyStats = new List<FamilyMemberStat> { stat },
            WeeklyTrend = new List<DailyTrend> { trend },
            RecentAchievements = new List<RecentAchievement> { recent },
            ResultDistribution = dist
        };

        Assert.Single(data.FamilyStats);
        Assert.Equal("Bob", data.FamilyStats[0].Name);
        Assert.Single(data.WeeklyTrend);
        Assert.Equal(10, data.WeeklyTrend[0].Count);
        Assert.Single(data.RecentAchievements);
        Assert.Equal("First Lesson", data.RecentAchievements[0].Title);
        Assert.Equal(5, data.ResultDistribution.Remember);
    }
}
