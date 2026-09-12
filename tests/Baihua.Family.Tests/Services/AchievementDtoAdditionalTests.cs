using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class AchievementDtoAdditionalTests
{
    [Fact]
    public void AchievementDef_Constructor_SetsAllProperties()
    {
        var def = new AchievementDef("k", "🏆", "Title", "Desc", "Gold", "Study");

        Assert.Equal("k", def.Key);
        Assert.Equal("🏆", def.Icon);
        Assert.Equal("Title", def.Title);
        Assert.Equal("Desc", def.Description);
        Assert.Equal("Gold", def.Tier);
        Assert.Equal("Study", def.Category);
    }

    [Fact]
    public void AchievementViewModel_Defaults_EmptyStringsFalseNull()
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
    public void AchievementViewModel_SetProperties_StoresValues()
    {
        var unlockedAt = new DateTime(2024, 1, 15, 10, 30, 0);
        var vm = new AchievementViewModel
        {
            Key = "first_lesson",
            Title = "First Lesson",
            Description = "Complete your first lesson",
            Icon = "📚",
            Tier = "Bronze",
            Category = "Study",
            IsUnlocked = true,
            UnlockedAt = unlockedAt
        };

        Assert.Equal("first_lesson", vm.Key);
        Assert.Equal("First Lesson", vm.Title);
        Assert.Equal("Complete your first lesson", vm.Description);
        Assert.Equal("📚", vm.Icon);
        Assert.Equal("Bronze", vm.Tier);
        Assert.Equal("Study", vm.Category);
        Assert.True(vm.IsUnlocked);
        Assert.Equal(unlockedAt, vm.UnlockedAt);
    }
}
