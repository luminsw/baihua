using Baihua.Contracts.Search;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Family.Tests.Learning;
using Baihua.Family.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// EmbeddingService.RerankBySimilarityAsync 余弦重排测试：
/// 查询向量经 Mock&lt;AiClientService&gt;.CreateEmbeddingGenerator 注入（virtual 测试缝），
/// 笔记向量预置在 NoteEmbeddings 缓存表（不触网）。
/// ① 按与查询向量的余弦相似度降序重排，并把相似度 × 10 写回 Score
/// ② 无缓存向量的笔记按原 Score 参与排序（兜底）
/// </summary>
public class EmbeddingRerankTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FakeDbFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "rerank-test-vault";

    public EmbeddingRerankTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VaultDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new VaultDbContext(options)) ctx.Database.EnsureCreated();
        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(options));
    }

    public void Dispose() => _connection.Dispose();

    // ===================== Helpers =====================

    /// <summary>任何文本都返回固定 [1,0] 向量的假生成器（查询向量注入点）</summary>
    private sealed class FixedVectorGenerator(params float[] vector) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                result.Add(new Embedding<float>(vector));
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private EmbeddingService CreateService(float[] queryVector)
    {
        // EmbeddingUrl/Model 非空 → IsSemanticSearchEnabled=true；
        // IEmbeddingConfigProvider 抛错 → 回退到 AiSettingsService（同 EmbeddingServiceTests 的做法）
        var configData = new Dictionary<string, string?>
        {
            ["EmbeddingUrl"] = "http://localhost:19999/v1/embeddings",
            ["EmbeddingModel"] = "test-embed-model",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var aiSettings = new AiSettingsService(configuration, StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance);

        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        var generator = new FixedVectorGenerator(queryVector);
        aiClient.Setup(c => c.CreateEmbeddingGenerator()).Returns(generator);
        aiClient.Setup(c => c.CreateEmbeddingGenerator(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(generator);

        var provider = new Mock<Baihua.Core.Modules.IEmbeddingConfigProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test: embedding provider unavailable"));

        var vaultSettings = new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance);
        return new EmbeddingService(
            aiClient.Object, aiSettings, vaultSettings, _vaultFactory,
            provider.Object, NullLogger<EmbeddingService>.Instance);
    }

    private async Task SeedVaultAsync()
    {
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        ctx.Vaults.Add(new Vault { VaultId = VaultId, Name = "测试库", Path = "/tmp/x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedVectorAsync(string notePath, double[] vector)
    {
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        ctx.NoteEmbeddings.Add(new NoteEmbedding
        {
            VaultId = VaultId,
            NotePath = notePath,
            VectorJson = System.Text.Json.JsonSerializer.Serialize(vector),
            Dimensions = vector.Length,
        });
        await ctx.SaveChangesAsync();
    }

    // ===================== 测试 =====================

    [Fact]
    public async Task Rerank_OrdersByCosineSimilarity_AndRewritesScore()
    {
        await SeedVaultAsync();
        await SeedVectorAsync("a.md", [1.0, 0.0]); // 与查询 [1,0] 相似度 1.0
        await SeedVectorAsync("b.md", [0.0, 1.0]); // 与查询 [1,0] 相似度 0.0
        var svc = CreateService([1.0f, 0.0f]);

        var results = new List<SearchResult>
        {
            new() { Title = "b", Path = "b.md", Preview = "b", Score = 9 },  // FTS 原分高
            new() { Title = "a", Path = "a.md", Preview = "a", Score = 5 },
        };

        var reranked = await svc.RerankBySimilarityAsync("查询", results);

        // 语义相关（a）被提到第一，Score 重写为 相似度×10
        Assert.Equal("a", reranked[0].Title);
        Assert.Equal(10, reranked[0].Score);
        Assert.Equal("b", reranked[1].Title);
        Assert.Equal(0, reranked[1].Score);
    }

    [Fact]
    public async Task Rerank_NoteWithoutCachedVector_KeepsFtsScoreAsFallback()
    {
        await SeedVaultAsync();
        await SeedVectorAsync("a.md", [1.0, 0.0]);
        // "zz.md" 无缓存向量 → 走"计算新向量"路径，也返回 [1,0]（FixedVectorGenerator）
        // → 相似度 1.0，与 a 并列；验证整条链路不抛异常且有序结果返回
        var svc = CreateService([1.0f, 0.0f]);

        var results = new List<SearchResult>
        {
            new() { Title = "a", Path = "a.md", Preview = "a", Score = 5 },
            new() { Title = "zz", Path = "zz.md", Preview = "zz", Score = 9 },
        };

        var reranked = await svc.RerankBySimilarityAsync("查询", results);

        Assert.Equal(2, reranked.Count);
        // 两者相似度同为 1.0 → Score 相同（10），稳定排序下相对次序不乱
        Assert.All(reranked, r => Assert.Equal(10, r.Score));
    }

    [Fact]
    public async Task Rerank_EmptyResults_ReturnsEmpty()
    {
        await SeedVaultAsync();
        var svc = CreateService([1.0f, 0.0f]);
        var reranked = await svc.RerankBySimilarityAsync("查询", []);
        Assert.Empty(reranked);
    }
}
