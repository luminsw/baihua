using BaihuaSdk.Storage;
using Xunit;

namespace BaihuaSdk.Tests.Storage;

public class FileSystemVaultStorageTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileSystemVaultStorage _storage;

    public FileSystemVaultStorageTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"vault_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _storage = new FileSystemVaultStorage(_testDir);
    }

    [Fact]
    public async Task WriteTextFileAsync_WritesContent()
    {
        await _storage.WriteTextFileAsync("test.md", "# Hello");

        var fullPath = Path.Combine(_testDir, "test.md");
        Assert.True(File.Exists(fullPath));
        Assert.Equal("# Hello", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task WriteTextFileAsync_WithSubdirectory_CreatesDir()
    {
        await _storage.WriteTextFileAsync("sub/folder/nested.md", "nested content");

        var fullPath = Path.Combine(_testDir, "sub", "folder", "nested.md");
        Assert.True(File.Exists(fullPath));
        Assert.Equal("nested content", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task WriteTextFileAsync_WithMtime_SetsLastWriteTime()
    {
        var mtime = 1700000000000;
        await _storage.WriteTextFileAsync("mtime.md", "test", mtime);

        var fullPath = Path.Combine(_testDir, "mtime.md");
        var actual = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)).ToUnixTimeMilliseconds();
        Assert.Equal(mtime, actual);
    }

    [Fact]
    public async Task WriteBinaryFileAsync_WritesBytes()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        await _storage.WriteBinaryFileAsync("image.png", bytes);

        var fullPath = Path.Combine(_testDir, "image.png");
        Assert.True(File.Exists(fullPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task WriteBinaryFileAsync_WithSubdirectory_CreatesDir()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await _storage.WriteBinaryFileAsync("assets/icon.png", bytes);

        var fullPath = Path.Combine(_testDir, "assets", "icon.png");
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task DeleteFileIfExistsAsync_ExistingFile_Deletes()
    {
        await _storage.WriteTextFileAsync("todelete.md", "delete me");

        await _storage.DeleteFileIfExistsAsync("todelete.md");

        Assert.False(File.Exists(Path.Combine(_testDir, "todelete.md")));
    }

    [Fact]
    public async Task DeleteFileIfExistsAsync_NonExistent_DoesNotThrow()
    {
        await _storage.DeleteFileIfExistsAsync("nonexistent.md");
    }

    [Fact]
    public async Task FileExistsAsync_Existing_ReturnsTrue()
    {
        await _storage.WriteTextFileAsync("exists.md", "test");

        Assert.True(await _storage.FileExistsAsync("exists.md"));
    }

    [Fact]
    public async Task FileExistsAsync_NonExistent_ReturnsFalse()
    {
        Assert.False(await _storage.FileExistsAsync("nonexistent.md"));
    }

    [Fact]
    public async Task GetFileMtimeAsync_Existing_ReturnsCorrectValue()
    {
        var mtime = 1700000000000;
        await _storage.WriteTextFileAsync("mtime.md", "test", mtime);

        var result = await _storage.GetFileMtimeAsync("mtime.md");
        Assert.Equal(mtime, result);
    }

    [Fact]
    public async Task GetFileMtimeAsync_NonExistent_ReturnsZero()
    {
        var result = await _storage.GetFileMtimeAsync("nonexistent.md");
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task EnsureDirForFileAsync_CreatesParentDirectory()
    {
        await _storage.EnsureDirForFileAsync("deep/nested/file.md");

        var dirPath = Path.Combine(_testDir, "deep", "nested");
        Assert.True(Directory.Exists(dirPath));
    }

    [Fact]
    public async Task EnsureDirForFileAsync_RootFile_NoOp()
    {
        await _storage.EnsureDirForFileAsync("root.md");

        Assert.True(Directory.Exists(_testDir));
    }

    [Fact]
    public async Task WriteTextFileAsync_ZeroMtime_DoesNotSetTime()
    {
        await _storage.WriteTextFileAsync("default-mtime.md", "test", 0);

        var fullPath = Path.Combine(_testDir, "default-mtime.md");
        var actual = File.GetLastWriteTimeUtc(fullPath);
        Assert.True(actual > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task WriteBinaryFileAsync_ZeroMtime_DoesNotSetTime()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await _storage.WriteBinaryFileAsync("default-mtime.bin", bytes, 0);

        var fullPath = Path.Combine(_testDir, "default-mtime.bin");
        var actual = File.GetLastWriteTimeUtc(fullPath);
        Assert.True(actual > DateTime.UtcNow.AddMinutes(-1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }
}