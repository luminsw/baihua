namespace Baihua.Web.Services;

/// <summary>
/// AI 状态服务 - 检测和跟踪 API Key 配置状态（从 SQLite 读取）
/// </summary>
public class AIStatusService
{
    private readonly IApiService _apiService;
    private List<AiConfigProvider>? _cachedProviders;
    private DateTime _lastFetchTime = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 服务器代理模式（FAM-03）：服务器端 AI 网关已配置（provider 存在）时视为可用，
    /// 用户无需自配 API Key，首页不显示"API Key 未配置"红色警报。
    /// </summary>
    public bool IsServerProxyMode { get; private set; }

    /// <summary>
    /// 状态变更事件 - 当 AI 配置发生变化时触发
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// 触发状态变更事件
    /// </summary>
    public void NotifyStateChanged()
    {
        // 清除缓存，强制下次重新获取
        _cachedProviders = null;
        _lastFetchTime = DateTime.MinValue;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 清除缓存（下次获取时将重新从服务器加载）
    /// </summary>
    public void ClearCache()
    {
        _cachedProviders = null;
        _lastFetchTime = DateTime.MinValue;
    }

    public AIStatusService(IApiService apiService)
    {
        _apiService = apiService;
    }

    /// <summary>
    /// 获取 AI 提供商列表（带缓存）
    /// </summary>
    private async Task<List<AiConfigProvider>> GetProvidersAsync()
    {
        if (_cachedProviders == null || DateTime.Now - _lastFetchTime > _cacheDuration)
        {
            _cachedProviders = await _apiService.GetAiConfigProvidersAsync();
            _lastFetchTime = DateTime.Now;
        }
        return _cachedProviders ?? new List<AiConfigProvider>();
    }

    /// <summary>
    /// 检查 API Key 是否已配置（异步）
    /// </summary>
    public async Task<bool> IsApiKeyConfiguredAsync()
    {
        var providers = await GetProvidersAsync();
        return providers.Any(p => p.IsMain) || providers.Any();
    }

    /// <summary>
    /// 获取主提供商配置
    /// </summary>
    private async Task<AiConfigProvider?> GetMainProviderAsync()
    {
        var providers = await GetProvidersAsync();
        return providers.FirstOrDefault(p => p.IsMain) ??
               providers.FirstOrDefault();
    }

    /// <summary>
    /// 获取 API Key 预览（从 KeyMask）
    /// </summary>
    public async Task<string> GetApiKeyPreviewAsync()
    {
        var provider = await GetMainProviderAsync();
        if (provider == null)
            return "未配置";
        
        return provider.KeyMask ?? "已配置";
    }

    /// <summary>
    /// 获取当前 AI API URL
    /// </summary>
    public async Task<string> GetAiApiUrlAsync()
    {
        var provider = await GetMainProviderAsync();
        return provider?.BaseUrl ?? "";
    }

    /// <summary>
    /// 获取当前 AI 模型
    /// </summary>
    public async Task<string> GetAiModelAsync()
    {
        var provider = await GetMainProviderAsync();
        var mainModel = provider?.Models?.FirstOrDefault(m => m.IsMain);
        return mainModel?.Name ?? provider?.Models?.FirstOrDefault()?.Name ?? "";
    }

    /// <summary>
    /// 获取 AI 提供商名称（根据 URL 推断）
    /// </summary>
    public async Task<string> GetProviderNameAsync()
    {
        var provider = await GetMainProviderAsync();
        if (provider == null)
            return "未配置";

        var url = provider.BaseUrl.ToLower();
        var name = provider.Name.ToLower();
        
        if (url.Contains("siliconflow") || url.Contains("silicon") || name.Contains("silicon"))
            return "硅基流动";
        if (url.Contains("dashscope") || url.Contains("aliyun") || name.Contains("阿里"))
            return "阿里云";
        if (url.Contains("volces") || name.Contains("火山"))
            return "火山引擎";
        if (url.Contains("deepseek") || name.Contains("deepseek"))
            return "DeepSeek";
        if (url.Contains("moonshot") || name.Contains("moonshot"))
            return "Moonshot";
        if (url.Contains("openai"))
            return "OpenAI";
        
        return provider.Name;
    }

    /// <summary>
    /// 异步刷新状态
    /// </summary>
    public async Task<AIStatusSummary> GetStatusSummaryAsync()
    {
        var provider = await GetMainProviderAsync();
        var isConfigured = provider != null;
        // FAM-03：服务器端已配置 AI 网关（provider 存在）→ 代理模式可用，无需用户自配 API Key
        IsServerProxyMode = isConfigured;

        return new AIStatusSummary
        {
            IsConfigured = isConfigured,
            ProviderName = await GetProviderNameAsync(),
            ApiKeyPreview = await GetApiKeyPreviewAsync(),
            ApiUrl = await GetAiApiUrlAsync(),
            Model = await GetAiModelAsync(),
            Status = isConfigured ? AIConfigurationStatus.Configured : AIConfigurationStatus.NotConfigured
        };
    }

    /// <summary>
    /// 刷新缓存
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            _cachedProviders = await _apiService.GetAiConfigProvidersAsync();
            _lastFetchTime = DateTime.Now;
        }
        catch
        {
            // 忽略错误，使用缓存或空列表
        }
    }

    /// <summary>
    /// 获取配置建议文本
    /// </summary>
    public string GetConfigurationAdvice()
    {
        var isConfigured = _cachedProviders?.Any() ?? false;
        if (isConfigured)
            return "";

        return """
            💡 配置 API Key 后，您可以：
            • 🤖 与 AI 对话，询问各类知识问题
            • 📝 让 AI 生成结构化笔记
            • 🔍 使用 AI 辅助搜索知识库
            • ✂️ 自动拆分笔记为原子笔记
            """;
    }
}

/// <summary>
/// AI 配置状态
/// </summary>
public enum AIConfigurationStatus
{
    NotConfigured,
    Configured,
    Invalid
}

/// <summary>
/// AI 状态摘要
/// </summary>
public class AIStatusSummary
{
    public bool IsConfigured { get; set; }
    public AIConfigurationStatus Status { get; set; }
    public string ProviderName { get; set; } = "";
    public string ApiKeyPreview { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string Model { get; set; } = "";
}
