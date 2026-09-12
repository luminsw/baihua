using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Baihua.Contracts.Ai;
using Baihua.Contracts.Master;
using Baihua.Modules.Family.Controllers.AI.Stages;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 师父阶段推进逻辑
/// </summary>
public partial class MasterController
{
    /// <summary>
    /// 阶段完成 — 生成摘要、祝福语、纠正点，推进到下一阶段。
    /// 各个阶段的行为通过 <see cref="StageStrategyFactory"/> 中的策略类定制。
    /// </summary>
    [HttpPost("{id}/stage-complete")]
    public async Task<ActionResult<StageCompleteResponse>> StageComplete(string id, [FromBody] StageCompleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new StageCompleteResponse { Success = false, Message = _loc["Master_IdRequired"] });

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var master = await db.Masters.FirstOrDefaultAsync(m => m.MasterId == id);
            if (master == null)
                return NotFound(new StageCompleteResponse { Success = false, Message = _loc["Master_NotFound"] });

            // 使用策略模式获取阶段信息
            var stageStrategy = _stageStrategyFactory.GetStrategy(request.StageName);
            if (stageStrategy == null)
                return BadRequest(new StageCompleteResponse { Success = false, Message = string.Format(_loc["Master_StageUnknown"], request.StageName) });

            var nextStrategy = _stageStrategyFactory.GetNextStrategy(request.StageName);
            var nextStageName = nextStrategy?.StageName ?? "";

            var (provider, model) = ResolveProviderAndModel(null, null);

            // AI 生成阶段摘要
            var summaryPrompt = string.Format(_loc["Master_StageSummaryPrompt"], request.StageName);
            var summaryMessages = new List<ChatMessage>
            {
                new(ChatRole.System, _loc["Master_StageSummarySystem"]),
                new(ChatRole.User, summaryPrompt)
            };

            var options = AiClientService.BuildChatOptions(temperature: 0.3f, maxOutputTokens: 500);
            var summaryResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                provider, model, summaryMessages, options, HttpContext.RequestAborted, operation: "master-stage-summary");

            var summary = summaryResponse.Text ?? "";

            // 使用策略获取祝福语
            var blessing = stageStrategy.GetBlessing(master.MasterName);

            // AI 生成纠正点
            var correctionsPrompt = string.Format(_loc["Master_StageCorrectionsPrompt"], request.StageName);
            var correctionsMessages = new List<ChatMessage>
            {
                new(ChatRole.System, _loc["Master_StageCorrectionsSystem"]),
                new(ChatRole.User, correctionsPrompt)
            };
            var correctionsResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                provider, model, correctionsMessages, options, HttpContext.RequestAborted, operation: "master-stage-corrections");
            var keyCorrections = correctionsResponse.Text ?? "";

            // 更新 Master 的阶段信息
            var graduated = System.Text.Json.JsonSerializer.Deserialize<List<string>>(master.GraduatedStagesJson) ?? new();
            if (!graduated.Contains(request.StageName))
                graduated.Add(request.StageName);
            master.GraduatedStagesJson = System.Text.Json.JsonSerializer.Serialize(graduated);
            master.CurrentStage = string.IsNullOrEmpty(nextStageName) ? master.CurrentStage : nextStageName;

            db.StageSummaries.Add(new StageSummary
            {
                MasterId = id,
                StageName = request.StageName,
                Summary = summary
            });

            await db.SaveChangesAsync();

            // 过渡到下一阶段时重置 vault focus 状态
            if (!string.IsNullOrEmpty(nextStageName))
            {
                var focusedVaults = await db.VaultFocusStates
                    .Where(v => v.MasterId == id && v.State == "focused")
                    .ToListAsync();
                foreach (var v in focusedVaults)
                {
                    v.State = "archived";
                    v.UpdatedAt = DateTime.UtcNow;
                }
                var discoveredVaults = await db.VaultFocusStates
                    .Where(v => v.MasterId == id && v.State == "discovered" && v.StageName == nextStageName)
                    .ToListAsync();
                foreach (var v in discoveredVaults)
                {
                    v.State = "focused";
                    v.UpdatedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();
            }

            _logger.LogInformation("师父 {MasterId} 阶段 {Stage} 完成，下一阶段：{Next}", id, request.StageName, nextStageName);

            return Ok(new StageCompleteResponse
            {
                Success = true,
                Message = string.Format(_loc["Master_StageComplete"], request.StageName),
                NextStage = nextStageName,
                Summary = summary,
                Blessing = blessing,
                KeyCorrections = keyCorrections
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "阶段完成处理失败");
            return StatusCode(500, new StageCompleteResponse { Success = false, Message = string.Format(_loc["Master_StageCompleteFailed"], ex.Message) });
        }
    }
}
