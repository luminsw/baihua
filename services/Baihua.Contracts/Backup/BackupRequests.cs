namespace Baihua.Contracts.Backup;

public class FullBackupRequest
{
    public string? BackupDir { get; set; }
    public string? Password { get; set; }
}

public class FullRestoreRequest
{
    public string BackupPath { get; set; } = "";
    public string? Password { get; set; }
    public string? VaultRootPathOverride { get; set; }
    public bool Overwrite { get; set; } = true;
}

public class ValidateBackupRequest
{
    public string BackupPath { get; set; } = "";
    public string? Password { get; set; }
}