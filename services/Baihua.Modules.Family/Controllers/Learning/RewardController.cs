using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Baihua.Contracts.Achievements;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// FAM-31 成就贴纸墙 + 家庭奖励 API
/// </summary>
[ApiController]
[Route("api/rewards")]
public class RewardController : ControllerBase
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly RewardService _rewardService;

    public RewardController(IDbContextFactory<FamilyDbContext> dbFactory, RewardService rewardService)
    {
        _dbFactory = dbFactory;
        _rewardService = rewardService;
    }

    /// <summary>获取全部奖励配置</summary>
    [HttpGet]
    public async Task<ActionResult<List<RewardConfigDto>>> GetRewards()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var rewards = await db.FamilyRewards.OrderBy(r => r.Id).ToListAsync();
        return Ok(rewards.Select(r => new RewardConfigDto
        {
            RewardId = r.Id,
            ConditionType = r.ConditionType,
            TargetValue = r.TargetValue,
            RewardName = r.RewardName,
            RewardIcon = r.RewardIcon
        }).ToList());
    }

    /// <summary>创建奖励配置（FAM-31-AC3：家长自定义奖励）</summary>
    [HttpPost]
    public async Task<ActionResult<RewardConfigDto>> CreateReward([FromBody] CreateRewardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RewardName) || request.TargetValue <= 0)
            return BadRequest(new { error = "奖励名称不能为空，目标值必须大于 0" });

        using var db = await _dbFactory.CreateDbContextAsync();
        var reward = new FamilyReward
        {
            ConditionType = string.IsNullOrWhiteSpace(request.ConditionType) ? "streak_days" : request.ConditionType,
            TargetValue = request.TargetValue,
            RewardName = request.RewardName.Trim(),
            RewardIcon = string.IsNullOrWhiteSpace(request.RewardIcon) ? "🎁" : request.RewardIcon
        };
        db.FamilyRewards.Add(reward);
        await db.SaveChangesAsync();

        return Ok(new RewardConfigDto
        {
            RewardId = reward.Id,
            ConditionType = reward.ConditionType,
            TargetValue = reward.TargetValue,
            RewardName = reward.RewardName,
            RewardIcon = reward.RewardIcon
        });
    }

    /// <summary>奖励进度（FAM-31-AC3 孩子视角进度条）</summary>
    [HttpGet("progress")]
    public async Task<ActionResult<List<RewardProgressDto>>> GetProgress([FromQuery] string? vaultId = null)
    {
        var progress = await _rewardService.GetRewardProgressAsync(vaultId);
        return Ok(progress);
    }

    /// <summary>检查并触发达成（FAM-31-AC4：每条件一次，去重）</summary>
    [HttpPost("trigger")]
    public async Task<ActionResult<List<RewardClaimDto>>> Trigger([FromQuery] string? vaultId = null)
    {
        var claims = await _rewardService.CheckAndTriggerAsync(vaultId);
        return Ok(claims);
    }
}
