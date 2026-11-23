using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Models;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

public partial class ChatCompletionsController
{
        private async Task<IActionResult> HandleStreamingAsync(
            AiProviderConfig provider, string model,
            List<ChatMessage> messages, ChatOptions options, CancellationToken ct)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            var id = $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            try
            {
                // 流式模式下 RAG 增强
                var (enrichedMessages, wasEnriched) = await _ragService.TryEnrichForStreamingAsync(messages, ct);
                if (wasEnriched)
                {
                    var searchChunk = new OpenAiChatStreamChunk
                    {
                        Id = id,
                        Created = created,
                        Model = $"{provider.Id}/{model}",
                        Choices = [new OpenAiStreamChoice
                        {
                            Index = 0,
                            Delta = new OpenAiDelta { Content = _loc["AiChat_RetrievingVault"] + "\n\n" },
                            FinishReason = null
                        }]
                    };
                    await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(searchChunk)}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
                messages = enrichedMessages;

                var client = _aiClientService.CreateChatClient(provider, model);
                await foreach (var chunk in client.GetStreamingResponseAsync(messages, options, ct))
                {
                    if (string.IsNullOrEmpty(chunk.Text)) continue;

                    var sseData = new OpenAiChatStreamChunk
                    {
                        Id = id,
                        Created = created,
                        Model = $"{provider.Id}/{model}",
                        Choices =
                        [
                            new OpenAiStreamChoice
                            {
                                Index = 0,
                                Delta = new OpenAiDelta { Content = chunk.Text },
                                FinishReason = null
                            }
                        ]
                    };

                    await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(sseData)}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }

                // 发送结束标记
                var doneChunk = new OpenAiChatStreamChunk
                {
                    Id = id,
                    Created = created,
                    Model = $"{provider.Id}/{model}",
                    Choices =
                    [
                        new OpenAiStreamChoice
                        {
                            Index = 0,
                            Delta = new OpenAiDelta(),
                            FinishReason = "stop"
                        }
                    ]
                };
                await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(doneChunk)}\n\n", ct);
                await Response.WriteAsync("data: [DONE]\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSE 流式响应失败");
                await Response.WriteAsync($"data: {{\"error\":\"{ex.Message.Replace("\"", "\\\"")}\"}}\n\n", ct);
            }

            return new EmptyResult();
        }

        /// <summary>
        /// 注入场景化系统提示
        /// </summary>
        private void InjectSystemPrompt(List<ChatMessage> messages)
        {
            if (messages.Any(m => m.Role == ChatRole.System)) return;

            var activeVault = _vaultSettings.GetActiveVault();
            var industry = activeVault?.Industry ?? "";
            var template = _scenePromptService.GetTemplateByName(industry);
            var systemPrompt = template.ChatSystemPrompt;

            if (activeVault != null)
            {
                systemPrompt += $"\n\n知识库：{activeVault.Name}。回答问题时请结合知识库内容。";
            }

            messages.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));
        }

        private static string GetDefaultModel(AiProviderConfig provider)
        {
            // 从 Models 列表找 IsMain
            var mainModel = provider.Models.FirstOrDefault(m => m.IsMain);
            if (mainModel != null) return mainModel.Name;

            return provider.Models.FirstOrDefault()?.Name ?? "default";
        }

}
