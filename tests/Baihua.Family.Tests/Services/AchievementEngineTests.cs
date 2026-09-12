using Baihua.Modules.Family.Services;
using Baihua.Data;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class AchievementEngineTests
{
    private static readonly IStringLocalizer<SharedResources> _loc = TestLocalizer.Instance;

    private static List<AchievementDef> GetDefinitions()
    {
        var dbFactory = Mock.Of<IDbContextFactory<FamilyDbContext>>();
        var logger = Mock.Of<ILogger<AchievementEngine>>();
        return new AchievementEngine(dbFactory, logger, _loc, Learning.FakeTimeProvider.Beijing20260807_0730()).Definitions;
    }

    [Fact]
    public void Definitions_HasExpectedCount()
    {
        Assert.Equal(14, GetDefinitions().Count);
    }

    [Fact]
    public void Definitions_AllHaveValidKeys()
    {
        foreach (var def in GetDefinitions())
        {
            Assert.False(string.IsNullOrEmpty(def.Key));
            Assert.Contains('_', def.Key); // Keys follow pattern like "first_step", "streak_7"
        }
    }

    [Fact]
    public void Definitions_AllHaveValidIcons()
    {
        foreach (var def in GetDefinitions())
        {
            Assert.False(string.IsNullOrEmpty(def.Icon));
            Assert.True(def.Icon.Length <= 2); // Emoji icons are typically 1-2 characters
        }
    }

    [Fact]
    public void Definitions_AllHaveValidTitles()
    {
        foreach (var def in GetDefinitions())
        {
            Assert.False(string.IsNullOrEmpty(def.Title));
        }
    }

    [Fact]
    public void Definitions_AllHaveValidDescriptions()
    {
        foreach (var def in GetDefinitions())
        {
            Assert.False(string.IsNullOrEmpty(def.Description));
        }
    }

    [Fact]
    public void Definitions_AllHaveValidTiers()
    {
        var validTiers = new[] { "bronze", "silver", "gold", "diamond" };
        foreach (var def in GetDefinitions())
        {
            Assert.Contains(def.Tier, validTiers);
        }
    }

    [Fact]
    public void Definitions_AllHaveValidCategories()
    {
        var validCategories = new[] { "study", "creation", "exploration" };
        foreach (var def in GetDefinitions())
        {
            Assert.Contains(def.Category, validCategories);
        }
    }

    [Fact]
    public void Definitions_HasFirstStepAchievement()
    {
        var firstStep = GetDefinitions().FirstOrDefault(d => d.Key == "first_step");
        Assert.NotNull(firstStep);
        Assert.Equal("第一步", firstStep.Title);
        Assert.Equal("bronze", firstStep.Tier);
    }

    [Fact]
    public void Definitions_HasStreakAchievements()
    {
        var streakAchievements = GetDefinitions().Where(d => d.Key.StartsWith("streak_")).ToList();
        Assert.Equal(3, streakAchievements.Count);
        Assert.Contains(streakAchievements, a => a.Key == "streak_3");
        Assert.Contains(streakAchievements, a => a.Key == "streak_7");
        Assert.Contains(streakAchievements, a => a.Key == "streak_30");
    }

    [Fact]
    public void Definitions_HasCardsAchievements()
    {
        var cardsAchievements = GetDefinitions().Where(d => d.Key.StartsWith("cards_")).ToList();
        Assert.Equal(4, cardsAchievements.Count);
        Assert.Contains(cardsAchievements, a => a.Key == "cards_10");
        Assert.Contains(cardsAchievements, a => a.Key == "cards_50");
        Assert.Contains(cardsAchievements, a => a.Key == "cards_100");
        Assert.Contains(cardsAchievements, a => a.Key == "cards_500");
    }

    [Fact]
    public void Definitions_HasCreatorAchievements()
    {
        var creatorAchievements = GetDefinitions().Where(d => d.Key.StartsWith("creator_")).ToList();
        Assert.Equal(2, creatorAchievements.Count);
        Assert.Contains(creatorAchievements, a => a.Key == "creator_1");
        Assert.Contains(creatorAchievements, a => a.Key == "creator_10");
    }

    [Fact]
    public void Definitions_HasExplorerAchievements()
    {
        var explorerAchievements = GetDefinitions().Where(d => d.Key.StartsWith("explorer_")).ToList();
        Assert.Equal(2, explorerAchievements.Count);
        Assert.Contains(explorerAchievements, a => a.Key == "explorer_1");
        Assert.Contains(explorerAchievements, a => a.Key == "explorer_10");
    }

    [Fact]
    public void Definitions_KeysAreUnique()
    {
        var keys = GetDefinitions().Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void AchievementDef_Record_CanBeCreated()
    {
        var def = new AchievementDef("test_key", "🎯", "Test Title", "Test Description", "bronze", "test");
        Assert.Equal("test_key", def.Key);
        Assert.Equal("🎯", def.Icon);
        Assert.Equal("Test Title", def.Title);
        Assert.Equal("Test Description", def.Description);
        Assert.Equal("bronze", def.Tier);
        Assert.Equal("test", def.Category);
    }

    [Fact]
    public void AchievementViewModel_DefaultValues_AreEmpty()
    {
        var vm = new AchievementViewModel();
        Assert.Equal("", vm.Key);
        Assert.Equal("", vm.Title);
        Assert.Equal("", vm.Description);
        Assert.Equal("", vm.Icon);
        Assert.Equal("", vm.Tier);
        Assert.Equal("", vm.Category);
        Assert.False(vm.IsUnlocked);
        Assert.Null(vm.UnlockedAt);
    }

    [Fact]
    public void AchievementViewModel_CanSetProperties()
    {
        var unlockedAt = DateTime.UtcNow;
        var vm = new AchievementViewModel
        {
            Key = "first_step",
            Title = "第一步",
            Description = "完成首次卡片学习",
            Icon = "👶",
            Tier = "bronze",
            Category = "study",
            IsUnlocked = true,
            UnlockedAt = unlockedAt
        };
        Assert.Equal("first_step", vm.Key);
        Assert.True(vm.IsUnlocked);
        Assert.Equal(unlockedAt, vm.UnlockedAt);
    }
}
