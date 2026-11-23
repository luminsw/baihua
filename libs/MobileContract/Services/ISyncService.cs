using MobileContract.VaultSync;

namespace MobileContract.Services;

/// <summary>
/// 知识库同步服务接口。
/// 封装与百花后端的所有同步协议交互。
/// </summary>
public interface ISyncService
{
    /// <summary>测试与服务器的连通性</summary>
    Task TestConnectionAsync(string serverUrl, string vaultId, CancellationToken ct = default);

    /// <summary>拉取知识库文件清单</summary>
    Task<VaultManifestResponse> FetchManifestAsync(
        string serverUrl, string vaultId, string deviceId,
        int cursor = 0, CancellationToken ct = default);

    /// <summary>下载单个文件（文本）</summary>
    Task<string> DownloadTextFileAsync(
        string serverUrl, string vaultId, string relPath, CancellationToken ct = default);

    /// <summary>下载单个文件（二进制）</summary>
    Task<byte[]> DownloadBinaryFileAsync(
        string serverUrl, string vaultId, string relPath, CancellationToken ct = default);

    /// <summary>拉取 Anki 卡片 JSON</summary>
    Task<string> FetchCardsAsync(
        string serverUrl, string vaultId, string deviceId, CancellationToken ct = default);

    /// <summary>列出服务器上的所有知识库</summary>
    Task<IReadOnlyList<VaultInfo>> FetchVaultListAsync(
        string serverUrl, CancellationToken ct = default);

    /// <summary>执行完整的知识库同步</summary>
    Task<SyncResult> SyncVaultAsync(
        string serverUrl, string vaultId, string deviceId,
        IVaultStorageAdapter storage, CancellationToken ct = default);
}
