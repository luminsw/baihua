using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Family.Tests.Learning;
using Baihua.Family.Tests.TestDoubles;
using Baihua.Modules.Family.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// RestoreService 全量备份恢复测试：
/// ① 前置校验（文件不存在 / 缺 manifest / 版本过低 / 需要密码）直接失败不落库
/// ② 恢复路径重映射：vaults.json 的 RelativePath + vaultRootPathOverride 拼出最终 Path
/// ③ Tasks/Devices 导入：幂等去重、恢复后的设备强制 PendingReauth（令牌失效）
/// ④ 知识库文件落盘：有 DB 记录才复制 notes/cards/images，无记录跳过
/// zip 由测试手工构造（与 BackupService 导出格式一致：PascalCase + 默认序列化选项）。
/// </summary>
public class RestoreServiceTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _vaultRoot;      // vaultRootPathOverride：恢复目标
    private readonly SqliteConnection _vaultConn;
    private readonly SqliteConnection _familyConn;
    private readonly IDbContextFactory<VaultDbContext> _vaultFactory;
    private readonly IDbContextFactory<FamilyDbContext> _familyFactory;

    public RestoreServiceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "RestoreServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _vaultRoot = Path.Combine(_rootDir, "vault-root");
        Directory.CreateDirectory(_vaultRoot);

        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        _familyConn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        _familyConn.Open();

        var vaultOptions = new DbContextOptionsBuilder<VaultDbContext>().UseSqlite(_vaultConn).Options;
        var familyOptions = new DbContextOptionsBuilder<FamilyDbContext>().UseSqlite(_familyConn).Options;
        using (var ctx = new VaultDbContext(vaultOptions)) ctx.Database.EnsureCreated();
        using (var ctx = new FamilyDbContext(familyOptions)) ctx.Database.EnsureCreated();

        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(vaultOptions));
        _familyFactory = new FakeDbFactory<FamilyDbContext>(() => new FamilyDbContext(familyOptions));
    }

    public void Dispose()
    {
        _vaultConn.Dispose();
        _familyConn.Dispose();
        try { Directory.Delete(_rootDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    // ===================== Helpers =====================

    private RestoreService CreateService()
        => new(
            new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance),
            _vaultFactory,
            _familyFactory,
            StubAiConfigService.Empty,
            NullLogger<RestoreService>.Instance,
            TestLocalizer.Instance);

    private string WriteBackupZip(string fileName, Action<string> buildContent)
    {
        var zipPath = Path.Combine(_rootDir, fileName);
        var extractDir = zipPath + ".src";
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(Path.Combine(extractDir, "db"));
        Directory.CreateDirectory(Path.Combine(extractDir, "vaults"));
        buildContent(extractDir);
        ZipFile.CreateFromDirectory(extractDir, zipPath);
        return zipPath;
    }

    private static string Manifest(int version = 2, bool hasPassword = false, string platform = "Windows")
        => $$"""
            {"Version": {{version}}, "CreatedAt": "2026-11-27T00:00:00Z", "SourcePlatform": "{{platform}}",
             "SourceOS": "Windows", "SourceMachineName": "test", "HasPassword": {{(hasPassword ? "true" : "false").ToLower()}}, "AppVersion": "0.0.0"}
            """;

    /// <summary>vaults.json 使用 PascalCase key（RestoreService 用默认序列化选项按属性名读取）</summary>
    private static string VaultJson(string vaultId, string name, string relativePath, bool isActive = true)
        => $$"""
            [{"Id": 1, "VaultId": "{{vaultId}}", "Name": "{{name}}", "Path": "/old/machine/path/{{name}}",
              "RelativePath": "{{relativePath}}", "IsActive": {{(isActive ? "true" : "false").ToLower()}},
              "CreatedAt": "2026-01-01T00:00:00Z", "UpdatedAt": "2026-01-01T00:00:00Z"}]
            """;

    private async Task<Vault> GetVaultAsync(string vaultId)
    {
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        return await ctx.Vaults.AsNoTracking().SingleAsync(v => v.VaultId == vaultId);
    }

    // ===================== 前置校验 =====================

    [Fact]
    public async Task MissingBackupFile_Fails()
    {
        var result = await CreateService().RestoreFullBackupAsync(
            Path.Combine(_rootDir, "no-such.zip"));
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task MissingManifest_Fails()
    {
        var zip = WriteBackupZip("no-manifest.zip", dir => File.WriteAllText(Path.Combine(dir, "db", "x.txt"), "x"));
        var result = await CreateService().RestoreFullBackupAsync(zip);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task UnsupportedVersion_Fails()
    {
        var zip = WriteBackupZip("v1.zip", dir => File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest(version: 1)));
        var result = await CreateService().RestoreFullBackupAsync(zip);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task PasswordRequired_WhenManifestHasPassword()
    {
        var zip = WriteBackupZip("pw.zip", dir => File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest(hasPassword: true)));
        var result = await CreateService().RestoreFullBackupAsync(zip);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CancelledToken_ReturnsCancelled()
    {
        var zip = WriteBackupZip("cancel.zip", dir => File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest()));
        var result = await CreateService().RestoreFullBackupAsync(
            zip, cancellationToken: new CancellationToken(canceled: true));
        Assert.False(result.Success);
    }

    // ===================== 主流程：DB + 文件 =====================

    [Fact]
    public async Task RestoresVault_Task_Device_AndFiles()
    {
        var vaultName = "学习笔记";
        var relativePath = "学习笔记";
        var noteContent = "# 笔记\n内容";
        var zip = WriteBackupZip("full.zip", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest());
            var dbDir = Path.Combine(dir, "db");
            File.WriteAllText(Path.Combine(dbDir, "vaults.json"), VaultJson("vault-1", vaultName, relativePath));
            File.WriteAllText(Path.Combine(dbDir, "tasks.json"),
                """[{"TaskId": "t-1", "TaskType": "AnkiCard", "Status": "Completed", "Progress": 100, "CreatedAt": "2026-01-01T00:00:00Z", "UpdatedAt": "2026-01-01T00:00:00Z"}]""");
            File.WriteAllText(Path.Combine(dbDir, "devices.json"),
                """[{"DeviceId": "dev-1", "DeviceName": "手机", "AccessToken": "secret-token", "Status": "Authorized", "AuthorizedTime": "2026-01-01T00:00:00Z", "CreatedAt": "2026-01-01T00:00:00Z", "UpdatedAt": "2026-01-01T00:00:00Z"}]""");
            var notes = Path.Combine(dir, "vaults", vaultName, "notes");
            Directory.CreateDirectory(notes);
            File.WriteAllText(Path.Combine(notes, "note1.md"), noteContent, new UTF8Encoding(false));
        });

        var result = await CreateService().RestoreFullBackupAsync(zip, vaultRootPathOverride: _vaultRoot);
        Assert.True(result.Success, result.Error);
        Assert.Equal("Windows", result.SourcePlatform);

        // Vault：路径按 vaultRootPathOverride + RelativePath 重映射
        var vault = await GetVaultAsync("vault-1");
        Assert.Equal(Path.Combine(_vaultRoot, relativePath), vault.Path);
        Assert.True(vault.IsActive);

        // Task 幂等导入
        await using (var familyCtx = await _familyFactory.CreateDbContextAsync())
        {
            var task = await familyCtx.Tasks.AsNoTracking().SingleAsync(t => t.TaskId == "t-1");
            Assert.Equal("AnkiCard", task.TaskType);
        }

        // 设备恢复后强制 PendingReauth（旧 AccessToken 不再有效）
        await using (var familyCtx = await _familyFactory.CreateDbContextAsync())
        {
            var device = await familyCtx.AuthorizedDevices.AsNoTracking().SingleAsync(d => d.DeviceId == "dev-1");
            Assert.Equal("PendingReauth", device.Status);
        }

        // 知识库文件复制到重映射后的路径
        var restoredNote = Path.Combine(vault.Path, "notes", "note1.md");
        Assert.True(File.Exists(restoredNote), $"笔记文件应恢复到 {restoredNote}");
        Assert.Equal(noteContent, File.ReadAllText(restoredNote));
    }

    [Fact]
    public async Task DuplicateTask_WithoutOverwrite_NotImportedTwice()
    {
        await using (var seed = await _familyFactory.CreateDbContextAsync())
        {
            seed.Tasks.Add(new TaskEntity { TaskId = "t-dup", TaskType = "Embedding", Status = "Completed", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var zip = WriteBackupZip("dup.zip", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest());
            var dbDir = Path.Combine(dir, "db");
            File.WriteAllText(Path.Combine(dbDir, "vaults.json"), VaultJson("vault-dup", "v", "v"));
            File.WriteAllText(Path.Combine(dbDir, "tasks.json"),
                """[{"TaskId": "t-dup", "TaskType": "AnkiCard", "Status": "Completed", "CreatedAt": "2026-01-01T00:00:00Z", "UpdatedAt": "2026-01-01T00:00:00Z"}]""");
        });

        var result = await CreateService().RestoreFullBackupAsync(zip, vaultRootPathOverride: _vaultRoot, overwrite: false);
        Assert.True(result.Success, result.Error);

        await using var ctx = await _familyFactory.CreateDbContextAsync();
        var tasks = await ctx.Tasks.AsNoTracking().Where(t => t.TaskId == "t-dup").ToListAsync();
        var task = Assert.Single(tasks);
        Assert.Equal("Embedding", task.TaskType); // 保留已有记录，不被备份覆盖
    }

    [Fact]
    public async Task VaultDirWithoutDbRecord_Skipped()
    {
        var zip = WriteBackupZip("orphan.zip", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest());
            // vaults.json 为空，但 vaults/ 下有孤儿目录 → 不应创建任何本地目录
            File.WriteAllText(Path.Combine(dir, "db", "vaults.json"), "[]");
            var notes = Path.Combine(dir, "vaults", "孤儿库", "notes");
            Directory.CreateDirectory(notes);
            File.WriteAllText(Path.Combine(notes, "a.md"), "a");
        });

        var result = await CreateService().RestoreFullBackupAsync(zip, vaultRootPathOverride: _vaultRoot);
        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(Path.Combine(_vaultRoot, "孤儿库")),
            "无 DB 记录的孤儿知识库目录必须跳过，不得凭空创建");
    }

    [Fact]
    public async Task EmptyVaultsList_NothingRestored_NoError()
    {
        var zip = WriteBackupZip("empty.zip", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest());
            File.WriteAllText(Path.Combine(dir, "db", "vaults.json"), "[]");
        });
        var result = await CreateService().RestoreFullBackupAsync(zip, vaultRootPathOverride: _vaultRoot);
        Assert.True(result.Success, result.Error);
        Assert.Empty(await _vaultFactory.CreateDbContext().Vaults.ToListAsync());
    }
}
