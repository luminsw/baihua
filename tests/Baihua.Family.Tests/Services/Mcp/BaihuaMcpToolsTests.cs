using System.Text.Json;
using Baihua.Core;
using Baihua.Core.Modules;
using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Family.Tests.Learning;
using Baihua.Family.Tests.TestDoubles;
using Baihua.Modules.Family.Services;
using Baihua.Modules.Family.Services.Mcp;
using Baihua.Modules.Vault.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services.Mcp;

/// <summary>
/// 百花 MCP 工具测试（挂在 /mcp 端点、供 DSH 等客户端调用的对外边界）：
/// ① BaihuaVaultTools：按 vaultId 检索 / 未指定时跨库合并按得分排序 / 读笔记失败降级为 ok=false
/// ② 写笔记的目录遍历防护必须以 ok=false 返回（MCP 工具不抛异常给调用方）
/// ③ VaultCreate 重名报错 → ok=false；VaultList 列出新建库
/// ④ BaihuaFamilyTools.TasksList：状态过滤 + limit 夹取（1..200）
/// ⑤ BaihuaLocalModelTools：注册幂等（tool+model_id 去重更新）、注销
/// </summary>
public class BaihuaMcpToolsTests : IDisposable
{
    private readonly string _vaultDirA;
    private readonly string _vaultDirB;
    private readonly SqliteConnection _vaultConn;
    private readonly SqliteConnection _familyConn;
    private readonly FakeDbFactory<VaultDbContext> _vaultFactory;
    private readonly FakeDbFactory<FamilyDbContext> _familyFactory;

    private const string VaultIdA = "mcp-vault-a";
    private const string VaultIdB = "mcp-vault-b";

    public BaihuaMcpToolsTests()
    {
        _vaultDirA = Path.Combine(Path.GetTempPath(), "McpToolsA_" + Guid.NewGuid().ToString("N"));
        _vaultDirB = Path.Combine(Path.GetTempPath(), "McpToolsB_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDirA);
        Directory.CreateDirectory(_vaultDirB);

        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        var vaultOptions = new DbContextOptionsBuilder<VaultDbContext>().UseSqlite(_vaultConn).Options;
        using (var ctx = new VaultDbContext(vaultOptions)) ctx.Database.EnsureCreated();
        _vaultFactory = new FakeDbFactory<VaultDbContext>(() => new VaultDbContext(vaultOptions));

        _familyConn = new SqliteConnection("DataSource=:memory:");
        _familyConn.Open();
        var familyOptions = new DbContextOptionsBuilder<FamilyDbContext>().UseSqlite(_familyConn).Options;
        using (var ctx = new FamilyDbContext(familyOptions)) ctx.Database.EnsureCreated();
        _familyFactory = new FakeDbFactory<FamilyDbContext>(() => new FamilyDbContext(familyOptions));
    }

    public void Dispose()
    {
        _vaultConn.Dispose();
        _familyConn.Dispose();
        try { Directory.Delete(_vaultDirA, recursive: true); } catch { }
        try { Directory.Delete(_vaultDirB, recursive: true); } catch { }
    }

    // ===================== Helpers =====================

    private async Task<VaultSettingsService> SeedVaultsAsync()
    {
        await using var ctx = await _vaultFactory.CreateDbContextAsync();
        ctx.Vaults.AddRange(
            new Vault { VaultId = VaultIdA, Name = "中医库", Path = _vaultDirA, CreatedAt = DateTime.UtcNow.AddMinutes(-1), UpdatedAt = DateTime.UtcNow },
            new Vault { VaultId = VaultIdB, Name = "通用库", Path = _vaultDirB, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        return new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance);
    }

    /// <summary>语义搜索未配置的 EmbeddingService（SearchAsync 走 FTS/文件扫描）</summary>
    private EmbeddingService CreateEmbeddingService(VaultSettingsService vaultSettings)
    {
        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var aiSettings = new AiSettingsService(configuration, StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance);
        var provider = new Mock<Baihua.Core.Modules.IEmbeddingConfigProvider>();
        return new EmbeddingService(
            aiClient.Object, aiSettings, vaultSettings, _vaultFactory,
            provider.Object, NullLogger<EmbeddingService>.Instance);
    }

    private IVaultQueryService CreateVaultQuery(VaultSettingsService vaultSettings)
        => new VaultQueryService(
            vaultSettings,
            CreateEmbeddingService(vaultSettings),
            new VaultNoteIndexer(_vaultFactory, NullLogger<VaultNoteIndexer>.Instance),
            NullLogger<VaultQueryService>.Instance,
            TestLocalizer.Instance);

    private static (JsonElement Root, string Raw) ParseJson(string raw)
        => (JsonDocument.Parse(raw).RootElement.Clone(), raw);

    // ===================== ① BaihuaVaultTools =====================

    [Fact]
    public async Task VaultSearch_SingleVault_ReturnsResults()
    {
        var vaultSettings = await SeedVaultsAsync();
        Directory.CreateDirectory(Path.Combine(_vaultDirA, "notes"));
        File.WriteAllText(Path.Combine(_vaultDirA, "notes", "桂枝汤.md"), "桂枝汤是伤寒论经典方剂。");
        var tools = new BaihuaVaultTools(vaultSettings, CreateVaultQuery(vaultSettings));

        var (json, _) = ParseJson(await tools.VaultSearch("桂枝汤", VaultIdA));

        Assert.True(json.TryGetProperty("results", out var results));
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("桂枝汤", results[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task VaultSearch_NoVaultId_MergesAllVaults_OrderByScore()
    {
        var vaultSettings = await SeedVaultsAsync();
        Directory.CreateDirectory(Path.Combine(_vaultDirA, "notes"));
        Directory.CreateDirectory(Path.Combine(_vaultDirB, "notes"));
        File.WriteAllText(Path.Combine(_vaultDirA, "notes", "桂枝汤.md"), "桂枝汤：伤寒论方剂。"); // 标题命中 → 10
        File.WriteAllText(Path.Combine(_vaultDirB, "notes", "读书摘记.md"), "今天读到了桂枝汤的段落。"); // 内容命中 → 5
        var tools = new BaihuaVaultTools(vaultSettings, CreateVaultQuery(vaultSettings));

        var (json, _) = ParseJson(await tools.VaultSearch("桂枝汤"));

        Assert.True(json.TryGetProperty("results", out var results));
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("桂枝汤", results[0].GetProperty("title").GetString()); // 得分高的在前
        Assert.Equal("读书摘记", results[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task VaultReadNote_MissingNote_ReturnsOkFalse()
    {
        var vaultSettings = await SeedVaultsAsync();
        var tools = new BaihuaVaultTools(vaultSettings, CreateVaultQuery(vaultSettings));

        var (json, _) = ParseJson(await tools.VaultReadNote("ghost.md", VaultIdA));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task VaultWriteNote_PathTraversal_ReturnsOkFalse()
    {
        var vaultSettings = await SeedVaultsAsync();
        var tools = new BaihuaVaultTools(vaultSettings, CreateVaultQuery(vaultSettings));

        var (json, _) = ParseJson(await tools.VaultWriteNote("../escape.md", VaultIdA, "恶意内容"));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_vaultDirA)!, "escape.md")),
            "目录遍历必须被拦截，不得写出知识库目录");
    }

    [Fact]
    public async Task VaultWriteNote_HappyPath_WritesFile()
    {
        var vaultSettings = await SeedVaultsAsync();
        var tools = new BaihuaVaultTools(vaultSettings, CreateVaultQuery(vaultSettings));

        var (json, _) = ParseJson(await tools.VaultWriteNote("学习计划/入门.md", VaultIdA, "# 入门"));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_vaultDirA, "notes", "学习计划", "入门.md")));
    }

    [Fact]
    public void VaultCreate_DuplicateName_ReturnsOkFalse()
    {
        var vaultSettings = new VaultSettingsService(_vaultFactory, NullLogger<VaultSettingsService>.Instance);
        var tools = new BaihuaVaultTools(vaultSettings, null!);

        var (first, _) = ParseJson(tools.VaultCreate("新知识库", industry: "国学", path: _vaultDirA));
        Assert.True(first.GetProperty("ok").GetBoolean());

        var (second, _) = ParseJson(tools.VaultCreate("新知识库", industry: "国学", path: _vaultDirB));
        Assert.False(second.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(second.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task VaultList_ContainsSeededVaults()
    {
        var vaultSettings = await SeedVaultsAsync();
        var tools = new BaihuaVaultTools(vaultSettings, null!);

        var (json, _) = ParseJson(tools.VaultList());

        Assert.True(json.TryGetProperty("vaults", out var vaults));
        Assert.Equal(2, vaults.GetArrayLength());
        var names = vaults.EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToHashSet();
        Assert.Equal(new HashSet<string> { "中医库", "通用库" }, names);
    }

    // ===================== ④ BaihuaFamilyTools =====================

    [Fact]
    public async Task TasksList_FiltersByStatus_AndClampsLimit()
    {
        var taskManager = new TaskManager(_familyFactory);
        var t1 = taskManager.CreateTask("atom_note_split");
        var t2 = taskManager.CreateTask("anki_card_generate");
        await taskManager.UpdateStatus(t2, RunnerTaskStatus.Success);
        var tools = new BaihuaFamilyTools(new FamilyBudgetService(NullLogger<FamilyBudgetService>.Instance), taskManager);

        // 状态过滤
        var (done, _) = ParseJson(tools.TasksList(status: "success"));
        Assert.Equal(1, done.GetProperty("total").GetInt32());
        Assert.Equal(t2, done.GetProperty("tasks")[0].GetProperty("id").GetString());

        // limit 夹取：请求 1 条只回 1 条；超过 200 也安全（全部任务只有 2 条）
        var (limited, _) = ParseJson(tools.TasksList(limit: 1));
        Assert.Equal(1, limited.GetProperty("total").GetInt32());
        var (all, _) = ParseJson(tools.TasksList(limit: 500));
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        // t1 仍是 Pending
        var (pending, _) = ParseJson(tools.TasksList(status: "pending"));
        Assert.Equal(t1, pending.GetProperty("tasks")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task BudgetSummary_ReturnsValidJson()
    {
        // 只读冒烟：工具必须返回可解析 JSON（读不到数据文件时为空汇总，不得抛异常）
        var tools = new BaihuaFamilyTools(new FamilyBudgetService(NullLogger<FamilyBudgetService>.Instance), new TaskManager(_familyFactory));
        var (_, raw) = ParseJson(await tools.BudgetSummary());
        Assert.StartsWith("{", raw.Trim());
    }

    // ===================== ⑤ BaihuaLocalModelTools =====================

    [Fact]
    public async Task LocalModel_RegisterIsIdempotent_ListAndUnregister()
    {
        var registry = new LocalModelRegistryService(_familyFactory, NullLogger<LocalModelRegistryService>.Instance);
        var tools = new BaihuaLocalModelTools(registry);

        var (first, _) = ParseJson(await tools.Register("oms", "qwen2.5-14b", "千问14B", "http://localhost:8000", registered_by: "dsh"));
        Assert.True(first.GetProperty("ok").GetBoolean());

        // 同 tool+model_id 再注册 → 更新而非新增
        var (second, _) = ParseJson(await tools.Register("oms", "qwen2.5-14b", "千问14B-Instruct", "http://localhost:8001"));
        Assert.True(second.GetProperty("ok").GetBoolean());

        var (list, _) = ParseJson(await tools.List("oms"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal("千问14B-Instruct", list.GetProperty("models")[0].GetProperty("displayName").GetString());

        var (unreg, _) = ParseJson(await tools.Unregister("oms", "qwen2.5-14b"));
        Assert.True(unreg.GetProperty("removed").GetBoolean());

        var (after, _) = ParseJson(await tools.List());
        Assert.Equal(0, after.GetProperty("total").GetInt32());
    }
}
