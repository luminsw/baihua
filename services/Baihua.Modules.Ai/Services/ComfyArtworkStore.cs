using Baihua.Contracts.Ai;
using Baihua.Core.Modules;
using Baihua.Data;
using Baihua.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Baihua.Modules.Ai.Services;

/// <summary>
/// ComfyArtworks（绘图/视频产物流水）存取实现：数据归 AI 模块独占，
/// 家庭模块的绘图控制器经 <see cref="IComfyArtworkStore"/> 接口访问。
/// </summary>
public sealed class ComfyArtworkStore : IComfyArtworkStore
{
    private readonly IDbContextFactory<AIDbContext> _dbFactory;
    private readonly ILogger<ComfyArtworkStore> _logger;

    public ComfyArtworkStore(IDbContextFactory<AIDbContext> dbFactory, ILogger<ComfyArtworkStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<AiComfyArtworkDto>> ListAsync(int limit = 50, string? kind = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ComfyArtworks.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(kind)) query = query.Where(e => e.Kind == kind);
        var items = await query.OrderByDescending(e => e.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
        return items.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<AiComfyArtworkDto> CreateAsync(SaveAiComfyArtworkRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogDebug("已保存 Comfy 生成记录：{Kind}/{PromptId} Success={Success}", entity.Kind, entity.PromptId, entity.IsSuccess);
        return ToDto(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ComfyArtworks.FindAsync([id], cancellationToken);
        if (entity == null) return false;
        db.ComfyArtworks.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
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
