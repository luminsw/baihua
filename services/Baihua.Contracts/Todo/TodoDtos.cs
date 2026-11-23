namespace Baihua.Contracts.Todo;

/// <summary>
/// 个人待办事项（单用户、极简：标题 + 完成状态 + 可选目标分组与执行指引）。
/// </summary>
public class TodoItemDto
{
    public int Id { get; set; }

    /// <summary>待办标题</summary>
    public string Title { get; set; } = "";

    /// <summary>是否已完成</summary>
    public bool IsDone { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>完成时间（未完成时为 null）</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>所属目标 Id（无目标时为 null）</summary>
    public int? GoalId { get; set; }

    /// <summary>执行指引（AI 生成：去哪个机构、登录哪个网站、准备什么证件、填哪些表单等）</summary>
    public string? Note { get; set; }
}

/// <summary>
/// 待办目标（一级）：用目标组织一组具体的待办事项。
/// </summary>
public class TodoGoalDto
{
    public int Id { get; set; }

    /// <summary>目标描述</summary>
    public string Title { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>该目标下的具体待办（按创建顺序）</summary>
    public List<TodoItemDto> Items { get; set; } = new();
}

/// <summary>创建待办请求</summary>
public class CreateTodoRequest
{
    /// <summary>待办标题（必填，去空格后最长 200 字符）</summary>
    public string Title { get; set; } = "";

    /// <summary>所属目标 Id（可选）</summary>
    public int? GoalId { get; set; }
}

/// <summary>更新待办请求（标题、完成状态、执行指引均可选，至少传一项）</summary>
public class UpdateTodoRequest
{
    public string? Title { get; set; }

    public bool? IsDone { get; set; }

    /// <summary>执行指引（传入 null 表示不修改，传入空字符串表示清空）</summary>
    public string? Note { get; set; }
}

/// <summary>AI 生成待办请求：输入一个目标，AI 拆解为一组具体待办（仅生成预览，不保存）</summary>
public class GenerateTodosRequest
{
    /// <summary>目标描述（必填，如"办理机动车驾驶证"）</summary>
    public string Goal { get; set; } = "";
}

/// <summary>AI 生成的待办预览（未保存，供用户确认后再入库）</summary>
public class AiTodoPreviewDto
{
    /// <summary>目标标题（AI 生成；为空时回退为用户的原始输入）</summary>
    public string Title { get; set; } = "";

    /// <summary>具体待办（已按保存规则过滤：标题非空且 ≤200 字、指引 ≤1000 字）</summary>
    public List<AiTodoPreviewItemDto> Items { get; set; } = new();
}

/// <summary>AI 生成的单个待办（预览/保存共用）</summary>
public class AiTodoPreviewItemDto
{
    /// <summary>具体动作标题</summary>
    public string Title { get; set; } = "";

    /// <summary>执行指引（机构、网站、证件、表单等）</summary>
    public string? Note { get; set; }
}

/// <summary>保存 AI 生成的待办请求（预览确认后提交）</summary>
public class SaveGeneratedTodosRequest
{
    /// <summary>目标标题（必填，去空格后 ≤200 字）</summary>
    public string Title { get; set; } = "";

    /// <summary>待办列表（至少一项，标题非空且 ≤200 字）</summary>
    public List<AiTodoPreviewItemDto> Items { get; set; } = new();
}
