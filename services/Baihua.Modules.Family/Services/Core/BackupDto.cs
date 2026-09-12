using Baihua.Contracts.Backup;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 备份清单
/// </summary>
public class BackupManifest
{
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public string SourcePlatform { get; set; } = "";
    public string SourceOS { get; set; } = "";
    public string SourceMachineName { get; set; } = "";
    public bool HasPassword { get; set; }
    public string AppVersion { get; set; } = "";
}

/// <summary>
/// 全量备份结果
/// </summary>
public class FullBackupResult
{
    public bool Success { get; set; }
    public string? BackupPath { get; set; }
    public DateTime? BackupTime { get; set; }
    public long? FileSize { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 全量恢复结果
/// </summary>
public class FullRestoreResult
{
    public bool Success { get; set; }
    public string? SourcePlatform { get; set; }
    public string? SourceOS { get; set; }
    public DateTime? RestoredAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 备份验证结果
/// </summary>
public class BackupValidationResult
{
    public bool IsValid { get; set; }
    public int Version { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? SourcePlatform { get; set; }
    public string? SourceOS { get; set; }
    public bool HasPassword { get; set; }
    public bool HasDatabase { get; set; }
    public bool HasConfig { get; set; }
    public bool HasVaults { get; set; }
    public int VaultCount { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 备份文件信息
/// </summary>
public class BackupFileInfo : Baihua.Contracts.Backup.BackupFileInfo { }
