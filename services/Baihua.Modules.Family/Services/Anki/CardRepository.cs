using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 卡片存储仓库：卡片加载、路径解析、复习调度
/// </summary>
public class CardRepository
{
    private readonly VaultSettingsService _vaultSettings;
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly LearnerService _learnerService;
    private readonly ILogger<CardRepository> _logger;

    public CardRepository(
        VaultSettingsService vaultSettings,
        IDbContextFactory<FamilyDbContext> dbFactory,
        LearnerService learnerService,
        ILogger<CardRepository> logger)
    {
        _vaultSettings = vaultSettings;
        _dbFactory = dbFactory;
        _learnerService = learnerService;
        _logger = logger;
    }

    public string? ResolveCardsPath(string vaultId)
    {
        if (string.IsNullOrWhiteSpace(vaultId)) return null;
        var vaultPath = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
        if (string.IsNullOrEmpty(vaultPath)) return null;
        return Path.Combine(vaultPath, "cards");
    }

    public string? GetStudyDir(string vaultId)
    {
        var cardsPath = ResolveCardsPath(vaultId);
        if (string.IsNullOrEmpty(cardsPath)) return null;
        var dir = Path.Combine(cardsPath, ".study");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public List<CardItem> LoadAllCards(string cardsPath)
    {
        var cards = new List<CardItem>();
        var files = Directory.GetFiles(cardsPath, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var deck = JsonSerializer.Deserialize<JsonDeckData>(json);
                if (deck?.Cards == null) continue;

                var source = Path.GetFileNameWithoutExtension(file);
                foreach (var c in deck.Cards)
                {
                    var id = ComputeCardId(c.Front, c.Back);
                    cards.Add(new CardItem
                    {
                        Id = id,
                        Front = c.Front,
                        Back = c.Back,
                        Tags = c.Tags,
                        Deck = deck.Name ?? source,
                        Source = source
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "解析卡片文件失败：{File}", file);
            }
        }

        return cards;
    }

    public static string ComputeCardId(string front, string back)
    {
        var input = (front ?? "") + "\n" + (back ?? "");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16];
    }

    public async Task<List<CardItem>> GetDueReviewsAsync(string vaultId, DateTime today)
    {
        try
        {
            var learner = await _learnerService.GetDefaultAsync();
            var learnerId = learner?.Id ?? 0;
            if (learnerId == 0) return new List<CardItem>();

            using var db = await _dbFactory.CreateDbContextAsync();
            var dueIds = await db.CardReviewStates
                .Where(r => r.LearnerId == learnerId && r.VaultId == vaultId && r.NextReviewDate <= today)
                .Select(r => r.CardId)
                .ToListAsync();

            if (dueIds.Count == 0) return new List<CardItem>();

            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath)) return new List<CardItem>();

            var allCards = LoadAllCards(cardsPath);
            return allCards.Where(c => dueIds.Contains(c.Id)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "获取到期复习卡片失败");
            return new List<CardItem>();
        }
    }

    public async Task<HashSet<string>> GetAllStudiedIdsAsync(string vaultId)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var ids = await db.StudyActivities
                .Where(a => a.VaultId == vaultId && a.ActivityType == "study" && a.CardId != null)
                .Select(a => a.CardId!)
                .Distinct()
                .ToListAsync();
            return new HashSet<string>(ids);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "获取已学卡片 ID 失败");
            return new HashSet<string>();
        }
    }

    private class JsonDeckData
    {
        public string? Name { get; set; }
        public List<JsonCardData> Cards { get; set; } = new();
    }

    private class JsonCardData
    {
        public string Front { get; set; } = "";
        public string Back { get; set; } = "";
        public List<string> Tags { get; set; } = new();
    }
}
