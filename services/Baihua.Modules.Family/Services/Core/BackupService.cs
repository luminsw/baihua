using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core.Security;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Contracts.Backup;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 全量备份恢复服务 - 备份所有配置与数据，支持跨平台恢复
///
/// 备份内容：
/// - SQLite 数据库（5 张表导出为 JSON）
/// - JSON 配置文件（webui.settings.json 等，知识库根路径改为环境变量 BAIHUA_HOME/vaults 配置）
/// - 知识库文件（notes/, cards/）
/// - WebUI 配置（webui.settings.json, user_preferences.json）
///
/// 跨平台支持：
/// - 路径存储为相对路径 + 根路径标记，恢复时重映射
/// - API Key 在备份时解密并用备份密码重新加密，恢复时用目标机器指纹重新加密
/// - 备份格式为 ZIP 内含 JSON，无二进制兼容问题
///
/// 备份格式：
///   backup.zip
///   ├── manifest.json          # 元数据（版本、时间、源平台、校验和）
///   ├── db/
///   │   ├── vaults.json        # Vaults 表数据
///   │   ├── ai_providers.json  # AiProviderSettings 表数据（API Key 用备份密码加密）
///   │   ├── tasks.json         # Tasks 表数据
///   │   ├── devices.json       # AuthorizedDevices 表数据
///   │   └── server_address.json # ServerAddressSettings 表数据
///   ├── config/
///   │   ├── webui_settings.json
///   │   └── user_preferences.json
///   └── vaults/
///       └── {vault_name}/      # 每个知识库一个目录
///           ├── notes/
///           └── cards/
/// </summary>
public class BackupService
{
    private readonly VaultSettingsService _vaultSettings;
    private readonly IDbContextFactory<VaultDbContext> _vaultDbContextFactory;
    private readonly IDbContextFactory<FamilyDbContext> _familyDbContextFactory;
    // 一服务一数据库：AI 提供方（含 API Key）由 AI 服务导出/导入（Family 不接触明文 key）
    private readonly IAiConfigService _aiConfig;
    private readonly RestoreService _restoreService;
    private readonly ILogger<BackupService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public BackupService(
        VaultSettingsService vaultSettings,
        IDbContextFactory<VaultDbContext> vaultDbContextFactory,
        IDbContextFactory<FamilyDbContext> familyDbContextFactory,
        IAiConfigService aiConfig,
        RestoreService restoreService,
        ILogger<BackupService> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _vaultSettings = vaultSettings;
        _vaultDbContextFactory = vaultDbContextFactory;
        _familyDbContextFactory = familyDbContextFactory;
        _aiConfig = aiConfig;
        _restoreService = restoreService;
        _logger = logger;
        _loc = loc;
    }

    #region Create Full Backup

    /// <summary>
    /// 创建全量备份
    /// </summary>
    public async Task<FullBackupResult> CreateFullBackupAsync(string? backupDir = null, string? password = null, CancellationToken cancellationToken = default)
    {
        var timestamp = DateTime.UtcNow;
        var backupFileName = $"huaji_backup_{timestamp:yyyyMMdd_HHmmss}.zip";
        var backupFullPath = string.IsNullOrEmpty(backupDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HuajiBackups", backupFileName)
            : Path.Combine(backupDir, backupFileName);

        var backupDirPath = Path.GetDirectoryName(backupFullPath);
        if (!string.IsNullOrEmpty(backupDirPath) && !Directory.Exists(backupDirPath))
        {
            Directory.CreateDirectory(backupDirPath);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"dn_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. 导出数据库表为 JSON
            await ExportDatabaseAsync(tempDir, password, cancellationToken);

            // 2. 导出配置文件
            ExportConfigFiles(tempDir);

            // 3. 导出知识库文件
            await ExportVaultFilesAsync(tempDir, cancellationToken);

            // 4. 写入 manifest
            var manifest = new BackupManifest
            {
                Version = 2,
                CreatedAt = timestamp,
                SourcePlatform = Environment.OSVersion.Platform.ToString(),
                SourceOS = Environment.OSVersion.ToString(),
                SourceMachineName = Environment.MachineName,
                HasPassword = !string.IsNullOrEmpty(password),
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown"
            };
            var manifestJson = JsonSerializer.Serialize(manifest, _jsonOpts);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "manifest.json"), manifestJson);

            // 5. 创建 ZIP
            ZipFile.CreateFromDirectory(tempDir, backupFullPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            return new FullBackupResult
            {
                Success = true,
                BackupPath = backupFullPath,
                BackupTime = timestamp,
                FileSize = new FileInfo(backupFullPath).Length
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("创建全量备份已取消");
            return new FullBackupResult
            {
                Success = false,
                Error = _loc["Backup_Cancelled"]
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建全量备份失败");
            return new FullBackupResult
            {
                Success = false,
                Error = ex.Message
            };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "清理临时目录失败: {TempDir}", tempDir); }
            }
        }
    }

    /// <summary>
    /// 导出数据库表为 JSON
    /// </summary>
    private async Task ExportDatabaseAsync(string tempDir, string? password, CancellationToken cancellationToken)
    {
        var dbDir = Path.Combine(tempDir, "db");
        Directory.CreateDirectory(dbDir);

        using var db = _vaultDbContextFactory.CreateDbContext();
        using var familyDb = _familyDbContextFactory.CreateDbContext();

        cancellationToken.ThrowIfCancellationRequested();

        // Vaults - 路径转为相对路径
        var vaults = await db.Vaults.ToListAsync(cancellationToken);
        var vaultRootPath = _vaultSettings.VaultRootPathPreference;
        var vaultsData = vaults.Select(v => new
        {
            v.Id, v.VaultId, v.Name,
            Path = v.Path,
            RelativePath = BackupPathHelper.MakeRelativePath(v.Path, vaultRootPath),
            v.IsActive, v.CreatedAt, v.UpdatedAt
        }).ToList();
        await File.WriteAllTextAsync(Path.Combine(dbDir, "vaults.json"),
            JsonSerializer.Serialize(vaultsData, _jsonOpts));

        // AiProviderSettings（含 API Key）——API Key 归 AI 模块独占：
        // 经 IAiConfigService 在进程内导出（AI 模块内解密后用备份密码/机器密钥重新加密，其他模块不接触明文 key）
        var providersJson = JsonSerializer.Serialize(_aiConfig.ExportForBackup(password), _jsonOpts);
        await File.WriteAllTextAsync(Path.Combine(dbDir, "ai_providers.json"),
            providersJson, cancellationToken);

        // Tasks
        var tasks = await familyDb.Tasks.ToListAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dbDir, "tasks.json"),
            JsonSerializer.Serialize(tasks, _jsonOpts), cancellationToken);

        // AuthorizedDevices
        var devices = await familyDb.AuthorizedDevices.ToListAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dbDir, "devices.json"),
            JsonSerializer.Serialize(devices, _jsonOpts), cancellationToken);

        // ServerAddressSettings - 不导出 ServerInstanceId（恢复时重新生成）
        var serverAddr = await familyDb.ServerAddressSettings.ToListAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dbDir, "server_address.json"),
            JsonSerializer.Serialize(serverAddr, _jsonOpts), cancellationToken);
    }

    /// <summary>
    /// 导出配置文件
    /// </summary>
    private void ExportConfigFiles(string tempDir)
    {
        var configDir = Path.Combine(tempDir, "config");
        Directory.CreateDirectory(configDir);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // vault.root.path.json 已废弃（知识库根路径改为环境变量配置），不再备份
        // 旧备份中可能仍有此文件，但恢复时会被忽略

        // WebUI 配置文件（可能在 WebUI 的 base directory）
        var webuiSettingsPath = Path.Combine(baseDir, "webui.settings.json");
        if (File.Exists(webuiSettingsPath))
        {
            File.Copy(webuiSettingsPath, Path.Combine(configDir, "webui_settings.json"), overwrite: true);
        }

        var userPrefsPath = Path.Combine(baseDir, "data", "user_preferences.json");
        if (File.Exists(userPrefsPath))
        {
            File.Copy(userPrefsPath, Path.Combine(configDir, "user_preferences.json"), overwrite: true);
        }
    }

    /// <summary>
    /// 导出知识库文件
    /// </summary>
    private async Task ExportVaultFilesAsync(string tempDir, CancellationToken cancellationToken)
    {
        var vaultsDir = Path.Combine(tempDir, "vaults");
        Directory.CreateDirectory(vaultsDir);

        using var db = _vaultDbContextFactory.CreateDbContext();
        var vaults = await db.Vaults.ToListAsync(cancellationToken);

        foreach (var vault in vaults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(vault.Path) || !Directory.Exists(vault.Path))
                continue;

            // 用知识库名称作为目录名（替换不安全字符）
            var safeName = string.Join("_", vault.Name.Split(Path.GetInvalidFileNameChars()));
            var vaultBackupDir = Path.Combine(vaultsDir, safeName);
            Directory.CreateDirectory(vaultBackupDir);

            // 复制 notes/ 和 cards/ 子目录
            var notesDir = Path.Combine(vault.Path, "notes");
            if (Directory.Exists(notesDir))
            {
                BackupPathHelper.CopyDirectory(notesDir, Path.Combine(vaultBackupDir, "notes"), cancellationToken: cancellationToken);
            }

            var cardsDir = Path.Combine(vault.Path, "cards");
            if (Directory.Exists(cardsDir))
            {
                BackupPathHelper.CopyDirectory(cardsDir, Path.Combine(vaultBackupDir, "cards"), cancellationToken: cancellationToken);
            }

            // 复制 images/ 子目录（如果存在）
            var imagesDir = Path.Combine(vault.Path, "images");
            if (Directory.Exists(imagesDir))
            {
                BackupPathHelper.CopyDirectory(imagesDir, Path.Combine(vaultBackupDir, "images"), cancellationToken: cancellationToken);
            }
        }
    }

    #endregion

    #region Restore

    public Task<FullRestoreResult> RestoreFullBackupAsync(
        string backupPath,
        string? password = null,
        string? vaultRootPathOverride = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
        => _restoreService.RestoreFullBackupAsync(backupPath, password, vaultRootPathOverride, overwrite, cancellationToken);

    #endregion

    #region Validate & List

    /// <summary>
    /// 验证备份文件
    /// </summary>
    public async Task<BackupValidationResult> ValidateFullBackupAsync(string backupPath, string? password = null)
    {
        if (!File.Exists(backupPath))
        {
            return new BackupValidationResult { IsValid = false, Error = _loc["Backup_FileNotFound"] };
        }

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"dn_validate_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ZipFile.ExtractToDirectory(backupPath, tempDir);

                var manifestPath = Path.Combine(tempDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return new BackupValidationResult { IsValid = false, Error = _loc["Backup_MissingManifest"] };
                }

                var manifestJson = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

                if (manifest == null)
                {
                    return new BackupValidationResult { IsValid = false, Error = _loc["Backup_InvalidManifestFormat"] };
                }

                // 统计备份内容
                var dbDir = Path.Combine(tempDir, "db");
                var configDir = Path.Combine(tempDir, "config");
                var vaultsDir = Path.Combine(tempDir, "vaults");

                return new BackupValidationResult
                {
                    IsValid = true,
                    Version = manifest.Version,
                    CreatedAt = manifest.CreatedAt,
                    SourcePlatform = manifest.SourcePlatform,
                    SourceOS = manifest.SourceOS,
                    HasPassword = manifest.HasPassword,
                    HasDatabase = Directory.Exists(dbDir),
                    HasConfig = Directory.Exists(configDir),
                    HasVaults = Directory.Exists(vaultsDir),
                    VaultCount = Directory.Exists(vaultsDir) ? Directory.GetDirectories(vaultsDir).Length : 0
                };
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "清理临时目录失败: {TempDir}", tempDir); }
                }
            }
        }
        catch (Exception ex)
        {
            return new BackupValidationResult { IsValid = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// 获取备份列表
    /// </summary>
    public List<Baihua.Contracts.Backup.BackupFileInfo> GetBackupList(string? backupPath = null)
    {
        var backupDir = string.IsNullOrEmpty(backupPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HuajiBackups")
            : backupPath;

        if (!Directory.Exists(backupDir))
        {
            return new List<Baihua.Contracts.Backup.BackupFileInfo>();
        }

        return Directory.GetFiles(backupDir, "huaji_backup_*.zip")
            .Concat(Directory.GetFiles(backupDir, "backup_*.zip"))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Select(f => new Baihua.Contracts.Backup.BackupFileInfo
            {
                Path = f,
                FileName = Path.GetFileName(f),
                Size = new FileInfo(f).Length,
                CreationTime = File.GetCreationTime(f)
            })
            .ToList();
    }

    #endregion
}
