using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Baihua.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Onboarding;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// Onboarding 与初始化任务 API
/// </summary>
[ApiController]
[Route("api/onboarding")]
public partial class OnboardingController : ControllerBase
{
    private readonly FamilyDbContext _dbContext;
    private readonly VaultSettingsService _vaultSettings;
    private readonly DeviceService _deviceService;
    private readonly IAiConfigService _aiConfigService;
    private readonly ILogger<OnboardingController> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public OnboardingController(
        FamilyDbContext dbContext,
        VaultSettingsService vaultSettings,
        DeviceService deviceService,
        IAiConfigService aiConfigService,
        ILogger<OnboardingController> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _dbContext = dbContext;
        _vaultSettings = vaultSettings;
        _deviceService = deviceService;
        _aiConfigService = aiConfigService;
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 获取 Onboarding 状态
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<OnboardingStatusDto>> GetStatus()
    {
        var state = await _dbContext.OnboardingStates.FirstOrDefaultAsync();
        var hasAiConfig = _aiConfigService.GetProviders().Any();

        return Ok(new OnboardingStatusDto
        {
            IsOnboardingCompleted = state?.IsCompleted ?? false,
            HasAiConfig = hasAiConfig,
            CompletedAt = state?.CompletedAt
        });
    }

    /// <summary>
    /// 完成 Onboarding
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete()
    {
        var state = await _dbContext.OnboardingStates.FirstOrDefaultAsync();
        if (state == null)
        {
            state = new OnboardingState();
            _dbContext.OnboardingStates.Add(state);
        }

        state.IsCompleted = true;
        state.CompletedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        // 确保初始化任务记录已创建
        await EnsureInitTasksCreatedAsync();

        return Ok(new { success = true });
    }
    /// <summary>
    /// 重置 Onboarding（调试用）
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        var state = await _dbContext.OnboardingStates.FirstOrDefaultAsync();
        if (state != null)
        {
            state.IsCompleted = false;
            state.CompletedAt = null;
        }

        var progresses = await _dbContext.InitTaskProgresses.ToListAsync();
        foreach (var p in progresses)
        {
            p.IsCompleted = false;
            p.IsSkipped = false;
            p.CompletedAt = null;
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Onboarding 已重置");
        return Ok(new { success = true });
    }
}
