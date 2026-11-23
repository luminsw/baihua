namespace MobileContract.Services;

/// <summary>
/// 文件系统适配器接口。
/// SDK 通过此接口读写同步下来的文件，平台层提供具体实现。
/// </summary>
public interface IVaultStorageAdapter
{
    /// <summary>存储根目录（平台层实现提供，用于元数据读写）</summary>
    string RootPath { get; }

    Task EnsureDirForFileAsync(string relPath);
    Task WriteTextFileAsync(string relPath, string content, long mtime = 0);
    Task WriteBinaryFileAsync(string relPath, byte[] content, long mtime = 0);
    Task DeleteFileIfExistsAsync(string relPath);
    Task<bool> FileExistsAsync(string relPath);
    Task<long> GetFileMtimeAsync(string relPath);
}
