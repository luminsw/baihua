using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 学习记录迁移服务：将旧文件系统记录（.study/daily-*.json）迁移到 SQLite StudyActivities
/// </summary>
public class StudyRecordMigrationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StudyRecordMigrationService> _logger;

    public StudyRecordMigrationService(IServiceProvider serviceProvider, ILogger<StudyRecordMigrationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 延迟启动，等待其他服务就绪
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        try
        {
            await MigrateAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "学习记录迁移失败");
        }
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FamilyDbContext>>();
        var vaultSettings = scope.ServiceProvider.GetRequiredService<VaultSettingsService>();
        var learnerService = scope.ServiceProvider.GetRequiredService<LearnerService>();

        using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // 检查是否已有 StudyActivities 记录
        var hasExistingRecords = await db.StudyActivities.AnyAsync(cancellationToken);
        if (hasExistingRecords)
        {
            return;
        }

        // 确保有默认学习者
        var defaultLearner = await learnerService.GetDefaultAsync();
        if (defaultLearner == null)
        {
            return;
        }

        var vaults = vaultSettings.GetVaults();
        int totalMigrated = 0;

        foreach (var vault in vaults)
        {
            var cardsPath = Path.Combine(vault.Path, "cards");
            var studyDir = Path.Combine(cardsPath, ".study");
            if (!Directory.Exists(studyDir)) continue;

            var files = Directory.GetFiles(studyDir, "daily-*.json");
            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!fileName.StartsWith("daily-") || !DateTime.TryParseExact(
                    fileName[6..], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
                {
                    continue;
                }

                // 修复：TryParseExact 产出 Kind=Unspecified，写入 PostgreSQL timestamptz 会抛
                // "only UTC is supported"；统一转成 UTC 当日零点，查询与写入均用 UTC。
                var dateUtc = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var daily = System.Text.Json.JsonSerializer.Deserialize<DailyRecord>(json);
                    if (daily?.Answers == null || daily.Answers.Count == 0) continue;

                    foreach (var kv in daily.Answers)
                    {
                        // 去重：同一天同一卡片同一学习者只迁移一次
                        var alreadyExists = await db.StudyActivities.AnyAsync(
                            a => a.LearnerId == defaultLearner.Id
                                 && a.VaultId == vault.Id
                                 && a.CardId == kv.Key
                                 && a.CreatedAt >= dateUtc
                                 && a.CreatedAt < dateUtc.AddDays(1)
                                 && a.ActivityType == "study",
                            cancellationToken);

                        if (alreadyExists) continue;

                        db.StudyActivities.Add(new StudyActivity
                        {
                            LearnerId = defaultLearner.Id,
                            VaultId = vault.Id,
                            ActivityType = "study",
                            CardId = kv.Key,
                            Result = kv.Value,
                            CreatedAt = dateUtc.AddHours(12) // 默认中午 12 点（UTC）
                        });
                        totalMigrated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "迁移文件失败：{File}", file);
                }
            }
        }

        if (totalMigrated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("学习记录迁移完成：共迁移 {Count} 条记录", totalMigrated);
        }
    }
}
