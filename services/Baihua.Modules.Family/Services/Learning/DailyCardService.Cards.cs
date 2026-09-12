using System.Security.Cryptography;
using System.Text.Json;
using Baihua.Contracts.Anki;
using Baihua.AI.Provider;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

public partial class DailyCardService
{
    public bool SaveCustomCard(string vaultId, CustomCardRequest request)
    {
        try
        {
            var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath)) return false;

            Directory.CreateDirectory(cardsPath);
            var customFile = Path.Combine(cardsPath, "custom.json");

            var deck = new JsonDeckData
            {
                Name = request.Deck ?? _loc["DailyCard_DefaultDeck"],
                Cards = new List<JsonCard>()
            };

            if (File.Exists(customFile))
            {
                var json = File.ReadAllText(customFile);
                var deserialized = JsonSerializer.Deserialize<JsonDeckData>(json);
                if (deserialized != null)
                {
                    deck = deserialized;
                }
            }

            deck ??= new JsonDeckData
            {
                Name = request.Deck ?? _loc["DailyCard_DefaultDeck"],
                Cards = new List<JsonCard>()
            };

            deck.Cards ??= new List<JsonCard>();
            deck.Cards.Add(new JsonCard
            {
                Front = request.Front,
                Back = request.Back,
                Tags = request.Tags ?? new List<string>()
            });

            var output = JsonSerializer.Serialize(deck, JsonHelper.IndentedUnicode);
            File.WriteAllText(customFile, output);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存自定义卡片失败");
            return false;
        }
    }

    /// <summary>
    /// 获取最近学过的卡片（用于复习）
    /// </summary>
    public List<StudiedCard> GetRecentStudied(string vaultId, int days = 7)
    {
        var result = new List<StudiedCard>();
        try
        {
            var studyDir = _cardRepo.GetStudyDir(vaultId);
            if (string.IsNullOrEmpty(studyDir)) return result;

            var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
            var allCards = cardsPath != null && Directory.Exists(cardsPath) ? _cardRepo.LoadAllCards(cardsPath) : new List<CardItem>();
            var cardDict = allCards.ToDictionary(c => c.Id, c => c);

            for (int i = 0; i < days; i++)
            {
                var date = BeijingToday.AddDays(-i);
                var file = Path.Combine(studyDir, $"daily-{date:yyyy-MM-dd}.json");
                if (!File.Exists(file)) continue;

                var daily = ReadDailyRecord(file);
                foreach (var kv in daily.Answers)
                {
                    if (cardDict.TryGetValue(kv.Key, out var card))
                    {
                        result.Add(new StudiedCard
                        {
                            Card = card,
                            Result = kv.Value,
                            Date = date.ToString("yyyy-MM-dd")
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取最近学习记录失败");
        }
        return result;
    }

}
