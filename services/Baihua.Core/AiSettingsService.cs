using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Baihua.Core.Models;

namespace Baihua.Core.Services;

/// <summary>
/// AI 运行时配置服务：聚合 AI 提供商、模型、API Key、请求参数、Embedding 配置。
/// 作为 SettingsService 的继任者，专注 AI 域的运行时读取需求。
/// </summary>
public class AiSettingsService
{
    private readonly IConfiguration _configuration;
    private readonly IAiConfigService _aiConfigService;
    private readonly ILogger<AiSettingsService> _logger;
    private IReadOnlyList<AiProviderConfig>? _aiProvidersCache;

    public AiSettingsService(
        IConfiguration configuration,
        IAiConfigService aiConfigService,
        ILogger<AiSettingsService> logger)
    {
        _configuration = configuration;
        _aiConfigService = aiConfigService;
        _logger = logger;
    }

    public void ClearAiProvidersCache()
    {
        _aiProvidersCache = null;
        _logger.LogInformation("AI 提供商缓存已清除");
    }

    public IReadOnlyList<AiProviderConfig> GetAiProviders()
    {
        // 一服务一数据库：Family（shim 模式）不缓存——每次经 AI 服务 HTTP 拉取，
        // 保证算力池对端注册/选用后的新 Provider 立即可见；AI 服务（直读 ai.db）可缓存。
        var cacheable = !RouteInferenceViaShim;
        if (cacheable && _aiProvidersCache != null)
            return _aiProvidersCache;

        try
        {
            var dbProviders = _aiConfigService.GetProviders();
            if (dbProviders != null && dbProviders.Count > 0)
            {
                if (cacheable)
                    _aiProvidersCache = dbProviders;
                return dbProviders;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 AI 提供商配置失败，回退到 appsettings.json");
        }

        var list = _configuration.GetSection("Ai").Get<List<AiProviderConfig>>() ?? new List<AiProviderConfig>();
        if (cacheable)
            _aiProvidersCache = list;
        return list;
    }

    public AiProviderConfig? GetAiProvider(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return GetAiProviders().FirstOrDefault(p =>
            p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public AiProviderConfig? GetMainAiProvider()
    {
        try
        {
            var mainFromDb = _aiConfigService.GetMainProvider();
            if (mainFromDb != null)
                return mainFromDb;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载主 AI 提供商失败，回退到配置文件中查找");
        }

        var list = GetAiProviders();
        var main = list.FirstOrDefault(p => p.IsMain);
        if (main != null)
            return main;
        return list.FirstOrDefault();
    }

    public string GetApiKeyForProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return "";

        var idTrim = providerId.Trim();

        try
        {
            var keyFromDb = _aiConfigService.GetApiKey(idTrim);
            if (!string.IsNullOrEmpty(keyFromDb))
                return keyFromDb;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 AI 提供商 API Key 失败: {ProviderId}", idTrim);
        }

        return "";
    }

    public virtual string GetAiApiKey(string providerId)
    {
        return GetApiKeyForProvider(providerId);
    }

    /// <summary>
    /// 本机 OpenAI 兼容 shim 地址（/mg/ai/v1）。
    /// 合并为单进程后 shim 与后端同址，默认取本机 8788（可用 BAIHUA_AI_URL / AiApi:BaseUrl 显式覆盖）。
    /// </summary>
    public string AiShimUrl =>
        Environment.GetEnvironmentVariable("BAIHUA_AI_URL")
        ?? Environment.GetEnvironmentVariable("TASK_RUNNER_AI_API_URL")
        ?? _configuration["AiApi:BaseUrl"]
        ?? _configuration["BaihuaServer:BaseUrl"]
        ?? "http://127.0.0.1:8788";

    /// <summary>
    /// 推理是否经本机 AI shim 转发（一服务一库的转发开关）：
    /// Family 进程设为 true（经 shim，不持有 key）；AI 服务进程默认 false（shim 内部直连真实 provider，
    /// 避免 AI 服务自指转发）。
    /// </summary>
    public bool RouteInferenceViaShim =>
        string.Equals(_configuration["AiClient__UseShim"], "true", StringComparison.OrdinalIgnoreCase);

    public string AiApiKey => GetApiKeyForProvider(GetMainAiProvider()?.Id ?? "");

    public string AiApiUrl
    {
        get
        {
            var envUrl = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_API_URL");
            if (!string.IsNullOrEmpty(envUrl))
                return envUrl;

            var main = GetMainAiProvider();
            if (main != null && !string.IsNullOrWhiteSpace(main.AiBaseUrl))
                return main.AiBaseUrl.TrimEnd('/');

            return _configuration["AiBaseUrl"]?.TrimEnd('/')
                ?? "https://coding.dashscope.aliyuncs.com/v1";
        }
    }

    public string AiModel => GetModelForProvider(GetMainAiProvider()?.Id ?? "");

    public string GetModelForProvider(string providerId, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return _configuration["AiModel"] ?? "Qwen/Qwen2.5-14B-Instruct";

        var provider = GetAiProvider(providerId);
        if (provider == null)
            return _configuration["AiModel"] ?? "Qwen/Qwen2.5-14B-Instruct";

        var models = provider.GetModelOptions();
        if (models.Count == 0)
            return _configuration["AiModel"] ?? "Qwen/Qwen2.5-14B-Instruct";

        if (!string.IsNullOrWhiteSpace(model))
        {
            var matched = models.FirstOrDefault(m =>
                m.Name.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matched != null)
                return matched.Name;
        }

        var mainModel = models.FirstOrDefault(m => m.IsMain);
        return mainModel?.Name ?? models[0].Name;
    }

    public int AiRequestTimeoutMinutes
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_TIMEOUT_MINUTES");
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                return v;
            var cfg = _configuration["AiRequestTimeoutMinutes"];
            if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                return c;
            return 5;
        }
    }

    public int AiRequestMaxAttempts
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_MAX_ATTEMPTS");
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                return v;
            var cfg = _configuration["AiRequestMaxAttempts"];
            if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                return c;
            return 3;
        }
    }

    public int AiRequestInitialBackoffMs
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_INITIAL_BACKOFF_MS");
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                return v;
            var cfg = _configuration["AiRequestInitialBackoffMs"];
            if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                return c;
            return 1000;
        }
    }

    public int AiRequestMaxBackoffMs
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_MAX_BACKOFF_MS");
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                return v;
            var cfg = _configuration["AiRequestMaxBackoffMs"];
            if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                return c;
            return 30000;
        }
    }

    public string SemanticEmbeddingUrl =>
        Environment.GetEnvironmentVariable("TASK_RUNNER_EMBEDDING_URL")
        ?? _configuration["EmbeddingUrl"]
        ?? "";

    public string SemanticEmbeddingModel =>
        Environment.GetEnvironmentVariable("TASK_RUNNER_EMBEDDING_MODEL")
        ?? _configuration["EmbeddingModel"]
        ?? "";
}
