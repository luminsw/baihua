using Baihua.Core.Models;
using Baihua.Core.Services;
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
        private async Task<ActionResult<VaultGenerationResponse>> HandleCreateVaultGenerationTaskAsync(VaultGenerationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Industry) || string.IsNullOrWhiteSpace(request.Keyword))
            {
                return BadRequest(new { error = _loc["Task_IndustryKeywordEmpty"] });
            }

            var noteCount = request.NoteCount;
            if (noteCount < 1 || noteCount > 50)
                noteCount = 30;

            // 详细度档位（简洁/适中/详细）：控制篇数范围与每篇篇幅，替代死板的固定数量。
            // 未显式指定时跟随全局设置（默认最简洁；编程任务除外）
            var detailLevel = Baihua.Contracts.Tasks.VaultGenDetail.Normalize(
                string.IsNullOrWhiteSpace(request.DetailLevel) ? _aiDetailSettings.GetDetailLevel() : request.DetailLevel);
            var (countHint, lengthHint, maxNotes) = Baihua.Contracts.Tasks.VaultGenDetail.Describe(detailLevel);

            try
            {
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
                if (provider == null)
                {
                    return BadRequest(new { error = _loc["Task_VaultGenAiProviderMissing"] });
                }

                var parameters = new Dictionary<string, string>
                {
                    ["industry"] = request.Industry,
                    ["keyword"] = request.Keyword,
                    ["model"] = modelName,
                    ["noteCount"] = noteCount.ToString(),
                    ["detailLevel"] = detailLevel,
                    ["providerId"] = provider.Id,
                };

                var taskId = _taskManager.CreateTask("ai_vault_generation", parameters);

                _ = Task.Run(async () =>
                {
                    using var cts = _taskManager.CreateTaskCts(taskId, null); // 不设超时，用户通过进度条感知进度
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.ApplicationStopping, cts.Token);

                    var totalSteps = 4 + maxNotes; // 按档位上限估算进度（AI 在范围内自主决定篇数）
                    var currentStep = 0;

                    try
                    {
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);

                        var options = Baihua.Core.Services.AiClientService.BuildChatOptions(temperature: 0.7f, maxOutputTokens: 4000);

                        // Step 1: 生成知识库名称
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, _loc["Task_Progress_VaultName"]);
                        var vaultName = await GenerateVaultNameAsync(provider, modelName, request.Industry, request.Keyword, options, linkedCts.Token);

                        // Step 2: 生成 system prompt
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, _loc["Task_Progress_SystemPrompt"]);
                        var systemPrompt = await GenerateSystemPromptAsync(provider, modelName, request.Industry, options, linkedCts.Token);

                        // Step 3: 生成笔记列表
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, _loc["Task_Progress_Outline"]);
                        var outline = await GenerateNoteListAsync(provider, modelName, vaultName, request.Industry, request.Keyword, systemPrompt, countHint, options, linkedCts.Token);

                        if (outline.Count == 0)
                        {
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, _loc["Task_OutlineFailed"]);
                            return;
                        }

                        // Step 4: 创建知识库
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, _loc["Task_Progress_CreatingVault", vaultName]);
                        var vault = _vaultSettings.AddVault(vaultName, "", request.Industry);
                        var vaultId = vault.Id;
                        var vaultPath = vault.Path;

                        // Ensure notes directory exists
                        var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                        System.IO.Directory.CreateDirectory(notesRoot);

                        // Step 5+: 逐条生成笔记内容
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var generatedNotes = new List<(string title, string path)>();

                        for (int i = 0; i < outline.Count; i++)
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();
                            currentStep++;
                            var item = outline[i];
                            await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, _loc["Task_Progress_GeneratingNote", i + 1, outline.Count, item.title]);

                            try
                            {
                                var content = await GenerateNoteContentAsync(
                                    provider!, modelName, item.title, item.category, vaultName, systemPrompt, lengthHint, options, linkedCts.Token);

                                var safeTitle = item.title.Replace("\\", "_").Replace("/", "_").Replace(":", "_")
                                    .Replace("*", "_").Replace("?", "_").Replace("\"", "_").Replace("<", "_")
                                    .Replace(">", "_").Replace("|", "_");
                                var categoryDir = System.IO.Path.Combine(notesRoot, item.category);
                                System.IO.Directory.CreateDirectory(categoryDir);
                                var noteFilePath = System.IO.Path.Combine(categoryDir, $"{safeTitle}.md");
                                var frontmatter = $"---\nai_generated: true\nai_provider: {provider?.Name ?? ""}\nai_model: {modelName}\ngenerated_at: {DateTimeOffset.UtcNow:O}\n---\n";
                                await System.IO.File.WriteAllTextAsync(noteFilePath, frontmatter + content, linkedCts.Token);
                                var notePath = $"{item.category}/{safeTitle}";
                                generatedNotes.Add((item.title, notePath));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[AiVaultGeneration] 笔记 \"{Title}\" 生成失败，跳过", item.title);
                            }
                        }

                        stopwatch.Stop();

                        // 重建 FTS5 索引
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, _loc["Task_Progress_RebuildIndex"]);
                        await _vaultNoteIndexer.IndexVaultAsync(vaultId, vaultPath, linkedCts.Token);

                        await _taskManager.UpdateProgress(taskId, totalSteps, totalSteps, _loc["Task_Progress_Done"]);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: new
                        {
                            vaultId = vaultId,
                            vaultName = vaultName,
                            industry = request.Industry,
                            noteCount = generatedNotes.Count,
                            notes = generatedNotes.Select(n => new { title = n.title, path = n.path }).ToArray(),
                            model = modelName,
                            providerName = provider?.Name ?? "",
                            totalElapsedMs = stopwatch.ElapsedMilliseconds
                        });

                        if (request.GenerateCards && !string.IsNullOrEmpty(vaultId))
                        {
                            var cardTaskId = _taskManager.CreateTask("anki_generate_ai", new Dictionary<string, string>
                            {
                                ["vaultId"] = vaultId,
                                ["vaultName"] = vaultName,
                                ["trigger"] = "vault_generation"
                            });
                            await _taskManager.UpdateProgress(cardTaskId, 0, 100, _loc["Task_Progress_StartCardGen", vaultName]);

                            var notesPath = Path.Combine(vaultPath, "notes");
                            if (Directory.Exists(notesPath))
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var result = await _cardGenerator.GenerateBatchWithAiAsync(notesPath, recursive: true, vaultId: vaultId);
                                        await _taskManager.UpdateProgress(cardTaskId, 100, 100, result.Message);
                                        await _taskManager.UpdateStatus(cardTaskId, RunnerTaskStatus.Success, data: new { totalCards = result.TotalCards, message = result.Message });
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[VaultGen] 卡片生成失败: {TaskId}", cardTaskId);
                                        await _taskManager.UpdateStatus(cardTaskId, RunnerTaskStatus.Failed, error: ex.Message);
                                    }
                                });
                            }
                        }

                    }
                    catch (OperationCanceledException)
                    {
                        var currentTask = _taskManager.GetTask(taskId);
                        if (currentTask?.Status == RunnerTaskStatus.Cancelled)
                        {
                            _logger.LogInformation("AI 知识库生成任务被用户取消：{TaskId}", taskId);
                        }
                        else
                        {
                            _logger.LogWarning("AI 知识库生成任务超时：{TaskId}", taskId);
                            var timeoutMin = _aiSettings.AiRequestTimeoutMinutes * 4;
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Timeout,
                                _loc["Task_VaultGenTimeout", timeoutMin, modelName]);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AI 知识库生成任务失败：{TaskId}", taskId);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, ex.Message);
                    }
                    finally
                    {
                        _taskManager.RemoveTaskCts(taskId);
                    }
                });

                return Ok(new VaultGenerationResponse
                {
                    Success = true,
                    Message = _loc["Task_Created"],
                    TaskId = taskId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 AI 知识库生成任务失败");
                return Ok(new VaultGenerationResponse
                {
                    Success = false,
                    Message = _loc["Task_CreateFailed", ex.Message]
                });
            }
        }

        private async Task<string> GenerateVaultNameAsync(
            AiProviderConfig provider, string model, string industry, string keyword,
            ChatOptions options, CancellationToken ct)
        {
            var prompt = $"你是知识库命名专家。请为\"{industry}\"领域的\"{keyword}\"生成一个简短、准确、有吸引力的中文知识库名称（2-8个字）。只返回名称本身，不要有任何解释、标点或书名号。";
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "你只输出名称，不要任何额外内容。"),
                new(ChatRole.User, prompt)
            };
            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_name");
            var name = (response.Text ?? "").Trim()
                .Replace("\"", "").Replace("'", "").Replace("「", "").Replace("」", "")
                .Replace("《", "").Replace("》", "").Replace("\n", "").Replace("\r", "");
            if (name.Length > 20) name = name.Substring(0, 20);
            if (string.IsNullOrWhiteSpace(name)) name = $"{industry}知识库";
            return name;
        }

        private async Task<string> GenerateSystemPromptAsync(
            AiProviderConfig provider, string model, string industry,
            ChatOptions options, CancellationToken ct)
        {
            var prompt = $"""
                你是一位专业的系统提示词工程师。你的任务是为"{industry}"行业生成一个系统提示词，该提示词将用于指导 AI 生成该领域的「原子笔记」。

                原子笔记必须严格遵循以下原则：
                1. 一个笔记 = 一个核心概念，聚焦单一主题，绝不展开多个主题
                2. 内容高度结构化，拒绝冗长描述和背景铺垫
                3. 每篇笔记必须包含：核心定义（1-3句话）、关键要点（3-5条）、关联概念（1-2个）、记忆锚点（口诀/歌诀/类比）、典型场景/案例
                4. 使用 Markdown 格式输出
                5. 语言专业、准确、客观，使用行业标准术语

                请直接返回生成的系统提示词内容，不要有任何额外说明。
                """;
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "你只输出提示词内容，不要任何额外内容。"),
                new(ChatRole.User, prompt)
            };
            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_prompt");
            var promptText = (response.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(promptText))
                promptText = _loc["Task_FallbackPrompt", industry];
            return promptText;
        }

        private async Task<List<NoteOutlineItem>> GenerateNoteListAsync(
            AiProviderConfig provider, string model, string vaultName, string industry, string keyword,
            string systemPrompt, string countHint, ChatOptions options, CancellationToken ct)
        {
            var prompt = $"{systemPrompt}\n\n请为知识库\"{vaultName}\"（{industry}-{keyword}）生成一份覆盖核心知识点的大纲，笔记数量控制在 {countHint}（AI 按主题复杂度在范围内自主决定，不要超出）。每条笔记包含：title（标题，简洁专业）、category（分类，2-4字）。\n\n要求：\n1. 覆盖{keyword}的核心知识点，由浅入深\n2. 标题要具体，避免过于笼统\n3. 分类要合理，同一知识库内分类不宜超过5个\n4. 必须严格返回 JSON 数组格式，不要加 markdown 代码块标记\n\n格式示例：\n[{{\"title\": \"示例标题\", \"category\": \"示例分类\"}}]";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "你是一个严格的 JSON 生成器，只输出合法的 JSON 数组，不添加任何额外文字或 markdown 标记。"),
                new(ChatRole.User, prompt)
            };

            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_outline");
            var raw = response.Text ?? "";

            // 尝试从代码块中提取 JSON
            var jsonStr = raw;
            var codeBlock = System.Text.RegularExpressions.Regex.Match(raw, @"```(?:json)?\s*([\s\S]*?)```");
            if (codeBlock.Success) jsonStr = codeBlock.Groups[1].Value;

            try
            {
                var outline = JsonSerializer.Deserialize<List<NoteOutlineItem>>(jsonStr, JsonHelper.CaseInsensitive);
                if (outline == null || outline.Count == 0) throw new Exception(_loc["Task_OutlineParsedEmpty"]);
                return outline.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiVaultGeneration] 大纲 JSON 解析失败，尝试 fallback 解析");
                // Fallback: 从文本中逐行提取 title 和 category
                var fallback = new List<NoteOutlineItem>();
                var lines = raw.Split('\n').Where(l => l.Contains("\"title\"")).ToList();
                foreach (var line in lines)
                {
                    var titleMatch = System.Text.RegularExpressions.Regex.Match(line, @"""title""\s*:\s*""([^""]+)""");
                    var catMatch = System.Text.RegularExpressions.Regex.Match(line, @"""category""\s*:\s*""([^""]+)""");
                    if (titleMatch.Success)
                    {
                        fallback.Add(new NoteOutlineItem
                        {
                            title = titleMatch.Groups[1].Value,
                            category = catMatch.Success ? catMatch.Groups[1].Value : _loc["Task_CategoryOther"]
                        });
                    }
                }
                return fallback.ToList();
            }
        }

        private async Task<string> GenerateNoteContentAsync(
            AiProviderConfig provider, string model, string title, string category, string vaultName,
            string systemPrompt, string lengthHint, ChatOptions options, CancellationToken ct)
        {
            var prompt = $"""
                {systemPrompt}

                请严格遵循「原子笔记」原则生成该笔记的 Markdown 内容：
                知识库：{vaultName}
                分类：{category}
                标题：{title}
                篇幅要求：{lengthHint}

                1. **聚焦单一主题**：只讨论"{title}"这一个核心概念，不展开关联概念
                2. **高度结构化**：必须包含以下部分（按顺序）：
                   - 核心定义（1-3句话精确定义）
                   - 关键要点（3-5条最核心的知识点，用列表）
                   - 关联概念（1-2个直接关联的其他概念，仅名称）
                   - 记忆锚点（1个简短的口诀、歌诀或类比，帮助记忆）
                   - 典型场景/案例（1个真实或典型的应用示例）
                3. **无冗余**：不讨论历史沿革、文化背景、个人经验
                4. **语言风格**：专业、清晰、客观、中立

                请直接返回 Markdown 格式的笔记内容，不要有任何额外说明。
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, prompt)
            };

            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_content");
            return response.Text ?? "";
        }

        private class NoteOutlineItem
        {
            public string title { get; set; } = "";
            public string category { get; set; } = "";
        }
    }
}
