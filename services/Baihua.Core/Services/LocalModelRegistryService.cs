using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Baihua.Data;
using Baihua.Data.Entities;

namespace Baihua.Core.Services;

/// <summary>
/// 本地大模型注册表服务（唯一事实来源）。
/// Agent 经百花 MCP 调用 Upsert/Remove，WebUI 调用 List 只读展示。
/// </summary>
public class LocalModelRegistryService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbContextFactory;
    private readonly ILogger<LocalModelRegistryService>? _logger;

    public LocalModelRegistryService(
        IDbContextFactory<FamilyDbContext> dbContextFactory,
        ILogger<LocalModelRegistryService>? logger = null)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<List<LocalModelRegistry>> ListAsync(string? tool = null)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var query = db.LocalModelRegistries.AsNoTracking();
        if (!string.IsNullOrEmpty(tool))
            query = query.Where(e => e.Tool == tool);
        return await query.OrderBy(e => e.Tool).ThenBy(e => e.DisplayName).ToListAsync();
    }

    public async Task<LocalModelRegistry> UpsertAsync(
        string tool, string modelId, string displayName, string endpoint,
        string? parameterSize = null, string? quantization = null, string? usage = null,
        long? sizeBytes = null, string? capabilities = null, string? notes = null,
        string registeredBy = "")
    {
        using var db = _dbContextFactory.CreateDbContext();
        var existing = await db.LocalModelRegistries
            .FirstOrDefaultAsync(e => e.Tool == tool && e.ModelId == modelId);

        if (existing is null)
        {
            var entry = new LocalModelRegistry
            {
                Tool = tool,
                ModelId = modelId,
                DisplayName = displayName,
                Endpoint = endpoint,
                ParameterSize = parameterSize,
                Quantization = quantization,
                Usage = usage,
                SizeBytes = sizeBytes,
                Capabilities = capabilities,
                Notes = notes,
                RegisteredBy = registeredBy,
                RegisteredAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.LocalModelRegistries.Add(entry);
            await db.SaveChangesAsync();
            _logger?.LogInformation("注册本地模型: {Tool}/{ModelId} -> {Endpoint}", tool, modelId, endpoint);
            return entry;
        }

        existing.DisplayName = displayName;
        existing.Endpoint = endpoint;
        existing.ParameterSize = parameterSize;
        existing.Quantization = quantization;
        existing.Usage = usage;
        existing.SizeBytes = sizeBytes;
        existing.Capabilities = capabilities;
        existing.Notes = notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _logger?.LogInformation("更新本地模型注册: {Tool}/{ModelId} -> {Endpoint}", tool, modelId, endpoint);
        return existing;
    }

    public async Task<bool> RemoveAsync(string tool, string modelId)
    {
        using var db = _dbContextFactory.CreateDbContext();
        var existing = await db.LocalModelRegistries
            .FirstOrDefaultAsync(e => e.Tool == tool && e.ModelId == modelId);
        if (existing is null) return false;
        db.LocalModelRegistries.Remove(existing);
        await db.SaveChangesAsync();
        _logger?.LogInformation("注销本地模型: {Tool}/{ModelId}", tool, modelId);
        return true;
    }
}