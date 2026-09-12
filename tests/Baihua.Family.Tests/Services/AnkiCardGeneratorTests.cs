using Baihua.Core.Models;
using Baihua.Core.Services;
using AnkiGen.Core;
using Baihua.Modules.Family.Services;
using Baihua.Data;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Anki;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Xunit;
using Moq;

namespace Baihua.Family.Tests.Services;

public class AnkiCardGeneratorTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly IStringLocalizer<SharedResources> _loc = TestLocalizer.Instance;

    public AnkiCardGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AnkiCardGeneratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* ignore cleanup failures */ }
        }
    }

    // ===================== Factory Methods =====================

    private AnkiCardGenerator CreateGenerator(bool configureAi = true)
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<AnkiCardGenerator>();

        var vaultSettings = CreateVaultSettingsService();

        var aiSettings = configureAi ? CreateAiSettingsService() : CreateEmptyAiSettingsService();
        var aiClient = CreateAiClientService(aiSettings);

        var mockTaskDbContextFactory = new Mock<IDbContextFactory<FamilyDbContext>>();
        var taskManager = new TaskManager(mockTaskDbContextFactory.Object);

        return new AnkiCardGenerator(vaultSettings, aiClient, aiSettings, taskManager, logger, _loc);
    }

    /// <summary>
    /// Creates a VaultSettingsService backed by InMemory database, seeded with
    /// a vault record pointing at _tempDir so NotesPath/CardsPath return valid paths.
    /// </summary>
    private VaultSettingsService CreateVaultSettingsService()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        // Seed a vault record
        using (var db = new VaultDbContext(options))
        {
            db.Vaults.Add(new Baihua.Data.Entities.Vault
            {
                VaultId = Guid.NewGuid().ToString("N"),
                Name = "TestVault",
                Path = _tempDir,
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var factory = new InMemoryDbContextFactory<VaultDbContext>(options);
        var vaultLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<VaultSettingsService>();

        return new VaultSettingsService(factory, vaultLogger, _loc);
    }

    /// <summary>
    /// Configures AiSettingsService with a test provider (GetMainAiProvider returns it).
    /// </summary>
    private AiSettingsService CreateAiSettingsService()
    {
        var configData = new Dictionary<string, string?>
        {
            {"Ai:0:Id", "test-provider"},
            {"Ai:0:Name", "Test Provider"},
            {"Ai:0:AiBaseUrl", "http://localhost:19999/v1"},
            {"Ai:0:IsMain", "true"},
            {"Ai:0:Models:0:Name", "test-model"},
            {"Ai:0:Models:0:IsMain", "true"},
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<AiSettingsService>();

        return new AiSettingsService(configuration, TestDoubles.StubAiConfigService.Empty, logger);
    }

    /// <summary>
    /// Creates an AiSettingsService with no providers configured (GetMainAiProvider returns null).
    /// </summary>
    private AiSettingsService CreateEmptyAiSettingsService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<AiSettingsService>();

        return new AiSettingsService(configuration, TestDoubles.StubAiConfigService.Empty, logger);
    }

    /// <summary>
    /// Creates a real AiClientService wired up with the given AiSettingsService.
    /// </summary>
    private AiClientService CreateAiClientService(AiSettingsService aiSettings)
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var autoStarterLogger = loggerFactory.CreateLogger<LocalAiAutoStarter>();
        var aiClientLogger = loggerFactory.CreateLogger<AiClientService>();
        var anthropicLogger = loggerFactory.CreateLogger<AnthropicAiClient>();

        var mockAutoStarter = new Mock<LocalAiAutoStarter>(autoStarterLogger);
        var mockAiDbFactory = new Mock<IDbContextFactory<AIDbContext>>();
        var mockCache = new Mock<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();

        var metricsService = new AiMetricsService(_loc);
        var httpClient = new HttpClient();
        var anthropicClient = new AnthropicAiClient(httpClient, anthropicLogger, _loc);

        return new AiClientService(
            aiSettings,
            mockAutoStarter.Object,
            metricsService,
            mockCache.Object,
            anthropicClient,
            aiClientLogger,
            _loc,
            mockAiDbFactory.Object);
    }

    // ===================== Unit Tests: ExtractJsonArray =====================

    [Fact]
    public void ExtractJsonArray_WithJsonMarkdownBlock_ExtractsArray()
    {
        var input = """
            Here is the result:
            ```json
            [{"front":"Q1","back":"A1"},{"front":"Q2","back":"A2"}]
            ```
            """;

        var result = InvokeExtractJsonArray(input);

        Assert.Equal(@"[{""front"":""Q1"",""back"":""A1""},{""front"":""Q2"",""back"":""A2""}]", result);
    }

    [Fact]
    public void ExtractJsonArray_WithBareArray_ReturnsArray()
    {
        var input = """[{"front":"Q1","back":"A1"}]""";

        var result = InvokeExtractJsonArray(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void ExtractJsonArray_WithTextBeforeAndAfter_ExtractsArray()
    {
        var input = """
            Some text before
            [{"front":"Q1","back":"A1"}]
            Some text after
            """;

        var result = InvokeExtractJsonArray(input);

        Assert.Equal(@"[{""front"":""Q1"",""back"":""A1""}]", result);
    }

    [Fact]
    public void ExtractJsonArray_NoArray_ReturnsOriginal()
    {
        var input = "Just plain text without any array";

        var result = InvokeExtractJsonArray(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void ExtractJsonArray_WithCodeBlockOnly_StripsMarkers()
    {
        var input = """
            ```json
            [{"front":"Q","back":"A"}]
            ```
            """;

        var result = InvokeExtractJsonArray(input);

        Assert.Equal(@"[{""front"":""Q"",""back"":""A""}]", result);
    }

    // ===================== Unit Tests: ExtractTitle =====================

    [Fact]
    public void ExtractTitle_WithMarkdownHeading_ReturnsTitle()
    {
        var content = """
            # 桂枝汤
            ## 组成
            桂枝三两
            """;

        var result = InvokeExtractTitle(content);

        Assert.Equal("桂枝汤", result);
    }

    [Fact]
    public void ExtractTitle_NoHeading_ReturnsEmpty()
    {
        var content = "Just some text without heading.\nAnother line.";

        var result = InvokeExtractTitle(content);

        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractTitle_HeadingNotFirst_ReturnsFirstHeading()
    {
        var content = """
            Some preamble text
            # The Real Title
            Content here
            """;

        var result = InvokeExtractTitle(content);

        Assert.Equal("The Real Title", result);
    }

    [Fact]
    public void ExtractTitle_WithWeirdSpacing_TrimsCorrectly()
    {
        var content = "#     Spaced Title   ";

        var result = InvokeExtractTitle(content);

        Assert.Equal("Spaced Title", result);
    }

    // ===================== Integration Tests: GenerateWithAiAsync =====================

    [Fact]
    public async Task GenerateWithAiAsync_NoteNotFound_ReturnsError()
    {
        var generator = CreateGenerator();

        var result = await generator.GenerateWithAiAsync(
            "nonexistent-note",
            notesBasePath: _tempDir);

        Assert.False(result.Success);
        Assert.Contains("不存在", result.Message);
        Assert.Equal(0, result.CardCount);
    }

    [Fact]
    public async Task GenerateWithAiAsync_NoBasePath_ReturnsError()
    {
        // VaultSettingsService with empty DB so NotesPath returns ""
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger<AnkiCardGenerator>();

        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new InMemoryDbContextFactory<VaultDbContext>(options);
        var vaultLogger = loggerFactory.CreateLogger<VaultSettingsService>();
        var vaultSettings = new VaultSettingsService(factory, vaultLogger, _loc);

        var aiSettings = CreateEmptyAiSettingsService();
        var aiClient = CreateAiClientService(aiSettings);

        var mockTaskDbContextFactory = new Mock<IDbContextFactory<FamilyDbContext>>();
        var taskManager = new TaskManager(mockTaskDbContextFactory.Object);

        var generator = new AnkiCardGenerator(vaultSettings, aiClient, aiSettings, taskManager, logger, _loc);

        var result = await generator.GenerateWithAiAsync("any-note");

        Assert.False(result.Success);
        Assert.Contains("未配置", result.Message);
        Assert.Equal(0, result.CardCount);
    }

    [Fact]
    public async Task GenerateWithAiAsync_NoteFound_AiReturnsEmpty_ReturnsZeroCards()
    {
        // Create a test note file
        var notesPath = Path.Combine(_tempDir, "notes");
        Directory.CreateDirectory(notesPath);
        var noteContent = "# Test Note\nThis is a test note content for AI generation.";
        var notePath = "test-note";
        await File.WriteAllTextAsync(Path.Combine(notesPath, notePath + ".md"), noteContent);

        var generator = CreateGenerator(configureAi: false);

        // No AI providers configured → GetMainAiProvider returns null → AI returns empty
        var result = await generator.GenerateWithAiAsync(
            notePath,
            notesBasePath: notesPath);

        Assert.True(result.Success);
        Assert.Equal(0, result.CardCount);
        Assert.Contains("未从笔记生成卡片", result.Message);
    }

    [Fact]
    public async Task GenerateWithAiAsync_WithProviderId_ThatReturnsNull()
    {
        // Create a test note file
        var notesPath = Path.Combine(_tempDir, "notes");
        Directory.CreateDirectory(notesPath);
        await File.WriteAllTextAsync(Path.Combine(notesPath, "some-note.md"), "# Title\nContent.");

        var generator = CreateGenerator(configureAi: true);

        // Pass a providerId that doesn't exist → GetAiProvider returns null
        var result = await generator.GenerateWithAiAsync(
            "some-note",
            notesBasePath: notesPath,
            providerId: "non-existent-provider");

        Assert.True(result.Success);
        Assert.Equal(0, result.CardCount);
        Assert.Contains("未从笔记生成卡片", result.Message);
    }

    [Fact]
    public async Task GenerateWithAiAsync_WithEmptyFile_SucceedsWithZeroCards()
    {
        var notesPath = Path.Combine(_tempDir, "notes");
        Directory.CreateDirectory(notesPath);
        await File.WriteAllTextAsync(Path.Combine(notesPath, "empty-note.md"), "");

        var generator = CreateGenerator(configureAi: false);

        var result = await generator.GenerateWithAiAsync(
            "empty-note",
            notesBasePath: notesPath);

        Assert.True(result.Success);
        Assert.Equal(0, result.CardCount);
    }

    // ===================== Tests for ExportToCsv =====================

    [Fact]
    public async Task ExportToCsv_CardsPathDoesNotExist_ReturnsNull()
    {
        var generator = CreateGenerator();

        var result = await generator.ExportToCsv(cardsPath: Path.Combine(_tempDir, "nonexistent_cards"));

        Assert.Null(result);
    }

    [Fact]
    public async Task ExportToCsv_WithValidCardFiles_ExportsCorrectly()
    {
        var cardsPath = Path.Combine(_tempDir, "cards");
        Directory.CreateDirectory(cardsPath);
        var generator = CreateGenerator();

        // Write a mock JSON card file
        var deckData = new JsonDeckData
        {
            Name = "经方::TestDeck",
            Cards = new List<Baihua.Contracts.Anki.JsonCard>
            {
                new() { Front = "Q1", Back = "A1", Tags = new List<string> { "tag1" } },
                new() { Front = "Q2", Back = "A2", Tags = new List<string> { "tag1", "tag2" } },
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(deckData);
        await File.WriteAllTextAsync(Path.Combine(cardsPath, "test_note.json"), json);

        var result = await generator.ExportToCsv(cardsPath: cardsPath);

        Assert.NotNull(result);
        Assert.Contains("Front,Back,Tags,Deck", result);
        Assert.Contains("Q1", result);
        Assert.Contains("A1", result);
        Assert.Contains("Q2", result);
        Assert.Contains("A2", result);
        Assert.Contains("经方::TestDeck", result);
        Assert.Contains("tag1", result);
        Assert.Contains("tag2", result);
    }

    [Fact]
    public async Task ExportToCsv_CsvEscaping_HandlesCommas()
    {
        var cardsPath = Path.Combine(_tempDir, "cards");
        Directory.CreateDirectory(cardsPath);
        var generator = CreateGenerator();

        var deckData = new JsonDeckData
        {
            Name = "Deck",
            Cards = new List<Baihua.Contracts.Anki.JsonCard>
            {
                new() { Front = "Value,with,commas", Back = "Also,commas", Tags = new List<string> { "a,b" } },
            }
        };
        await File.WriteAllTextAsync(Path.Combine(cardsPath, "escape_test.json"),
            System.Text.Json.JsonSerializer.Serialize(deckData));

        var result = await generator.ExportToCsv(cardsPath: cardsPath);

        Assert.NotNull(result);
        // Should be quoted
        Assert.Contains("\"Value,with,commas\"", result);
        Assert.Contains("\"Also,commas\"", result);
    }

    [Fact]
    public async Task ExportToCsv_MultipleFiles_CombinesAll()
    {
        var cardsPath = Path.Combine(_tempDir, "cards");
        Directory.CreateDirectory(cardsPath);
        var generator = CreateGenerator();

        var deck1 = new JsonDeckData { Name = "Deck1", Cards = new List<Baihua.Contracts.Anki.JsonCard> { new() { Front = "F1", Back = "B1" } } };
        var deck2 = new JsonDeckData { Name = "Deck2", Cards = new List<Baihua.Contracts.Anki.JsonCard> { new() { Front = "F2", Back = "B2" } } };
        await File.WriteAllTextAsync(Path.Combine(cardsPath, "deck1.json"),
            System.Text.Json.JsonSerializer.Serialize(deck1));
        await File.WriteAllTextAsync(Path.Combine(cardsPath, "deck2.json"),
            System.Text.Json.JsonSerializer.Serialize(deck2));

        var result = await generator.ExportToCsv(cardsPath: cardsPath);

        Assert.NotNull(result);
        Assert.Contains("F1", result);
        Assert.Contains("F2", result);
        // Header should appear exactly once
        Assert.Single(result.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("Front"));
    }

    // ===================== Tests for GetTotalCardCount =====================

    [Fact]
    public void GetTotalCardCount_NoCardsPath_ReturnsZero()
    {
        var generator = CreateGenerator();

        var count = generator.GetTotalCardCount(vaultId: "nonexistent-vault");

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetTotalCardCount_WithCards_ReturnsCount()
    {
        var cardsPath = Path.Combine(_tempDir, "cards");
        Directory.CreateDirectory(cardsPath);
        var generator = CreateGenerator();

        var deck1 = new JsonDeckData { Name = "D1", Cards = new List<Baihua.Contracts.Anki.JsonCard> { new() { Front = "F1", Back = "B1" } } };
        var deck2 = new JsonDeckData { Name = "D2", Cards = new List<Baihua.Contracts.Anki.JsonCard> { new() { Front = "F2", Back = "B2" }, new() { Front = "F3", Back = "B3" } } };
        File.WriteAllText(Path.Combine(cardsPath, "d1.json"), System.Text.Json.JsonSerializer.Serialize(deck1));
        File.WriteAllText(Path.Combine(cardsPath, "d2.json"), System.Text.Json.JsonSerializer.Serialize(deck2));

        var count = generator.GetTotalCardCount();

        Assert.Equal(3, count);
    }

    [Fact]
    public void GetTotalCardCount_EmptyDirectory_ReturnsZero()
    {
        var cardsPath = Path.Combine(_tempDir, "cards");
        Directory.CreateDirectory(cardsPath);
        var generator = CreateGenerator();

        var count = generator.GetTotalCardCount();

        Assert.Equal(0, count);
    }

    // ===================== Reflection Helpers =====================

    private static string InvokeExtractJsonArray(string text)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("ExtractJsonArray",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { text })!;
    }

    private static string InvokeExtractTitle(string content)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("ExtractTitle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { content })!;
    }

    private class InMemoryDbContextFactory<TContext> : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        private readonly DbContextOptions<TContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<TContext> options) => _options = options;
        public TContext CreateDbContext()
            => (TContext)Activator.CreateInstance(typeof(TContext), _options)!;
        public Task<TContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
