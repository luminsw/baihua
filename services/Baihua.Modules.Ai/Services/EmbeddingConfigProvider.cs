using Baihua.Core.Modules;
using Baihua.Data;
using Microsoft.EntityFrameworkCore;

namespace Baihua.Modules.Ai.Services;

/// <summary>
/// Embedding 配置读取实现（AI 模块独占 EmbeddingConfigs 表）。
/// 与 <c>EmbeddingConfigController.GetConfig</c> 保持同一默认值语义：无记录时返回 ollama 默认配置。
/// </summary>
public sealed class EmbeddingConfigProvider : IEmbeddingConfigProvider
{
    private readonly IDbContextFactory<AIDbContext> _dbContextFactory;

    public EmbeddingConfigProvider(IDbContextFactory<AIDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc />
    public async Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var config = await db.EmbeddingConfigs.AsNoTracking().OrderBy(e => e.Id).FirstOrDefaultAsync(cancellationToken);

        return config == null
            ? new EmbeddingSettings("ollama", "nomic-embed-text", "http://localhost:11434/v1", true, null)
            : new EmbeddingSettings(config.ProviderId, config.Model, config.BaseUrl, config.IsEnabled, config.Dimensions);
    }
}
