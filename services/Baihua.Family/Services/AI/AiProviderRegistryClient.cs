using System.Text.Json;
using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Baihua.Data.Entities;

namespace Baihua.Family.Services.AI;

/// <summary>
/// AI 提供方配置写入客户端：把 Family 侧对 ai.db 的写操作收口到 AI 服务（8791）的
/// /api/ai/config/providers，避免 Family/AI 双进程并发写同一 SQLite 库的写锁竞争
/// （SQLite 同时只允许一个写事务）。
/// 读取仍走 AiConfigService 直读（WAL 下并发读安全）。
/// </summary>
public class AiProviderRegistryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiProviderRegistryClient> _logger;

    public AiProviderRegistryClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AiProviderRegistryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string AiBaseUrl =>
        _configuration["BAIHUA_AI_URL"]
        ?? _configuration["TASK_RUNNER_AI_API_URL"]
        ?? "http://127.0.0.1:8791";

    /// <summary>
    /// 保存/更新提供方（写收口到 AI 服务）。
    /// plainApiKey 语义与 AiConfigService.SaveProvider 一致：null=保留旧 key，""=清空，非空=更新并加密。
    /// </summary>
    public async Task<bool> SaveProviderAsync(AiProviderSetting setting, string? plainApiKey, CancellationToken ct = default)
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
            using var resp = await client.PostAsJsonAsync(
                $"{AiBaseUrl.TrimEnd('/')}/api/ai/config/providers", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("AI 提供方 {Id} 保存失败：HTTP {(int)resp.StatusCode} {Body}",
                    setting.ProviderId, resp.StatusCode, body.Length > 300 ? body[..300] : body);
                return false;
            }
            _logger.LogDebug("AI 提供方 {Id} 已保存（经 AI 服务）", setting.ProviderId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 提供方 {Id} 保存失败", setting.ProviderId);
            return false;
        }
    }

    /// <summary>
    /// 导出全部 AI 提供方（含禁用项）用于全量备份：返回 db/ai_providers.json 的 JSON 数组文本。
    /// 一服务一数据库：API Key 的加解密/重加密全部由 AI 服务完成，Family 不接触明文 key。
    /// </summary>
    public async Task<string?> ExportProvidersAsync(string? password, CancellationToken ct = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(30);
            var url = $"{AiBaseUrl.TrimEnd('/')}/api/ai/config/export";
            if (!string.IsNullOrEmpty(password))
                url += $"?password={Uri.EscapeDataString(password)}";
            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("AI 提供方导出失败：HTTP {(int)resp.StatusCode} {Body}", resp.StatusCode, body.Length > 300 ? body[..300] : body);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 提供方导出失败（AI 服务不可达）");
            return null;
        }
    }

    /// <summary>
    /// 从备份恢复 AI 提供方：把 ZIP 中 db/ai_providers.json 的内容（JSON 数组）交给 AI 服务导入。
    /// </summary>
    public async Task<bool> ImportProvidersAsync(string providersJson, string? password, bool replaceAll = false, CancellationToken ct = default)
    {
        try
        {
            var providers = JsonSerializer.Deserialize<List<AiProviderBackupItem>>(providersJson, System.Text.Json.JsonSerializerOptions.Web)
                ?? new List<AiProviderBackupItem>();
            var request = new ImportAiProvidersRequest { Providers = providers, Password = password, ReplaceAll = replaceAll };

            using var client = _httpClientFactory.CreateClient("ComputePool");
            client.Timeout = TimeSpan.FromSeconds(120);
            using var resp = await client.PostAsJsonAsync(
                $"{AiBaseUrl.TrimEnd('/')}/api/ai/config/import", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("AI 提供方导入失败：HTTP {(int)resp.StatusCode} {Body}", resp.StatusCode, body.Length > 300 ? body[..300] : body);
                return false;
            }
            _logger.LogInformation("AI 提供方已从备份导入（{Count} 条）", providers.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 提供方导入失败（AI 服务不可达）");
            return false;
        }
    }
}
