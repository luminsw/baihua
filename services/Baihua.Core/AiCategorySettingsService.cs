using System.Text.Json;
using Baihua.Contracts;
using Baihua.Contracts.Ai;
using Baihua.Core.Models;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Services;

/// <summary>
/// 任务分类模型指派（每类一个模型配置）。
/// 存储：$BAIHUA_HOME/ai-categories.json（由 AI 服务进程读写，聊天等场景按分类路由）。
/// </summary>
public class AiCategorySettingsService
{
    private readonly ILogger<AiCategorySettingsService> _logger;
    private readonly object _lock = new();

    public AiCategorySettingsService(ILogger<AiCategorySettingsService> logger)
    {
        _logger = logger;
    }

    private static string SettingsPath => Path.Combine(BaihuaPaths.Home, "ai-categories.json");

    /// <summary>内置分类定义（名称/图标由前端本地化，此处给出服务端模态提示）</summary>
    public static List<AiCategoryDefinitionDto> GetDefinitions() => new()
    {
        new() { Key = AiTaskCategory.Chat, Icon = "💬", Description = "日常对话、问答、写作等通用文本场景" },
        new() { Key = AiTaskCategory.Reasoning, Icon = "🧠", Description = "数学、逻辑、深度分析与长链条推理（建议推理型模型）" },
        new() { Key = AiTaskCategory.Code, Icon = "💻", Description = "代码生成、补全、解释与调试（建议代码模型）" },
        new() { Key = AiTaskCategory.Vision, Icon = "🖼️", Description = "图像理解（必须为视觉/多模态模型，如 Qwen2.5-VL）" },
    };

    public List<AiCategoryAssignmentDto> GetAssignments()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("Assignments", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<AiCategoryAssignmentDto>>(arr.GetRawText())
                           ?? new List<AiCategoryAssignmentDto>();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取任务分类指派失败");
        }
        return new List<AiCategoryAssignmentDto>();
    }

    public void SaveAssignments(List<AiCategoryAssignmentDto>? assignments)
    {
        var normalized = (assignments ?? new List<AiCategoryAssignmentDto>())
            .Where(a => AiTaskCategory.IsValid(a.Category))
            .GroupBy(a => AiTaskCategory.Normalize(a.Category))
            .Select(g => g.Last())
            .ToList();

        lock (_lock)
        {
            Directory.CreateDirectory(BaihuaPaths.Home);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new { Assignments = normalized }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// 按分类解析应使用的提供方与模型：
    /// 1) 显式指派（校验提供方/模型存在；提供方存在但模型不在列表 → 用该提供方主模型）；
    /// 2) 提供方中标记了该分类的模型（优先主提供方）；
    /// 3) 回退主提供方 + 主模型（再回退第一个提供方）。
    /// </summary>
    public (string ProviderId, string ModelName, bool FromAssignment) Resolve(
        string? category, IReadOnlyList<AiProviderConfig>? providers)
    {
        var cat = AiTaskCategory.Normalize(category);
        var list = providers ?? new List<AiProviderConfig>();
        if (list.Count == 0)
            return ("", "", false);

        // 1) 显式指派
        var assignment = GetAssignments().FirstOrDefault(a =>
            a.Category?.Equals(cat, StringComparison.OrdinalIgnoreCase) == true);
        if (assignment != null && !string.IsNullOrWhiteSpace(assignment.ProviderId))
        {
            var provider = list.FirstOrDefault(p =>
                p.Id.Equals(assignment.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider != null)
            {
                var hasModel = provider.Models.Any(m =>
                    m.Name.Equals(assignment.ModelName, StringComparison.OrdinalIgnoreCase));
                if (hasModel)
                    return (provider.Id, assignment.ModelName, true);
                if (provider.Models.Count > 0)
                    return (provider.Id, provider.GetMainModel(), false);
            }
        }

        // 2) 标记了该分类的模型（优先主提供方）
        var main = list.FirstOrDefault(p => p.IsMain);
        foreach (var p in list.OrderByDescending(p => p == main))
        {
            var tagged = p.Models.FirstOrDefault(m =>
                m.Category?.Equals(cat, StringComparison.OrdinalIgnoreCase) == true);
            if (tagged != null)
                return (p.Id, tagged.Name, false);
        }

        // 3) 回退主提供方 + 主模型
        if (main != null && main.Models.Count > 0)
            return (main.Id, main.GetMainModel(), false);

        var first = list[0];
        return (first.Id, first.Models.Count > 0 ? first.GetMainModel() : "", false);
    }
}
