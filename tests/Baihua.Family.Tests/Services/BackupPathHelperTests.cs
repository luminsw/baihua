using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class BackupPathHelperTests
{
    [Fact]
    public void MakeRelativePath_NullPath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath(null!, "/base");

        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_NullBasePath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath("/path/to/file", null!);

        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_EmptyPath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath("", "/base");

        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_EmptyBasePath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath("/path/to/file", "");

        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_PathUnderBase_ReturnsRelative()
    {
        var result = BackupPathHelper.MakeRelativePath("/home/user/vaults/notes/test.md", "/home/user/vaults");

        Assert.Equal("notes/test.md", result);
    }

    [Fact]
    public void MakeRelativePath_PathUnderBaseWithSubdir_ReturnsRelative()
    {
        var result = BackupPathHelper.MakeRelativePath("/home/user/vaults/notes/chinese/伤寒论.md", "/home/user/vaults/notes");

        Assert.Equal("chinese/伤寒论.md", result);
    }

    [Fact]
    public void MakeRelativePath_PathNotUnderBase_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath("/home/other/file.md", "/home/user/vaults");

        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_PathEqualsBase_ReturnsEmpty()
    {
        var result = BackupPathHelper.MakeRelativePath("/home/user/vaults", "/home/user/vaults");

        Assert.Equal("", result);
    }

    [Fact]
    public void MakeRelativePath_CaseInsensitive_ReturnsRelative()
    {
        var result = BackupPathHelper.MakeRelativePath("/HOME/USER/VAULTS/notes/test.md", "/home/user/vaults");

        Assert.Equal("notes/test.md", result);
    }

    [Fact]
    public void MakeRelativePath_BackslashPath_ReturnsRelative()
    {
        var result = BackupPathHelper.MakeRelativePath("D:\\Vaults\\notes\\test.md", "D:\\Vaults");

        Assert.Equal("notes/test.md", result);
    }

    [Fact]
    public void MakeRelativePath_TrailingSlashInBase_HandledCorrectly()
    {
        var result = BackupPathHelper.MakeRelativePath("/home/user/vaults/notes/test.md", "/home/user/vaults/");

        Assert.Equal("notes/test.md", result);
    }

    [Fact]
    public void MakeRelativePath_PathWithMixedSlashes_ReturnsRelative()
    {
        var result = BackupPathHelper.MakeRelativePath("/home/user/vaults\\notes/test.md", "/home/user/vaults");

        Assert.Equal("notes/test.md", result);
    }

    [Fact]
    public void MakeRelativePath_ChinesePath_ReturnsRelative()
    {
        var result = BackupPathHelper.MakeRelativePath("/home/user/vaults/方剂/桂枝汤.md", "/home/user/vaults");

        Assert.Equal("方剂/桂枝汤.md", result);
    }

    [Fact]
    public void RemapPath_EmptyPath_ReturnsEmpty()
    {
        var result = BackupPathHelper.RemapPath("");

        Assert.Equal("", result);
    }

    [Fact]
    public void RemapPath_ExistingPath_ReturnsOriginal()
    {
        var tempPath = Path.GetTempPath();
        var result = BackupPathHelper.RemapPath(tempPath);

        Assert.Equal(tempPath, result);
    }

    [Fact]
    public void RemapPath_NonExistingPath_ReturnsOriginal()
    {
        var nonExistingPath = "/non/existing/path/12345";
        var result = BackupPathHelper.RemapPath(nonExistingPath);

        Assert.Equal(nonExistingPath, result);
    }
}