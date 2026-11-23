using Baihua.Core;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Baihua.AI.Provider;
using Baihua.Contracts.Anki;

namespace Baihua.Modules.Family.Controllers;

public partial class AnkiController
{
        [HttpGet("search")]
        public ActionResult<AnkiSearchResult> SearchCards([FromQuery] string? q, [FromQuery] string vaultId, [FromQuery] int limit = 50)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath) || !System.IO.Directory.Exists(cardsPath))
            {
                return Ok(new AnkiSearchResult { Success = true, Cards = new List<CardItemDto>(), Total = 0 });
            }

            var results = new List<CardItemDto>();
            var files = System.IO.Directory.GetFiles(cardsPath, "*.json");
            var keywords = string.IsNullOrWhiteSpace(q)
                ? Array.Empty<string>()
                : q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                    
                    var cardsArray = ReadCardsFromFile(json, fileName);
                    
                    for (int i = 0; i < cardsArray.Count; i++)
                    {
                        var card = cardsArray[i];
                        // 为每张卡片生成稳定 ID（文件名 + 索引）
                        if (string.IsNullOrEmpty(card.Id))
                            card.Id = $"{fileName}_{i}";
                        card.Source = fileName;

                        if (keywords.Length == 0)
                        {
                            results.Add(card);
                            if (results.Count >= limit) break;
                            continue;
                        }

                        var frontLower = (card.Front ?? "").ToLower();
                        var backLower = (card.Back ?? "").ToLower();
                        
                        var matchCount = keywords.Count(k => 
                            frontLower.Contains(k) || backLower.Contains(k));
                        
                        if (matchCount == keywords.Length)
                        {
                            results.Add(card);
                            if (results.Count >= limit) break;
                        }
                    }
                    
                    if (results.Count >= limit) break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "解析卡片文件失败：{File}", file);
                }
            }

            return Ok(new AnkiSearchResult
            {
                Success = true,
                Cards = results,
                Total = results.Count,
                Query = q ?? ""
            });
        }

        /// <summary>
        /// 获取知识库总卡片数
        /// </summary>
        [HttpGet("card-count")]
        public ActionResult<int> GetCardCount([FromQuery] string vaultId)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath) || !System.IO.Directory.Exists(cardsPath))
            {
                return Ok(0);
            }

            int totalCount = 0;
            var files = System.IO.Directory.GetFiles(cardsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                    if (deckData?.Cards != null)
                    {
                        totalCount += deckData.Cards.Count;
                    }
                }
                catch { }
            }

            return Ok(totalCount);
        }

        /// <summary>
        /// 获取所有牌组列表
        /// </summary>
        [HttpGet("decks")]
        public ActionResult<DeckListResult> GetDecks([FromQuery] string vaultId)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath) || !System.IO.Directory.Exists(cardsPath))
            {
                return Ok(new DeckListResult { Decks = new List<DeckInfo>() });
            }

            var decks = new Dictionary<string, DeckInfo>();
            var files = System.IO.Directory.GetFiles(cardsPath, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                    
                    var cardsArray = ReadCardsFromFile(json, fileName);
                    
                    if (cardsArray.Count == 0) continue;

                    // 按 Deck 分组
                    foreach (var card in cardsArray)
                    {
                        var deckName = card.Deck ?? "Default";
                        if (!decks.ContainsKey(deckName))
                        {
                            decks[deckName] = new DeckInfo
                            {
                                Name = deckName,
                                CardCount = 0,
                                Sources = new List<string>()
                            };
                        }
                        
                        decks[deckName].CardCount++;
                        if (!decks[deckName].Sources.Contains(fileName))
                        {
                            decks[deckName].Sources.Add(fileName);
                        }
                    }
                }
                catch { }
            }

            return Ok(new DeckListResult
            {
                Decks = decks.Values.OrderByDescending(d => d.CardCount).ToList()
            });
        }
}
