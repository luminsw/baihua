namespace Baihua.Contracts.Ai;

/// <summary>
/// 任务分类键：模型按用途分类，每类指派一个模型配置。
/// 与本地模型目录（ModelEntry.Modality/Tags）及聊天路由共用。
/// </summary>
public static class AiTaskCategory
{
    /// <summary>通用对话（默认）</summary>
    public const string Chat = "chat";
    /// <summary>深度推理（数学/逻辑/长链条分析）</summary>
    public const string Reasoning = "reasoning";
    /// <summary>代码编程</summary>
    public const string Code = "code";
    /// <summary>图像视觉（多模态）</summary>
    public const string Vision = "vision";

    public static readonly string[] All = { Chat, Reasoning, Code, Vision };

    public static bool IsValid(string? category) =>
        !string.IsNullOrWhiteSpace(category) &&
        All.Contains(category, StringComparer.OrdinalIgnoreCase);

    /// <summary>规范化分类键（无效返回 chat）</summary>
    public static string Normalize(string? category) =>
        IsValid(category) ? category!.ToLowerInvariant() : Chat;
}

/// <summary>分类定义（元数据，名称/图标由前端本地化）</summary>
public class AiCategoryDefinitionDto
{
    public string Key { get; set; } = "";
    public string Icon { get; set; } = "";
    /// <summary>该分类适合的模型类型说明（模态/能力提示）</summary>
    public string Description { get; set; } = "";
}

/// <summary>分类 → 模型指派（每类一个模型配置）</summary>
public class AiCategoryAssignmentDto
{
    public string Category { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ModelName { get; set; } = "";
}

/// <summary>分类配置总览（设置页加载）</summary>
public class AiCategoryConfigDto
{
    public List<AiCategoryDefinitionDto> Categories { get; set; } = new();
    public List<AiCategoryAssignmentDto> Assignments { get; set; } = new();

    /// <summary>每个分类当前实际生效的解析结果（未指派时回退主提供方/主模型）</summary>
    public List<AiCategoryResolutionDto> Resolved { get; set; } = new();
}

/// <summary>分类解析结果（展示"当前生效"用）</summary>
public class AiCategoryResolutionDto
{
    public string Category { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string ModelName { get; set; } = "";
    /// <summary>是否命中显式指派（false = 回退到主提供方/主模型或分类标记模型）</summary>
    public bool FromAssignment { get; set; }
}

public class SaveAiCategoriesRequest
{
    public List<AiCategoryAssignmentDto> Assignments { get; set; } = new();
}
