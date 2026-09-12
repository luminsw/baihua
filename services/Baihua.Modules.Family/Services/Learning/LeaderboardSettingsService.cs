namespace Baihua.Modules.Family.Services;

/// <summary>
/// FAM-22 排行榜设置：全家排行 Tab 开关。
/// 新用户默认关闭（AC5）。进程内持久化（DI 单例），后续可迁移到 FamilyDbContext 设置表。
/// </summary>
public class LeaderboardSettingsService
{
    private bool _allFamilyTabEnabled;

    /// <summary>全家排行 Tab 是否开启（AC5：新用户默认 false）</summary>
    public bool IsAllFamilyTabEnabled() => _allFamilyTabEnabled;

    /// <summary>设置全家排行 Tab 开关（AC4：家长可在设置中启用/禁用）</summary>
    public void SetAllFamilyTabEnabled(bool enabled) => _allFamilyTabEnabled = enabled;
}
