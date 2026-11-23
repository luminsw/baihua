using System.Text.Json;
using Baihua.Contracts;
using Baihua.Contracts.Assistant;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 用户活动采集：记录用户在百花中的输入（聊天/搜索/任务等），
/// JSONL 按天存储到 $BAIHUA_HOME/assistant/activity-YYYY-MM-DD.jsonl。
/// 受助理开关控制；超期数据自动清理。
/// </summary>
public class UserActivityService
{
    private readonly ILogger<UserActivityService> _logger;

    public UserActivityService(ILogger<UserActivityService> logger)
    {
        _logger = logger;
    }

    private string DataDir => Path.Combine(BaihuaPaths.Home, "assistant");

    private string ActivityFile(DateTime day) =>
        Path.Combine(DataDir, $"activity-{day:yyyy-MM-dd}.jsonl");

    /// <summary>助理是否启用（直接读设置文件，避免与 AssistantService 循环依赖）</summary>
    private bool IsAssistantEnabled()
    {
        try
        {
            var settingsPath = Path.Combine(DataDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                return !doc.RootElement.TryGetProperty("Enabled", out var e) || e.GetBoolean();
            }
        }
        catch { }
        return true;
    }

    /// <summary>记录一条用户活动（开关关闭时忽略）</summary>
    public void Record(string type, string text, int maxLen = 500)
    {
        try
        {
            if (!IsAssistantEnabled()) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            var entry = new UserActivityDto
            {
                Time = DateTime.Now,
                Type = type,
                Text = text.Trim().Length > maxLen ? text.Trim()[..maxLen] : text.Trim(),
                Length = text.Trim().Length
            };
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(ActivityFile(DateTime.Now),
                JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "记录用户活动失败");
        }
    }

    /// <summary>读取某天的活动记录</summary>
    public List<UserActivityDto> GetActivities(DateTime day)
    {
        try
        {
            var file = ActivityFile(day);
            if (!File.Exists(file)) return new List<UserActivityDto>();
            var list = new List<UserActivityDto>();
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<UserActivityDto>(line);
                    if (e != null) list.Add(e);
                }
                catch { }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取活动记录失败");
            return new List<UserActivityDto>();
        }
    }

    /// <summary>统计最近 N 天每天的活动量（供页面展示）</summary>
    public Dictionary<string, int> GetRecentActivityCounts(int days = 14)
    {
        var result = new Dictionary<string, int>();
        for (var i = days - 1; i >= 0; i--)
        {
            var day = DateTime.Today.AddDays(-i);
            var count = 0;
            try
            {
                var file = ActivityFile(day);
                if (File.Exists(file)) count = File.ReadLines(file).Count(l => !string.IsNullOrWhiteSpace(l));
            }
            catch { }
            result[day.ToString("MM-dd")] = count;
        }
        return result;
    }

    /// <summary>清理超期活动数据</summary>
    public void CleanupOld(int retentionDays)
    {
        try
        {
            if (!Directory.Exists(DataDir)) return;
            var cutoff = DateTime.Today.AddDays(-retentionDays);
            foreach (var f in Directory.GetFiles(DataDir, "activity-*.jsonl"))
            {
                try
                {
                    var name = Path.GetFileName(f);
                    if (DateTime.TryParseExact(name, "activity-yyyy-MM-dd.jsonl", null,
                            System.Globalization.DateTimeStyles.None, out var day) && day < cutoff)
                    {
                        File.Delete(f);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "清理活动数据失败");
        }
    }
}
