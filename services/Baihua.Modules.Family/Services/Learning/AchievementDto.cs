namespace Baihua.Modules.Family.Services;

public record AchievementDef(string Key, string Icon, string Title, string Description, string Tier, string Category);

public class AchievementViewModel
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsUnlocked { get; set; }
    public DateTime? UnlockedAt { get; set; }
}
