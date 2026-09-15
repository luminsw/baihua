using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Baihua.Web.Services;

public class SettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly string _configPath;
    private SettingsData _data;
    public string AiApiKey 
    { 
        get => _data.AiApiKey; 
        set { _data.AiApiKey = value; Save(); }
    }
    
    public string AiApiUrl 
    { 
        get => _data.AiApiUrl; 
        set { 
            _data.AiApiUrl = value; 
            // 合并后 AI 与家庭/知识库同属一个后端进程，且 WebUI 与后端是两个进程：
            // 这里设环境变量不再能影响后端。后端地址由 BaihuaServer:BaseUrl（容器内 BaihuaServer__BaseUrl）决定。
            Save(); 
        }
    }
    
    public string AiModel 
    { 
        get => _data.AiModel; 
        set { _data.AiModel = value; Save(); }
    }
    
    public string BackendUrl 
    { 
        get => _data.BackendUrl; 
        set
        {
            var next = string.IsNullOrWhiteSpace(value)
                ? "http://127.0.0.1:8788"
                : BaihuaEndpointHelper.NormalizeOutboundBaseUrl(value);
            _data.BackendUrl = next;
            Save();
        }
    }

    public SettingsService(ILogger<SettingsService> logger, IConfiguration configuration)
    {
        _logger = logger;
        var configDir = Environment.GetEnvironmentVariable("WEBUI_CONFIG_DIR")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "webui.settings.json");
        _data = Load() ?? new SettingsData();

        // 后端地址以部署配置为准（k8s ConfigMap 的 BaihuaServer__BaseUrl、compose/native 同名键）。
        // 原先这里只有硬编码默认值 http://127.0.0.1:8788：k8s 下 WebUI 跑在 Pod 里，
        // 服务端的 SignalR / HttpClient 去连 Pod 自己的 127.0.0.1:8788 必然 Connection refused，
        // 表现为任务页「WebSocket 未连接」、家庭页 DeviceHub 连接失败、头部状态徽标刷不到。
        // 用户若在设置页显式改过后端地址，则尊重已持久化的值（仅当仍是默认回环时才采用配置）。
        var configured = configuration?["BaihuaServer:BaseUrl"]
            ?? configuration?["FamilyApi:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var normalized = BaihuaEndpointHelper.NormalizeOutboundBaseUrl(configured);
            var isDefaultLoopback = string.IsNullOrWhiteSpace(_data.BackendUrl)
                || string.Equals(_data.BackendUrl, "http://127.0.0.1:8788", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_data.BackendUrl, "http://localhost:8788", StringComparison.OrdinalIgnoreCase);
            if (isDefaultLoopback && !string.Equals(_data.BackendUrl, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _data.BackendUrl = normalized;
                Save();
            }
        }

        PersistBackendUrlIfLoopbackNormalized();
    }

    private SettingsData? Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                return JsonSerializer.Deserialize<SettingsData>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载配置失败");
        }
        return null;
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(_configPath, json);
            _logger.LogDebug("配置已保存：{ConfigPath}", _configPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存配置失败");
        }
    }

    public void Reload()
    {
        var loaded = Load();
        if (loaded != null)
        {
            _data = loaded;
            PersistBackendUrlIfLoopbackNormalized();
        }
        // 密码机制已移除，无需清除缓存
    }

    /// <summary>将配置文件里的 localhost / ::1 写回为 127.0.0.1，避免后续仍走 IPv6 回环。</summary>
    private void PersistBackendUrlIfLoopbackNormalized()
    {
        var n = BaihuaEndpointHelper.NormalizeOutboundBaseUrl(_data.BackendUrl);
        if (string.Equals(n, _data.BackendUrl, StringComparison.Ordinal))
            return;
        _data.BackendUrl = n;
        Save();
    }

    private class SettingsData
    {
        public string AiApiKey { get; set; } = string.Empty;
        public string AiApiUrl { get; set; } = string.Empty;
        public string AiModel { get; set; } = string.Empty;
        public string BackendUrl { get; set; } = "http://127.0.0.1:8788";
        // 注意：AdminPasswordHash 不再本地存储，改为从 Baihua.Server API 获取
    }
}
