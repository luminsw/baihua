using Baihua.Core.Localization;
using Baihua.Core.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Baihua.Data.Entities;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core.Security;
using Baihua.Contracts.Ai;

namespace Baihua.Family.Controllers;

/// <summary>
/// AI 配置管理 API - SQLite 加密存储
/// </summary>
[ApiController]
[Route("api/ai/config")]
public partial class AiConfigController : ControllerBase
{
    private readonly AiConfigService _aiConfigService;
    private readonly AiSettingsService _aiSettings;
    private readonly Baihua.Core.Services.AiCategorySettingsService _categorySettings;
    private readonly WebUINotificationService _webUINotification;
    private readonly Baihua.Core.Services.CapabilityService _capabilityService;
    private readonly ILogger<AiConfigController> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public AiConfigController(
        AiConfigService aiConfigService,
        AiSettingsService aiSettings,
        Baihua.Core.Services.AiCategorySettingsService categorySettings,
        WebUINotificationService webUINotification,
        Baihua.Core.Services.CapabilityService capabilityService,
        ILogger<AiConfigController> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _aiConfigService = aiConfigService;
        _aiSettings = aiSettings;
        _categorySettings = categorySettings;
        _webUINotification = webUINotification;
        _capabilityService = capabilityService;
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 获取所有 AI 提供商配置（不含敏感信息；含 KeyMask/HasApiKey 供设置页展示）
    /// </summary>
    [HttpGet("providers")]
    public ActionResult<List<AiConfigProvider>> GetProviders()
    {
        var providers = _aiConfigService.GetProviders();
        var summaries = _aiConfigService.GetApiKeySummaries();
        
        var result = providers.Select(p =>
        {
            var summary = summaries.FirstOrDefault(s => s.ProviderId == p.Id);
            return new AiConfigProvider
            {
                Id = p.Id,
                Name = p.Name,
                BaseUrl = p.AiBaseUrl,
                AnthropicBaseUrl = p.AnthropicBaseUrl,
                IsMain = p.IsMain,
                Models = p.GetModelOptions().Select(m => new AiConfigModel
                {
                    Name = m.Name,
                    IsPaid = m.IsPaid,
                    IsMain = m.IsMain,
                    Category = m.Category
                }).ToList(),
                HasApiKey = summary?.HasApiKey ?? false,
                KeyMask = summary?.KeyMask,
                Tier = p.Tier
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// 获取 API Key 配置摘要（用于设置页面）
    /// </summary>
    [HttpGet("apikeys")]
    public ActionResult<List<ApiKeySummary>> GetApiKeySummaries()
    {
        return Ok(_aiConfigService.GetApiKeySummaries());
    }

    /// <summary>
    /// 查询远程 provider 的最新模型列表（使用存储的 API Key）
    /// </summary>
    [HttpGet("providers/{providerId}/remote-models")]
    public async Task<ActionResult<object>> GetRemoteModels(string providerId)
    {
        var provider = _aiConfigService.GetProvider(providerId);
        if (provider == null)
            return NotFound(new { error = $"Provider '{providerId}' not found" });

        var apiKey = _aiConfigService.GetApiKey(providerId);
        if (string.IsNullOrEmpty(apiKey))
            return Ok(new { models = Array.Empty<string>(), reason = "no_api_key" });

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var baseUrl = provider.AiBaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/models";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return Ok(new { models = Array.Empty<string>(), reason = $"http_{response.StatusCode}" });

            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var dataArr))
            {
                foreach (var item in dataArr.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id))
                            models.Add(id);
                    }
                }
            }
            return Ok(new { models, reason = "ok" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询远程模型列表失败，ProviderId: {ProviderId}", providerId);
            return Ok(new { models = Array.Empty<string>(), reason = "error" });
        }
    }

    /// <summary>
    /// 获取单个提供商配置
    /// </summary>
    [HttpGet("providers/{providerId}")]
    public ActionResult<AiConfigProvider> GetProvider(string providerId)
    {
        var provider = _aiConfigService.GetProvider(providerId);
        if (provider == null)
            return NotFound(new { error = $"Provider '{providerId}' not found" });

        var summary = _aiConfigService.GetApiKeySummaries().FirstOrDefault(s => s.ProviderId == providerId);

        return Ok(new AiConfigProvider
        {
            Id = provider.Id,
            Name = provider.Name,
            BaseUrl = provider.AiBaseUrl,
            AnthropicBaseUrl = provider.AnthropicBaseUrl,
            IsMain = provider.IsMain,
            Models = provider.GetModelOptions().Select(m => new AiConfigModel
            {
                Name = m.Name,
                IsPaid = m.IsPaid,
                IsMain = m.IsMain,
                Category = m.Category
            }).ToList(),
            HasApiKey = summary?.HasApiKey ?? false,
            KeyMask = summary?.KeyMask,
            Tier = provider.Tier
        });
    }

    /// <summary>
    /// 导出全部 AI 提供方（含禁用项）用于全量备份（db/ai_providers.json）。
    /// 一服务一数据库：API Key 加解密只发生在 AI 服务进程内（唯一持有 key 的进程）。
    /// </summary>
    [HttpGet("export")]
    public ActionResult<List<AiProviderBackupItem>> ExportProviders([FromQuery] string? password)
    {
        try
        {
            var items = _aiConfigService.ExportForBackup(password);
            _logger.LogInformation("已导出 {Count} 个 AI 提供方用于备份", items.Count);
            return Ok(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出 AI 提供方失败");
            return StatusCode(500, new { error = $"导出失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 从备份恢复 AI 提供方（POST 全量恢复时调用）。
    /// </summary>
    [HttpPost("import")]
    public ActionResult ImportProviders([FromBody] ImportAiProvidersRequest request)
    {
        try
        {
            if (request?.Providers == null || request.Providers.Count == 0)
                return Ok(new { success = true, imported = 0 });

            _aiConfigService.ImportFromBackup(request.Providers, request.Password, request.ReplaceAll);
            _aiSettings.ClearAiProvidersCache();
            _ = _webUINotification.NotifyAIStatusChangedAsync();
            return Ok(new { success = true, imported = request.Providers.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从备份导入 AI 提供方失败");
            return StatusCode(500, new { error = $"导入失败: {ex.Message}" });
        }
    }

}
