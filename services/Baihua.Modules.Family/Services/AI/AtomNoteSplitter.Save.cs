using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using Baihua.AI.Provider;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Scene;

namespace Baihua.Modules.Family.Services;

public partial class AtomNoteSplitter
{
        private List<AiProviderConfig> ResolveProviders(IReadOnlyList<string>? aiProviderIds)
        {
            var rawIds = aiProviderIds?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList() ?? new List<string>();

            if (rawIds.Count == 0)
            {
                var main = _aiSettings.GetMainAiProvider();
                return main != null ? new List<AiProviderConfig> { main } : new List<AiProviderConfig>();
            }

            var ordered = new List<AiProviderConfig>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in rawIds)
            {
                if (!seen.Add(id))
                    continue;
                var p = _aiSettings.GetAiProvider(id);
                if (p != null)
                    ordered.Add(p);
                else
                    _logger.LogWarning("忽略未知的 AI 提供方 Id：{Id}", id);
            }

            return ordered;
        }

        private static List<(AiProviderConfig Provider, string Model, bool IsSplit)> BuildSteps(
            IReadOnlyList<AiProviderConfig> orderedProviders,
            IReadOnlyDictionary<string, List<string>>? aiModels = null)
        {
            var steps = new List<(AiProviderConfig Provider, string Model, bool IsSplit)>();
            bool splitAssigned = false;

            foreach (var p in orderedProviders)
            {
                var models = new List<string>();
                
                // 如果前端指定了该 provider 的模型列表，使用指定模型
                if (aiModels != null && aiModels.TryGetValue(p.Id, out var specifiedModels) && specifiedModels != null && specifiedModels.Count > 0)
                {
                    models.AddRange(specifiedModels);
                }
                else
                {
                    // 使用主模型（第一个）
                    var mainModel = p.GetMainModel();
                    if (string.IsNullOrEmpty(mainModel))
                        mainModel = "Qwen/Qwen2.5-14B-Instruct";
                    models.Add(mainModel);
                }

                // 为每个选中的模型创建一个步骤
                foreach (var model in models)
                {
                    var isSplit = !splitAssigned;
                    steps.Add((p, model, isSplit));
                    if (isSplit)
                        splitAssigned = true;
                }
            }

            return steps;
        }

        private async Task<string> CallAiApiAsync(
            string title,
            string content,
            string model,
            string providerId,
            bool isSupplement = false,
            List<Note>? existingNotes = null,
            CancellationToken cancellationToken = default,
            Baihua.Contracts.Scene.AppScene? scene = null)
        {
            string userPrompt = BuildUserPrompt(title, content, isSupplement, existingNotes);
            _logger.LogDebug("调用 AI API: provider={ProviderId}, model={Model}, 模式：{Mode}", providerId, model, isSupplement ? "补充" : "拆分");
            return await SendRequestWithRetryAsync(providerId, model, userPrompt, isSupplement, cancellationToken, scene);
        }

        // 新增重载，支持传入需要补充的缺失目标（relative path or raw link）
        private async Task<string> CallAiApiAsync(
            string title,
            string content,
            string model,
            string providerId,
            bool isSupplement,
            List<Note>? existingNotes,
            List<string>? missingTargets,
            CancellationToken cancellationToken = default,
            Baihua.Contracts.Scene.AppScene? scene = null)
        {
            if (missingTargets == null || missingTargets.Count == 0)
                return await CallAiApiAsync(title, content, model, providerId, isSupplement, existingNotes, cancellationToken, scene);

            var targetList = string.Join('\n', missingTargets.Select(t => $"- {t}"));
            var extra = $"\n\n【需要补充的缺失目标】\n{targetList}\n\n请只输出这些目标对应的笔记（JSON 数组），不要输出其他内容或解释。";

            string userPrompt = BuildUserPrompt(title, content, isSupplement, existingNotes) + extra;
            _logger.LogDebug("调用 AI 补充接口，目标数：{Count}", missingTargets.Count);
            return await SendRequestWithRetryAsync(providerId, model, userPrompt, isSupplement: true, cancellationToken: cancellationToken, scene);
        }

        private string BuildUserPrompt(string title, string content, bool isSupplement, List<Note>? existingNotes)
        {
            var template = _scenePromptService.GetTemplate();

            if (isSupplement && existingNotes != null && existingNotes.Count > 0)
            {
                var existingPaths = string.Join("\n", existingNotes.Select(n => $"- {n.Path} ({n.Title})"));
                return template.SupplementUserPrompt
                    .Replace("{title}", title)
                    .Replace("{existingNotes}", existingPaths)
                    .Replace("{content}", content);
            }
            else
            {
                return template.SplitUserPrompt
                    .Replace("{title}", title)
                    .Replace("{content}", content);
            }
        }

        private async Task<List<Note>> SaveNotes(List<Note> notes, string? overrideNotesPath = null)
        {
            var savedNotes = new List<Note>();
            var notesPath = overrideNotesPath ?? _vaultSettings.NotesPath;
            if (string.IsNullOrEmpty(notesPath)) return savedNotes;

            Directory.CreateDirectory(notesPath);

            var directoriesToCreate = new HashSet<string>();
            foreach (var note in notes)
            {
                var fullPath = Path.Combine(notesPath, note.Path + ".md");
                var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"Cannot get directory: {fullPath}");
                directoriesToCreate.Add(directory);
            }

            foreach (var directory in directoriesToCreate)
            {
                Directory.CreateDirectory(directory);
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
            var savedNotesSync = new System.Collections.Concurrent.ConcurrentBag<Note>();

            await Parallel.ForEachAsync(notes, options, async (note, ct) =>
            {
                try
                {
                    var fullPath = Path.Combine(notesPath, note.Path + ".md");
                    var frontmatter = $"---\nai_generated: true\ngenerated_at: {DateTimeOffset.UtcNow:O}\n---\n";
                    await File.WriteAllTextAsync(fullPath, frontmatter + note.Content, ct);
                    savedNotesSync.Add(note);
                    _logger.LogDebug("保存笔记：{Path}", note.Path);

                    try
                    {
                        var vaultPath = Path.GetDirectoryName(notesPath);
                        var cardsRoot = string.IsNullOrEmpty(vaultPath) ? "" : System.IO.Path.Combine(vaultPath, "cards");
                        var vaultId = _vaultSettings.GetVaults().FirstOrDefault(v => v.Path == vaultPath)?.Id ?? "";
                        var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                        {
                            ["notePath"] = note.Path,
                            ["vaultId"] = vaultId
                        });
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);
                                var result = await _cardGenerator.GenerateWithAiAsync(note.Path, cardsPath: cardsRoot, notesBasePath: notesPath);
                                await _taskManager.UpdateStatus(taskId, result.Success ? RunnerTaskStatus.Success : RunnerTaskStatus.Failed,
                                    data: new { message = result.Message, cardCount = result.CardCount });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[Split] 卡片生成失败：{Path}", note.Path);
                                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: ex.Message);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Split] 自动触发卡片生成失败：{Path}", note.Path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "保存笔记失败：{Path}", note.Path);
                }
            });

            savedNotes.AddRange(savedNotesSync);
            return savedNotes;
        }

        private string GetSystemPrompt(Baihua.Contracts.Scene.AppScene? scene = null)
        {
            return _scenePromptService.GetTemplate(scene).SplitSystemPrompt;
        }
}
