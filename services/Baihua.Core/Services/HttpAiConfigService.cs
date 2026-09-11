using System.Net.Http.Json;
using System.Text.Json;
using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Baihua.Core.Security;
using Baihua.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Services;

/// <summary>
/// AI 提供方配置的 HTTP 数据源（一服务一数据库）：Family 进程不再直读 ai.db，
/// 全部经 AI 服务（8791）HTTP API 访问：
/// - 读：GET /api/ai/config/providers、/api/ai/config/apikeys、/api/ai/config/providers/{id}
/// - 写：POST /api/ai/config/providers、DELETE /api/ai/config/providers/{id}
/// API Key 只存在于 AI 服务进程；本实现 GetApiKey 恒返回空串（Family 不持有 key）。
/// </summary>
public class HttpAiConfigService : IAiConfigService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HttpAiConfigService> _logger;

    public HttpAiConfigService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<HttpAiConfigService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string AiBaseUrl =>
        _configuration["BAIHUA_AI_URL"]
        ?? _configuration["TASK_RUNNER_AI_API_URL"]
        ?? "http://127.0.0.1:8791";

    /// <summary>获取所有启用的 AI 提供商（不含密钥）</summary>
    public List<AiProviderConfig> GetProviders()
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(10);
            var items = client.GetFromJsonAsync<List<AiConfigProvider>>(
                $"{AiBaseUrl.TrimEnd('/')}/api/ai/config/providers", _jsonOpts).GetAwaiter().GetResult();
            return (items ?? new List<AiConfigProvider>()).Select(MapToProviderConfig).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "经 AI 服务读取 AI 提供方配置失败，返回空列表");
            return new List<AiProviderConfig>();
        }
    }

    /// <summary>获取 API Key 配置摘要（掩码）</summary>
    public List<ApiKeySummary> GetApiKeySummaries()
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(10);
            var items = client.GetFromJsonAsync<List<ApiKeySummary>>(
                $"{AiBaseUrl.TrimEnd('/')}/api/ai/config/apikeys", _jsonOpts).GetAwaiter().GetResult();
            return items ?? new List<ApiKeySummary>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "经 AI 服务读取 API Key 摘要失败，返回空列表");
            return new List<ApiKeySummary>();
        }
    }

    /// <summary>获取单个 Provider 配置</summary>
    public AiProviderConfig? GetProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return null;

        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(10);
            var item = client.GetFromJsonAsync<AiConfigProvider>(
                $"{AiBaseUrl.TrimEnd('/')}/api/ai/config/providers/{Uri.EscapeDataString(providerId.Trim())}", _jsonOpts)
                .GetAwaiter().GetResult();
            return item != null ? MapToProviderConfig(item) : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "经 AI 服务读取 Provider {ProviderId} 失败", providerId);
            return null;
        }
    }

    /// <summary>获取主 Provider（无主时回退第一个启用的）</summary>
    public AiProviderConfig? GetMainProvider()
    {
        var providers = GetProviders();
        return providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault();
    }

    /// <summary>
    /// Family 进程不持有 API Key（推理统一经 AI 服务 shim 转发），恒返回空串。
    /// </summary>
    public string GetApiKey(string providerId)
    {
        _logger.LogWarning("Family 进程不持有 API Key（一服务一数据库），GetApiKey({ProviderId}) 返回空", providerId);
        return "";
    }

    /// <summary>保存 Provider 配置（写收口：经 AI 服务 API）</summary>
    public void SaveProvider(AiProviderSetting setting, string? plainApiKey = null)
    {
        try
        {
            var models = new List<AiModelConfig>();
            if (!string.IsNullOrWhiteSpace(setting.ModelsJson))
            {
                try { models = JsonSerializer.Deserialize<List<AiModelConfig>>(setting.ModelsJson) ?? new(); }
                catch { models = new(); }
            }

            var request = new SaveAiProviderRequest
            {
                Id = setting.ProviderId,
                Name = setting.ProviderName,
                BaseUrl = setting.BaseUrl ?? "",
                AnthropicBaseUrl = setting.AnthropicBaseUrl,
                IsMain = setting.IsMain,
                Models = models
                    .Select(m => new AiModelRequest { Name = m.Name, IsMain = m.IsMain })
                    .ToList(),
                ApiKey = plainApiKey,
                SortOrder = setting.SortOrder,
                Tier = (AiModelTier)setting.Tier
            };

            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(20);
            var resp = client.PostAsJsonAsync($"{AiBaseUrl.TrimEnd('/')}/api/ai/config/providers", request)
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                _logger.LogWarning("AI 提供方 {Id} 保存失败：HTTP {(int)resp.StatusCode} {Body}",
                    setting.ProviderId, resp.StatusCode, body.Length > 300 ? body[..300] : body);
            }
            else
            {
                _logger.LogDebug("AI 提供方 {Id} 已保存（经 AI 服务）", setting.ProviderId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 提供方 {Id} 保存失败", setting.ProviderId);
        }
    }

    /// <summary>删除 Provider 配置（经 AI 服务 API）</summary>
    public bool DeleteProvider(string providerId)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(20);
            var resp = client.DeleteAsync($"{AiBaseUrl.TrimEnd('/')}/api/ai/config/providers/{Uri.EscapeDataString(providerId)}")
                .GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除 AI 提供方 {ProviderId} 失败", providerId);
            return false;
        }
    }

    private static AiProviderConfig MapToProviderConfig(AiConfigProvider p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        AiBaseUrl = p.BaseUrl,
        AnthropicBaseUrl = p.AnthropicBaseUrl,
        IsMain = p.IsMain,
        Models = (p.Models ?? new List<AiConfigModel>())
            .Select(m => new AiModelConfig { Name = m.Name, IsMain = m.IsMain })
            .ToList(),
        Tier = p.Tier
    };
}
