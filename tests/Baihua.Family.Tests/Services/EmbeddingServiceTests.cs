using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core.Security;
using Baihua.Data;
using Baihua.Modules.Family.Services;
using Baihua.Family.Tests.Learning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// EmbeddingService 向量索引测试：
/// ① 增量只对变更笔记调用 embedding API（注入计数）
/// ② 删除笔记删除其向量行
/// ③ 未变更零调用
/// ④ per-vault 锁：同 vault 并发串行、异 vault 可并行
/// ⑤ 全量重建清理磁盘已删除笔记的残留向量行（顺带验证旧签名全量重载）
/// 使用内存 SQLite（共享连接）+ FakeDbFactory，参考 VaultNoteIndexerTests 的写法。
/// </summary>
public class EmbeddingServiceTests : IDisposable
{
    private readonly string _vaultDir;
    private readonly SqliteConnection _connection;
    private readonly FakeDbFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "embedding-test-vault";

    public EmbeddingServiceTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "EmbeddingServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (var ctx = new VaultDbContext(options))
            ctx.Database.EnsureCreated();
        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(options));
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_vaultDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    // ===================== Helpers =====================

    private void WriteNote(string relativePath, string content)
    {
        var full = Path.Combine(_vaultDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>构造启用语义搜索配置的 AiSettingsService（EmbeddingUrl/Model 非空 → IsSemanticSearchEnabled=true）</summary>
    private static AiSettingsService CreateAiSettings()
    {
        var configData = new Dictionary<string, string?>
        {
            { "EmbeddingUrl", "http://localhost:19999/v1/embeddings" },
            { "EmbeddingModel", "test-embed-model" },
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        return new AiSettingsService(configuration, TestDoubles.StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance);
    }

    /// <summary>
    /// 文件型 SQLite 工厂：每个 DbContext 独立连接（支持并发事务）。
    /// 仅并发测试（不同 vault 并行）使用——内存共享连接无法同时承载两个事务。
    /// </summary>
    private FakeDbFactory<VaultDbContext> CreateFileVaultFactory()
    {
        var dbPath = Path.Combine(_vaultDir, "parallel-vault.db");
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True;Default Timeout=10;")
            .Options;
        using (var ctx = new VaultDbContext(options))
            ctx.Database.EnsureCreated();
        return new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(options));
    }

    /// <summary>
    /// 构造 CountingEmbeddingService：真实依赖 + Mock&lt;AiClientService&gt;（索引路径只走
    /// ComputeEmbeddingAsync 注入点，不会触达真实 HTTP 客户端）；aiDbFactory 返回 null
    /// 使 GetEmbeddingConfig 兜底回退到 AiSettingsService 配置。
    /// </summary>
    private CountingEmbeddingService CreateService(TimeSpan? delay = null, IDbContextFactory<VaultDbContext>? vaultFactory = null)
    {
        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        var vaultSettings = new VaultSettingsService(vaultFactory ?? _vaultFactory, NullLogger<VaultSettingsService>.Instance);
        // 合并后 EmbeddingService 经 IEmbeddingConfigProvider 进程内读配置；
        // 这里让 provider 抛错，等价于旧测试的"AI 服务不可达"，从而回退到
        // AiSettingsService 的 EmbeddingUrl/Model 配置
        var embeddingConfigProvider = new Mock<Baihua.Core.Modules.IEmbeddingConfigProvider>();
        embeddingConfigProvider
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test: embedding provider unavailable"));

        return new CountingEmbeddingService(
            aiClient.Object,
            CreateAiSettings(),
            vaultSettings,
            vaultFactory ?? _vaultFactory,
            embeddingConfigProvider.Object,
            NullLogger<EmbeddingService>.Instance,
            delay ?? TimeSpan.Zero);
    }

    // ===================== 测试①：增量只调变更笔记 =====================

    [Fact]
    public async Task Incremental_NewAndChangedNotes_CallApiOnlyForThem()
    {
        WriteNote("a.md", "alpha");
        WriteNote("b.md", "beta");
        var svc = CreateService();

        var first = await svc.IndexVaultAsync(VaultId, _vaultDir, (IReadOnlyDictionary<string, NoteFileStamp>?)null);
        Assert.True(first.IsFullRebuild);
        Assert.Equal(2, first.Added);
        Assert.Equal(2, svc.ApiCallCount); // 全量：两篇都调 API

        // 变更 b、新增 c
        WriteNote("b.md", "beta NEW");
        File.SetLastWriteTimeUtc(Path.Combine(_vaultDir, "b.md"), DateTime.UtcNow.AddSeconds(2));
        WriteNote("c.md", "gamma");

        var before = svc.ApiCallCount;
        var second = await svc.IndexVaultAsync(VaultId, _vaultDir, first.Snapshot);

        Assert.False(second.IsFullRebuild);
        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Updated);
        Assert.Equal(0, second.Removed);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(2, svc.ApiCallCount - before); // 只对 b（更新）、c（新增）调 API

        await using var db = await _vaultFactory.CreateDbContextAsync();
        var rows = await db.NoteEmbeddings.Where(e => e.VaultId == VaultId).ToListAsync();
        Assert.Equal(3, rows.Count);
        var c = Assert.Single(rows, r => r.NotePath == "c.md");
        Assert.Equal("[0.1,0.2,0.3]", c.VectorJson);
        Assert.Equal(3, c.Dimensions);
    }

    // ===================== 测试②：删除笔记删除向量行 =====================

    [Fact]
    public async Task Incremental_DeletedNote_RemovesItsVectorRow()
    {
        WriteNote("a.md", "alpha");
        WriteNote("b.md", "beta");
        var svc = CreateService();

        var first = await svc.IndexVaultAsync(VaultId, _vaultDir, (IReadOnlyDictionary<string, NoteFileStamp>?)null);

        File.Delete(Path.Combine(_vaultDir, "a.md"));

        var before = svc.ApiCallCount;
        var second = await svc.IndexVaultAsync(VaultId, _vaultDir, first.Snapshot);

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Removed);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, svc.ApiCallCount - before); // 删除无需调 API

        await using var db = await _vaultFactory.CreateDbContextAsync();
        var paths = await db.NoteEmbeddings.Where(e => e.VaultId == VaultId).Select(e => e.NotePath).ToListAsync();
        var row = Assert.Single(paths);
        Assert.Equal("b.md", row);
    }

    // ===================== 测试③：未变更零调用 =====================

    [Fact]
    public async Task Incremental_NoChanges_ZeroApiCalls()
    {
        WriteNote("a.md", "alpha");
        var svc = CreateService();

        var first = await svc.IndexVaultAsync(VaultId, _vaultDir, (IReadOnlyDictionary<string, NoteFileStamp>?)null);

        var before = svc.ApiCallCount;
        var second = await svc.IndexVaultAsync(VaultId, _vaultDir, first.Snapshot);

        Assert.False(second.Changed);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, svc.ApiCallCount - before);

        await using var db = await _vaultFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.NoteEmbeddings.CountAsync(e => e.VaultId == VaultId));
    }

    // ===================== 测试④：per-vault 锁 =====================

    [Fact]
    public async Task SameVault_ConcurrentIndexTasks_AreSerialized()
    {
        WriteNote("a.md", "alpha");
        WriteNote("b.md", "beta");
        var svc = CreateService(TimeSpan.FromMilliseconds(400));

        // 同 vault 两个并发索引任务（均为全量重建）
        var t1 = svc.IndexVaultAsync(VaultId, _vaultDir, (IReadOnlyDictionary<string, NoteFileStamp>?)null);
        var t2 = svc.IndexVaultAsync(VaultId, _vaultDir, (IReadOnlyDictionary<string, NoteFileStamp>?)null);
        var results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.Equal(2, r.Added));
        Assert.Equal(4, svc.ApiCallCount);       // 两个任务都完整执行
        Assert.Equal(1, svc.MaxConcurrent);      // 同 vault 串行：任意时刻至多 1 个 API 调用在飞
    }

    [Fact]
    public async Task DifferentVaults_ConcurrentIndexTasks_RunInParallel()
    {
        var dirA = Path.Combine(_vaultDir, "vaultA");
        var dirB = Path.Combine(_vaultDir, "vaultB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        File.WriteAllText(Path.Combine(dirA, "a1.md"), "alpha one");
        File.WriteAllText(Path.Combine(dirA, "a2.md"), "alpha two");
        File.WriteAllText(Path.Combine(dirB, "b1.md"), "beta one");
        File.WriteAllText(Path.Combine(dirB, "b2.md"), "beta two");

        var svc = CreateService(TimeSpan.FromMilliseconds(600), CreateFileVaultFactory());

        var t1 = svc.IndexVaultAsync("vault-a", dirA, (IReadOnlyDictionary<string, NoteFileStamp>?)null);
        var t2 = svc.IndexVaultAsync("vault-b", dirB, (IReadOnlyDictionary<string, NoteFileStamp>?)null);
        var results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.Equal(2, r.Added));
        // 不同 vault 无锁竞争：两个任务的首次 API 调用必然重叠（600ms 延迟内）
        Assert.True(svc.MaxConcurrent >= 2, $"不同 vault 应可并行执行（实测 MaxConcurrent={svc.MaxConcurrent}）");
    }

    // ===================== 测试⑤：全量重建清理残留 =====================

    [Fact]
    public async Task FullRebuild_CleansRowsForDeletedNotes()
    {
        WriteNote("a.md", "alpha");
        WriteNote("b.md", "beta");
        var svc = CreateService();

        await svc.IndexVaultAsync(VaultId, _vaultDir, (IReadOnlyDictionary<string, NoteFileStamp>?)null);

        File.Delete(Path.Combine(_vaultDir, "a.md"));

        // 旧签名全量重载（无快照参数）：同时清理磁盘已删除笔记的残留向量行
        var (indexed, failed) = await svc.IndexVaultAsync(VaultId, _vaultDir);
        Assert.Equal(1, indexed);
        Assert.Equal(0, failed);

        await using var db = await _vaultFactory.CreateDbContextAsync();
        var paths = await db.NoteEmbeddings.Where(e => e.VaultId == VaultId).Select(e => e.NotePath).ToListAsync();
        var row = Assert.Single(paths);
        Assert.Equal("b.md", row);
    }

    // ===================== 测试注入子类 =====================

    /// <summary>
    /// 统计 ComputeEmbeddingAsync 调用次数与最大并发数（delay &gt; 0 时模拟慢 API，
    /// 用于验证 per-vault 锁的串行/并行行为）
    /// </summary>
    private sealed class CountingEmbeddingService : EmbeddingService
    {
        private readonly TimeSpan _delay;
        private int _concurrent;
        private int _maxConcurrent;

        public int ApiCallCount { get; private set; }
        public int MaxConcurrent => _maxConcurrent;

        public CountingEmbeddingService(
            AiClientService aiClientService,
            AiSettingsService aiSettings,
            VaultSettingsService vaultSettings,
            IDbContextFactory<VaultDbContext> vaultDbFactory,
            Baihua.Core.Modules.IEmbeddingConfigProvider embeddingConfigProvider,
            ILogger<EmbeddingService> logger,
            TimeSpan delay)
            : base(aiClientService, aiSettings, vaultSettings, vaultDbFactory, embeddingConfigProvider, logger)
        {
            _delay = delay;
        }

        protected override async Task<List<double>?> ComputeEmbeddingAsync(string text, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref _concurrent);
            while (true)
            {
                var max = _maxConcurrent;
                if (now <= max || Interlocked.CompareExchange(ref _maxConcurrent, now, max) == max)
                    break;
            }
            ApiCallCount++;
            try
            {
                if (_delay > TimeSpan.Zero)
                    await Task.Delay(_delay, ct);
                return new List<double> { 0.1, 0.2, 0.3 };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
