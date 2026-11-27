using Baihua.Contracts.Ai;
using Baihua.Core;
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
/// ChatMemoryService 摘要压缩（Layer 2）测试：
/// ① 空历史 → 空 MemoryContext
/// ② 轮数 ≤ 阈值（10）→ 不调 AI，直接返回截断历史
/// ③ 轮数 > 阈值 → 早期历史压缩为摘要（AI 一次）、最近 5 轮完整保留，摘要落库（Round=0）
/// ④ 命中缓存摘要 → 不再调 AI
/// ⑤ AI 摘要失败 → 降级：Summary 为空但最近历史仍返回（聊天不被打断）
/// </summary>
public class ChatMemorySummaryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FakeDbFactory<FamilyDbContext> _familyFactory;

    public ChatMemorySummaryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<FamilyDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new FamilyDbContext(options)) ctx.Database.EnsureCreated();
        _familyFactory = new FakeDbFactory<FamilyDbContext>(() => new FamilyDbContext(options));
    }

    public void Dispose() => _connection.Dispose();

    // ===================== Helpers =====================

    /// <summary>按调用次序返回预设响应的假 ChatClient</summary>
    private sealed class FakeChatClient(IList<string> responses) : IChatClient
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _callCount);
            var text = responses[Math.Min(count - 1, responses.Count - 1)];
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private (ChatMemoryService Service, FakeChatClient Chat) CreateService(string[]? summaryResponses = null, Exception? chatThrows = null)
    {
        var chat = new FakeChatClient(summaryResponses ?? ["这是对话摘要"]);
        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        if (chatThrows != null)
            aiClient.Setup(c => c.CreateChatClient(It.IsAny<string>(), It.IsAny<string>()))
                .Throws(chatThrows);
        else
            aiClient.Setup(c => c.CreateChatClient(It.IsAny<string>(), It.IsAny<string>())).Returns(chat);

        // EmbeddingService 仅构造用（Layer 3 语义记忆不在此测试范围）
        var embedding = new Mock<EmbeddingService>(
            aiClient.Object,
            new AiSettingsService(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
                StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance),
            new VaultSettingsService(
                new FakeDbFactory<VaultDbContext>(() => throw new InvalidOperationException("本测试不使用 vault DB")),
                NullLogger<VaultSettingsService>.Instance),
            new FakeDbFactory<VaultDbContext>(() => throw new InvalidOperationException("本测试不使用 vault DB")),
            new Mock<Baihua.Core.Modules.IEmbeddingConfigProvider>().Object,
            NullLogger<EmbeddingService>.Instance);

        var service = new ChatMemoryService(
            aiClient.Object,
            embedding.Object,
            new DefaultPromptProvider(NullLogger<DefaultPromptProvider>.Instance),
            _familyFactory,
            NullLogger<ChatMemoryService>.Instance,
            TestLocalizer.Instance);

        return (service, chat);
    }

    private static List<ChatHistoryItem> History(int messageCount)
    {
        var list = new List<ChatHistoryItem>();
        for (var i = 0; i < messageCount; i++)
            list.Add(new ChatHistoryItem
            {
                Role = i % 2 == 0 ? "user" : "ai",
                Content = $"第{i}条消息内容：讨论了知识点{i}。"
            });
        return list;
    }

    private async Task<ChatMemoryEntry?> GetCachedSummaryAsync(string sessionId)
    {
        await using var ctx = await _familyFactory.CreateDbContextAsync();
        return await ctx.ChatMemoryEntries
            .Where(e => e.SessionId == sessionId && e.Round == 0)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();
    }

    // ===================== 测试 =====================

    [Fact]
    public async Task EmptyHistory_ReturnsEmptyContext()
    {
        var (svc, chat) = CreateService();
        var result = await svc.BuildMemoryContextAsync([], "test-model", "p1", "s1");
        Assert.Null(result.Summary);
        Assert.Empty(result.RecentHistory);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task WithinThreshold_NoSummarization_NoAiCall()
    {
        // 20 条消息 = 10 轮 = 阈值边界 → 不压缩
        var (svc, chat) = CreateService();
        var history = History(20);

        var result = await svc.BuildMemoryContextAsync(history, "test-model", "p1", "s1");

        Assert.Null(result.Summary);
        Assert.Equal(history.Count, result.RecentHistory.Count);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task OverThreshold_SummarizesEarly_KeepsRecentRounds_CachesSummary()
    {
        // 22 条消息 = 11 轮 > 10 → 早期 12 条摘要、最近 10 条保留
        var (svc, chat) = CreateService(["早期对话的关键事实摘要"]);
        var history = History(22);

        var result = await svc.BuildMemoryContextAsync(history, "test-model", "p1", sessionId: "session-1");

        Assert.Equal(1, chat.CallCount);
        Assert.Equal("早期对话的关键事实摘要", result.Summary);
        Assert.Equal(10, result.RecentHistory.Count); // RecentRoundsToKeep=5 → 5 轮 × 2
        Assert.Equal("第12条消息内容：讨论了知识点12。", result.RecentHistory[0].Content);

        // 摘要已缓存到 DB（Round=0）
        var cached = await GetCachedSummaryAsync("session-1");
        Assert.NotNull(cached);
        Assert.Equal("早期对话的关键事实摘要", cached!.UserSummary);
    }

    [Fact]
    public async Task CachedSummaryHit_SkipsAiCall()
    {
        // 预置缓存摘要
        await using (var ctx = await _familyFactory.CreateDbContextAsync())
        {
            ctx.ChatMemoryEntries.Add(new ChatMemoryEntry
            {
                SessionId = "session-2",
                Round = 0,
                UserSummary = "数据库里的旧摘要",
                AssistantSummary = "",
                UserContent = "",
                AssistantContent = "",
                EstimatedTokens = 10,
                CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var (svc, chat) = CreateService();
        var result = await svc.BuildMemoryContextAsync(History(22), "test-model", "p1", sessionId: "session-2");

        Assert.Equal(0, chat.CallCount); // 命中缓存不再调 AI
        Assert.Equal("数据库里的旧摘要", result.Summary);
    }

    [Fact]
    public async Task AiSummaryFails_DegradesGracefully_RecentHistoryStillReturned()
    {
        var (svc, chat) = CreateService(chatThrows: new InvalidOperationException("AI down"));

        var result = await svc.BuildMemoryContextAsync(History(22), "test-model", "p1", sessionId: "session-3");

        _ = chat;
        Assert.Null(result.Summary); // 摘要失败降级为 null，不抛异常
        Assert.Equal(10, result.RecentHistory.Count);
        Assert.All(result.RecentHistory, h => Assert.Contains("讨论了知识点", h.Content));
    }
}
