using Baihua.Core.Modules;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Family.Tests.Learning;
using Baihua.Family.Tests.TestDoubles;
using Baihua.Modules.Vault.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// VaultQueryService 测试（控制器与 MCP 工具共用的唯一查询实现）：
/// ① ResolveVaultPath：空 id / 未知 id 返回 null，存在则返回磁盘路径
/// ② SearchAsync 三级回退：无索引时 file-scan 命中 / 有 FTS 索引时 fts5 / 前置校验错误状态
/// ③ ReadNoteAsync：路径规范化（去 .md）、frontmatter 标签解析、NotFound
/// ④ WriteNoteAsync：目录遍历防护（.. 与 GetFullPath 前缀检查）、自动建目录、路径/后缀规范化
/// </summary>
public class VaultQueryServiceTests : IDisposable
{
    private readonly string _vaultDir;
    private readonly SqliteConnection _connection;
    private readonly FakeDbFactory<VaultDbContext> _vaultFactory;

    private const string VaultId = "vault-query-test";

    public VaultQueryServiceTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "VaultQueryServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VaultDbContext>().UseSqlite(_connection).Options;
        using (var ctx = new VaultDbContext(options)) ctx.Database.EnsureCreated();
        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(options));
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_vaultDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    // ===================== Helpers =====================

    private VaultSettingsService CreateVaultSettings()
        => new(_vaultFactory, NullLogger<VaultSettingsService>.Instance);

    /// <summary>语义搜索未配置的 EmbeddingService（IsSemanticSearchEnabled=false → SearchAsync 走 FTS/文件扫描）</summary>
    private EmbeddingService CreateEmbeddingService()
    {
        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var aiSettings = new AiSettingsService(configuration, StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance);
        var provider = new Mock<Baihua.Core.Modules.IEmbeddingConfigProvider>();
        return new EmbeddingService(
            aiClient.Object, aiSettings, CreateVaultSettings(), _vaultFactory,
            provider.Object, NullLogger<EmbeddingService>.Instance);
    }

    private VaultQueryService CreateService()
        => new(
            CreateVaultSettings(),
            CreateEmbeddingService(),
            new VaultNoteIndexer(_vaultFactory, NullLogger<VaultNoteIndexer>.Instance),
            NullLogger<VaultQueryService>.Instance,
            TestLocalizer.Instance);

    private async Task SeedVaultAsync(string path)
    {
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        ctx.Vaults.Add(new Vault { VaultId = VaultId, Name = "测试库", Path = path, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private string WriteNoteFile(string relativePath, string content)
    {
        var full = Path.Combine(_vaultDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    // ===================== ① ResolveVaultPath =====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveVaultPath_EmptyId_ReturnsNull(string? vaultId)
        => Assert.Null(CreateService().ResolveVaultPath(vaultId));

    [Fact]
    public void ResolveVaultPath_UnknownId_ReturnsNull()
        => Assert.Null(CreateService().ResolveVaultPath("no-such-vault"));

    [Fact]
    public async Task ResolveVaultPath_KnownId_ReturnsPath()
    {
        await SeedVaultAsync(_vaultDir);
        Assert.Equal(_vaultDir, CreateService().ResolveVaultPath(VaultId));
    }

    // ===================== ② SearchAsync =====================

    [Fact]
    public async Task Search_EmptyVaultId_ReturnsNotConfigured()
    {
        var outcome = await CreateService().SearchAsync("关键词", "");
        Assert.Empty(outcome.Results);
        Assert.False(outcome.Status.VaultConfigured);
        Assert.False(string.IsNullOrEmpty(outcome.Status.ErrorMessage));
    }

    [Fact]
    public async Task Search_UnknownVault_ReturnsNotConfigured()
    {
        var outcome = await CreateService().SearchAsync("关键词", "no-such-vault");
        Assert.Empty(outcome.Results);
        Assert.False(outcome.Status.VaultConfigured);
    }

    [Fact]
    public async Task Search_VaultPathMissing_ReturnsVaultNotExists()
    {
        await SeedVaultAsync(Path.Combine(_vaultDir, "deleted-dir"));
        var outcome = await CreateService().SearchAsync("关键词", VaultId);
        Assert.Empty(outcome.Results);
        Assert.True(outcome.Status.VaultConfigured);
        Assert.False(outcome.Status.VaultExists);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmptyWithoutError()
    {
        await SeedVaultAsync(_vaultDir);
        var outcome = await CreateService().SearchAsync("  ", VaultId);
        Assert.Empty(outcome.Results);
        Assert.True(outcome.Status.VaultConfigured);
        Assert.True(outcome.Status.VaultExists);
        Assert.Null(outcome.Status.ErrorMessage);
    }

    [Fact]
    public async Task Search_NoIndex_FallsBackToFileScan()
    {
        await SeedVaultAsync(_vaultDir);
        WriteNoteFile(Path.Combine("notes", "桂枝汤.md"), "桂枝汤是伤寒论经典方剂，主治风寒表证。");
        WriteNoteFile(Path.Combine("notes", "README.md"), "方剂 索引文件，不应出现在结果里");

        // 查询词命中标题（标题命中得高分），README.md 被排除
        var outcome = await CreateService().SearchAsync("桂枝汤", VaultId);

        Assert.True(outcome.Status.VaultConfigured);
        Assert.True(outcome.Status.VaultExists);
        Assert.Equal("file-scan", outcome.Status.SearchMethod);
        var result = Assert.Single(outcome.Results);
        Assert.Equal("桂枝汤", result.Title);
        Assert.Equal(10, result.Score); // 标题命中得高分
        Assert.Contains("桂枝汤", result.Preview);
    }

    [Fact]
    public async Task Search_WithFtsIndex_UsesFts5()
    {
        await SeedVaultAsync(_vaultDir);
        WriteNoteFile(Path.Combine("notes", "合谷.md"), "穴位：合谷是止痛要穴。");

        // 先建 FTS 表并插入索引行
        var indexer = new VaultNoteIndexer(_vaultFactory, NullLogger<VaultNoteIndexer>.Instance);
        await indexer.EnsureFtsTableAsync();
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = (SqliteCommand)conn.CreateCommand();
        cmd.CommandText = "INSERT INTO VaultNoteFts (title, content, vault_id, file_path) VALUES ($t, $c, $v, $f)";
        cmd.Parameters.Add(new SqliteParameter("$t", "合谷"));
        cmd.Parameters.Add(new SqliteParameter("$c", "穴位：合谷是止痛要穴。"));
        cmd.Parameters.Add(new SqliteParameter("$v", VaultId));
        cmd.Parameters.Add(new SqliteParameter("$f", "notes/合谷.md"));
        await cmd.ExecuteNonQueryAsync();

        var outcome = await CreateService().SearchAsync("穴位", VaultId);

        Assert.Equal("fts5", outcome.Status.SearchMethod);
        var result = Assert.Single(outcome.Results);
        Assert.Equal("合谷", result.Title);
    }

    // ===================== ③ ReadNoteAsync =====================

    [Fact]
    public async Task ReadNote_EmptyPath_ThrowsPathRequired()
        => await Assert.ThrowsAsync<VaultQueryException>(() => CreateService().ReadNoteAsync("", VaultId));

    [Fact]
    public async Task ReadNote_UnknownVault_ThrowsVaultRequired()
        => await Assert.ThrowsAsync<VaultQueryException>(() => CreateService().ReadNoteAsync("a.md", "no-such"));

    [Fact]
    public async Task ReadNote_MissingNote_ThrowsNoteNotFound()
    {
        await SeedVaultAsync(_vaultDir);
        var ex = await Assert.ThrowsAsync<VaultQueryException>(() => CreateService().ReadNoteAsync("ghost", VaultId));
        Assert.Equal(VaultErrorCode.NoteNotFound, ex.Code);
    }

    [Fact]
    public async Task ReadNote_StripsMdSuffix_AndParsesFrontmatterTags()
    {
        await SeedVaultAsync(_vaultDir);
        WriteNoteFile(Path.Combine("notes", "子目录", "笔记.md"),
            "---\ntags: [中医, 穴位]\n---\n# 笔记\n正文内容");

        // 带 .md 后缀 + 带子目录：应规范化后命中同一文件
        var note = await CreateService().ReadNoteAsync("子目录/笔记.md", VaultId);

        Assert.Equal("子目录/笔记", note.Path);
        Assert.Equal("笔记", note.Title);
        Assert.Contains("正文内容", note.Content);
        Assert.NotNull(note.Tags);
        Assert.Equal(["中医", "穴位"], note.Tags);
        Assert.False(note.AiGenerated);
    }

    // ===================== ④ WriteNoteAsync =====================

    [Fact]
    public async Task WriteNote_EmptyPath_ThrowsPathRequired()
        => await Assert.ThrowsAsync<VaultQueryException>(() => CreateService().WriteNoteAsync("", VaultId, "内容"));

    [Fact]
    public async Task WriteNote_NullContent_ThrowsContentRequired()
        => await Assert.ThrowsAsync<VaultQueryException>(() => CreateService().WriteNoteAsync("a.md", VaultId, null!));

    [Fact]
    public async Task WriteNote_UnknownVault_ThrowsVaultRequired()
        => await Assert.ThrowsAsync<VaultQueryException>(() => CreateService().WriteNoteAsync("a.md", "no-such", "内容"));

    [Fact]
    public async Task WriteNote_DotDotTraversal_ThrowsIllegalPath()
    {
        await SeedVaultAsync(_vaultDir);
        var ex = await Assert.ThrowsAsync<VaultQueryException>(
            () => CreateService().WriteNoteAsync("../escape.md", VaultId, "内容"));
        Assert.Equal(VaultErrorCode.IllegalPath, ex.Code);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_vaultDir)!, "escape.md")));
    }

    [Fact]
    public async Task WriteNote_CreatesSubdirectories_AndWritesContent()
    {
        await SeedVaultAsync(_vaultDir);
        var svc = CreateService();

        await svc.WriteNoteAsync("子目录/新笔记.md", VaultId, "# 新笔记\n内容");

        var written = Path.Combine(_vaultDir, "notes", "子目录", "新笔记.md");
        Assert.True(File.Exists(written));
        Assert.Contains("内容", File.ReadAllText(written));

        // 幂等覆盖：再写一次不报错
        await svc.WriteNoteAsync("子目录/新笔记", VaultId, "覆盖后内容");
        Assert.Equal("覆盖后内容", File.ReadAllText(written));
    }
}
