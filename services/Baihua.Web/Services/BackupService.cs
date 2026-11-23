using System.Text.Json;

namespace Baihua.Web.Services;

/// <summary>
/// 备份恢复服务 - 调用后端 API
/// </summary>
public class BackupService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public BackupService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// 创建全量备份
    /// </summary>
    public async Task<FullBackupResponse> CreateFullBackupAsync(string? backupDir = null, string? password = null)
    {
        var client = _httpClientFactory.CreateClient("FamilyApi");
        var request = new { BackupDir = backupDir, Password = password };
        var response = await client.PostAsJsonAsync("api/backup/full", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FullBackupResponse>() ?? new() { Success = false };
    }

    /// <summary>
    /// 恢复全量备份
    /// </summary>
    public async Task<FullRestoreResponse> RestoreFullBackupAsync(
        string backupPath,
        string? password = null,
        string? vaultRootPathOverride = null,
        bool overwrite = true)
    {
        var client = _httpClientFactory.CreateClient("FamilyApi");
        var request = new { BackupPath = backupPath, Password = password, VaultRootPathOverride = vaultRootPathOverride, Overwrite = overwrite };
        var response = await client.PostAsJsonAsync("api/backup/restore", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FullRestoreResponse>() ?? new() { Success = false };
    }

    /// <summary>
    /// 验证备份文件
    /// </summary>
    public async Task<ValidateBackupResponse> ValidateBackupAsync(string backupPath, string? password = null)
    {
        var client = _httpClientFactory.CreateClient("FamilyApi");
        var request = new { BackupPath = backupPath, Password = password };
        var response = await client.PostAsJsonAsync("api/backup/validate", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ValidateBackupResponse>() ?? new() { IsValid = false };
    }

    /// <summary>
    /// 获取备份列表
    /// </summary>
    public async Task<BackupListResponse> GetBackupListAsync(string? backupPath = null)
    {
        var client = _httpClientFactory.CreateClient("FamilyApi");
        var url = string.IsNullOrEmpty(backupPath) ? "api/backup/list" : $"api/backup/list?backupPath={Uri.EscapeDataString(backupPath)}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BackupListResponse>() ?? new() { Success = false };
    }
}


