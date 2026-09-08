using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;
using Baihua.Core.Services;

namespace Baihua.AI.Provider;

/// <summary>
/// 本地模型运行时配置服务：管理下载目录、镜像偏好等本地模型相关设置。
/// </summary>
public class LocalModelSettingsService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalModelSettingsService> _logger;

    private string? _localModelDownloadDirectory;
    private string? _preferredDownloadSource;
    private bool? _useChinaMirror;

    public LocalModelSettingsService(
        IConfiguration configuration,
        ILogger<LocalModelSettingsService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        LoadLocalModelConfigFromFile();
    }

    /// <summary>
    /// 本地模型下载目录
    /// </summary>
    public string LocalModelDownloadDirectory
    {
        get
        {
            if (!string.IsNullOrEmpty(_localModelDownloadDirectory))
                return _localModelDownloadDirectory;

            var envDir = Environment.GetEnvironmentVariable("BAIHUA_LOCAL_MODEL_DIR");
            if (!string.IsNullOrEmpty(envDir))
                return envDir;

            var cfgDir = _configuration["LocalAI:DownloadDirectory"];
            if (!string.IsNullOrEmpty(cfgDir))
                return cfgDir;

            return GetDefaultLocalModelDirectory();
        }
        set
        {
            _localModelDownloadDirectory = value;
            SaveLocalModelConfigToFile();
        }
    }

    /// <summary>
    /// 模型下载源偏好
    /// </summary>
    public string PreferredDownloadSource
    {
        get => _preferredDownloadSource ?? "auto";
        set
        {
            _preferredDownloadSource = value;
            SaveLocalModelConfigToFile();
        }
    }

    /// <summary>
    /// 是否优先使用国内镜像
    /// </summary>
    public bool UseChinaMirror
    {
        get => _useChinaMirror ?? true;
        set
        {
            _useChinaMirror = value;
            SaveLocalModelConfigToFile();
        }
    }

    private static string GetDefaultLocalModelDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(home, ".baihua", "models");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Path.Combine(home, ".baihua", "models");
        return Path.Combine(home, ".baihua", "models");
    }

    public void LoadLocalModelConfigFromFile()
    {
        try
        {
            var configPath = Path.Combine(AppPaths.GetConfigDirectory(), "local-model.config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var data = System.Text.Json.JsonSerializer.Deserialize<LocalModelConfig>(json);
                _localModelDownloadDirectory = data?.DownloadDirectory;
                _preferredDownloadSource = data?.PreferredSource;
                if (data?.UseChinaMirror.HasValue == true)
                    _useChinaMirror = data.UseChinaMirror.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载本地模型配置失败");
        }
    }

    private void SaveLocalModelConfigToFile()
    {
        try
        {
            var configPath = Path.Combine(AppPaths.GetConfigDirectory(), "local-model.config.json");
            var data = new LocalModelConfig
            {
                DownloadDirectory = _localModelDownloadDirectory,
                PreferredSource = _preferredDownloadSource,
                UseChinaMirror = _useChinaMirror
            };
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存本地模型配置失败");
        }
    }

    private class LocalModelConfig
    {
        public string? DownloadDirectory { get; set; }
        public string? PreferredSource { get; set; }
        public bool? UseChinaMirror { get; set; }
    }
}
