using Baihua.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Ai;
using Baihua.Modules.Family.Models;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers
{
    public partial class AIController
    {
        [HttpPost("chat")]
        public async Task<ActionResult<Baihua.Contracts.Ai.ChatResponse>> Chat([FromBody] ChatRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { error = _loc["AiChat_MessageEmpty"] });
            }

            try
            {
                var (provider, model) = ResolveProviderAndModel(request.ProviderId, request.Model);

                // 数字助理：采集用户聊天输入
                _activityService.Record("chat", request.Message);

                // 构建消息列表（使用三层记忆系统）
                var messages = await BuildMessagesWithMemoryAsync(
                    request.History, provider.Id, model, request.Message, request.SessionId, HttpContext.RequestAborted);
                // RAG 增强
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await CallAiApiAsync(messages, model, provider.Id, enableTools: request.EnableTools ?? true, ct: HttpContext.RequestAborted);
                stopwatch.Stop();

                var sourceInfo = _loc["Ai_Chat_SourceInfo", model, provider.Name, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), stopwatch.ElapsedMilliseconds] + "\n\n";

                return Ok(new Baihua.Contracts.Ai.ChatResponse
                {
                    Success = true,
                    Message = _loc["Ai_Chat_ReplySuccess"],
                    Reply = sourceInfo + result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 聊天失败");
                return Ok(new Baihua.Contracts.Ai.ChatResponse
                {
                    Success = false,
                    Message = _loc["Ai_Chat_Failed", ex.Message]
                });
            }
        }
    }
}
