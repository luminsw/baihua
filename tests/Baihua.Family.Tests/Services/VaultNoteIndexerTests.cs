using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Modules.Family.Services;
using Baihua.Family.Tests.Learning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// VaultNoteIndexer FTS5 索引完整性测试：
/// ① 重建事务：中途失败回滚，旧索引保持完整；正常重建完整且幂等
/// ② 增量索引：新增只入该笔记、删除只删该笔记、变更只更新该笔记、未变更不重复处理
/// 使用内存 SQLite（共享连接）+ FakeDbFactory，参考 Learning/Fam02 的写法。
/// </summary>
public class VaultNoteIndexerTests : IDisposable
{
    private readonly string _vaultDir;
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<VaultDbContext> _factory;

    private const string VaultId = "vault-indexer-test";

    public VaultNoteIndexerTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "VaultNoteIndexerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (var ctx = new VaultDbContext(options))
            ctx.Database.EnsureCreated();
        _factory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(options));
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

    private VaultNoteIndexer CreateIndexer()
        => new(_factory, NullLogger<VaultNoteIndexer>.Instance);

    private async Task<List<(string FilePath, string Title, string Content)>> ReadFtsRowsAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync();

        var rows = new List<(string, string, string)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path, title, content FROM VaultNoteFts WHERE vault_id = @vaultId ORDER BY file_path";
        var p = cmd.CreateParameter();
        p.ParameterName = "@vaultId";
        p.Value = VaultId;
        cmd.Parameters.Add(p);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static int FtsRowCount(List<(string FilePath, string Title, string Content)> rows, string filePath)
        => rows.Count(r => r.FilePath == filePath);

    // ===================== 测试①：重建事务 =====================

    [Fact]
    public async Task Rebuild_MidwayReadFailure_RollsBack_KeepsOldIndexIntact()
    {
        WriteNote("a.md", "alpha old content");
        WriteNote("b.md", "beta old content");
        await CreateIndexer().IndexVaultAsync(VaultId, _vaultDir);

        // 变更 b、新增 c；c 的读取被注入异常 → 重建中途失败
        WriteNote("b.md", "beta NEW content");
        WriteNote("c.md", "gamma content");

        var failing = new FailingVaultNoteIndexer(_factory, NullLogger<VaultNoteIndexer>.Instance, "c.md");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.IndexVaultAsync(VaultId, _vaultDir));
        Assert.Equal("injected read failure", ex.Message);

        // 回滚后：仍是旧的两行，且 b 的新内容未生效、c 不存在
        var rows = await ReadFtsRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.FilePath == "a.md" && r.Content == "alpha old content");
        Assert.Contains(rows, r => r.FilePath == "b.md" && r.Content == "beta old content");
        Assert.DoesNotContain(rows, r => r.FilePath == "c.md");
    }

    [Fact]
    public async Task Rebuild_CompleteAndIdempotent_SkipsReadme()
    {
        WriteNote("a.md", "alpha");
        WriteNote("sub/b.md", "beta nested");
        WriteNote("README.md", "should not be indexed");

        var indexer = CreateIndexer();
        await indexer.IndexVaultAsync(VaultId, _vaultDir);

        var rows = await ReadFtsRowsAsync();
        var nestedPath = Path.Combine("sub", "b.md");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.FilePath == "a.md" && r.Title == "a");
        Assert.Contains(rows, r => r.FilePath == nestedPath && r.Title == "b");

        // 幂等：重复重建不产生重复行，内容仍一致
        await indexer.IndexVaultAsync(VaultId, _vaultDir);
        var again = await ReadFtsRowsAsync();
        Assert.Equal(2, again.Count);
        Assert.Equal(1, FtsRowCount(again, "a.md"));
        Assert.Equal(1, FtsRowCount(again, nestedPath));
    }

    // ===================== 测试②：增量索引 =====================

    [Fact]
    public async Task Incremental_NewNote_IndexesOnlyThatNote()
    {
        WriteNote("a.md", "alpha");
        WriteNote("b.md", "beta");
        var indexer = CreateIndexer();
        var first = await indexer.IndexVaultChangesAsync(VaultId, _vaultDir, null);
        Assert.True(first.IsFullRebuild);
        Assert.Equal(2, first.Added);

        WriteNote("c.md", "gamma");

        var counting = new CountingVaultNoteIndexer(_factory, NullLogger<VaultNoteIndexer>.Instance);
        var result = await counting.IndexVaultChangesAsync(VaultId, _vaultDir, first.Snapshot);

        Assert.False(result.IsFullRebuild);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, result.Unchanged);
        Assert.True(result.Changed);
        Assert.Equal(1, counting.ReadCount); // 只重读新增笔记

        var rows = await ReadFtsRowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.FilePath == "c.md" && r.Content == "gamma");
    }

    [Fact]
    public async Task Incremental_DeletedNote_RemovesOnlyItsRow()
    {
        WriteNote("a.md", "alpha");
        WriteNote("b.md", "beta");
        var indexer = CreateIndexer();
        var first = await indexer.IndexVaultChangesAsync(VaultId, _vaultDir, null);

        File.Delete(Path.Combine(_vaultDir, "a.md"));

        var counting = new CountingVaultNoteIndexer(_factory, NullLogger<VaultNoteIndexer>.Instance);
        var result = await counting.IndexVaultChangesAsync(VaultId, _vaultDir, first.Snapshot);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, counting.ReadCount); // 删除无需读内容

        var rows = await ReadFtsRowsAsync();
        var row = Assert.Single(rows);
        Assert.Equal("b.md", row.FilePath);
    }

    [Fact]
    public async Task Incremental_ChangedNote_UpdatesOnlyThatNote()
    {
        WriteNote("a.md", "alpha");
        WriteNote("b.md", "beta old");
        var indexer = CreateIndexer();
        var first = await indexer.IndexVaultChangesAsync(VaultId, _vaultDir, null);

        WriteNote("b.md", "beta NEW");
        File.SetLastWriteTimeUtc(Path.Combine(_vaultDir, "b.md"), DateTime.UtcNow.AddSeconds(2));

        var counting = new CountingVaultNoteIndexer(_factory, NullLogger<VaultNoteIndexer>.Instance);
        var result = await counting.IndexVaultChangesAsync(VaultId, _vaultDir, first.Snapshot);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, counting.ReadCount); // 只重读变更笔记

        var rows = await ReadFtsRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.FilePath == "b.md" && r.Content == "beta NEW");
        Assert.Contains(rows, r => r.FilePath == "a.md" && r.Content == "alpha");
    }

    [Fact]
    public async Task Incremental_NoChanges_DoesNothing()
    {
        WriteNote("a.md", "alpha");
        var indexer = CreateIndexer();
        var first = await indexer.IndexVaultChangesAsync(VaultId, _vaultDir, null);

        var counting = new CountingVaultNoteIndexer(_factory, NullLogger<VaultNoteIndexer>.Instance);
        var result = await counting.IndexVaultChangesAsync(VaultId, _vaultDir, first.Snapshot);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Added + result.Updated + result.Removed);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(0, counting.ReadCount);

        var rows = await ReadFtsRowsAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task Incremental_NullSnapshot_FallsBackToFullRebuild()
    {
        WriteNote("a.md", "alpha old");
        var indexer = CreateIndexer();
        var first = await indexer.IndexVaultChangesAsync(VaultId, _vaultDir, null);
        Assert.True(first.IsFullRebuild);
        Assert.Equal(1, first.Added);

        // 快照丢失（传入 null）→ 整库重建，所有内容为最新
        WriteNote("a.md", "alpha new");
        WriteNote("b.md", "beta");
        var result = await indexer.IndexVaultChangesAsync(VaultId, _vaultDir, null);
        Assert.True(result.IsFullRebuild);
        Assert.Equal(2, result.Added);

        var rows = await ReadFtsRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.FilePath == "a.md" && r.Content == "alpha new");
        Assert.Contains(rows, r => r.FilePath == "b.md" && r.Content == "beta");
    }

    // ===================== 测试注入子类 =====================

    /// <summary>读取指定文件时注入异常（模拟重建中途失败）</summary>
    private sealed class FailingVaultNoteIndexer : VaultNoteIndexer
    {
        private readonly string _failFileName;
        public FailingVaultNoteIndexer(
            IDbContextFactory<VaultDbContext> factory,
            ILogger<VaultNoteIndexer> logger,
            string failFileName) : base(factory, logger)
            => _failFileName = failFileName;

        protected override Task<string> ReadNoteContentAsync(string filePath, CancellationToken ct)
        {
            if (Path.GetFileName(filePath).Equals(_failFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("injected read failure");
            return base.ReadNoteContentAsync(filePath, ct);
        }
    }

    /// <summary>统计内容读取次数（验证未变更笔记不重复处理）</summary>
    private sealed class CountingVaultNoteIndexer : VaultNoteIndexer
    {
        public int ReadCount { get; private set; }
        public CountingVaultNoteIndexer(
            IDbContextFactory<VaultDbContext> factory,
            ILogger<VaultNoteIndexer> logger) : base(factory, logger)
        { }

        protected override Task<string> ReadNoteContentAsync(string filePath, CancellationToken ct)
        {
            ReadCount++;
            return base.ReadNoteContentAsync(filePath, ct);
        }
    }
}
