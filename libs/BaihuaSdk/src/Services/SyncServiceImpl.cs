using System.Collections.Concurrent;
using System.Net;
using MobileContract.Services;
using MobileContract.VaultSync;
using BaihuaSdk.Signing;
using BaihuaSdk.Transport;

namespace BaihuaSdk.Services;

/// <summary>
/// 知识库同步服务实现。
/// 封装 manifest 拉取、文件下载、知识库列表、同步循环。
/// 与 Kotlin 端同步客户端 逻辑对齐。
/// </summary>
public class SyncServiceImpl : ISyncService
{
    private static readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".json", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico"
    };

    private static readonly ConcurrentDictionary<string, VaultListCacheEntry> _vaultListCache = new();
    private static readonly TimeSpan _vaultListCacheTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly IRequestSigner _signer;

    public SyncServiceImpl(HttpClient httpClient, IRequestSigner signer)
    {
        _httpClient = httpClient;
        _signer = signer;
    }

    // ---- ISyncService ----

    public async Task TestConnectionAsync(string serverUrl, string vaultId, CancellationToken ct = default)
    {
        var transport = CreateTransport(serverUrl, vaultId);
        var resp = await transport.GetAsync("/mg/manifest", ct: ct);
        if (!resp.IsSuccess && resp.StatusCode == 0)
            throw new HttpRequestException(resp.ErrorMessage ?? "无法连接到服务器");
    }

    public async Task<VaultManifestResponse> FetchManifestAsync(
        string serverUrl, string vaultId, string deviceId,
        int cursor = 0, CancellationToken ct = default)
    {
        var transport = CreateTransport(serverUrl, vaultId, deviceId);
        var query = new Dictionary<string, string>();
        if (cursor > 0) query["cursor"] = cursor.ToString();

        var resp = await transport.GetJsonAsync<VaultManifestResponse>("/mg/manifest", query, ct);
        if (!resp.IsSuccess)
            throw new HttpRequestException(resp.ErrorMessage ?? "获取清单失败");
        return resp.Data!;
    }

    public async Task<string> DownloadTextFileAsync(
        string serverUrl, string vaultId, string relPath, CancellationToken ct = default)
    {
        AssertValidRelPath(relPath);
        var transport = CreateTransport(serverUrl, vaultId);
        var resp = await transport.GetAsync("/mg/file",
            new Dictionary<string, string> { ["path"] = relPath }, ct);
        if (!resp.IsSuccess)
            throw new HttpRequestException(resp.ErrorMessage ?? "下载文件失败", null, ToStatusCode(resp));
        return resp.Data!;
    }

    public async Task<byte[]> DownloadBinaryFileAsync(
        string serverUrl, string vaultId, string relPath, CancellationToken ct = default)
    {
        AssertValidRelPath(relPath);
        var transport = CreateTransport(serverUrl, vaultId);
        var resp = await transport.GetBytesAsync("/mg/file",
            new Dictionary<string, string> { ["path"] = relPath }, ct);
        if (!resp.IsSuccess)
            throw new HttpRequestException(resp.ErrorMessage ?? "下载文件失败", null, ToStatusCode(resp));
        return resp.Data!;
    }

    public async Task<string> FetchCardsAsync(
        string serverUrl, string vaultId, string deviceId, CancellationToken ct = default)
    {
        var transport = CreateTransport(serverUrl, vaultId, deviceId);
        var resp = await transport.GetAsync("/mg/cards", ct: ct);
        if (!resp.IsSuccess)
            throw new HttpRequestException(resp.ErrorMessage ?? "获取卡片失败");
        return resp.Data!;
    }

    public async Task<IReadOnlyList<VaultInfo>> FetchVaultListAsync(
        string serverUrl, CancellationToken ct = default)
    {
        var key = serverUrl.TrimEnd('/').ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        if (_vaultListCache.TryGetValue(key, out var cached) && (now - cached.CachedAt) < _vaultListCacheTtl)
            return cached.Vaults;

        var transport = CreateTransport(serverUrl);
        var resp = await transport.GetJsonAsync<VaultInfo[]>("/mg/vaults", ct: ct);
        if (!resp.IsSuccess)
            throw new HttpRequestException(resp.ErrorMessage ?? "获取知识库列表失败");

        var list = resp.Data ?? Array.Empty<VaultInfo>();
        _vaultListCache[key] = new VaultListCacheEntry(list, now);
        return list;
    }

    public async Task<SyncResult> SyncVaultAsync(
        string serverUrl, string vaultId, string deviceId,
        IVaultStorageAdapter storage, CancellationToken ct = default)
    {
        var manifest = await FetchManifestAsync(serverUrl, vaultId, deviceId, ct: ct);
        var files = manifest.Files ?? Array.Empty<ManifestFile>();

        int downloaded = 0, deleted = 0, failed = 0;
        var errors = new List<string>();

        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.Op) || string.IsNullOrEmpty(file.RelPath))
                continue;

            if (file.Op == "delete")
            {
                try
                {
                    await storage.DeleteFileIfExistsAsync(file.RelPath);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"删除 {file.RelPath} 失败: {ex.Message}");
                }
                continue;
            }

            var ext = Path.GetExtension(file.RelPath).ToLowerInvariant();
            if (!_allowedExtensions.Contains(ext)) continue;

            try
            {
                var validPath = AssertValidRelPath(file.RelPath);

                // 全量同步：清单里每个文件都下载写入（不做客户端增量跳过，保持行为最简）。
                // 服务端 manifest 的 mtime 为 unix 秒，而 IVaultStorageAdapter 按毫秒读写，
                // 统一换算为毫秒写入，保证本地文件时间戳正确。
                var serverMtimeMs = (file.Mtime ?? 0) * 1000L;

                await storage.EnsureDirForFileAsync(validPath);

                if (IsTextFile(validPath))
                {
                    var content = await DownloadTextFileAsync(serverUrl, vaultId, validPath, ct);
                    await storage.WriteTextFileAsync(validPath, content, serverMtimeMs);
                }
                else
                {
                    var content = await DownloadBinaryFileAsync(serverUrl, vaultId, validPath, ct);
                    await storage.WriteBinaryFileAsync(validPath, content, serverMtimeMs);
                }
                downloaded++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // 服务端文件已不存在（manifest 快照与文件删除之间存在竞态），跳过
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{file.RelPath}: {ex.Message}");
            }
        }

        return new SyncResult(
            Cursor: manifest.Cursor ?? 0,
            TotalFiles: files.Count,
            Downloaded: downloaded,
            Skipped: 0,
            Deleted: deleted,
            Failed: failed,
            Errors: errors.Count > 0 ? errors : null);
    }

    /// <summary>清空知识库列表缓存（服务器切换时调用）</summary>
    public static void ClearVaultListCache(string baseUrl)
    {
        _vaultListCache.TryRemove(baseUrl.TrimEnd('/').ToLowerInvariant(), out _);
    }

    // ---- internal helpers ----

    private HttpTransport CreateTransport(string serverUrl, string vaultId = "", string deviceId = "") =>
        new(_httpClient, _signer, serverUrl, vaultId, deviceId);

    /// <summary>验证 relPath 安全性</summary>
    public static string AssertValidRelPath(string relPath)
    {
        var p = relPath.Replace('\\', '/');
        if (string.IsNullOrEmpty(p) || p.StartsWith('/') || p.Contains("..") || p.Contains(':'))
            throw new ArgumentException($"invalid relPath: {relPath}");
        return p;
    }

    /// <summary>判断是否为文本文件</summary>
    public static bool IsTextFile(string relPath)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant().TrimStart('.');
        return ext is "md" or "json";
    }

    /// <summary>将 ApiResponse 的状态码转为 HttpStatusCode（0/无效时返回 null）</summary>
    private static HttpStatusCode? ToStatusCode<T>(ApiResponse<T> resp) =>
        resp.StatusCode > 0 ? (HttpStatusCode)resp.StatusCode : null;

    private record VaultListCacheEntry(IReadOnlyList<VaultInfo> Vaults, DateTimeOffset CachedAt);
}
