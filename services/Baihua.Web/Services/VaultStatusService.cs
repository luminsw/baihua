using System.Text.Json;
using Baihua.Contracts.Vaults;

namespace Baihua.Web.Services;

/// <summary>
/// 知识库状态服务 - 检测和跟踪知识库配置状态
/// 使用 Singleton 模式确保所有组件共享同一实例和事件
/// </summary>
public class VaultStatusService
{
    private static readonly JsonSerializerOptions _caseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VaultStatusService> _logger;

    /// <summary>
    /// 状态变更事件 - 当知识库配置发生变化时触发
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 触发状态变更事件
    /// </summary>
    public void NotifyStateChanged()
    {
        var handlerCount = StateChanged?.GetInvocationList().Length ?? 0;
        _logger.LogInformation($"NotifyStateChanged 被调用，订阅者数量: {handlerCount}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public VaultStatusService(IHttpClientFactory httpClientFactory, ILogger<VaultStatusService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private async Task<VaultsResponse> GetVaultsAsync(bool forceRefresh = false)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("VaultApi");
            return await client.GetFromJsonAsync<VaultsResponse>("api/settings/vaults") ?? new VaultsResponse();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取知识库列表失败");
            return new VaultsResponse();
        }
    }

    /// <summary>
    /// 检查知识库是否已配置
    /// </summary>
    public async Task<bool> IsVaultConfiguredAsync()
    {
        try
        {
            var vaults = await GetVaultsAsync();
            return vaults.Vaults.Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取知识库状态摘要
    /// WebUI不使用缓存，每次都从后台服务获取最新状态
    /// </summary>
    public async Task<VaultStatusSummary> GetStatusSummaryAsync(bool forceRefresh = false)
    {
        try
        {
            var vaults = await GetVaultsAsync(forceRefresh);
            // 新逻辑：优先判断是否存在已注册的 vault
            if (vaults.Vaults.Count == 0)
            {
                // 如果没有已注册的 vault，但后端提供了固定的根路径偏好（VaultRootPath），
                // 则认为已配置（可以在该路径下创建知识库）。优先使用后端的偏好设置以避免误报。
                try
                {
                    var client = _httpClientFactory.CreateClient("VaultApi");
                    var pref = await client.GetFromJsonAsync<VaultRootPathPreferenceResponse>("api/settings/vault-root-path-preference", _caseInsensitiveOptions);
                    var rootPath = pref?.VaultRootPath ?? string.Empty;

                    if (!string.IsNullOrEmpty(rootPath))
                    {
                        var pathExists = Directory.Exists(rootPath);
                        return new VaultStatusSummary
                        {
                            IsConfigured = true,
                            Status = pathExists ? VaultConfigurationStatus.Configured : VaultConfigurationStatus.PathNotFound,
                            VaultCount = 0,
                            ActiveVaultName = "",
                            ActiveVaultPath = rootPath,
                            PathExists = pathExists
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "获取 VaultRootPathPreference 失败，继续按未配置处理");
                }

                // 没有 vault 且无法取得后端根路径偏好 -> 视为未配置
                return new VaultStatusSummary
                {
                    IsConfigured = false,
                    Status = VaultConfigurationStatus.NotConfigured,
                    VaultCount = 0,
                    ActiveVaultName = "",
                    ActiveVaultPath = "",
                    PathExists = false
                };
            }

            // 检查是否有路径不存在的知识库
            var vaultsWithMissingPath = vaults.Vaults
                .Where(v => !string.IsNullOrWhiteSpace(v.Path) && !Directory.Exists(v.Path))
                .ToList();

            // 至少有一个知识库就算已配置，但如果有路径丢失则标记
            var hasMissingPath = vaultsWithMissingPath.Count > 0;
            var firstMissing = vaultsWithMissingPath.FirstOrDefault();

            return new VaultStatusSummary
            {
                IsConfigured = !hasMissingPath,
                Status = hasMissingPath ? VaultConfigurationStatus.PathNotFound : VaultConfigurationStatus.Configured,
                VaultCount = vaults.Vaults.Count,
                ActiveVaultName = hasMissingPath ? firstMissing!.Name : vaults.Vaults[0].Name,
                ActiveVaultPath = hasMissingPath ? firstMissing!.Path : vaults.Vaults[0].Path,
                PathExists = !hasMissingPath
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取知识库状态失败");
            return new VaultStatusSummary
            {
                IsConfigured = false,
                Status = VaultConfigurationStatus.Error,
                Error = ex.Message,
                PathExists = false
            };
        }
    }

    /// <summary>
    /// 获取配置建议文本
    /// </summary>
    public string GetConfigurationAdvice()
    {
        return """
        💡 配置知识库后，您可以：
        • 📝 管理和搜索笔记
        • 💾 保存 AI 生成的笔记到知识库
        • 🔄 同步笔记到移动端
        • 📚 构建个人笔记知识库
        """;
    }
}

/// <summary>
/// 知识库配置状态
/// </summary>
public enum VaultConfigurationStatus
{
    NotConfigured,
    Configured,
    PathNotFound,
    Error
}

/// <summary>
/// 知识库状态摘要
/// </summary>
public class VaultStatusSummary
{
    public bool IsConfigured { get; set; }
    public VaultConfigurationStatus Status { get; set; }
    public int VaultCount { get; set; }
    public string ActiveVaultName { get; set; } = "";
    public string ActiveVaultPath { get; set; } = "";
    public string? Error { get; set; }
    /// <summary>
    /// 知识库路径是否存在
    /// </summary>
    public bool PathExists { get; set; }
}
