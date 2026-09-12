using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Baihua.AI.Provider;
using Baihua.Contracts.Anki;

namespace Baihua.Modules.Family.Controllers;

public partial class AnkiController
{
        /// <summary>
        /// 使用 AI 从单篇笔记生成 Anki 卡片
        /// </summary>
        [HttpPost("generate-ai")]
        public async Task<ActionResult<GenerateResult>> GenerateWithAi([FromBody] AiGenerateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NotePath))
            {
                return BadRequest(new GenerateResult { Success = false, Message = _loc["Anki_NotePathEmpty"] });
            }

            var result = await _cardGenerator.GenerateWithAiAsync(request.NotePath, providerId: request.ProviderId, model: request.Model);
            return Ok(result);
        }

        /// <summary>
        /// 使用 AI 批量为知识库生成 Anki 卡片
        /// </summary>
        [HttpGet("generate-all-ai")]
        public async Task<IActionResult> GenerateAllCardsWithAi([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { success = false, message = _loc["Anki_VaultIdEmpty"] });

            var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
            if (vault == null)
                return NotFound(new { success = false, message = _loc["Anki_VaultNotFound"] });

            var notesPath = System.IO.Path.Combine(vault.Path, "notes");
            if (!Directory.Exists(notesPath))
                return Ok(new { success = true, message = _loc["Anki_NotesDirNotExist"], totalCards = 0 });

            var taskId = _taskManager.CreateTask("anki_generate_ai", new Dictionary<string, string>
            {
                ["vaultId"] = vaultId,
                ["vaultName"] = vault.Name,
                ["notesPath"] = notesPath
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    await _taskManager.UpdateProgress(taskId, 0, 100, _loc["Anki_GeneratingCardsFor", vault.Name]);
                    var result = await _cardGenerator.GenerateBatchWithAiAsync(notesPath, recursive: true, vaultId: vaultId, progressTaskId: taskId);
                    await _taskManager.UpdateProgress(taskId, 100, 100, result.Message);
                    if (result.Success && result.TotalCards > 0)
                    {
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: new { totalCards = result.TotalCards, message = result.Message });
                    }
                    else
                    {
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: result.Message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AnkiController] AI 任务 {TaskId} 生成卡片失败", taskId);
                    await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: ex.Message);
                }
            });

            return Ok(new { success = true, taskId, message = _loc["Anki_AiCardTaskCreated"], vaultName = vault.Name });
        }

        /// <summary>
        /// 从 JSON 文件中读取卡片列表，支持 JsonDeckData 和 List&lt;CardItemDto&gt; 两种格式
        /// </summary>
        private List<CardItemDto> ReadCardsFromFile(string json, string fileName)
        {
            try
            {
                // 先尝试解析为 JsonDeckData 格式（{ Name, Cards: [...] }）
                var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                if (deckData?.Cards != null && deckData.Cards.Count > 0)
                {
                    return deckData.Cards.Select((c, i) => new CardItemDto
                    {
                        Id = $"{fileName}_{i}",
                        Deck = deckData.Name ?? fileName,
                        Front = c.Front,
                        Back = c.Back,
                        Tags = c.Tags ?? new(),
                        Source = fileName
                    }).ToList();
                }
            }
            catch { }

            try
            {
                // 回退到数组格式（[{ Front, Back, Deck, Tags }]）
                var cardsArray = JsonSerializer.Deserialize<List<CardItemDto>>(json);
                if (cardsArray != null)
                {
                    foreach (var card in cardsArray)
                    {
                        if (string.IsNullOrEmpty(card.Source))
                            card.Source = fileName;
                    }
                    return cardsArray;
                }
            }
            catch { }

            return new List<CardItemDto>();
        }

        private string? ResolveCardsPath(string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return null;
            var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
            if (string.IsNullOrEmpty(vaultPath))
                return null;
            return System.IO.Path.Combine(vaultPath, "cards");
        }
}
