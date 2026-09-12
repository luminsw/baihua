using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Text;
using System.Text.Json;
using Baihua.Contracts.Anki;
using Baihua.Core.Localization;
using Baihua.AI.Provider;
using AnkiGen.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Modules.Family.Models;
using Baihua.Core;

namespace Baihua.Modules.Family.Services
{
    /// <summary>
    /// Anki 卡片生成服务：将知识库笔记转换为 Anki 记忆卡片（支持 AI 生成）
    /// </summary>
    public partial class AnkiCardGenerator
    {
        private readonly VaultSettingsService _vaultSettings;
        private readonly AiClientService _aiClient;
        private readonly AiSettingsService _aiSettings;
        private readonly TaskManager _taskManager;
        private readonly ILogger<AnkiCardGenerator> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public AnkiCardGenerator(VaultSettingsService vaultSettings, AiClientService aiClient, AiSettingsService aiSettings, TaskManager taskManager, ILogger<AnkiCardGenerator> logger, IStringLocalizer<SharedResources> loc)
        {
            _vaultSettings = vaultSettings;
            _aiClient = aiClient;
            _aiSettings = aiSettings;
            _taskManager = taskManager;
            _logger = logger;
            _loc = loc;
        }


        /// <summary>
        /// 从路径获取牌组名
        /// </summary>
        private string GetDeckName(string notePath)
        {
            var parts = notePath.Split('/');
            if (parts.Length >= 2)
            {
                return string.Format(_loc["Anki_DeckPrefixJingFang"], parts[^2]);
            }
            return _loc["Anki_TagJingFang"];
        }

        /// <summary>
        /// 从路径获取标签
        /// </summary>
        private List<string> GetTagsFromPath(string notePath)
        {
            var tags = new List<string> { _loc["Anki_TagJingFang"] };
            var parts = notePath.Split('/');
            
            foreach (var part in parts.Take(parts.Length - 1))
            {
                tags.Add(part);
            }
            
            return tags;
        }

        // ========== AI 驱动卡片生成 ==========

        private const string AnkiCardSystemPrompt = """"
你是一位知识整理专家。请根据用户提供的 Markdown 笔记内容，提取核心知识点并生成 Anki 记忆卡片。
要求：
1. 生成 2-5 张卡片，覆盖笔记中最重要、最难记忆的知识点
2. 每张卡片正面（front）是一个能引发主动回忆的简洁问题，不超过 50 字
3. 每张卡片背面（back）是完整、准确的答案，不超过 200 字
4. 如果笔记内容较少（少于 100 字），只生成 1-2 张概述卡片
5. 输出严格为 JSON 数组格式，不要包含 markdown 代码块标记或其他说明文字
"""";

        /// <summary>
        /// 使用 AI 从单篇笔记生成 Anki 卡片并保存为 JSON
        /// </summary>
        public async Task<GenerateResult> GenerateWithAiAsync(
            string notePath, string? cardsPath = null, string? notesBasePath = null,
            string? providerId = null, string? model = null)
        {
            try
            {
                var basePath = notesBasePath ?? _vaultSettings.NotesPath;
                if (string.IsNullOrEmpty(basePath))
                {
                    return new GenerateResult { Success = false, Message = _loc["Anki_NotesDirNotConfigured"] };
                }

                var fullPath = Path.Combine(basePath, notePath + ".md");
                if (!File.Exists(fullPath))
                {
                    return new GenerateResult { Success = false, Message = string.Format(_loc["Anki_NoteNotFound"], fullPath) };
                }

                var content = await File.ReadAllTextAsync(fullPath);
                var title = ExtractTitle(content);

                var cards = await CallAiForCardsAsync(title, content, providerId, model);

                if (cards.Count == 0)
                {
                    return new GenerateResult
                    {
                        Success = true,
                        Message = string.Format(_loc["Anki_AiNoCards"], title),
                        CardCount = 0
                    };
                }

                var deckName = GetDeckName(notePath);
                var deck = AnkiDeck.Create(deckName);
                foreach (var card in cards)
                {
                    deck.AddCard(card.Front, card.Back, card.Tags?.ToArray() ?? Array.Empty<string>());
                }

                await SaveAsJson(notePath, deck, cardsPath);

                return new GenerateResult
                {
                    Success = true,
                    Message = string.Format(_loc["Anki_AiSuccess"], deck.Notes.Count, title),
                    CardCount = deck.Notes.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 生成卡片失败: {NotePath}", notePath);
                return new GenerateResult
                {
                    Success = false,
                    Message = string.Format(_loc["Anki_AiFailed"], ex.Message),
                    CardCount = 0
                };
            }
        }

        /// <summary>
        /// 批量使用 AI 为目录中的所有笔记生成卡片
        /// </summary>
        public async Task<BatchGenerateResult> GenerateBatchWithAiAsync(
            string directory, bool recursive = true, string? vaultId = null,
            string? providerId = null, string? model = null,
            string? progressTaskId = null)
        {
            var dirPath = directory;
            if (!Directory.Exists(dirPath))
            {
                return new BatchGenerateResult { Success = false, Message = string.Format(_loc["Anki_DirNotFound"], directory) };
            }

            string? cardsPath = null;
            if (!string.IsNullOrEmpty(vaultId))
            {
                var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
                if (vault != null)
                {
                    cardsPath = Path.Combine(vault.Path, "cards");
                }
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(dirPath, "*.md", searchOption);

            var results = new List<GenerateResult>();
            var totalCards = 0;
            var totalFiles = files.Length;

            // 有 taskId 时先汇报总进度
            if (!string.IsNullOrEmpty(progressTaskId))
            {
                await _taskManager.UpdateProgress(progressTaskId, 0, totalFiles, string.Format(_loc["Anki_PreparingNotes"], totalFiles));
            }

            for (int i = 0; i < totalFiles; i++)
            {
                var file = files[i];
                var relativePath = Path.GetRelativePath(dirPath, file);
                var notePath = relativePath.Substring(0, relativePath.Length - 3);

                // 汇报进度
                if (!string.IsNullOrEmpty(progressTaskId))
                {
                    var progressMsg = $"[{i + 1}/{totalFiles}] {notePath}";
                    await _taskManager.UpdateProgress(progressTaskId, i + 1, totalFiles, progressMsg);
                }

                var result = await GenerateWithAiAsync(notePath, cardsPath, notesBasePath: dirPath, providerId, model);
                results.Add(result);
                if (result.Success)
                {
                    totalCards += result.CardCount;
                }

                await Task.Delay(500);
            }

            return new BatchGenerateResult
            {
                Success = true,
                Message = string.Format(_loc["Anki_BatchComplete"], totalCards, totalFiles),
                TotalCards = totalCards,
                Results = results
            };
        }

        /// <summary>
        /// 调用 AI 大模型生成卡片内容
        /// </summary>
        private async Task<List<Baihua.Contracts.Anki.JsonCard>> CallAiForCardsAsync(
            string title, string content,
            string? providerId, string? model)
        {
            var provider = string.IsNullOrWhiteSpace(providerId)
                ? _aiSettings.GetMainAiProvider()
                : _aiSettings.GetAiProvider(providerId);

            if (provider == null)
            {
                _logger.LogWarning("未找到 AI Provider 配置");
                return new List<Baihua.Contracts.Anki.JsonCard>();
            }

            var modelName = string.IsNullOrWhiteSpace(model)
                ? GetDefaultModel(provider)
                : model;

            var chatClient = _aiClient.CreateChatClient(provider.Id, modelName);

            var userPrompt = $"笔记标题：{title}\n笔记内容：\n---\n{content}\n---\n\n请只输出 JSON 数组，不要有任何其他文字：\n[\n  {{ \"front\": \"...\", \"back\": \"...\" }}\n]";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, AnkiCardSystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            try
            {
                var response = await chatClient.GetResponseAsync(messages);
                var rawText = response.Text?.Trim() ?? "";

                rawText = ExtractJsonArray(rawText);
                var cards = JsonSerializer.Deserialize<List<Baihua.Contracts.Anki.JsonCard>>(rawText,
                    JsonHelper.CaseInsensitive);

                if (cards == null || cards.Count == 0)
                {
                    _logger.LogWarning("AI 返回了空的卡片列表");
                    return new List<Baihua.Contracts.Anki.JsonCard>();
                }

                foreach (var card in cards)
                {
                    card.Front = (card.Front ?? "").Trim();
                    card.Back = (card.Back ?? "").Trim();
                    if (card.Tags == null)
                        card.Tags = new List<string>();
                }

                return cards.Where(c => !string.IsNullOrWhiteSpace(c.Front)
                                         && !string.IsNullOrWhiteSpace(c.Back)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 调用失败生成卡片");
                return new List<Baihua.Contracts.Anki.JsonCard>();
            }
        }

        /// <summary>
        /// 从 AI 响应文本中提取 JSON 数组部分
        /// </summary>
        private static string ExtractJsonArray(string text)
        {
            if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (end > 7)
                    text = text[7..end].Trim();
            }
            else if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (end > 3)
                    text = text[3..end].Trim();
            }

            var start = text.IndexOf('[');
            var endBracket = text.LastIndexOf(']');
            if (start >= 0 && endBracket > start)
            {
                text = text[start..(endBracket + 1)];
            }

            return text;
        }

        private static string GetDefaultModel(AiProviderConfig provider)
        {
            var mainModel = provider.Models.FirstOrDefault(m => m.IsMain);
            if (mainModel != null)
                return mainModel.Name;
            return provider.Models.FirstOrDefault()?.Name ?? "default";
        }

        private static string ExtractTitle(string content)
        {
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("# "))
                {
                    return trimmed.Substring(2).Trim();
                }
            }
            return "";
        }

    }
}
