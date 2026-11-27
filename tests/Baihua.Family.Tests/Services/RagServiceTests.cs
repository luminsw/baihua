using Baihua.Contracts.Search;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Family.Tests.Learning;
using Baihua.Family.Tests.TestDoubles;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// RagService 检索增强测试（真实 VaultNoteIndexer + 内存 SQLite FTS，不触达 HTTP）：
/// ① ShouldSearchVault 关键词门控（含空串）
/// ② 无关键词 / 无活动知识库 / 无检索结果 / 依赖抛异常 → 原样返回消息
/// ③ 命中 → 最后一条用户消息替换为"知识库上下文 + 原问题"，只取前 5 条
/// ④ 语义未启用时重排保持原顺序（RagService 默认集成的兜底契约）
/// </summary>
public class RagServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FakeDbFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "rag-test-vault";

    public RagServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VaultDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new VaultDbContext(options)) ctx.Database.EnsureCreated();
        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(options));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // ===================== Helpers =====================

    private VaultSettingsService CreateVaultSettings()
        => new(_vaultFactory, NullLogger<VaultSettingsService>.Instance);

    private VaultNoteIndexer CreateIndexer()
        => new(_vaultFactory, NullLogger<VaultNoteIndexer>.Instance);

    private RagService CreateRagService(EmbeddingService? embedding = null, VaultSettingsService? vaultSettings = null)
        => new(
            CreateIndexer(),
            embedding ?? CreateEmbeddingService(),
            vaultSettings ?? CreateVaultSettings(),
            NullLogger<RagService>.Instance);

    /// <summary>语义搜索未配置（EmbeddingUrl/Model 为空）的 EmbeddingService —— IsSemanticSearchEnabled=false</summary>
    private EmbeddingService CreateEmbeddingService(AiSettingsService? aiSettings = null)
    {
        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        var config = aiSettings is null
            ? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build()
            : null;
        var settings = aiSettings ?? new AiSettingsService(
            config!, StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance);
        var provider = new Mock<Baihua.Core.Modules.IEmbeddingConfigProvider>();
        return new EmbeddingService(
            aiClient.Object, settings, CreateVaultSettings(), _vaultFactory,
            provider.Object, NullLogger<EmbeddingService>.Instance);
    }

    private async Task SeedActiveVaultAsync()
    {
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        ctx.Vaults.Add(new Vault { VaultId = VaultId, Name = "中医", Path = "/tmp/x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedFtsRowAsync(string title, string content, string filePath)
    {
        var indexer = CreateIndexer();
        await indexer.EnsureFtsTableAsync();
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText = "INSERT INTO VaultNoteFts (title, content, vault_id, file_path) VALUES ($t, $c, $v, $f)";
        cmd.Parameters.Add(new SqliteParameter("$t", title));
        cmd.Parameters.Add(new SqliteParameter("$c", content));
        cmd.Parameters.Add(new SqliteParameter("$v", VaultId));
        cmd.Parameters.Add(new SqliteParameter("$f", filePath));
        await cmd.ExecuteNonQueryAsync();
    }

    private static List<ChatMessage> Messages(params string[] userTexts)
        => userTexts.Select(t => new ChatMessage(ChatRole.User, t)).ToList();

    // ===================== ① ShouldSearchVault =====================

    [Theory]
    [InlineData("帮我看看桂枝汤的方剂", true)]
    [InlineData("找一下我的笔记", true)]
    [InlineData("什么是辨证论治", true)]
    [InlineData("查一下 vault 里的内容", true)]
    [InlineData("今天天气怎么样", false)]
    [InlineData("你好", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ShouldSearchVault_GatesOnKeywords(string message, bool expected)
        => Assert.Equal(expected, RagService.ShouldSearchVault(message));

    // ===================== ② 原样返回分支 =====================

    [Fact]
    public async Task Enrich_NoKeyword_ReturnsMessagesUnchanged()
    {
        await SeedActiveVaultAsync();
        var svc = CreateRagService();
        var messages = Messages("今天天气怎么样");

        var result = await svc.EnrichMessagesWithVaultContextAsync(messages);

        Assert.Same(messages, result);
        Assert.Equal("今天天气怎么样", result.Last().Text);
    }

    [Fact]
    public async Task Enrich_NoActiveVault_ReturnsMessagesUnchanged()
    {
        var svc = CreateRagService();
        var messages = Messages("找一下我的笔记");

        var result = await svc.EnrichMessagesWithVaultContextAsync(messages);

        Assert.Same(messages, result);
    }

    [Fact]
    public async Task Enrich_NoSearchResults_ReturnsMessagesUnchanged()
    {
        await SeedActiveVaultAsync();
        await SeedFtsRowAsync("感冒", "流鼻涕、打喷嚏", "a.md");
        var svc = CreateRagService();
        var messages = Messages("经络穴位"); // 查询短语不被任何已索引笔记包含

        var result = await svc.EnrichMessagesWithVaultContextAsync(messages);

        Assert.Same(messages, result);
    }

    [Fact]
    public async Task Enrich_DependencyThrows_ReturnsMessagesUnchanged()
    {
        var throwingFactory = new FakeDbFactory<VaultDbContext>(() => throw new InvalidOperationException("db unavailable"));
        var vaultSettings = new VaultSettingsService(throwingFactory, NullLogger<VaultSettingsService>.Instance);
        var svc = CreateRagService(vaultSettings: vaultSettings);
        var messages = Messages("找一下我的笔记");

        var result = await svc.EnrichMessagesWithVaultContextAsync(messages);

        Assert.Same(messages, result); // 检索失败不应让聊天直接崩掉
    }

    // ===================== ③ 命中注入 =====================

    [Fact]
    public async Task Enrich_WithResults_InjectsContextIntoLastUserMessage()
    {
        // 注意：SearchAsync 用整条消息做 LIKE 子串匹配，笔记内容必须包含完整查询短语
        await SeedActiveVaultAsync();
        await SeedFtsRowAsync("足三里", "穴位养生：胃经要穴，常用于保健灸", "notes/zusanli.md");
        await SeedFtsRowAsync("合谷", "穴位养生：大肠经原穴，止痛要穴", "notes/hegu.md");
        var svc = CreateRagService();
        var messages = Messages("介绍一下常见的穴位知识", "穴位养生");

        var result = await svc.EnrichMessagesWithVaultContextAsync(messages);

        Assert.NotSame(messages, result);
        Assert.Equal(messages.Count, result.Count);
        Assert.Equal(messages[0].Text, result[0].Text); // 更早的消息保持不动
        var last = result.Last().Text;
        Assert.Contains("以下是与问题相关的知识库内容", last);
        Assert.Contains("【足三里】", last);
        Assert.Contains("【合谷】", last);
        Assert.Contains("穴位养生", last); // 原问题保留在末尾
    }

    [Fact]
    public async Task Enrich_TakesOnlyTopFiveResults()
    {
        await SeedActiveVaultAsync();
        for (var i = 1; i <= 7; i++)
            await SeedFtsRowAsync($"穴位笔记{i}", $"穴位笔记{i}：第{i}篇", $"notes/n{i}.md");
        var svc = CreateRagService();
        var messages = Messages("穴位笔记");

        var result = await svc.EnrichMessagesWithVaultContextAsync(messages);
        var last = result.Last().Text;

        Assert.Equal(5, last.Count(ch => ch == '【')); // 只注入前 5 条
        Assert.Contains("穴位笔记1", last);
        Assert.DoesNotContain("穴位笔记6", last);
        Assert.DoesNotContain("穴位笔记7", last);
    }

    // ===================== ④ 流式状态 + 重排兜底 =====================

    [Fact]
    public async Task TryEnrichForStreaming_ReportsWhetherEnriched()
    {
        await SeedActiveVaultAsync();
        await SeedFtsRowAsync("方剂", "桂枝汤", "a.md");
        var svc = CreateRagService();

        var (noKeywordMessages, noKeywordEnriched) =
            await svc.TryEnrichForStreamingAsync(Messages("今天天气怎么样"));
        Assert.False(noKeywordEnriched);

        var (enrichedMessages, enriched) = await svc.TryEnrichForStreamingAsync(Messages("方剂"));
        Assert.True(enriched);
        Assert.Contains("【方剂】", enrichedMessages.Last().Text);
    }

    [Fact]
    public async Task Rerank_SemanticDisabled_KeepsOriginalOrderAndScores()
    {
        // 语义搜索未配置时，重排必须原样透传（不重排、不改分）
        var embedding = CreateEmbeddingService();
        var results = new List<SearchResult>
        {
            new() { Title = "b", Path = "b.md", Preview = "b", Score = 9 },
            new() { Title = "a", Path = "a.md", Preview = "a", Score = 5 },
        };

        var reranked = await embedding.RerankBySimilarityAsync("方剂", results);

        Assert.Equal(2, reranked.Count);
        Assert.Equal("b", reranked[0].Title);
        Assert.Equal("a", reranked[1].Title);
        Assert.Equal(9, reranked[0].Score);
        Assert.Equal(5, reranked[1].Score);
    }
}
