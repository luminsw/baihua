using MobileContract.Services;

namespace BaihuaSdk.Storage;

/// <summary>
/// 基于本地文件系统的 IVaultStorageAdapter 实现。
/// 将知识库文件写入指定根目录，子目录按 relPath 自动创建。
/// </summary>
public class FileSystemVaultStorage : IVaultStorageAdapter
{
    private readonly string _vaultRoot;

    public FileSystemVaultStorage(string vaultRoot)
    {
        _vaultRoot = vaultRoot;
    }

    public string RootPath => _vaultRoot;

    public Task EnsureDirForFileAsync(string relPath)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return Task.CompletedTask;
    }

    public async Task WriteTextFileAsync(string relPath, string content, long mtime = 0)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        await EnsureDirForFileAsync(relPath).ConfigureAwait(false);
        await File.WriteAllTextAsync(fullPath, content, System.Text.Encoding.UTF8).ConfigureAwait(false);
        if (mtime > 0)
            File.SetLastWriteTimeUtc(fullPath, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime);
    }

    public async Task WriteBinaryFileAsync(string relPath, byte[] content, long mtime = 0)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        await EnsureDirForFileAsync(relPath).ConfigureAwait(false);
        await File.WriteAllBytesAsync(fullPath, content).ConfigureAwait(false);
        if (mtime > 0)
            File.SetLastWriteTimeUtc(fullPath, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime);
    }

    public Task DeleteFileIfExistsAsync(string relPath)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string relPath)
    {
        return Task.FromResult(File.Exists(Path.Combine(_vaultRoot, relPath)));
    }

    public Task<long> GetFileMtimeAsync(string relPath)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        if (!File.Exists(fullPath)) return Task.FromResult(0L);
        var mtime = File.GetLastWriteTimeUtc(fullPath);
        return Task.FromResult(new DateTimeOffset(mtime).ToUnixTimeMilliseconds());
    }
}