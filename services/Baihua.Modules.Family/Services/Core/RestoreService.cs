using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Baihua.Core.Services;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Core.Security;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 全量备份恢复服务
/// </summary>
public class RestoreService
{
    private readonly VaultSettingsService _vaultSettings;
    private readonly IDbContextFactory<VaultDbContext> _vaultDbContextFactory;
    private readonly IDbContextFactory<FamilyDbContext> _familyDbContextFactory;
    // 一服务一数据库：AI 提供方（含 API Key）由 AI 服务导入（Family 不接触明文 key）
    private readonly IAiConfigService _aiConfig;
    private readonly ILogger<RestoreService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public RestoreService(
        VaultSettingsService vaultSettings,
        IDbContextFactory<VaultDbContext> vaultDbContextFactory,
        IDbContextFactory<FamilyDbContext> familyDbContextFactory,
        IAiConfigService aiConfig,
        ILogger<RestoreService> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _vaultSettings = vaultSettings;
        _vaultDbContextFactory = vaultDbContextFactory;
        _familyDbContextFactory = familyDbContextFactory;
        _aiConfig = aiConfig;
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 恢复全量备份
    /// </summary>
    public async Task<FullRestoreResult> RestoreFullBackupAsync(
        string backupPath,
        string? password = null,
        string? vaultRootPathOverride = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            return new FullRestoreResult { Success = false, Error = _loc["Restore_FileNotFound"] };
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"dn_restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            ZipFile.ExtractToDirectory(backupPath, tempDir);

            var manifestPath = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return new FullRestoreResult { Success = false, Error = _loc["Restore_InvalidManifest"] };
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);
            if (manifest == null || manifest.Version < 2)
            {
                return new FullRestoreResult { Success = false, Error = _loc["Restore_UnsupportedVersion"] };
            }

            if (manifest.HasPassword && string.IsNullOrEmpty(password))
            {
                return new FullRestoreResult { Success = false, Error = _loc["Restore_PasswordRequired"] };
            }

            var vaultRootPath = !string.IsNullOrEmpty(vaultRootPathOverride)
                ? vaultRootPathOverride
                : _vaultSettings.VaultRootPathPreference;

            var dbResult = await RestoreDatabaseAsync(tempDir, password, vaultRootPath, overwrite, cancellationToken);
            if (!dbResult)
            {
                return new FullRestoreResult { Success = false, Error = _loc["Restore_DatabaseFailed"] };
            }

            cancellationToken.ThrowIfCancellationRequested();

            RestoreConfigFiles(tempDir);
            await RestoreVaultFilesAsync(tempDir, vaultRootPath, overwrite, cancellationToken);

            _logger.LogInformation("全量备份恢复成功：{Path}", backupPath);

            return new FullRestoreResult
            {
                Success = true,
                SourcePlatform = manifest.SourcePlatform,
                SourceOS = manifest.SourceOS,
                RestoredAt = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("恢复全量备份已取消");
            return new FullRestoreResult { Success = false, Error = _loc["Restore_Cancelled"] };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复全量备份失败");
            return new FullRestoreResult { Success = false, Error = ex.Message };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "清理临时目录失败: {TempDir}", tempDir); }
            }
        }
    }

    private async Task<bool> RestoreDatabaseAsync(string tempDir, string? password, string vaultRootPath, bool overwrite, CancellationToken cancellationToken)
    {
        var dbDir = Path.Combine(tempDir, "db");
        if (!Directory.Exists(dbDir)) return true;

        using var db = _vaultDbContextFactory.CreateDbContext();
        using var familyDb = _familyDbContextFactory.CreateDbContext();

        cancellationToken.ThrowIfCancellationRequested();

        // Vaults
        var vaultsPath = Path.Combine(dbDir, "vaults.json");
        if (File.Exists(vaultsPath))
        {
            var json = await File.ReadAllTextAsync(vaultsPath, cancellationToken);
            var vaultsData = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (vaultsData != null)
            {
                if (overwrite) db.Vaults.RemoveRange(db.Vaults);

                foreach (var v in vaultsData)
                {
                    var path = v.GetProperty("Path").GetString() ?? "";
                    var relativePath = v.TryGetProperty("RelativePath", out var rp) ? rp.GetString() : null;
                    var finalPath = !string.IsNullOrEmpty(relativePath) && !string.IsNullOrEmpty(vaultRootPath)
                        ? Path.Combine(vaultRootPath, relativePath)
                        : BackupPathHelper.RemapPath(path);

                    var vault = new Vault
                    {
                        VaultId = v.GetProperty("VaultId").GetString() ?? Guid.NewGuid().ToString(),
                        Name = v.GetProperty("Name").GetString() ?? "",
                        Path = finalPath,
                        IsActive = v.TryGetProperty("IsActive", out var ia) && ia.GetBoolean(),
                        CreatedAt = v.GetProperty("CreatedAt").GetDateTime(),
                        UpdatedAt = v.GetProperty("UpdatedAt").GetDateTime()
                    };

                    if (!db.Vaults.Any(x => x.VaultId == vault.VaultId))
                    {
                        db.Vaults.Add(vault);
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // AiProviderSettings——API Key 归 AI 模块独占：解密/重加密全在 IAiConfigService 实现内完成
        var providersPath = Path.Combine(dbDir, "ai_providers.json");
        if (File.Exists(providersPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(providersPath, cancellationToken);
                var providers = JsonSerializer.Deserialize<List<AiProviderBackupItem>>(json, System.Text.Json.JsonSerializerOptions.Web)
                    ?? new List<AiProviderBackupItem>();
                _aiConfig.ImportFromBackup(providers, password, replaceAll: overwrite);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI 提供方导入失败（数据无效），恢复中止");
                return false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Tasks
        var tasksPath = Path.Combine(dbDir, "tasks.json");
        if (File.Exists(tasksPath))
        {
            var json = await File.ReadAllTextAsync(tasksPath, cancellationToken);
            var tasksData = JsonSerializer.Deserialize<List<TaskEntity>>(json);
            if (tasksData != null)
            {
                if (overwrite) familyDb.Tasks.RemoveRange(familyDb.Tasks);

                foreach (var task in tasksData)
                {
                    if (!familyDb.Tasks.Any(t => t.TaskId == task.TaskId))
                    {
                        familyDb.Tasks.Add(task);
                    }
                }
                await familyDb.SaveChangesAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // AuthorizedDevices
        var devicesPath = Path.Combine(dbDir, "devices.json");
        if (File.Exists(devicesPath))
        {
            var json = await File.ReadAllTextAsync(devicesPath, cancellationToken);
            var devicesData = JsonSerializer.Deserialize<List<AuthorizedDevice>>(json);
            if (devicesData != null)
            {
                foreach (var device in devicesData)
                {
                    if (!familyDb.AuthorizedDevices.Any(d => d.DeviceId == device.DeviceId))
                    {
                        device.Status = "PendingReauth";
                        familyDb.AuthorizedDevices.Add(device);
                    }
                }
                await familyDb.SaveChangesAsync(cancellationToken);
            }
        }

        return true;
    }

    private void RestoreConfigFiles(string tempDir)
    {
        var configDir = Path.Combine(tempDir, "config");
        if (!Directory.Exists(configDir)) return;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        var webuiSrc = Path.Combine(configDir, "webui_settings.json");
        if (File.Exists(webuiSrc))
        {
            File.Copy(webuiSrc, Path.Combine(baseDir, "webui.settings.json"), overwrite: true);
        }

        var userPrefsSrc = Path.Combine(configDir, "user_preferences.json");
        if (File.Exists(userPrefsSrc))
        {
            var dataDir = Path.Combine(baseDir, "data");
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            File.Copy(userPrefsSrc, Path.Combine(dataDir, "user_preferences.json"), overwrite: true);
        }
    }

    private async Task RestoreVaultFilesAsync(string tempDir, string vaultRootPath, bool overwrite, CancellationToken cancellationToken)
    {
        var vaultsSrcDir = Path.Combine(tempDir, "vaults");
        if (!Directory.Exists(vaultsSrcDir)) return;

        using var db = _vaultDbContextFactory.CreateDbContext();
        var dbVaults = await db.Vaults.ToListAsync(cancellationToken);

        foreach (var vaultDir in Directory.GetDirectories(vaultsSrcDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vaultName = Path.GetFileName(vaultDir);

            var dbVault = dbVaults.FirstOrDefault(v =>
                string.Equals(v.Name, vaultName, StringComparison.OrdinalIgnoreCase));

            if (dbVault == null || string.IsNullOrEmpty(dbVault.Path))
            {
                _logger.LogWarning("跳过无数据库记录的知识库目录：{Name}", vaultName);
                continue;
            }

            if (!Directory.Exists(dbVault.Path))
            {
                Directory.CreateDirectory(dbVault.Path);
            }

            var notesSrc = Path.Combine(vaultDir, "notes");
            if (Directory.Exists(notesSrc))
            {
                var notesDest = Path.Combine(dbVault.Path, "notes");
                if (!Directory.Exists(notesDest)) Directory.CreateDirectory(notesDest);
                BackupPathHelper.CopyDirectory(notesSrc, notesDest, overwrite, cancellationToken);
            }

            var cardsSrc = Path.Combine(vaultDir, "cards");
            if (Directory.Exists(cardsSrc))
            {
                var cardsDest = Path.Combine(dbVault.Path, "cards");
                if (!Directory.Exists(cardsDest)) Directory.CreateDirectory(cardsDest);
                BackupPathHelper.CopyDirectory(cardsSrc, cardsDest, overwrite, cancellationToken);
            }

            var imagesSrc = Path.Combine(vaultDir, "images");
            if (Directory.Exists(imagesSrc))
            {
                var imagesDest = Path.Combine(dbVault.Path, "images");
                if (!Directory.Exists(imagesDest)) Directory.CreateDirectory(imagesDest);
                BackupPathHelper.CopyDirectory(imagesSrc, imagesDest, overwrite, cancellationToken);
            }
        }
    }
}
