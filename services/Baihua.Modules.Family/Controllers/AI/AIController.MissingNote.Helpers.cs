using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Ai;
using Baihua.Modules.Family.Models;

namespace Baihua.Modules.Family.Controllers
{
    public partial class AIController
    {
        private async Task<ActionResult<GenerateMissingNoteResponse>> HandleGenerateMissingNoteAsync(GenerateMissingNoteRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.LinkPath))
                return BadRequest(new { error = _loc["Ai_MissingNote_LinkPathEmpty"] });

            try
            {
                var (provider, model) = ResolveProviderAndModel(request.ProviderId, request.Model);

                // 解析知识库
                var vault = !string.IsNullOrWhiteSpace(request.VaultId)
                    ? _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)
                    : _vaultSettings.GetActiveVault();
                var vaultPath = vault?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                    return BadRequest(new { error = _loc["Ai_MissingNote_VaultNotFound"] });

                var notesPath = System.IO.Path.Combine(vaultPath, "notes");

                // 提取链接中的标题（去掉分类路径部分）
                var linkTitle = System.IO.Path.GetFileName(request.LinkPath);

                // 1. AI 生成笔记内容
                var industry = vault?.Industry ?? "";
                var template = _scenePromptService.GetTemplateByName(industry);
                var systemPrompt = template.ChatSystemPrompt;
                var userPrompt = _loc["Ai_MissingNote_GeneratePrompt", linkTitle];

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User, userPrompt)
                };
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages);

                var aiResult = await CallAiApiAsync(messages, model, provider.Id, enableTools: request.EnableTools ?? false);

                // 2. 解析 AI 返回，提取标题和内容
                var title = linkTitle;
                var firstLine = aiResult.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.TrimStart('#').Trim();
                if (!string.IsNullOrEmpty(firstLine))
                    title = firstLine;

                var content = _loc["Ai_MissingNote_SourceInfo", model, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")] + "\n\n" + aiResult;

                // 3. 确定保存路径：如果链接已包含分类则直接用，否则搜索知识库推断分类
                var notePath = request.LinkPath;
                var linkDir = System.IO.Path.GetDirectoryName(request.LinkPath) ?? throw new InvalidOperationException(_loc["Ai_MissingNote_NoDirectory", request.LinkPath]);
                if (!request.LinkPath.Contains('/') || !System.IO.Directory.Exists(System.IO.Path.Combine(notesPath, linkDir)))
                {
                    // 链接没有分类或分类目录不存在，尝试推断
                    var inferredCategory = InferCategoryFromReferences(linkTitle, notesPath, template);
                    if (!string.IsNullOrEmpty(inferredCategory))
                        notePath = $"{inferredCategory}/{linkTitle}";
                }

                // 4. 保存笔记
                var fullPath = System.IO.Path.Combine(notesPath, notePath + ".md");
                var fullDir = System.IO.Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException(_loc["Ai_MissingNote_NoDirectory", fullPath]);
                System.IO.Directory.CreateDirectory(fullDir);
                await System.IO.File.WriteAllTextAsync(fullPath, content);

                // 自动为该笔记生成 Anki 记忆卡片
                try
                {
                    var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                    var vaultForCard = _vaultSettings.GetVaults().FirstOrDefault(v => v.Path == vaultPath);
                    var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                    {
                        ["notePath"] = notePath,
                        ["vaultId"] = vaultForCard?.Id ?? "",
                        ["vaultName"] = vaultForCard?.Name ?? ""
                    });
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);
                            var result = await _cardGenerator.GenerateWithAiAsync(notePath, cardsPath: cardsRoot, notesBasePath: notesPath);
                            await _taskManager.UpdateStatus(taskId, result.Success ? RunnerTaskStatus.Success : RunnerTaskStatus.Failed,
                                data: new { message = result.Message, cardCount = result.CardCount });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[GenerateMissingNote] 卡片生成失败");
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: ex.Message);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GenerateMissingNote] 自动触发卡片生成失败");
                }

                // 5. 纠正引用该链接的其他笔记（将无分类链接替换为有分类链接）
                var fixedLinks = FixWikiLinksInVault(linkTitle, notePath, notesPath);

                return Ok(new GenerateMissingNoteResponse
                {
                    Success = true,
                    Message = _loc["Ai_MissingNote_Saved", notePath],
                    NotePath = notePath,
                    Title = title,
                    Content = content,
                    FixedLinks = fixedLinks
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成缺失笔记失败");
                return Ok(new GenerateMissingNoteResponse
                {
                    Success = false,
                    Message = _loc["Ai_MissingNote_Failed", ex.Message]
                });
            }
        }

        /// <summary>
        /// 从知识库中引用该标题的其他笔记推断分类
        /// </summary>
        private string? InferCategoryFromReferences(string title, string notesPath, DefaultPromptProvider.PromptTemplate template)
        {
            // 搜索知识库中包含 [[title]] 链接的笔记，从它们的路径推断分类
            try
            {
                if (!System.IO.Directory.Exists(notesPath))
                    return null;

                var linkPattern1 = $"[[{title}]]";
                var linkPattern2 = $"[[{title}|";
                var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in System.IO.Directory.EnumerateFiles(notesPath, "*.md", System.IO.SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(file);
                        if (content.Contains(linkPattern1, StringComparison.OrdinalIgnoreCase) ||
                            content.Contains(linkPattern2, StringComparison.OrdinalIgnoreCase))
                        {
                            // 从文件路径提取分类
                            var relPath = System.IO.Path.GetRelativePath(notesPath, file);
                            var dir = System.IO.Path.GetDirectoryName(relPath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                var topCategory = dir.Split('/')[0];
                                categories.TryGetValue(topCategory, out var count);
                                categories[topCategory] = count + 1;
                            }
                        }
                    }
                    catch { /* 跳过无法读取的文件 */ }
                }

                // 返回出现最多的分类
                if (categories.Count > 0)
                    return categories.OrderByDescending(c => c.Value).First().Key;

                // 如果没有引用，使用场景默认分类
                var defaultCategories = template.DefaultCategories;
                if (defaultCategories != null && defaultCategories.Count > 0)
                    return defaultCategories[0];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "推断分类失败");
            }

            return null;
        }

        /// <summary>
        /// 纠正知识库中引用该标题的 wikilink：将 [[title]] 替换为 [[category/title]]
        /// </summary>
        private int FixWikiLinksInVault(string title, string correctPath, string notesPath)
        {
            var fixedCount = 0;
            try
            {
                if (!System.IO.Directory.Exists(notesPath))
                    return 0;

                // 只有当 correctPath 包含分类时才需要纠正
                if (!correctPath.Contains('/'))
                    return 0;

                var oldLink = $"[[{title}]]";
                var newLink = $"[[{correctPath}]]";
                // 也处理带 alias 的链接 [[title|alias]]
                var oldLinkWithAliasPattern = $"[[{title}|";

                foreach (var file in System.IO.Directory.EnumerateFiles(notesPath, "*.md", System.IO.SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(file);
                        var modified = false;

                        // 替换 [[title]] -> [[category/title]]
                        if (content.Contains(oldLink, StringComparison.OrdinalIgnoreCase))
                        {
                            content = content.Replace(oldLink, newLink, StringComparison.OrdinalIgnoreCase);
                            modified = true;
                        }

                        // 替换 [[title|alias]] -> [[category/title|alias]]
                        if (content.Contains(oldLinkWithAliasPattern, StringComparison.OrdinalIgnoreCase))
                        {
                            content = System.Text.RegularExpressions.Regex.Replace(content,
                                $@"\[\[{System.Text.RegularExpressions.Regex.Escape(title)}\|",
                                $"[[{correctPath}|",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            modified = true;
                        }

                        if (modified)
                        {
                            System.IO.File.WriteAllText(file, content);
                            fixedCount++;
                        }
                    }
                    catch { /* 跳过无法处理的文件 */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "纠正链接失败");
            }

            return fixedCount;
        }
    }

    public class GenerateMissingNoteResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string? NotePath { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public int FixedLinks { get; set; }
    }
}
