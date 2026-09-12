using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class BackupDtoAdditionalTests
{
    [Fact]
    public void BackupManifest_Defaults_ZeroVersionEmptyStrings()
    {
        var m = new BackupManifest();

        Assert.Equal(0, m.Version);
        Assert.Equal(default(DateTime), m.CreatedAt);
        Assert.Equal("", m.SourcePlatform);
        Assert.Equal("", m.SourceOS);
        Assert.Equal("", m.SourceMachineName);
        Assert.False(m.HasPassword);
        Assert.Equal("", m.AppVersion);
    }

    [Fact]
    public void BackupManifest_SetAllProperties_StoresValues()
    {
        var created = new DateTime(2024, 6, 1, 10, 0, 0);
        var m = new BackupManifest
        {
            Version = 1,
            CreatedAt = created,
            SourcePlatform = "Family",
            SourceOS = "Linux",
            SourceMachineName = "home-server",
            HasPassword = true,
            AppVersion = "1.2.3"
        };

        Assert.Equal(1, m.Version);
        Assert.Equal(created, m.CreatedAt);
        Assert.Equal("Family", m.SourcePlatform);
        Assert.Equal("Linux", m.SourceOS);
        Assert.Equal("home-server", m.SourceMachineName);
        Assert.True(m.HasPassword);
        Assert.Equal("1.2.3", m.AppVersion);
    }

    [Fact]
    public void FullBackupResult_Defaults_SuccessFalseAllNull()
    {
        var r = new FullBackupResult();

        Assert.False(r.Success);
        Assert.Null(r.BackupPath);
        Assert.Null(r.BackupTime);
        Assert.Null(r.FileSize);
        Assert.Null(r.Error);
    }

    [Fact]
    public void FullBackupResult_SetProperties_StoresValues()
    {
        var r = new FullBackupResult
        {
            Success = true,
            BackupPath = "/tmp/backup.zip",
            BackupTime = DateTime.UtcNow,
            FileSize = 1024L * 1024L,
            Error = null
        };

        Assert.True(r.Success);
        Assert.Equal("/tmp/backup.zip", r.BackupPath);
        Assert.NotNull(r.BackupTime);
        Assert.Equal(1024L * 1024L, r.FileSize);
        Assert.Null(r.Error);
    }

    [Fact]
    public void FullRestoreResult_Defaults_SuccessFalseAllNull()
    {
        var r = new FullRestoreResult();

        Assert.False(r.Success);
        Assert.Null(r.SourcePlatform);
        Assert.Null(r.SourceOS);
        Assert.Null(r.RestoredAt);
        Assert.Null(r.Error);
    }

    [Fact]
    public void BackupValidationResult_Defaults_AllFalseZero()
    {
        var r = new BackupValidationResult();

        Assert.False(r.IsValid);
        Assert.Equal(0, r.Version);
        Assert.Null(r.CreatedAt);
        Assert.Null(r.SourcePlatform);
        Assert.Null(r.SourceOS);
        Assert.False(r.HasPassword);
        Assert.False(r.HasDatabase);
        Assert.False(r.HasConfig);
        Assert.False(r.HasVaults);
        Assert.Equal(0, r.VaultCount);
        Assert.Null(r.Error);
    }

    [Fact]
    public void BackupFileInfo_Defaults_EmptyPathZeroSize()
    {
        var f = new BackupFileInfo();

        Assert.Equal("", f.Path);
        Assert.Equal("", f.FileName);
        Assert.Equal(0L, f.Size);
        Assert.Equal(default(DateTime), f.CreationTime);
    }

    [Fact]
    public void BackupFileInfo_SetProperties_StoresValues()
    {
        var creation = new DateTime(2024, 1, 1);
        var f = new BackupFileInfo
        {
            Path = "/tmp/backup/file.zip",
            FileName = "file.zip",
            Size = 1024,
            CreationTime = creation
        };

        Assert.Equal("/tmp/backup/file.zip", f.Path);
        Assert.Equal("file.zip", f.FileName);
        Assert.Equal(1024L, f.Size);
        Assert.Equal(creation, f.CreationTime);
    }
}
