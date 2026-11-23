using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

namespace Baihua.Core.Notifications;

/// <summary>
/// 向 WebUI 推送状态变更通知（HTTP 回调方式）
/// </summary>
public class WebUINotificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebUINotificationService> _logger;

    public WebUINotificationService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<WebUINotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string GetWebUIBaseUrl()
    {
        var url = _configuration["WebUI:Url"];
        if (!string.IsNullOrWhiteSpace(url))
            return url.TrimEnd('/');
        return "http://127.0.0.1:5177";
    }

    /// <summary>
    /// 通知 WebUI AI 配置已变更
    /// </summary>
    public async Task NotifyAIStatusChangedAsync()
    {
        await NotifyAsync("ai");
    }

    /// <summary>
    /// 通知 WebUI 知识库配置已变更
    /// </summary>
    public async Task NotifyVaultStatusChangedAsync()
    {
        await NotifyAsync("vault");
    }

    private async Task NotifyAsync(string type)
    {
        try
        {
            var url = $"{GetWebUIBaseUrl()}/api/internal/notify-state-change";
            using var client = _httpClientFactory.CreateClient("WebUI");
            var response = await client.PostAsJsonAsync(url, new { type });
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("通知 WebUI 状态变更失败: {StatusCode} ({Type})", response.StatusCode, type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "通知 WebUI 状态变更异常 ({Type})", type);
        }
    }
}
