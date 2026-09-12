using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Achievements;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 成就与赛舟榜 API
/// </summary>
[ApiController]
[Route("api/achievements")]
public partial class AchievementsController : ControllerBase
{
    private readonly LearnerService _learnerService;
    private readonly AchievementEngine _achievementEngine;
    private readonly LeaderboardService _leaderboardService;
    private readonly LeaderboardSettingsService _leaderboardSettings;
    private readonly IStringLocalizer<SharedResources> _loc;

    public AchievementsController(
        LearnerService learnerService,
        AchievementEngine achievementEngine,
        LeaderboardService leaderboardService,
        LeaderboardSettingsService leaderboardSettings,
        IStringLocalizer<SharedResources> loc)
    {
        _learnerService = learnerService;
        _achievementEngine = achievementEngine;
        _leaderboardService = leaderboardService;
        _leaderboardSettings = leaderboardSettings;
        _loc = loc;
    }

    // ---- 学习者管理 ----

    [HttpGet("learners")]
    public async Task<ActionResult<List<LearnerDto>>> GetLearners()
    {
        var learners = await _learnerService.GetAllAsync();
        return Ok(learners.Select(l => new LearnerDto
        {
            Id = l.Id,
            Name = l.Name,
            AvatarEmoji = l.AvatarEmoji,
            Color = l.Color,
            IsDefault = l.IsDefault
        }).ToList());
    }

    [HttpPost("learners")]
    public async Task<ActionResult<LearnerDto>> CreateLearner([FromBody] CreateLearnerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = _loc["Achievement_NameRequired"] });

        try
        {
            var learner = await _learnerService.CreateAsync(request.Name.Trim(), request.AvatarEmoji ?? "👤", request.Color ?? "#007bff");
            return Ok(new LearnerDto
            {
                Id = learner.Id,
                Name = learner.Name,
                AvatarEmoji = learner.AvatarEmoji,
                Color = learner.Color,
                IsDefault = learner.IsDefault
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("learners/{id}/default")]
    public async Task<ActionResult> SetDefaultLearner(int id)
    {
        await _learnerService.SetDefaultAsync(id);
        return Ok(new { success = true });
    }

    [HttpDelete("learners/{id}")]
    public async Task<ActionResult> DeleteLearner(int id)
    {
        var success = await _learnerService.DeleteAsync(id);
        if (!success) return NotFound();
        return Ok(new { success = true });
    }

    // ---- 成就 ----

    [HttpGet]
    public async Task<ActionResult<List<AchievementDto>>> GetAchievements([FromQuery] int learnerId)
    {
        var achievements = await _achievementEngine.GetAchievementsAsync(learnerId);
        return Ok(achievements.Select(a => new AchievementDto
        {
            Key = a.Key,
            Title = a.Title,
            Description = a.Description,
            Icon = a.Icon,
            Tier = a.Tier,
            Category = a.Category,
            IsUnlocked = a.IsUnlocked,
            UnlockedAt = a.UnlockedAt
        }).ToList());
    }

    [HttpPost("check")]
    public async Task<ActionResult<List<AchievementDto>>> CheckAchievements([FromQuery] int learnerId)
    {
        var newlyUnlocked = await _achievementEngine.CheckAndUnlockAsync(learnerId);
        return Ok(newlyUnlocked.Select(a => new AchievementDto
        {
            Key = a.Key,
            Title = a.Title,
            Description = a.Description,
            Icon = a.Icon,
            Tier = a.Tier,
            Category = a.Category,
            IsUnlocked = true,
            UnlockedAt = DateTime.UtcNow
        }).ToList());
    }

    // ---- 赛舟榜 ----

    [HttpGet("leaderboard/weekly")]
    public async Task<ActionResult<List<LeaderboardEntryDto>>> GetWeeklyLeaderboard([FromQuery] string? vaultId = null)
    {
        var entries = await _leaderboardService.GetWeeklyLeaderboardAsync(vaultId);
        return Ok(ToDtos(entries));
    }

    [HttpGet("leaderboard/monthly")]
    public async Task<ActionResult<List<LeaderboardEntryDto>>> GetMonthlyLeaderboard([FromQuery] string? vaultId = null)
    {
        var entries = await _leaderboardService.GetMonthlyLeaderboardAsync(vaultId);
        return Ok(ToDtos(entries));
    }

    [HttpGet("leaderboard/alltime")]
    public async Task<ActionResult<List<LeaderboardEntryDto>>> GetAllTimeLeaderboard([FromQuery] string? vaultId = null)
    {
        var entries = await _leaderboardService.GetAllTimeLeaderboardAsync(vaultId);
        return Ok(ToDtos(entries));
    }

    [HttpGet("leaderboard/streak")]
    public async Task<ActionResult<List<LeaderboardEntryDto>>> GetStreakLeaderboard()
    {
        var entries = await _leaderboardService.GetStreakLeaderboardAsync();
        return Ok(ToDtos(entries));
    }

    [HttpGet("leaderboard/accuracy")]
    public async Task<ActionResult<List<LeaderboardEntryDto>>> GetAccuracyLeaderboard([FromQuery] string? vaultId = null)
    {
        var entries = await _leaderboardService.GetAccuracyLeaderboardAsync(vaultId);
        return Ok(ToDtos(entries));
    }

    // ---- FAM-22 排行榜友好化 ----

    /// <summary>和自己比：本周 vs 上周（AC1/AC2）</summary>
    [HttpGet("leaderboard/compare")]
    public async Task<ActionResult<WeeklyCompareResultDto>> GetWeeklyCompare([FromQuery] string? vaultId = null, [FromQuery] int? learnerId = null)
    {
        var result = await _leaderboardService.GetWeeklyCompareAsync(vaultId, learnerId);
        return Ok(new WeeklyCompareResultDto
        {
            WeekTotal = result.WeekTotal,
            LastWeekTotal = result.LastWeekTotal,
            Delta = result.Delta,
            Percent = result.Percent,
            Arrow = result.Arrow
        });
    }

    /// <summary>角色分组排行榜：孩子榜/大人榜（AC3）</summary>
    [HttpGet("leaderboard/role")]
    public async Task<ActionResult<List<LeaderboardEntryDto>>> GetRoleLeaderboard([FromQuery] string role, [FromQuery] string? vaultId = null)
    {
        var entries = await _leaderboardService.GetRoleLeaderboardAsync(role, vaultId);
        return Ok(ToDtos(entries));
    }

    /// <summary>全家排行开关（AC4/AC5：默认关闭）</summary>
    [HttpGet("leaderboard/settings/all-family-tab")]
    public ActionResult<LeaderboardSettingsDto> GetAllFamilyTabSetting()
        => Ok(new LeaderboardSettingsDto { AllFamilyTabEnabled = _leaderboardSettings.IsAllFamilyTabEnabled() });

    /// <summary>设置全家排行开关（AC4：家长可启用/禁用）</summary>
    [HttpPost("leaderboard/settings/all-family-tab")]
    public ActionResult<LeaderboardSettingsDto> SetAllFamilyTabSetting([FromBody] LeaderboardSettingsDto request)
    {
        _leaderboardSettings.SetAllFamilyTabEnabled(request.AllFamilyTabEnabled);
        return Ok(new LeaderboardSettingsDto { AllFamilyTabEnabled = _leaderboardSettings.IsAllFamilyTabEnabled() });
    }

    /// <summary>
    /// 家长看板数据（FAM-20：支持成员筛选 learnerId）
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardDataDto>> GetDashboard([FromQuery] string? vaultId = null, [FromQuery] int? learnerId = null)
        => Ok(await HandleGetDashboardAsync(vaultId, learnerId));
}
