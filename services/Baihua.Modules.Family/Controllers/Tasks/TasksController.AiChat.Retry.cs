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
        private async Task<ActionResult<AiTaskResponse>> HandleRetryAiTaskAsync(string taskId, RetryAiTaskRequest? retryRequest)
        {
            var task = _taskManager.GetTask(taskId);
            if (task == null)
            {
                return NotFound(new { error = _loc["Task_NotFound"] });
            }
            if (task.Status != RunnerTaskStatus.Timeout && task.Status != RunnerTaskStatus.Failed && task.Status != RunnerTaskStatus.Cancelled)
            {
                return BadRequest(new { error = _loc["Task_RetryOnly"] });
            }
            if (task.Type != "ai_query" && task.Type != "ai_vault_generation" && task.Type != "anki_generate_ai")
            {
                return BadRequest(new { error = _loc["Task_RetryTypeNotSupported"] });
            }

            // 从原任务参数中提取信息
            var query = task.Parameters?.GetValueOrDefault("query") ?? task.Parameters?.GetValueOrDefault("keyword") ?? "";
            var industry = task.Parameters?.GetValueOrDefault("industry") ?? "";
            var saveToVault = task.Parameters?.GetValueOrDefault("saveToVault") == "True";
            var model = retryRequest?.Model ?? task.Parameters?.GetValueOrDefault("model") ?? "";
            var vaultId = task.Parameters?.GetValueOrDefault("vaultId") ?? "";
            var timeoutMinutes = retryRequest?.TimeoutMinutes > 0 ? retryRequest.TimeoutMinutes : _aiSettings.AiRequestTimeoutMinutes;

            if (task.Type == "ai_vault_generation")
            {
                return await HandleRetryVaultGenerationTaskAsync(task, retryRequest);
            }

            if (task.Type == "anki_generate_ai")
            {
                return await HandleRetryAnkiGenerateTaskAsync(task);
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(new { error = _loc["Task_RetryQueryMissing"] });
            }

            string modelName;
            if (!string.IsNullOrWhiteSpace(model))
            {
                modelName = model;
            }
            else
            {
                modelName = _aiSettings.AiModel;
            }

            var retryProvider = ResolveProvider(modelName);
            var retryVault = !string.IsNullOrWhiteSpace(vaultId)
                ? _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)
                : null;
            var retryVaultName = retryVault?.Name ?? "";
            
            // 如果原任务需要保存到知识库，但知识库已不存在，提前报错
            if (saveToVault && retryVault == null)
            {
                _logger.LogWarning("[RetryDebug] 重试任务失败：原知识库已不存在，vaultId={VaultId}", vaultId);
                return BadRequest(new { error = _loc["Task_RetryVaultMissing"] });
            }
            
            var retryParameters = new Dictionary<string, string>
            {
                ["query"] = query,
                ["saveToVault"] = saveToVault.ToString(),
                ["model"] = modelName,
                ["vaultId"] = vaultId,
                ["vaultName"] = retryVaultName,
                ["retriedFrom"] = taskId
            };
            if (retryProvider != null)
            {
                retryParameters["providerId"] = retryProvider.Id;
            }
            if (!string.IsNullOrWhiteSpace(industry))
            {
                retryParameters["industry"] = industry;
            }

            var retryScene = ResolveScene(industry, vaultId);

            // 创建新任务
            var newTaskId = _taskManager.CreateTask("ai_query", retryParameters);

            _ = Task.Run(async () =>
            {
                using var cts = _taskManager.CreateTaskCts(newTaskId, TimeSpan.FromMinutes(timeoutMinutes));
                try
                {
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Running);
                    await _taskManager.UpdateProgress(newTaskId, 1, 3, _loc["Task_Progress_RetryPreparing"]);

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var requestTime = DateTime.Now;
                    await _taskManager.UpdateProgress(newTaskId, 2, 3, _loc["Task_Progress_RetryCallingModel", modelName, timeoutMinutes]);
                    var aiResult = await CallAiApiAsync(query, modelName, cts.Token, scene: retryScene, industry: industry);
                    stopwatch.Stop();

                    var sourceInfo = $"> 📌 **来源**: AI 生成（重试）  \n" +
                        $"> 🤖 **模型**: {aiResult.Model}  \n" +
                        $"> 🏢 **提供商**: {aiResult.ProviderName}  \n" +
                        $"> ⏰ **时间**: {requestTime:yyyy-MM-dd HH:mm:ss}  \n" +
                        $"> ⏱️ **耗时**: {stopwatch.ElapsedMilliseconds}ms  \n\n";

                    var content = sourceInfo + aiResult.Content;
                    var title = query.Length > 50 ? query.Substring(0, 50) + "..." : query;

                    string? notePath = null;
                    if (saveToVault)
                    {
                        // 使用之前已验证过的 retryVault，避免再次查找失败
                        var vaultPath = retryVault?.Path;
                        if (string.IsNullOrEmpty(vaultPath))
                        {
                            await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed, _loc["Vault_Required"]);
                            return;
                        }

                        var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                        var aiDir = System.IO.Path.Combine(notesRoot, _loc["AiGeneratedDir"]);
                        System.IO.Directory.CreateDirectory(aiDir);

                        var fileName = $"{title}.md";
                        var fullPath = System.IO.Path.Combine(aiDir, fileName);
                        await System.IO.File.WriteAllTextAsync(fullPath, content);
                        notePath = $"{_loc["AiGeneratedDir"]}/{Path.GetFileNameWithoutExtension(fileName)}";

                        // 自动为该笔记生成 Anki 记忆卡片
                        try
                        {
                            var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                            var cardTaskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                            {
                                ["notePath"] = notePath,
                                ["vaultId"] = vaultId
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
                                    _logger.LogError(ex, "[Retry AI Task] 卡片生成失败");
                                    await _taskManager.UpdateStatus(cardTaskId, RunnerTaskStatus.Failed, error: ex.Message);
                                }
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[Retry AI Task] 自动触发卡片生成失败");
                            }
                        }

                        await _taskManager.UpdateProgress(newTaskId, 3, 3, _loc["Task_Progress_Done"]);
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Success, data: new
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
                        query = query,
                        totalElapsedMs = stopwatch.ElapsedMilliseconds,
                        retriedFrom = taskId
                    });
                }
                catch (OperationCanceledException)
                {
                    var currentTask = _taskManager.GetTask(newTaskId);
                    if (currentTask?.Status == RunnerTaskStatus.Cancelled)
                    {
                        _logger.LogInformation("AI 重试任务被用户取消：{TaskId}", newTaskId);
                    }
                    else
                    {
                        _logger.LogWarning("AI 重试任务超时：{TaskId}", newTaskId);
                        await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Timeout,
                            _loc["Task_RetryTimeout", timeoutMinutes, modelName]);
                    }
                }
                catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("index"))
                {
                    // OpenAI SDK 在解析阿里云内容审核响应时（choices为空）会崩溃
                    _logger.LogWarning(ex, "AI 重试任务触发内容审核：{TaskId}", newTaskId);
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed,
                        _loc["Task_ContentReviewFailed"]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI 重试任务失败：{TaskId}", newTaskId);
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed, ex.Message);
                }
                finally
                {
                    _taskManager.RemoveTaskCts(newTaskId);
                }
            });

            return Ok(new AiTaskResponse
            {
                Success = true,
                Message = _loc["Task_RetryCreated"],
                TaskId = newTaskId
            });
        }

        private async Task<ActionResult<AiTaskResponse>> HandleRetryAnkiGenerateTaskAsync(Baihua.Core.TaskInfo task)
        {
            var vaultId = task.Parameters?.GetValueOrDefault("vaultId") ?? "";
            if (string.IsNullOrWhiteSpace(vaultId))
            {
                return BadRequest(new AiTaskResponse { Success = false, Message = _loc["Task_RetryAnkiVaultIdMissing"] });
            }

            var newTaskId = _taskManager.CreateTask("anki_generate_ai", task.Parameters);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Running);
                    await _taskManager.UpdateProgress(newTaskId, 0, 100, _loc["Task_Progress_RetryCardGen"]);

                    var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
                    if (vault == null)
                    {
                        await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed, error: _loc["Vault_NotFound"]);
                        return;
                    }

                    var notesPath = System.IO.Path.Combine(vault.Path, "notes");
                    var result = await _cardGenerator.GenerateBatchWithAiAsync(notesPath, recursive: true, vaultId: vaultId, progressTaskId: newTaskId);
                    await _taskManager.UpdateProgress(newTaskId, 100, 100, result.Message);

                    if (result.Success && result.TotalCards > 0)
                        await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Success, data: new { totalCards = result.TotalCards, message = result.Message });
                    else
                        await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed, error: result.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Retry] AI 卡片生成任务失败：{TaskId}", newTaskId);
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed, error: ex.Message);
                }
            });

            return Ok(new AiTaskResponse { Success = true, Message = _loc["Task_RetryCardCreated"], TaskId = newTaskId });
        }

        private async Task<ActionResult<AiTaskResponse>> HandleRetryVaultGenerationTaskAsync(Baihua.Core.TaskInfo task, RetryAiTaskRequest? retryRequest)
        {
            var industry = task.Parameters?.GetValueOrDefault("industry") ?? "";
            var keyword = task.Parameters?.GetValueOrDefault("keyword") ?? "";
            var model = retryRequest?.Model ?? task.Parameters?.GetValueOrDefault("model") ?? "";

            if (string.IsNullOrWhiteSpace(industry) || string.IsNullOrWhiteSpace(keyword))
            {
                return BadRequest(new { error = _loc["Task_RetryParamsMissing"] });
            }

            var request = new VaultGenerationRequest
            {
                Industry = industry,
                Keyword = keyword,
                Model = model,
                NoteCount = 0
            };

            var result = await HandleCreateVaultGenerationTaskAsync(request);
            if (result.Result is OkObjectResult ok && ok.Value is VaultGenerationResponse vgr)
            {
                return Ok(new AiTaskResponse { Success = vgr.Success, Message = vgr.Message, TaskId = vgr.TaskId });
            }
            return result.Result != null
                ? new ActionResult<AiTaskResponse>(result.Result)
                : new ActionResult<AiTaskResponse>(new AiTaskResponse { Success = false, Message = _loc["Task_RetryFailed"] });
        }


    }
}
