using Microsoft.EntityFrameworkCore;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Family.Services.Todo;

/// <summary>
/// 个人待办事项服务（单用户、极简：标题 + 完成状态 + 可选目标分组与执行指引）。
/// </summary>
public class TodoService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;

    public TodoService(IDbContextFactory<FamilyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>获取全部待办（按创建顺序）</summary>
    public async Task<List<TodoItem>> GetAllAsync(CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TodoItems.OrderBy(t => t.Id).ToListAsync(ct);
    }

    /// <summary>获取全部目标（含各自待办，按创建顺序）</summary>
    public async Task<List<TodoGoal>> GetGoalsAsync(CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TodoGoals
            .Include(g => g.Items)
            .OrderBy(g => g.Id)
            .ToListAsync(ct);
    }

    /// <summary>创建待办。标题空白或超长时返回 null（由控制器转 400）</summary>
    public async Task<TodoItem?> CreateAsync(string title, int? goalId = null, string? note = null, CancellationToken ct = default)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > 200)
            return null;
        var noteTrimmed = NormalizeNote(note);
        if (noteTrimmed == null && !string.IsNullOrWhiteSpace(note))
            return null; // Note 超长

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = new TodoItem { Title = trimmed, GoalId = goalId, Note = noteTrimmed };
        db.TodoItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// 更新待办（标题/完成状态/执行指引，至少传一项，调用方保证标题已校验）。
    /// 不存在时返回 null。
    /// </summary>
    public async Task<TodoItem?> UpdateAsync(int id, string? title, bool? isDone, string? note, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.TodoItems.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (item == null)
            return null;

        if (title != null)
        {
            item.Title = title.Trim();
        }

        if (isDone.HasValue && item.IsDone != isDone.Value)
        {
            item.IsDone = isDone.Value;
            item.CompletedAt = isDone.Value ? DateTime.UtcNow : null;
        }

        if (note != null)
        {
            var noteTrimmed = NormalizeNote(note);
            if (noteTrimmed == null && !string.IsNullOrWhiteSpace(note))
                return null; // Note 超长，视为非法请求
            item.Note = noteTrimmed;
        }

        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>删除待办。不存在时返回 false</summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.TodoItems.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (item == null)
            return false;

        db.TodoItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>创建目标（不附带待办）。标题空白或超长时返回 null</summary>
    public async Task<TodoGoal?> CreateGoalAsync(string title, CancellationToken ct = default)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > 200)
            return null;

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var goal = new TodoGoal { Title = trimmed };
        db.TodoGoals.Add(goal);
        await db.SaveChangesAsync(ct);
        return goal;
    }

    /// <summary>
    /// 创建目标并批量附加待办（AI 生成路径：单事务原子写入）。
    /// 目标标题非法或有效待办为 0 时返回 null（不创建任何数据）。
    /// </summary>
    public async Task<TodoGoal?> CreateGoalWithItemsAsync(
        string goalTitle, IEnumerable<(string Title, string? Note)> items, CancellationToken ct = default)
    {
        var trimmed = goalTitle?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > 200)
            return null;

        var validItems = new List<TodoItem>();
        foreach (var (title, note) in items)
        {
            var t = title?.Trim() ?? "";
            if (t.Length == 0 || t.Length > 200)
                continue;
            var n = NormalizeNote(note);
            if (n == null && !string.IsNullOrWhiteSpace(note))
                continue; // Note 超长，跳过该项
            validItems.Add(new TodoItem { Title = t, Note = n });
        }

        if (validItems.Count == 0)
            return null;

        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var goal = new TodoGoal { Title = trimmed, Items = validItems };
        db.TodoGoals.Add(goal);
        await db.SaveChangesAsync(ct);
        return goal;
    }

    /// <summary>删除目标（级联删除其下全部待办）。不存在时返回 false</summary>
    public async Task<bool> DeleteGoalAsync(int id, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var goal = await db.TodoGoals.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (goal == null)
            return false;

        db.TodoGoals.Remove(goal);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>规范化执行指引：空白 → null；超长（>1000）→ null（由调用方判断为非法）</summary>
    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;
        var trimmed = note.Trim();
        return trimmed.Length > 1000 ? null : trimmed;
    }
}
