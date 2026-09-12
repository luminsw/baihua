using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Baihua.Contracts.Ai;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Modules.Ai.Controllers;

/// <summary>
/// Comfy 生成记录 API（存于 AI 服务 ai.db 的 ComfyArtworks 表）。
/// 一服务一数据库：Comfy 历史数据归 AI 服务独占；Family 的 ComfyController 经本 API 读写，
/// 不再直连 ai.db。
/// </summary>
[ApiController]
[Route("api/ai/comfy/artworks")]
public class ComfyArtworksController : ControllerBase
{
    private readonly IDbContextFactory<AIDbContext> _dbFactory;
    private readonly ILogger<ComfyArtworksController> _logger;

    public ComfyArtworksController(
        IDbContextFactory<AIDbContext> dbFactory,
        ILogger<ComfyArtworksController> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>历史生成记录（最近 N 条，可按类型过滤）</summary>
    [HttpGet]
    public async Task<ActionResult<List<AiComfyArtworkDto>>> List(int limit = 50, string? kind = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.ComfyArtworks.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(kind)) query = query.Where(e => e.Kind == kind);
        var items = await query.OrderByDescending(e => e.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return Ok(items.Select(ToDto).ToList());
    }

    /// <summary>保存一条生成记录（成功/失败都记录）</summary>
    [HttpPost]
    public async Task<ActionResult<AiComfyArtworkDto>> Create([FromBody] SaveAiComfyArtworkRequest request, CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PromptId))
            return BadRequest(new { error = "PromptId is required" });

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = new ComfyArtworkEntity
        {
            Kind = request.Kind,
            Prompt = request.Prompt,
            Model = request.Model,
            ParamsJson = request.ParamsJson,
            FileName = request.FileName,
            Subfolder = request.Subfolder,
            FileType = request.FileType,
            PromptId = request.PromptId,
            IsSuccess = request.IsSuccess,
            ErrorMessage = request.ErrorMessage,
            DurationSeconds = request.DurationSeconds
        };
        db.ComfyArtworks.Add(entity);
        await db.SaveChangesAsync(ct);
        _logger.LogDebug("已保存 Comfy 生成记录：{Kind}/{PromptId} Success={Success}", entity.Kind, entity.PromptId, entity.IsSuccess);
        return Ok(ToDto(entity));
    }

    /// <summary>删除一条历史记录</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ComfyArtworks.FindAsync([id], ct);
        if (entity == null) return NotFound();
        db.ComfyArtworks.Remove(entity);
        await db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    private static AiComfyArtworkDto ToDto(ComfyArtworkEntity e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        Prompt = e.Prompt,
        Model = e.Model,
        ParamsJson = e.ParamsJson,
        FileName = e.FileName,
        Subfolder = e.Subfolder,
        FileType = e.FileType,
        PromptId = e.PromptId,
        IsSuccess = e.IsSuccess,
        ErrorMessage = e.ErrorMessage,
        DurationSeconds = e.DurationSeconds,
        CreatedAt = e.CreatedAt
    };
}
