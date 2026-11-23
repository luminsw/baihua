using Microsoft.AspNetCore.Mvc;
using Baihua.Contracts.Todo;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services.Todo;

namespace Baihua.Modules.Family.Controllers;

/// <summary>
/// 个人待办清单 API（单用户、极简：标题 + 完成状态 + 可选目标分组与执行指引）。
/// 目标为一级组织，AI 可把用户输入的目标拆解为一组具体待办。
/// </summary>
[ApiController]
[Route("api/todos")]
public class TodoController : ControllerBase
{
    private readonly TodoService _todoService;
    private readonly TodoAiService _todoAiService;

    public TodoController(TodoService todoService, TodoAiService todoAiService)
    {
        _todoService = todoService;
        _todoAiService = todoAiService;
    }

    /// <summary>获取全部待办（按创建顺序）</summary>
    [HttpGet]
    public async Task<ActionResult<List<TodoItemDto>>> GetAll(CancellationToken ct)
    {
        var items = await _todoService.GetAllAsync(ct);
        return Ok(items.Select(ToDto).ToList());
    }

    /// <summary>获取全部目标（含各自待办，按创建顺序）</summary>
    [HttpGet("goals")]
    public async Task<ActionResult<List<TodoGoalDto>>> GetGoals(CancellationToken ct)
    {
        var goals = await _todoService.GetGoalsAsync(ct);
        return Ok(goals.Select(ToGoalDto).ToList());
    }

    /// <summary>创建待办</summary>
    [HttpPost]
    public async Task<ActionResult<TodoItemDto>> Create([FromBody] CreateTodoRequest request, CancellationToken ct)
    {
        var title = request?.Title?.Trim() ?? "";
        if (title.Length == 0)
            return BadRequest(new { error = "待办内容不能为空" });
        if (title.Length > 200)
            return BadRequest(new { error = "待办内容过长（最多 200 字）" });

        var item = await _todoService.CreateAsync(title, request?.GoalId, null, ct);
        return Ok(ToDto(item!));
    }

    /// <summary>更新待办（标题 / 完成状态 / 执行指引）</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<TodoItemDto>> Update(int id, [FromBody] UpdateTodoRequest request, CancellationToken ct)
    {
        if (request == null || (request.Title == null && request.IsDone == null && request.Note == null))
            return BadRequest(new { error = "至少需要提供标题、完成状态或执行指引之一" });

        string? title = null;
        if (request.Title != null)
        {
            title = request.Title.Trim();
            if (title.Length == 0)
                return BadRequest(new { error = "待办内容不能为空" });
            if (title.Length > 200)
                return BadRequest(new { error = "待办内容过长（最多 200 字）" });
        }

        var item = await _todoService.UpdateAsync(id, title, request.IsDone, request.Note, ct);
        if (item == null)
            return NotFound(new { error = "待办不存在" });

        return Ok(ToDto(item));
    }

    /// <summary>删除待办</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _todoService.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { error = "待办不存在" });
    }

    /// <summary>
    /// AI 生成预览：输入一个目标，AI 拆解为一组具体待办（含执行指引）。
    /// 仅返回预览不落库，用户确认后再调用 ai-save 保存。
    /// </summary>
    [HttpPost("ai-generate")]
    public async Task<ActionResult<AiTodoPreviewDto>> AiGenerate([FromBody] GenerateTodosRequest request, CancellationToken ct)
    {
        var result = await _todoAiService.GeneratePreviewAsync(request?.Goal ?? "", ct);
        if (!result.Success || result.Preview == null)
            return BadRequest(new { error = result.Error ?? "生成失败" });

        return Ok(result.Preview);
    }

    /// <summary>保存 AI 生成的待办（预览确认后提交，目标 + 待办单事务写入）</summary>
    [HttpPost("ai-save")]
    public async Task<ActionResult<TodoGoalDto>> AiSave([FromBody] SaveGeneratedTodosRequest request, CancellationToken ct)
    {
        var title = request?.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > 200)
            return BadRequest(new { error = "目标标题不能为空或过长（最多 200 字）" });

        var items = (request?.Items ?? new List<AiTodoPreviewItemDto>())
            .Select(i => (Title: i?.Title ?? "", Note: i?.Note))
            .ToList();
        if (items.Count == 0)
            return BadRequest(new { error = "没有可保存的待办" });

        var goal = await _todoService.CreateGoalWithItemsAsync(title, items, ct);
        if (goal == null)
            return BadRequest(new { error = "没有可保存的待办" });

        return Ok(ToGoalDto(goal));
    }

    /// <summary>删除目标（级联删除其下全部待办）</summary>
    [HttpDelete("goals/{id:int}")]
    public async Task<IActionResult> DeleteGoal(int id, CancellationToken ct)
    {
        var deleted = await _todoService.DeleteGoalAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { error = "目标不存在" });
    }

    private static TodoItemDto ToDto(TodoItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        IsDone = item.IsDone,
        CreatedAt = item.CreatedAt,
        CompletedAt = item.CompletedAt,
        GoalId = item.GoalId,
        Note = item.Note
    };

    private static TodoGoalDto ToGoalDto(TodoGoal goal) => new()
    {
        Id = goal.Id,
        Title = goal.Title,
        CreatedAt = goal.CreatedAt,
        Items = (goal.Items ?? new List<TodoItem>())
            .OrderBy(i => i.Id)
            .Select(ToDto)
            .ToList()
    };
}
