using Baihua.Core;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using System.Text.Json;
using Baihua.AI.Provider;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Scene;
using Baihua.Contracts.Tasks;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Family.Controllers
{
    public partial class TasksController : ControllerBase
    {
        private async Task<ActionResult<AiTaskResponse>> HandleCreateAiTaskAsync(AiTaskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest(new { error = _loc["Task_QueryEmpty"] });
            }

            try
            {
                // 优先使用用户指定的模型，否则使用配置的主模型
                string modelName;
                if (!string.IsNullOrWhiteSpace(request.Model))
                {
                    modelName = request.Model.Trim();
                }
                else
                {
                    modelName = _aiSettings.AiModel;
                }

                var provider = ResolveProvider(modelName);

                var vault = !string.IsNullOrWhiteSpace(request.VaultId)
                    ? _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)
                    : null;
                var vaultName = vault?.Name ?? "";
                
                // 如果要保存到知识库，但知识库不存在，提前报错
                if (request.SaveToVault && vault == null)
                {
                    _logger.LogWarning("创建 AI 任务失败：知识库不存在，vaultId={VaultId}", request.VaultId ?? "(null)");
                    return BadRequest(new { error = _loc["Task_VaultNotFoundForCreate"] });
                }
                
                var parameters = new Dictionary<string, string>
                {
                    ["query"] = request.Query,
                    ["saveToVault"] = request.SaveToVault.ToString(),
                    ["model"] = modelName,
                    ["vaultId"] = request.VaultId ?? "",
                    ["vaultName"] = vaultName
                };
                if (provider != null)
                {
                    parameters["providerId"] = provider.Id;
                }
                if (!string.IsNullOrWhiteSpace(request.Industry))
                {
                    parameters["industry"] = request.Industry;
                }

                var scene = ResolveScene(request.Industry, request.VaultId);
                var taskId = _taskManager.CreateTask("ai_query", parameters);

                _ = Task.Run(async () =>
                {
                    using var cts = _taskManager.CreateTaskCts(taskId, TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes));
                    try
                    {
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);
                        await _taskManager.UpdateProgress(taskId, 1, 3, _loc["Task_Progress_Preparing"]);

                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var requestTime = DateTime.Now;
                        await _taskManager.UpdateProgress(taskId, 2, 3, _loc["Task_Progress_CallingModel", modelName]);

                        var aiResult = await CallAiApiAsync(request.Query, modelName, cts.Token, request.SystemPrompt, scene, request.Industry);
                        stopwatch.Stop();

                        var sourceInfo = $"> 📌 **来源**: AI 生成  \n" +
                            $"> 🤖 **模型**: {aiResult.Model}  \n" +
                            $"> 🏢 **提供商**: {aiResult.ProviderName}  \n" +
                            $"> ⏰ **时间**: {requestTime:yyyy-MM-dd HH:mm:ss}  \n" +
                            $"> ⏱️ **耗时**: {stopwatch.ElapsedMilliseconds}ms  \n\n";
                        
                        var content = sourceInfo + aiResult.Content;
                        var title = request.Query.Length > 50 ? request.Query.Substring(0, 50) + "..." : request.Query;
                        
                        string? notePath = null;
                        string? fullPath = null;
                        // AI 生成笔记统一写入 notes/ 子目录，便于后续 /vault/read/{path} 读取
                        if (request.SaveToVault)
                        {
                            var vault = await FindVaultWithRetryAsync(request.VaultId ?? "");
                            var vaultPath = vault?.Path;
                            if (string.IsNullOrEmpty(vaultPath))
                            {
                                _logger.LogError("AI 任务找不到知识库: VaultId={VaultId}, 可用知识库 IDs={AvailableVaultIds}",
                                    request.VaultId ?? "(null)", string.Join(", ", _vaultSettings.GetVaults().Select(v => v.Id)));
                                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, _loc["Vault_Required"]);
                                return;
                            }

                            var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                            var aiDir = System.IO.Path.Combine(notesRoot, _loc["AiGeneratedDir"]);
                            System.IO.Directory.CreateDirectory(aiDir);

                            var fileName = $"{title}.md";
                            fullPath = System.IO.Path.Combine(aiDir, fileName);
                            await System.IO.File.WriteAllTextAsync(fullPath, content);
                            notePath = $"{_loc["AiGeneratedDir"]}/{Path.GetFileNameWithoutExtension(fileName)}";

                            // 自动为该笔记生成 Anki 记忆卡片
                            try
                            {
                                var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                                var cardTaskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                                {
                                    ["notePath"] = notePath,
                                    ["vaultId"] = request.VaultId ?? ""
                                });
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await _taskManager.UpdateStatus(cardTaskId, RunnerTaskStatus.Running);
                                        var result = await _cardGenerator.GenerateWithAiAsync(notePath, cardsPath: cardsRoot, notesBasePath: notesRoot);
                                        await _taskManager.UpdateStatus(cardTaskId, result.Success ? RunnerTaskStatus.Success : RunnerTaskStatus.Failed,
                                            data: new { message = result.Message, cardCount = result.CardCount });
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[AI Task] 卡片生成失败");
                                        await _taskManager.UpdateStatus(cardTaskId, RunnerTaskStatus.Failed, error: ex.Message);
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[AI Task] 自动触发卡片生成失败");
                            }
                        }

                        await _taskManager.UpdateProgress(taskId, 3, 3, _loc["Task_Progress_Done"]);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: new
                        {
                            notes = new[] { new { title = title, path = notePath ?? "" } },
                            requests = new[]
                            {
                                new
                                {
                                    providerId = aiResult.ProviderId,
                                    providerName = aiResult.ProviderName,
                                    model = aiResult.Model,
                                    endpoint = aiResult.Endpoint,
                                    elapsedMs = stopwatch.ElapsedMilliseconds,
                                    timestamp = requestTime
                                }
                            },
                            query = request.Query,
                            totalElapsedMs = stopwatch.ElapsedMilliseconds
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        var currentTask = _taskManager.GetTask(taskId);
                        if (currentTask?.Status == RunnerTaskStatus.Cancelled)
                        {
                            _logger.LogInformation("AI 查询任务被用户取消：{TaskId}", taskId);
                        }
                        else
                        {
                            _logger.LogWarning("AI 查询任务超时：{TaskId}", taskId);
                            var timeoutMin = _aiSettings.AiRequestTimeoutMinutes;
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Timeout,
                                _loc["Task_QueryTimeout", timeoutMin, modelName, TruncateForError(request.Query, 100)]);
                        }
                    }
                    catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("index"))
                    {
                        // OpenAI SDK 在解析阿里云内容审核响应时（choices为空）会崩溃
                        _logger.LogWarning(ex, "AI 查询任务触发内容审核：{TaskId}", taskId);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed,
                            _loc["Task_ContentReviewFailed"]);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AI 查询任务失败：{TaskId}", taskId);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, ex.Message);
                    }
                    finally
                    {
                        _taskManager.RemoveTaskCts(taskId);
                    }
                });

                return Ok(new AiTaskResponse
                {
                    Success = true,
                    Message = _loc["Task_Created"],
                    TaskId = taskId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 AI 任务失败");
                return Ok(new AiTaskResponse
                {
                    Success = false,
                    Message = _loc["Task_CreateFailed", ex.Message]
                });
            }
        }
    }
}
