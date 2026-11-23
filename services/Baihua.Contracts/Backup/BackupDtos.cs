namespace Baihua.Contracts.Backup;

public class FullBackupResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? BackupPath { get; set; }
    public DateTime? BackupTime { get; set; }
    public long? FileSize { get; set; }
}

public class FullRestoreResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? SourcePlatform { get; set; }
    public string? SourceOS { get; set; }
    public DateTime? RestoredAt { get; set; }
}

public class ValidateBackupResponse
{
    public bool Success { get; set; }
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
    public string Message { get; set; } = "";
}

public class BackupListResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public List<BackupFileInfo> Backups { get; set; } = new();
}

public class BackupFileInfo
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreationTime { get; set; }
}
