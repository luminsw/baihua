using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class BackupDtoTests
{
    #region BackupManifest

    [Fact]
    public void BackupManifest_DefaultValues_AreCorrect()
    {
        var manifest = new BackupManifest();

        Assert.Equal(0, manifest.Version);
        Assert.Equal(default, manifest.CreatedAt);
        Assert.Equal("", manifest.SourcePlatform);
        Assert.Equal("", manifest.SourceOS);
        Assert.Equal("", manifest.SourceMachineName);
        Assert.False(manifest.HasPassword);
        Assert.Equal("", manifest.AppVersion);
    }

    [Fact]
    public void BackupManifest_CanSetProperties()
    {
        var createdAt = DateTime.UtcNow;
        var manifest = new BackupManifest
        {
            Version = 1,
            CreatedAt = createdAt,
            SourcePlatform = "Linux",
            SourceOS = "Ubuntu 22.04",
            SourceMachineName = "HomeServer",
            HasPassword = true,
            AppVersion = "1.0.0"
        };

        Assert.Equal(1, manifest.Version);
        Assert.Equal(createdAt, manifest.CreatedAt);
        Assert.Equal("Linux", manifest.SourcePlatform);
        Assert.Equal("Ubuntu 22.04", manifest.SourceOS);
        Assert.Equal("HomeServer", manifest.SourceMachineName);
        Assert.True(manifest.HasPassword);
        Assert.Equal("1.0.0", manifest.AppVersion);
    }

    #endregion

    #region FullBackupResult

    [Fact]
    public void FullBackupResult_DefaultValues_AreCorrect()
    {
        var result = new FullBackupResult();

        Assert.False(result.Success);
        Assert.Null(result.BackupPath);
        Assert.Null(result.BackupTime);
        Assert.Null(result.FileSize);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FullBackupResult_Success_CanSetProperties()
    {
        var backupTime = DateTime.UtcNow;
        var result = new FullBackupResult
        {
            Success = true,
            BackupPath = "/backups/backup-2024-06-21.zip",
            BackupTime = backupTime,
            FileSize = 1024000
        };

        Assert.True(result.Success);
        Assert.Equal("/backups/backup-2024-06-21.zip", result.BackupPath);
        Assert.Equal(backupTime, result.BackupTime);
        Assert.Equal(1024000, result.FileSize);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FullBackupResult_Failure_CanSetError()
    {
        var result = new FullBackupResult
        {
            Success = false,
            Error = "备份失败：磁盘空间不足"
        };

        Assert.False(result.Success);
        Assert.Equal("备份失败：磁盘空间不足", result.Error);
    }

    #endregion

    #region FullRestoreResult

    [Fact]
    public void FullRestoreResult_DefaultValues_AreCorrect()
    {
        var result = new FullRestoreResult();

        Assert.False(result.Success);
        Assert.Null(result.SourcePlatform);
        Assert.Null(result.SourceOS);
        Assert.Null(result.RestoredAt);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FullRestoreResult_Success_CanSetProperties()
    {
        var restoredAt = DateTime.UtcNow;
        var result = new FullRestoreResult
        {
            Success = true,
            SourcePlatform = "Windows",
            SourceOS = "Windows 11",
            RestoredAt = restoredAt
        };

        Assert.True(result.Success);
        Assert.Equal("Windows", result.SourcePlatform);
        Assert.Equal("Windows 11", result.SourceOS);
        Assert.Equal(restoredAt, result.RestoredAt);
    }

    [Fact]
    public void FullRestoreResult_Failure_CanSetError()
    {
        var result = new FullRestoreResult
        {
            Success = false,
            Error = "恢复失败：备份文件损坏"
        };

        Assert.False(result.Success);
        Assert.Equal("恢复失败：备份文件损坏", result.Error);
    }

    #endregion

    #region BackupValidationResult

    [Fact]
    public void BackupValidationResult_DefaultValues_AreCorrect()
    {
        var result = new BackupValidationResult();

        Assert.False(result.IsValid);
        Assert.Equal(0, result.Version);
        Assert.Null(result.CreatedAt);
        Assert.Null(result.SourcePlatform);
        Assert.Null(result.SourceOS);
        Assert.False(result.HasPassword);
        Assert.False(result.HasDatabase);
        Assert.False(result.HasConfig);
        Assert.False(result.HasVaults);
        Assert.Equal(0, result.VaultCount);
        Assert.Null(result.Error);
    }

    [Fact]
    public void BackupValidationResult_Valid_CanSetProperties()
    {
        var createdAt = DateTime.UtcNow;
        var result = new BackupValidationResult
        {
            IsValid = true,
            Version = 1,
            CreatedAt = createdAt,
            SourcePlatform = "Linux",
            SourceOS = "Ubuntu 22.04",
            HasPassword = false,
            HasDatabase = true,
            HasConfig = true,
            HasVaults = true,
            VaultCount = 3
        };

        Assert.True(result.IsValid);
        Assert.Equal(1, result.Version);
        Assert.Equal(createdAt, result.CreatedAt);
        Assert.Equal("Linux", result.SourcePlatform);
        Assert.Equal("Ubuntu 22.04", result.SourceOS);
        Assert.False(result.HasPassword);
        Assert.True(result.HasDatabase);
        Assert.True(result.HasConfig);
        Assert.True(result.HasVaults);
        Assert.Equal(3, result.VaultCount);
    }

    [Fact]
    public void BackupValidationResult_Invalid_CanSetError()
    {
        var result = new BackupValidationResult
        {
            IsValid = false,
            Error = "备份格式不兼容"
        };

        Assert.False(result.IsValid);
        Assert.Equal("备份格式不兼容", result.Error);
    }

    #endregion

    #region BackupFileInfo

    [Fact]
    public void BackupFileInfo_DefaultValues_AreCorrect()
    {
        var info = new BackupFileInfo();

        Assert.Equal("", info.Path);
        Assert.Equal("", info.FileName);
        Assert.Equal(0, info.Size);
        Assert.Equal(default, info.CreationTime);
    }

    [Fact]
    public void BackupFileInfo_CanSetProperties()
    {
        var creationTime = DateTime.UtcNow;
        var info = new BackupFileInfo
        {
            Path = "/backups/backup.zip",
            FileName = "backup.zip",
            Size = 1024000,
            CreationTime = creationTime
        };

        Assert.Equal("/backups/backup.zip", info.Path);
        Assert.Equal("backup.zip", info.FileName);
        Assert.Equal(1024000, info.Size);
        Assert.Equal(creationTime, info.CreationTime);
    }

    [Fact]
    public void BackupFileInfo_CanCalculateSizeInMB()
    {
        var info = new BackupFileInfo { Size = 1024 * 1024 * 5 }; // 5 MB
        var sizeMB = info.Size / (1024 * 1024);
        Assert.Equal(5, sizeMB);
    }

    #endregion
}