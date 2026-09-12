using System.Text;
using System.Text.Json;
using Baihua.Contracts.Anki;
using Baihua.AI.Provider;
using AnkiGen.Core;

namespace Baihua.Modules.Family.Services
{

    public partial class AnkiCardGenerator
    {
        /// <summary>
        /// 保存为 JSON 文件
        /// </summary>
        private async Task SaveAsJson(string notePath, AnkiDeck deck, string? cardsPath = null)
        {
            var targetCardsPath = cardsPath ?? _vaultSettings.CardsPath;
            if (string.IsNullOrEmpty(targetCardsPath)) return;

            Directory.CreateDirectory(targetCardsPath);

            var deckData = new JsonDeckData
            {
                Name = deck.Name,
                Cards = deck.Notes.SelectMany(n => NoteToCards(n, deck.Name)).ToList()
            };

            var fileName = notePath.Replace("/", "_").Replace("\\", "_") + ".json";
            var fullPath = Path.Combine(targetCardsPath, fileName);

            var json = JsonSerializer.Serialize(deckData, JsonHelper.IndentedUnicode);

            await File.WriteAllTextAsync(fullPath, json);
            _logger.LogInformation("保存 {Count} 张卡片到：{Path}", deck.Notes.Count, fullPath);
        }

        /// <summary>
        /// 将 AnkiNote 转换为 JsonCard
        /// </summary>
        private List<Contracts.Anki.JsonCard> NoteToCards(AnkiNote note, string deckName)
        {
            var cards = new List<Contracts.Anki.JsonCard>();
            var fields = note.Fields;

            if (fields.Count >= 2)
            {
                // 正向卡片
                cards.Add(new Contracts.Anki.JsonCard
                {
                    Front = fields[0],
                    Back = fields[1],
                    Tags = note.Tags.ToList()
                });

                // 如果是反向模板，添加反向卡片
                if (note.Model.Templates.Count == 2)
                {
                    cards.Add(new Contracts.Anki.JsonCard
                    {
                        Front = fields[1],
                        Back = fields[0],
                        Tags = note.Tags.ToList()
                    });
                }
            }

            return cards;
        }

        /// <summary>
        /// 导出所有卡片为 CSV 格式
        /// </summary>
        public async Task<string?> ExportToCsv(string? cardsPath = null)
        {
            cardsPath ??= _vaultSettings.CardsPath;
            if (string.IsNullOrEmpty(cardsPath) || !Directory.Exists(cardsPath))
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Front,Back,Tags,Deck");

            var files = Directory.GetFiles(cardsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                    
                    if (deckData?.Cards == null) continue;

                    foreach (var card in deckData.Cards)
                    {
                        var front = EscapeCsv(card.Front ?? "");
                        var back = EscapeCsv(card.Back ?? "");
                        var tags = EscapeCsv(string.Join(" ", card.Tags ?? new List<string>()));
                        var deck = EscapeCsv(deckData.Name ?? "");
                        
                        sb.AppendLine($"{front},{back},{tags},{deck}");
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "操作失败"); }
            }

            return sb.ToString();
        }

        private string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }

        /// <summary>
        /// 获取指定知识库（或全局）的卡片总数
        /// </summary>
        public int GetTotalCardCount(string? vaultId = null)
        {
            var cardsPath = string.IsNullOrEmpty(vaultId)
                ? _vaultSettings.CardsPath
                : System.IO.Path.Combine(_vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path ?? "", "cards");

            if (!Directory.Exists(cardsPath)) return 0;

            int count = 0;
            foreach (var file in Directory.GetFiles(cardsPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                    count += deckData?.Cards?.Count ?? 0;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "文件系统操作失败"); }
            }
            return count;
        }
    }
}
