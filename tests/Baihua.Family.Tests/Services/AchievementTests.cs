using Baihua.Modules.Family.Services;
using Baihua.Data;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class AchievementDefTests
{
    private static readonly IStringLocalizer<SharedResources> _loc = TestLocalizer.Instance;

    private static List<AchievementDef> GetDefinitions()
    {
        var dbFactory = Mock.Of<IDbContextFactory<FamilyDbContext>>();
        var logger = Mock.Of<ILogger<AchievementEngine>>();
        return new AchievementEngine(dbFactory, logger, _loc, Learning.FakeTimeProvider.Beijing20260807_0730()).Definitions;
    }

    [Fact]
    public void AchievementDef_RecordProperties_AreCorrect()
    {
        var def = new AchievementDef("test_key", "🏆", "Test Title", "Test Description", "gold", "study");

        Assert.Equal("test_key", def.Key);
        Assert.Equal("🏆", def.Icon);
        Assert.Equal("Test Title", def.Title);
        Assert.Equal("Test Description", def.Description);
        Assert.Equal("gold", def.Tier);
        Assert.Equal("study", def.Category);
    }

    [Fact]
    public void AchievementDef_Equality_Works()
    {
        var def1 = new AchievementDef("key", "🏆", "Title", "Desc", "gold", "study");
        var def2 = new AchievementDef("key", "🏆", "Title", "Desc", "gold", "study");
        var def3 = new AchievementDef("other", "🏆", "Title", "Desc", "gold", "study");

        Assert.Equal(def1, def2);
        Assert.NotEqual(def1, def3);
    }

    [Fact]
    public void AchievementEngine_Definitions_HasExpectedCount()
    {
        Assert.Equal(14, GetDefinitions().Count);
    }

    [Theory]
    [InlineData("first_step", "👶", "第一步")]
    [InlineData("streak_3", "🔥", "三日不断")]
    [InlineData("streak_7", "🔥", "周周坚持")]
    [InlineData("streak_30", "🔥", "月月不辍")]
    [InlineData("cards_10", "📚", "十题小试")]
    [InlineData("cards_50", "📚", "半百精进")]
    [InlineData("cards_100", "📚", "百题大关")]
    [InlineData("cards_500", "📚", "学富五车")]
    [InlineData("creator_1", "✏️", "初出茅庐")]
    [InlineData("creator_10", "✏️", "出题能手")]
    [InlineData("explorer_1", "🤖", "初识岐黄")]
    [InlineData("explorer_10", "🤖", "问道十次")]
    [InlineData("accuracy_80", "🎯", "百发百中")]
    [InlineData("early_bird", "🌅", "闻鸡起舞")]
    public void AchievementEngine_Definitions_ContainsExpectedAchievement(string key, string icon, string title)
    {
        var def = GetDefinitions().FirstOrDefault(d => d.Key == key);
        Assert.NotNull(def);
        Assert.Equal(icon, def.Icon);
        Assert.Equal(title, def.Title);
    }

    [Theory]
    [InlineData("first_step", "bronze")]
    [InlineData("streak_3", "bronze")]
    [InlineData("streak_7", "silver")]
    [InlineData("streak_30", "gold")]
    [InlineData("cards_10", "bronze")]
    [InlineData("cards_50", "silver")]
    [InlineData("cards_100", "gold")]
    [InlineData("cards_500", "diamond")]
    [InlineData("creator_1", "bronze")]
    [InlineData("creator_10", "silver")]
    [InlineData("explorer_1", "bronze")]
    [InlineData("explorer_10", "silver")]
    [InlineData("accuracy_80", "gold")]
    [InlineData("early_bird", "bronze")]
    public void AchievementEngine_Definitions_HasCorrectTier(string key, string expectedTier)
    {
        var def = GetDefinitions().FirstOrDefault(d => d.Key == key);
        Assert.NotNull(def);
        Assert.Equal(expectedTier, def.Tier);
    }

    [Theory]
    [InlineData("first_step", "study")]
    [InlineData("streak_3", "study")]
    [InlineData("cards_10", "study")]
    [InlineData("creator_1", "creation")]
    [InlineData("creator_10", "creation")]
    [InlineData("explorer_1", "exploration")]
    [InlineData("explorer_10", "exploration")]
    public void AchievementEngine_Definitions_HasCorrectCategory(string key, string expectedCategory)
    {
        var def = GetDefinitions().FirstOrDefault(d => d.Key == key);
        Assert.NotNull(def);
        Assert.Equal(expectedCategory, def.Category);
    }

    [Fact]
    public void AchievementEngine_Definitions_AllKeysAreUnique()
    {
        var keys = GetDefinitions().Select(d => d.Key).ToList();
        var uniqueKeys = keys.Distinct().ToList();
        Assert.Equal(keys.Count, uniqueKeys.Count);
    }
}

public class AchievementViewModelTests
{
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
            Key = "test_key",
            Title = "Test Title",
            Description = "Test Description",
            Icon = "🏆",
            Tier = "gold",
            Category = "study",
            IsUnlocked = true,
            UnlockedAt = unlockedAt
        };

        Assert.Equal("test_key", vm.Key);
        Assert.Equal("Test Title", vm.Title);
        Assert.Equal("Test Description", vm.Description);
        Assert.Equal("🏆", vm.Icon);
        Assert.Equal("gold", vm.Tier);
        Assert.Equal("study", vm.Category);
        Assert.True(vm.IsUnlocked);
        Assert.Equal(unlockedAt, vm.UnlockedAt);
    }
}
