using System.Text;
using System.Text.Json;
using Baihua.Contracts.Anki;
using Baihua.AI.Provider;

namespace Baihua.Modules.Family.Services
{

    public partial class AnkiCardGenerator
    {
        /// <summary>
        /// 保存为 JSON 文件
        /// </summary>
        private async Task SaveAsJson(string notePath, JsonDeckData deckData, string? cardsPath = null)
        {
            var targetCardsPath = cardsPath ?? _vaultSettings.CardsPath;
            if (string.IsNullOrEmpty(targetCardsPath)) return;

            Directory.CreateDirectory(targetCardsPath);

            var fileName = notePath.Replace("/", "_").Replace("\\", "_") + ".json";
            var fullPath = Path.Combine(targetCardsPath, fileName);

            var json = JsonSerializer.Serialize(deckData, JsonHelper.IndentedUnicode);

            await File.WriteAllTextAsync(fullPath, json);
            _logger.LogInformation("保存 {Count} 张卡片到：{Path}", deckData.Cards?.Count ?? 0, fullPath);
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
