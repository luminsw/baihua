using Baihua.Core.Models;
using Baihua.Core;
using Baihua.Core.Services;
using Baihua.Data;
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
/// AtomNoteSplitter 端到端编排测试（AI 调用经 FakeChatClient 注入，不触网）：
/// ① 未配置任何 AI 提供方 → 任务 Failed
/// ② 拆分主流程：AI 返回 JSON 数组 → 解析 → 规范化路径 → 落盘（frontmatter + wikilink 补全分类）→ 任务 Success
/// ③ 链式补充：多提供方时补充轮的重复 path 去重
/// ④ AI 调用失败 → 任务 Failed 且异常上抛
/// </summary>
public class AtomNoteSplitterTests : IDisposable
{
    private readonly string _vaultDir;
    private readonly SqliteConnection _familyConn;
    private readonly FakeDbFactory<FamilyDbContext> _familyFactory;

    public AtomNoteSplitterTests()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "AtomNoteSplitterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);

        _familyConn = new SqliteConnection("DataSource=:memory:");
        _familyConn.Open();
        var options = new DbContextOptionsBuilder<FamilyDbContext>().UseSqlite(_familyConn).Options;
        using (var ctx = new FamilyDbContext(options)) ctx.Database.EnsureCreated();
        _familyFactory = new FakeDbFactory<FamilyDbContext>(() => new FamilyDbContext(options));
    }

    public void Dispose()
    {
        _familyConn.Dispose();
        try { Directory.Delete(_vaultDir, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }

    // ===================== Helpers =====================

    /// <summary>按调用次序返回预设响应的假 ChatClient；注入异常类型时每次调用都抛出</summary>
    private sealed class FakeChatClient(List<string> responses, Exception? alwaysThrows = null) : IChatClient
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _callCount);
            if (alwaysThrows != null) throw alwaysThrows;
            var text = responses[Math.Min(count - 1, responses.Count - 1)];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试不覆盖流式路径");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>两个 AI 提供方（来自 appsettings 的 Ai 配置节，IAiConfigService 返回空 → 回退配置文件）</summary>
    private static AiSettingsService CreateAiSettings()
    {
        var configData = new Dictionary<string, string?>
        {
            ["Ai:0:Id"] = "provider-a",
            ["Ai:0:Name"] = "测试提供方A",
            ["Ai:0:AiBaseUrl"] = "http://localhost:19998/v1",
            ["Ai:0:IsMain"] = "true",
            ["Ai:0:Models:0:Name"] = "test-model-a",
            ["Ai:1:Id"] = "provider-b",
            ["Ai:1:Name"] = "测试提供方B",
            ["Ai:1:AiBaseUrl"] = "http://localhost:19999/v1",
            ["Ai:1:Models:0:Name"] = "test-model-b",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        return new AiSettingsService(configuration, StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance);
    }

    private (AtomNoteSplitter Splitter, FakeChatClient Chat, TaskManager TaskManager) CreateSplitter(
        List<string>? aiResponses = null, Exception? aiThrows = null)
    {
        var chat = new FakeChatClient(aiResponses ?? [], aiThrows);
        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        aiClient.Setup(c => c.CreateChatClient(It.IsAny<string>(), It.IsAny<string>())).Returns(chat);

        var aiSettings = CreateAiSettings();
        var vaultSettings = new VaultSettingsService(
            new FakeDbFactory<VaultDbContext>(() => throw new InvalidOperationException("本测试不使用 vault DB")),
            NullLogger<VaultSettingsService>.Instance);
        var taskManager = new TaskManager(_familyFactory);
        var noteParser = new NoteParser(NullLogger<NoteParser>.Instance, TestLocalizer.Instance);

        var splitter = new AtomNoteSplitter(
            aiClient.Object, taskManager, aiSettings, vaultSettings,
            new LocalAiAutoStarter(NullLogger<LocalAiAutoStarter>.Instance),
            new DefaultPromptProvider(NullLogger<DefaultPromptProvider>.Instance),
            new AnkiCardGenerator(vaultSettings, aiClient.Object, aiSettings, taskManager,
                NullLogger<AnkiCardGenerator>.Instance, TestLocalizer.Instance),
            noteParser,
            NullLogger<AtomNoteSplitter>.Instance,
            TestLocalizer.Instance);

        return (splitter, chat, taskManager);
    }

    private static string NoteJson(string path, string title, string content)
        => $$"""{"path": "{{path}}", "title": "{{title}}", "content": "{{content}}"}""";

    private async Task<RunnerTaskStatus> GetTaskStatusAsync(TaskManager taskManager, string taskId)
        => taskManager.GetTask(taskId)?.Status ?? throw new InvalidOperationException($"任务不存在: {taskId}");

    // ===================== ① 未配置提供方 =====================

    [Fact]
    public async Task NoProviders_TaskFails()
    {
        // 空 Ai 配置节 + StubAiConfigService.Empty → ResolveProviders 得到空列表
        var aiClient = new Mock<AiClientService>(null!, null!, null!, null!, null!, null!, null!, null!);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var aiSettings = new AiSettingsService(configuration, StubAiConfigService.Empty, NullLogger<AiSettingsService>.Instance);
        var vaultSettings = new VaultSettingsService(
            new FakeDbFactory<VaultDbContext>(() => throw new InvalidOperationException("不使用")),
            NullLogger<VaultSettingsService>.Instance);
        var taskManager = new TaskManager(_familyFactory);
        var splitter = new AtomNoteSplitter(
            aiClient.Object, taskManager, aiSettings, vaultSettings,
            new LocalAiAutoStarter(NullLogger<LocalAiAutoStarter>.Instance),
            new DefaultPromptProvider(NullLogger<DefaultPromptProvider>.Instance),
            new AnkiCardGenerator(vaultSettings, aiClient.Object, aiSettings, taskManager,
                NullLogger<AnkiCardGenerator>.Instance, TestLocalizer.Instance),
            new NoteParser(NullLogger<NoteParser>.Instance, TestLocalizer.Instance),
            NullLogger<AtomNoteSplitter>.Instance, TestLocalizer.Instance);

        var taskId = taskManager.CreateTask("atom_note_split");

        await Assert.ThrowsAsync<Exception>(() =>
            splitter.SplitAtomNotes(taskId, "标题", "内容", useChain: false, vaultPath: _vaultDir));

        Assert.Equal(RunnerTaskStatus.Failed, await GetTaskStatusAsync(taskManager, taskId));
    }

    // ===================== ② 拆分主流程 =====================

    [Fact]
    public async Task Split_SavesNotesWithFrontmatter_AndRewritesWikiLinks()
    {
        var aiResponse = $$"""
            ```json
            [
              {{NoteJson("方剂/桂枝汤", "桂枝汤", "# 桂枝汤\n组成见[[麻黄汤]]，发汗解表。")}},
              {{NoteJson("方剂/麻黄汤", "麻黄汤", "# 麻黄汤\n麻黄、桂枝、杏仁、甘草。")}}
            ]
            ```
            """;
        var (splitter, chat, taskManager) = CreateSplitter([aiResponse]);
        var taskId = taskManager.CreateTask("atom_note_split");

        await splitter.SplitAtomNotes(taskId, "伤寒论笔记", "长文本……", useChain: false, vaultPath: _vaultDir);

        Assert.Equal(1, chat.CallCount); // useChain=false 且 wikilink 全部可解析 → 只调一次 AI
        Assert.Equal(RunnerTaskStatus.Success, await GetTaskStatusAsync(taskManager, taskId));

        var notesDir = Path.Combine(_vaultDir, "notes");
        var guizhi = Path.Combine(notesDir, "方剂", "桂枝汤.md");
        var mahuang = Path.Combine(notesDir, "方剂", "麻黄汤.md");

        Assert.True(File.Exists(guizhi), $"应保存到 {guizhi}");
        Assert.True(File.Exists(mahuang));

        var guizhiContent = File.ReadAllText(guizhi);
        Assert.StartsWith("---", guizhiContent);           // frontmatter
        Assert.Contains("ai_generated: true", guizhiContent);
        Assert.Contains("[[方剂/麻黄汤]]", guizhiContent);  // wikilink 补全分类目录
        Assert.Contains("# 桂枝汤", guizhiContent);
    }

    // ===================== ③ 链式补充去重 =====================

    [Fact]
    public async Task Chain_SupplementDuplicatePath_NotDuplicated()
    {
        var splitResponse = $$"""[ {{NoteJson("方剂/桂枝汤", "桂枝汤", "# 桂枝汤\n解肌发表。")}} ]""";
        var supplementResponse = $$"""
            [
              {{NoteJson("方剂/桂枝汤", "桂枝汤", "# 桂枝汤\n重复内容不应落盘")}},
              {{NoteJson("方剂/白虎汤", "白虎汤", "# 白虎汤\n清热生津。")}}
            ]
            """;
        // 两个提供方：步骤 1 = 拆分（A），步骤 2 = 链式补充（B）
        var (splitter, chat, taskManager) = CreateSplitter([splitResponse, supplementResponse]);
        var taskId = taskManager.CreateTask("atom_note_split");

        await splitter.SplitAtomNotes(taskId, "伤寒论笔记", "长文本……", useChain: true,
            aiProviderIds: ["provider-a", "provider-b"], vaultPath: _vaultDir);

        Assert.Equal(2, chat.CallCount); // 拆分 + 补充各一次
        Assert.Equal(RunnerTaskStatus.Success, await GetTaskStatusAsync(taskManager, taskId));

        Assert.True(File.Exists(Path.Combine(_vaultDir, "notes", "方剂", "桂枝汤.md")));
        Assert.True(File.Exists(Path.Combine(_vaultDir, "notes", "方剂", "白虎汤.md")));
        var guizhi = File.ReadAllText(Path.Combine(_vaultDir, "notes", "方剂", "桂枝汤.md"));
        Assert.DoesNotContain("重复内容不应落盘", guizhi); // 重复 path 不覆盖已有笔记
        Assert.Contains("解肌发表", guizhi);
    }

    // ===================== ④ AI 失败 =====================

    [Fact]
    public async Task AiThrows_TaskFailsAndRethrows()
    {
        var (splitter, _, taskManager) = CreateSplitter(aiThrows: new InvalidOperationException("connection refused"));
        var taskId = taskManager.CreateTask("atom_note_split");

        await Assert.ThrowsAsync<Exception>(() =>
            splitter.SplitAtomNotes(taskId, "标题", "内容", useChain: false, vaultPath: _vaultDir));

        Assert.Equal(RunnerTaskStatus.Failed, await GetTaskStatusAsync(taskManager, taskId));
        var notesDir = Path.Combine(_vaultDir, "notes");
        Assert.True(!Directory.Exists(notesDir) || !Directory.GetFiles(notesDir, "*", SearchOption.AllDirectories).Any(),
            "AI 失败时不应落盘任何笔记");
    }
}
