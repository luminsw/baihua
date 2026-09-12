using Baihua.Core;
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
        /// <summary>
        /// AI 问答 - 生成一篇笔记
        /// </summary>
        [HttpPost("ask")]
        public async Task<ActionResult<AiNoteResponse>> Ask([FromBody] AskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest(new { error = _loc["Ai_Ask_QueryEmpty"] });
            }

            try
            {
                var (provider, model) = ResolveProviderAndModel(request.ProviderId, request.Model);

                // RAG 增强：检索知识库上下文
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, GetSystemPrompt(provider.Id)),
                    new(ChatRole.User, request.Query)
                };
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await CallAiApiAsync(messages, model, provider.Id, enableTools: request.EnableTools ?? false);
                stopwatch.Stop();

                var note = ParseNote(result, request.Query);
                
                // 添加来源信息到笔记开头
                var sourceInfo = _loc["Ai_Ask_SourceInfo", model, provider.Name, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), stopwatch.ElapsedMilliseconds] + "\n\n";
                
                note.Content = sourceInfo + note.Content;

                // 如果选择保存到知识库
                string? vaultPath = null;
                if (!string.IsNullOrWhiteSpace(request.VaultId))
                {
                    vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)?.Path;
                }
                if (string.IsNullOrWhiteSpace(vaultPath) && !string.IsNullOrWhiteSpace(request.VaultPath))
                {
                    vaultPath = request.VaultPath;
                }
                if (request.SaveToVault && !string.IsNullOrEmpty(vaultPath))
                {
                    await SaveNote(note, vaultPath, provider?.Name, model);

                    // 自动为该笔记生成 Anki 记忆卡片
                    try
                    {
                        var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                        var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                        var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId);
                        var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                        {
                            ["notePath"] = note.FilePath,
                            ["vaultId"] = request.VaultId ?? "",
                            ["vaultName"] = vault?.Name ?? ""
                        });
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);
                                var result = await _cardGenerator.GenerateWithAiAsync(note.FilePath, cardsPath: cardsRoot, notesBasePath: notesRoot);
                                await _taskManager.UpdateStatus(taskId, result.Success ? RunnerTaskStatus.Success : RunnerTaskStatus.Failed,
                                    data: new { message = result.Message, cardCount = result.CardCount });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[AI Ask] 卡片生成失败");
                                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: ex.Message);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AI Ask] 自动触发卡片生成失败");
                    }
                }

                return Ok(new AiNoteResponse
                {
                    Success = true,
                    Message = _loc["Ai_Ask_Success"],
                    Title = note.Title,
                    Content = note.Content,
                    NoteId = GenerateNoteId(note.Title)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 查询失败");
                return Ok(new AiNoteResponse
                {
                    Success = false,
                    Message = _loc["Ai_Ask_Failed", ex.Message]
                });
            }
        }

    }
}
