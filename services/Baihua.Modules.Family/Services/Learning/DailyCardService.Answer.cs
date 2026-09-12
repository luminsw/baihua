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
    public async Task<bool> RecordAnswerAsync(string vaultId, string cardId, string result)
    {
        var fileOk = WriteFileRecord(vaultId, cardId, result);
        var dbOk = await WriteDbRecordAsync(vaultId, cardId, result);
        await UpdateReviewStateAsync(vaultId, cardId, result);
        return fileOk || dbOk;
    }

    private bool WriteFileRecord(string vaultId, string cardId, string result)
    {
        try
        {
            var studyDir = _cardRepo.GetStudyDir(vaultId);
            if (string.IsNullOrEmpty(studyDir)) return false;

            var today = BeijingToday.ToString("yyyy-MM-dd");
            var dailyFile = Path.Combine(studyDir, $"daily-{today}.json");

            // 按 vault+date 加内存锁，防止并发覆盖
            var lockKey = $"{vaultId}:{today}";
            var fileLock = _fileLocks.GetOrAdd(lockKey, _ => new object());
            lock (fileLock)
            {
                var daily = ReadDailyRecord(dailyFile);
                if (!daily.Answers.ContainsKey(cardId))
                {
                    daily.Answers[cardId] = result;
                    daily.Completed++;
                }
                else
                {
                    daily.Answers[cardId] = result;
                }

                WriteDailyRecord(dailyFile, daily);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "文件系统记录学习结果失败（非关键）");
            return false;
        }
    }

    private async Task<bool> WriteDbRecordAsync(string vaultId, string cardId, string result)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var learner = await _learnerService.GetDefaultAsync();
            var learnerId = learner?.Id ?? 0;
            if (learnerId == 0)
            {
                // 没有学习者时自动创建一个默认学习者
                var newLearner = await _learnerService.CreateAsync(_loc["Default_Learner"]);
                learnerId = newLearner.Id;
            }

            db.StudyActivities.Add(new StudyActivity
            {
                LearnerId = learnerId,
                VaultId = vaultId,
                ActivityType = "study",
                CardId = cardId,
                Result = result,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite 记录学习结果失败");
            return false;
        }
    }

    /// <summary>
    /// 获取今日学习进度（FAM-02：DB 为单一事实源，不再 fallback 文件）
    /// </summary>
    public DailyProgress GetTodayProgress(string vaultId)
    {
        var progress = GetTodayProgressFromDb(vaultId);
        return progress ?? new DailyProgress();
    }

    private DailyProgress? GetTodayProgressFromDb(string vaultId)
    {
        using var db = _dbFactory.CreateDbContext();
        var today = BeijingToday;
        var todayCount = db.StudyActivities
            .Where(a => a.VaultId == vaultId && a.ActivityType == "study")
            .AsEnumerable()
            .Count(a => ToBeijingDate(a.CreatedAt) == today);

        var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
        var totalCards = 0;
        if (!string.IsNullOrEmpty(cardsPath) && Directory.Exists(cardsPath))
        {
            totalCards = _cardRepo.LoadAllCards(cardsPath).Count;
        }

        var streak = CalculateStreakFromDb(vaultId);

        return new DailyProgress
        {
            Completed = todayCount,
            Target = Math.Min(10, Math.Max(3, totalCards > 0 ? totalCards / 10 : 5)),
            TotalCards = totalCards,
            Streak = streak
        };
    }
}
