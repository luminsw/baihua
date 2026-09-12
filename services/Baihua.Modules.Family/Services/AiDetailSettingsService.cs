using System.Text.Json;
using Baihua.Contracts;
using Baihua.Contracts.Tasks;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 全局 AI 生成详细度设置（所有 AI 内容生成默认应用，编程任务除外）。
/// 存储：$BAIHUA_HOME/ai-detail.json，默认 concise（最简洁）。
/// </summary>
public class AiDetailSettingsService
{
    private readonly ILogger<AiDetailSettingsService> _logger;
    private readonly object _lock = new();

    public AiDetailSettingsService(ILogger<AiDetailSettingsService> logger)
    {
        _logger = logger;
    }

    private static string SettingsPath => Path.Combine(BaihuaPaths.Home, "ai-detail.json");

    public string GetDetailLevel()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("DetailLevel", out var e) && e.ValueKind == JsonValueKind.String)
                    return VaultGenDetail.Normalize(e.GetString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取全局详细度设置失败");
        }
        return VaultGenDetail.Concise; // 默认最简洁
    }

    public void SetDetailLevel(string level)
    {
        var normalized = VaultGenDetail.Normalize(level);
        lock (_lock)
        {
            Directory.CreateDirectory(BaihuaPaths.Home);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new { DetailLevel = normalized }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
