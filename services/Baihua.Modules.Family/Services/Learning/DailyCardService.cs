using System.Security.Cryptography;
using System.Text.Json;
using Baihua.Contracts.Anki;
using Baihua.Core.Localization;
using Baihua.Core.Time;
using Baihua.AI.Provider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 每日一帖服务：每日卡片推送、学习进度跟踪、家长出题
/// 同时维护文件系统记录（兼容旧数据）和 SQLite StudyActivities（新数据源）
/// </summary>
public partial class DailyCardService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly LearnerService _learnerService;
    private readonly CardRepository _cardRepo;
    private readonly ILogger<DailyCardService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;
    private readonly ITimeProvider _timeProvider;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _fileLocks = new();

    public DailyCardService(
        IDbContextFactory<FamilyDbContext> dbFactory,
        LearnerService learnerService,
        CardRepository cardRepo,
        ILogger<DailyCardService> logger,
        IStringLocalizer<SharedResources> loc,
        ITimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _learnerService = learnerService;
        _cardRepo = cardRepo;
        _logger = logger;
        _loc = loc;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 艾宾浩斯间隔序列（天）
    /// </summary>
    private static readonly int[] _intervalSequence = new[] { 1, 2, 4, 7, 15, 30, 60, 120 };

    /// <summary>
    /// 获取今日卡片（智能复习调度：优先复习到期卡片，其次新卡片，最后随机）
    /// </summary>
    public async Task<DailyCardResult> GetTodayCardAsync(string vaultId, int? seed = null)
    {
        var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
        if (string.IsNullOrEmpty(cardsPath) || !Directory.Exists(cardsPath))
        {
            return new DailyCardResult { HasCard = false, Message = _loc["DailyCard_None"] };
        }

        var allCards = _cardRepo.LoadAllCards(cardsPath);
        if (allCards.Count == 0)
        {
            return new DailyCardResult { HasCard = false, Message = _loc["DailyCard_None"] };
        }

        var todayStudied = GetTodayStudiedIds(vaultId);
        var today = BeijingToday;
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();

        // 1. 优先：今日到期的复习卡片（且今天还没学过）
        var dueCards = await _cardRepo.GetDueReviewsAsync(vaultId, today);
        var dueUnstudied = dueCards.Where(c => !todayStudied.Contains(c.Id)).ToList();
        if (dueUnstudied.Count > 0)
        {
            var card = dueUnstudied[rng.Next(dueUnstudied.Count)];
            var progress = GetTodayProgress(vaultId);
            return new DailyCardResult
            {
                HasCard = true,
                Card = card,
                TodayProgress = progress,
                Remaining = Math.Max(0, progress.Target - progress.Completed),
                IsReview = true
            };
        }

        // 2. 其次：新卡片（从未学过的）
        var studiedIds = await _cardRepo.GetAllStudiedIdsAsync(vaultId);
        var newCards = allCards.Where(c => !studiedIds.Contains(c.Id) && !todayStudied.Contains(c.Id)).ToList();
        if (newCards.Count > 0)
        {
            var card = newCards[rng.Next(newCards.Count)];
            var progress = GetTodayProgress(vaultId);
            return new DailyCardResult
            {
                HasCard = true,
                Card = card,
                TodayProgress = progress,
                Remaining = Math.Max(0, progress.Target - progress.Completed)
            };
        }

        // 3. 最后：未学过的任意卡片
        var unstudied = allCards.Where(c => !todayStudied.Contains(c.Id)).ToList();
        var pool = unstudied.Count > 0 ? unstudied : allCards;
        var fallbackCard = pool[rng.Next(pool.Count)];
        var fallbackProgress = GetTodayProgress(vaultId);

        return new DailyCardResult
        {
            HasCard = true,
            Card = fallbackCard,
            TodayProgress = fallbackProgress,
            Remaining = Math.Max(0, fallbackProgress.Target - fallbackProgress.Completed)
        };
    }
}
