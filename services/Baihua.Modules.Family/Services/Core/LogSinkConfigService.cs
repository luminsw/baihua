using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// OpenObserve 日志后端配置
/// </summary>
public class OpenObserveConfig
{
    /// <summary>Web UI 地址</summary>
    public string WebUrl { get; set; } = "";
    
    /// <summary>用户名</summary>
    public string User { get; set; } = "";
    
    /// <summary>密码</summary>
    public string Password { get; set; } = "";
}

/// <summary>
/// 日志后端配置服务。从 JSON 文件读写 OpenObserve 配置。
/// </summary>
public class LogSinkConfigService
{
    private readonly string _configPath;
    private OpenObserveConfig _config;
    private readonly ILogger<LogSinkConfigService> _logger;

    public LogSinkConfigService(ILogger<LogSinkConfigService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(AppContext.BaseDirectory, "logsink.config.json");
        _config = Load();
    }

    /// <summary>获取当前配置</summary>
    public OpenObserveConfig GetConfig() => _config;

    /// <summary>更新配置并持久化</summary>
    public void UpdateConfig(OpenObserveConfig config)
    {
        _config = config;
        Save();
    }

    /// <summary>获取 Web UI 地址</summary>
    public string GetWebUrl() => _config.WebUrl;

    private OpenObserveConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<OpenObserveConfig>(json, JsonOptions);
                if (config != null) return config;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载 OpenObserve 配置失败，使用默认值");
        }
        return new OpenObserveConfig();
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 OpenObserve 配置失败");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
