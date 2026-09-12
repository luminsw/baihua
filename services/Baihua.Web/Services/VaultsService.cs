using System.Text.Json;
using Baihua.Contracts.Vaults;

namespace Baihua.Web.Services;

/// <summary>
/// 知识库服务 - 管理多个知识库
/// </summary>
public class VaultsService
{
    private static readonly JsonSerializerOptions _caseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VaultsService> _logger;
    private VaultsResponse? _cachedVaults;
    private DateTime _lastFetch = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    public VaultsService(IHttpClientFactory httpClientFactory, ILogger<VaultsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有知识库
    /// </summary>
    public virtual async Task<VaultsResponse> GetVaultsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedVaults != null && DateTime.UtcNow - _lastFetch < _cacheDuration)
        {
            return _cachedVaults;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.GetFromJsonAsync<VaultsResponse>("api/settings/vaults");
            _cachedVaults = response ?? new VaultsResponse();
            _lastFetch = DateTime.UtcNow;
            return _cachedVaults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取知识库列表失败");
            return _cachedVaults ?? new VaultsResponse();
        }
    }

    /// <summary>
    /// 添加新知识库
    /// </summary>
    public async Task<(bool success, VaultConfig? vault, string? error)> AddVaultAsync(string name, string path, string? industry = null)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.PostAsJsonAsync("api/settings/vaults", new { name, path, industry });

            if (response.IsSuccessStatusCode)
            {
                var vault = await response.Content.ReadFromJsonAsync<VaultConfig>();
                _cachedVaults = null; // 清除缓存
                return (true, vault, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, null, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加知识库失败，名称: {Name}, 路径: {Path}", name, path);
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// 更新知识库名称
    /// </summary>
    public async Task<(bool success, string? error)> UpdateVaultNameAsync(string vaultId, string newName)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.PutAsJsonAsync($"api/settings/vaults/{vaultId}", new { name = newName });
            
            if (response.IsSuccessStatusCode)
            {
                _cachedVaults = null; // 清除缓存
                return (true, null);
            }
            
            var error = await response.Content.ReadAsStringAsync();
            return (false, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新知识库名称失败，ID: {VaultId}, 新名称: {NewName}", vaultId, newName);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 更新知识库标签
    /// </summary>
    public async Task<(bool success, string? error)> UpdateVaultTagsAsync(string vaultId, string tags)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.PutAsJsonAsync($"api/settings/vaults/{vaultId}", new { tags = tags });

            if (response.IsSuccessStatusCode)
            {
                _cachedVaults = null; // 清除缓存
                return (true, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新知识库标签失败，ID: {VaultId}, 标签: {Tags}", vaultId, tags);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 更新知识库行业
    /// </summary>
    public async Task<(bool success, string? error)> UpdateVaultIndustryAsync(string vaultId, string industry)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.PutAsJsonAsync($"api/settings/vaults/{vaultId}", new { industry = industry });

            if (response.IsSuccessStatusCode)
            {
                _cachedVaults = null; // 清除缓存
                return (true, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新知识库行业失败，ID: {VaultId}, 行业: {Industry}", vaultId, industry);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 删除知识库
    /// </summary>
    public async Task<(bool success, string? error)> RemoveVaultAsync(string vaultId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.DeleteAsync($"api/settings/vaults/{vaultId}");

            if (response.IsSuccessStatusCode)
            {
                _cachedVaults = null; // 清除缓存
                return (true, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除知识库失败，ID: {VaultId}", vaultId);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 获取回收站中的知识库列表
    /// </summary>
    public async Task<VaultsResponse> GetTrashVaultsAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.GetFromJsonAsync<VaultsResponse>("api/settings/vaults/trash");
            return response ?? new VaultsResponse();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取回收站知识库列表失败");
            return new VaultsResponse();
        }
    }

    /// <summary>
    /// 恢复回收站中的知识库
    /// </summary>
    public async Task<(bool success, string? error)> RestoreVaultAsync(string vaultId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.PostAsync($"api/settings/vaults/{vaultId}/restore", null);

            if (response.IsSuccessStatusCode)
            {
                _cachedVaults = null;
                return (true, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复知识库失败，ID: {VaultId}", vaultId);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 清空回收站（永久删除）
    /// </summary>
    public async Task<(bool success, string? error)> EmptyTrashAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.PostAsync("api/settings/vaults/trash/empty", null);

            if (response.IsSuccessStatusCode)
            {
                _cachedVaults = null;
                return (true, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清空回收站失败");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 清除缓存
    /// </summary>
    public void ClearCache()
    {
        _cachedVaults = null;
    }

    /// <summary>
    /// 同步知识库：扫描根目录，与数据库比对，补录新目录、清理已删除目录的记录
    /// </summary>
    public async Task<(bool success, int added, int removed, string? error)> SyncVaultsAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.PostAsync("api/settings/vaults/sync", null);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<SyncVaultsResponse>();
                _cachedVaults = null; // 清除缓存
                return (true, result?.Added ?? 0, result?.Removed ?? 0, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, 0, 0, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步知识库失败");
            return (false, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// 获取知识库根路径偏好
    /// </summary>
    public async Task<string> GetVaultRootPathPreferenceAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            var response = await client.GetFromJsonAsync<VaultRootPathPreferenceResponse>("api/settings/vault-root-path-preference", _caseInsensitiveOptions);
            return response?.VaultRootPath ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取知识库根路径偏好失败");
            return "";
        }
    }

}


public class SyncVaultsResponse
{
    public bool Success { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
}
