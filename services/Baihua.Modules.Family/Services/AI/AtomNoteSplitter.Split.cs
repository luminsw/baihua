using Baihua.Core;
using Baihua.Core.Localization;
using Microsoft.Extensions.Localization;
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

        public async Task SplitAtomNotes(
            string taskId,
            string title,
            string content,
            bool useChain,
            IReadOnlyList<string>? aiProviderIds = null,
            IReadOnlyDictionary<string, List<string>>? aiModels = null,
            string? vaultPath = null,
            CancellationToken cancellationToken = default,
            Baihua.Contracts.Scene.AppScene? scene = null)
        {
            try
            {
                await _taskManager.UpdateProgress(taskId, 0, 1, _loc["AtomNote_PrepareAiConfig"]);
                _logger.LogInformation("SplitAtomNotes 开始: taskId={TaskId}, vaultPath={VaultPath}, useChain={UseChain}, scene={Scene}", taskId, vaultPath ?? "(null)", useChain, scene?.ToString() ?? "默认");

                var orderedProviders = ResolveProviders(aiProviderIds);
                if (orderedProviders.Count == 0)
                    throw new Exception(_loc["AtomNote_NoProviderConfigured"]);

                var steps = BuildSteps(orderedProviders, aiModels);

                // 链式调用：全局首步拆分，之后可选补充（跨提供方、跨模型）
                List<Note> allNotes = new List<Note>();
                string originalContent = content;
                var modelErrors = new List<string>();
                int totalSteps = steps.Count + 2; // 模型执行 + 解析 + 保存

                // 记录每次 AI 请求的详情
                var requestRecords = new List<object>();
                var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < steps.Count; i++)
                {
                    var (provider, model, isSplitStep) = steps[i];
                    var displayName = string.IsNullOrWhiteSpace(provider.Name) ? provider.Id : provider.Name;

                    var phase = isSplitStep ? _loc["AtomNote_SplitPhase"] : _loc["AtomNote_SupplementPhase"];
                    await _taskManager.UpdateProgress(taskId, i + 1, totalSteps,
                        _loc["AtomNote_StepProgress", i + 1, totalSteps, phase, displayName, model]);

                    _logger.LogInformation("调用 {Provider} 模型 {Index}/{Total}: {Model} (模式：{Mode})",
                        provider.Id, i + 1, steps.Count, model, isSplitStep ? "拆分" : "补充");

                    if (string.IsNullOrEmpty(_aiSettings.GetApiKeyForProvider(provider.Id)))
                        _logger.LogWarning(
                            "提供方 {ProviderId} 未配置 API Key",
                            provider.Id);

                    try
                    {
                        string result;
                        var stepStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        if (isSplitStep)
                        {
                            result = await CallAiApiAsync(title, originalContent, model, provider.Id, isSupplement: false, cancellationToken: cancellationToken, scene: scene);

                            var firstRoundNotes = _noteParser.ParseResult(result);
                            allNotes.AddRange(firstRoundNotes);

                            _logger.LogInformation("首轮拆分得到 {Count} 条笔记", firstRoundNotes.Count);
                        }
                        else if (useChain)
                        {
                            result = await CallAiApiAsync(title, originalContent, model, provider.Id, isSupplement: true, existingNotes: allNotes, cancellationToken: cancellationToken, scene: scene);

                            var supplementNotes = _noteParser.ParseResult(result);

                            var existingPaths = allNotes.Select(n => n.Path).ToHashSet();
                            foreach (var note in supplementNotes)
                            {
                                if (!existingPaths.Contains(note.Path))
                                {
                                    allNotes.Add(note);
                                    existingPaths.Add(note.Path);
                                    _logger.LogInformation("补充新笔记：{Path}", note.Path);
                                }
                            }
                        }
                        stepStopwatch.Stop();
                        requestRecords.Add(new
                        {
                            providerId = provider.Id,
                            providerName = displayName,
                            model,
                            phase = isSplitStep ? _loc["AtomNote_Split"] : _loc["AtomNote_Supplement"],
                            elapsedMs = stepStopwatch.ElapsedMilliseconds,
                            success = true,
                            timestamp = DateTime.Now
                        });
                    }
                    catch (Exception ex)
                    {
                        var mode = isSplitStep ? _loc["AtomNote_Split"] : _loc["AtomNote_Supplement"];
                        var err = _loc["AtomNote_StepFailed", displayName, model, mode, ex.Message];
                        modelErrors.Add(err);
                        _logger.LogWarning(ex, err);
                        requestRecords.Add(new
                        {
                            providerId = provider.Id,
                            providerName = displayName,
                            model,
                            phase = mode,
                            elapsedMs = 0,
                            success = false,
                            error = ex.Message,
                            timestamp = DateTime.Now
                        });
                        continue;
                    }
                }

                if (allNotes.Count == 0)
                {
                    var errorText = modelErrors.Count > 0
                        ? _loc["AtomNote_AllModelsFailed", string.Join(" | ", modelErrors.Take(3))]
                        : _loc["AtomNote_NoNotesProduced"];
                    throw new Exception(errorText);
                }

                await _taskManager.UpdateProgress(taskId, steps.Count + 1, totalSteps, _loc["AtomNote_ParsingResults"]);

                await _taskManager.UpdateProgress(taskId, steps.Count + 2, totalSteps, _loc["AtomNote_SavingNotes", allNotes.Count]);

                // 拆分完成后：修复 wikilink 在 AI 拆分后丢失分类目录的问题。
                // 规则：若 wikilink 形如 [[桂枝汤]]（target 不包含 '/'），且 target 与某条生成笔记的 title 唯一匹配，
                // 则补全为 [[分类/标题]]（保留 #header 与 |alias）。
                
                // 预计算并缓存所有笔记的规范化路径
                var notePathMap = new Dictionary<Note, string>();
                foreach (var note in allNotes)
                {
                    notePathMap[note] = NormalizeRelPath(note.Path);
                }
                var existingNormalizedPaths = notePathMap.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                
                // 构建 title->path 映射
                var titleToPath = WikiLinkResolver.BuildTitleToPathMap(allNotes, notePathMap);

                // 在保存前，检查是否有 AI 引用但未生成/本地不存在的 wikilink 目标，若有则循环向 AI 请求补全，直到补齐或达到上限
                var notesPath = string.IsNullOrEmpty(vaultPath) ? _vaultSettings.NotesPath : Path.Combine(vaultPath, "notes");

                // 限制补充轮次上限，防止 AI 每轮返回新笔记但持续引入新 wikilink 导致无限循环
                const int maxSupplementRounds = 5;
                int round = 0;
                while (round < maxSupplementRounds)
                {
                    round++;

                    // 重新构建 title->path 映射（基于当前已知笔记）
                    titleToPath = WikiLinkResolver.BuildTitleToPathMap(allNotes, notePathMap);

                    // 找到所有被引用的链接目标（解析 [[...]]），并映射为相对路径（若能解析）
                    var referenced = WikiLinkResolver.ExtractWikiLinkTargets(allNotes);
                    var resolvedReferencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var unresolvedRaw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rawLink in referenced)
                    {
                        var resolved = WikiLinkResolver.ResolveLinkToPath(rawLink, titleToPath);
                        if (!string.IsNullOrEmpty(resolved)) resolvedReferencedPaths.Add(NormalizeRelPath(resolved));
                        else unresolvedRaw.Add(rawLink);
                    }

                    // 汇总需要补充的目标：未在内存 note 列表中且本地文件不存在
                    var needToFill = new List<string>();
                    foreach (var p in resolvedReferencedPaths)
                    {
                        if (existingNormalizedPaths.Contains(p)) continue;
                        var full = string.IsNullOrEmpty(notesPath) ? null : Path.Combine(notesPath, p + ".md");
                        if (full == null || !File.Exists(full))
                        {
                            needToFill.Add(p);
                        }
                    }

                    // 把无法解析为带目录的 raw 链接也作为补充项（交给 AI 帮忙补齐分类）
                    foreach (var raw in unresolvedRaw)
                    {
                        if (!needToFill.Contains(raw, StringComparer.OrdinalIgnoreCase))
                            needToFill.Add(raw);
                    }

                    if (needToFill.Count == 0)
                        break; // 没有缺失，退出循环

                    // 把补充次数计入总进度，确保前端进度条能显示真实进度
                    totalSteps += 1;
                    await _taskManager.UpdateProgress(taskId, steps.Count + 1 + round, totalSteps, _loc["AtomNote_SupplementRound", round, needToFill.Count]);
                    _logger.LogInformation("发现 {Count} 个缺失的 wikilink 目标，开始第 {Round} 轮补充", needToFill.Count, round);

                    try
                    {
                        // 本地幂等检查：如果相同目标集合已在历史中，跳过重复请求
                        var hash = ComputeTargetsHash(needToFill);
                        var shouldSkip = false;
                        lock (_suppHistoryLock)
                        {
                            if (_supplementHistory == null)
                            {
                                if (File.Exists(SupplementHistoryFile))
                                {
                                    try { _supplementHistory = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SupplementHistoryFile)) ?? new List<string>(); } catch (Exception ex) { _logger.LogDebug(ex, "读取补充历史文件失败"); _supplementHistory = new List<string>(); }
                                }
                                else
                                {
                                    _supplementHistory = new List<string>();
                                }
                            }
                            if (_supplementHistory.Contains(hash))
                                shouldSkip = true;
                        }

                        if (shouldSkip)
                        {
                            _logger.LogInformation("检测到补充目标集合已在本地历史中，跳过本轮补充（轮次 {Round}）", round);
                            try { await _taskManager.NotifySupplementEventAsync(taskId, "SupplementSkipped", new { round, targets = needToFill.Count }); } catch { /* SignalR 推送失败不影响主流程 */ }
                            break;
                        }

                        // 通知前端：补充开始
                        try { await _taskManager.NotifySupplementEventAsync(taskId, "SupplementStarted", new { round, targets = needToFill }); } catch { /* SignalR 推送失败不影响主流程 */ }

                        // 向 AI 请求针对这些缺失目标的笔记（明确要求只输出 JSON 数组）
                        var suppStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var suppProvider = steps.Last().Provider;
                        var suppModel = steps.Last().Model;
                        var suppDisplayName = string.IsNullOrWhiteSpace(suppProvider.Name) ? suppProvider.Id : suppProvider.Name;
                        List<Note> newNotes = new();
                        try
                        {
                            var supplementResult = await CallAiApiAsync(title, originalContent, suppModel, suppProvider.Id, isSupplement: true, existingNotes: allNotes, missingTargets: needToFill, cancellationToken: cancellationToken, scene: scene);
                            suppStopwatch.Stop();
                            newNotes = _noteParser.ParseResult(supplementResult);
                            requestRecords.Add(new
                            {
                                providerId = suppProvider.Id,
                                providerName = suppDisplayName,
                                model = suppModel,
                                phase = _loc["AtomNote_SupplementRoundLabel", round],
                                elapsedMs = suppStopwatch.ElapsedMilliseconds,
                                success = true,
                                timestamp = DateTime.Now
                            });
                        }
                        catch (Exception ex)
                        {
                            suppStopwatch.Stop();
                            _logger.LogWarning(ex, "循环补充 AI 调用失败（第 {Round} 轮）", round);
                            requestRecords.Add(new
                            {
                                providerId = suppProvider.Id,
                                providerName = suppDisplayName,
                                model = suppModel,
                                phase = _loc["AtomNote_SupplementRoundLabel", round],
                                elapsedMs = 0,
                                success = false,
                                error = ex.Message,
                                timestamp = DateTime.Now
                            });
                            break;
                        }

                        // 记录历史（哈希）以做本地幂等
                        lock (_suppHistoryLock)
                        {
                            if (_supplementHistory == null)
                                _supplementHistory = new List<string>();
                            if (!_supplementHistory.Contains(hash))
                            {
                                _supplementHistory.Add(hash);
                                if (_supplementHistory.Count > MaxSupplementHistoryEntries)
                                {
                                    _supplementHistory.RemoveRange(0, _supplementHistory.Count - MaxSupplementHistoryEntries);
                                }
                                try { File.WriteAllText(SupplementHistoryFile, System.Text.Json.JsonSerializer.Serialize(_supplementHistory)); } catch (Exception ex) { _logger.LogWarning(ex, "写入补充历史失败"); }
                            }
                        }

                        var beforeCount = allNotes.Count;
                        foreach (var n in newNotes)
                        {
                            var normalizedPath = NormalizeRelPath(n.Path);
                            n.Path = normalizedPath;
                            if (!existingNormalizedPaths.Contains(normalizedPath))
                            {
                                allNotes.Add(n);
                                notePathMap[n] = normalizedPath;
                                existingNormalizedPaths.Add(normalizedPath);
                                _logger.LogInformation("补充新笔记（循环）: {Path}", n.Path);
                            }
                        }

                        var added = allNotes.Count - beforeCount;
                        // 更新当前轮次进度并通知前端
                        await _taskManager.UpdateProgress(taskId, steps.Count + 1 + round, totalSteps, _loc["AtomNote_SupplementRoundDone", round, added]);

                        // 通知前端：补充完成
                        try { await _taskManager.NotifySupplementEventAsync(taskId, "SupplementFinished", new { round, added = added }); } catch { /* SignalR 推送失败不影响主流程 */ }

                        if (allNotes.Count == beforeCount)
                        {
                            // AI 没有产出新筆记，避免死循环
                            _logger.LogWarning("AI 在补充轮次未返回新笔记，终止补充循环");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "补充轮次失败（第 {Round} 轮）", round);
                        break;
                    }
                }

                // 在保存前，统一规范所有 note.Path 的格式（用 '/' 作为分隔，去除首尾斜杠），
                // 并在内容中使用与保存一致的路径格式来重写 wikilink
                for (int i = 0; i < allNotes.Count; i++)
                {
                    // 规范化路径用于保存和链接重写
                    allNotes[i].Path = NormalizeRelPath(allNotes[i].Path);
                    var original = allNotes[i].Content;
                    allNotes[i].Content = WikiLinkRewriter.RewriteMissingCategoryLinks(original, titleToPath);
                }

                _logger.LogInformation("SplitAtomNotes 准备保存 {Count} 条笔记到: {NotesPath}", allNotes.Count, notesPath ?? "(null)");
                var savedNotes = await SaveNotes(allNotes, notesPath);

                // 确保返回的笔记列表与 AI 查询格式一致，便于 WebUI 统一显示
                var notesList = savedNotes.Select(n => new { title = n.Title, path = n.Path }).ToList();
                
                if (savedNotes.Count == 0)
                {
                    _logger.LogWarning("任务 {TaskId} 没有成功保存任何笔记", taskId);
                    await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, _loc["AtomNote_NoSavedNotes"]);
                    return;
                }

                totalStopwatch.Stop();

                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Success, null, new
                {
                    count = savedNotes.Count,
                    notes = notesList,
                    requests = requestRecords,
                    totalElapsedMs = totalStopwatch.ElapsedMilliseconds
                });

                _logger.LogInformation("任务 {TaskId} 完成，共生成 {Count} 条笔记", taskId, savedNotes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务 {TaskId} 失败", taskId);
                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, ex.Message);
                throw;
            }
        }
}
