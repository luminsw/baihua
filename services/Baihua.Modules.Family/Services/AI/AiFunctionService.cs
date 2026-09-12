using Baihua.Core.Models;
using Baihua.Core.Services;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// AI Function Calling 服务：为 AI 聊天提供可调用工具
/// 使 AI 能够主动搜索知识库、获取系统信息等
/// </summary>
public class AiFunctionService
{
    private readonly VaultSettingsService _vaultSettings;
    private readonly VaultNoteIndexer _vaultNoteIndexer;
    private readonly SystemHealthService _healthService;
    private readonly AnkiCardGenerator _cardGenerator;
    private readonly ILogger<AiFunctionService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public AiFunctionService(
        VaultSettingsService vaultSettings,
        VaultNoteIndexer vaultNoteIndexer,
        SystemHealthService healthService,
        AnkiCardGenerator cardGenerator,
        ILogger<AiFunctionService> logger,
        IStringLocalizer<SharedResources> loc)
    {
        _vaultSettings = vaultSettings;
        _vaultNoteIndexer = vaultNoteIndexer;
        _healthService = healthService;
        _cardGenerator = cardGenerator;
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 获取所有可用的 AI 工具
    /// </summary>
    public IList<AITool> GetAllTools()
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(SearchVaultAsync, "search_vault", _loc["AiFunction_SearchVaultDesc"]),
            AIFunctionFactory.Create(GetCurrentDateAsync, "get_current_date", _loc["AiFunction_GetCurrentDateDesc"]),
            AIFunctionFactory.Create(ListVaultsAsync, "list_vaults", _loc["AiFunction_ListVaultsDesc"]),
            AIFunctionFactory.Create(CreateNoteAsync, "create_note", _loc["AiFunction_CreateNoteDesc"]),
            AIFunctionFactory.Create(GetSystemStatusAsync, "get_system_status", _loc["AiFunction_GetSystemStatusDesc"]),
        };
    }

    /// <summary>
    /// 搜索知识库中的笔记
    /// </summary>
    public async Task<string> SearchVaultAsync(
        [Description("搜索关键词，如\"桂枝汤\"、\"太阳中风\"、\"发热恶寒\"")] string query)
    {
        try
        {
            var activeVault = _vaultSettings.GetActiveVault();
            if (activeVault == null)
                return _loc["AiFunction_VaultNotConfiguredSearch"];

            _logger.LogInformation("[AI Function] search_vault: query={Query}, vault={VaultId}", query, activeVault.Id);

            var results = await _vaultNoteIndexer.SearchAsync(activeVault.Id, query);
            if (results.Count == 0)
                return _loc["AiFunction_SearchNoResults"];

            var lines = results.Take(5).Select(r =>
                $"📄 **{r.Title}**\n{r.Preview}\n");

            return string.Format(_loc["AiFunction_SearchResults"], results.Count) +
                   string.Join("\n---\n", lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI Function] search_vault 失败");
            return string.Format(_loc["AiFunction_SearchFailed"], ex.Message);
        }
    }

    /// <summary>
    /// 获取当前日期时间
    /// </summary>
    public Task<string> GetCurrentDateAsync()
    {
        return Task.FromResult(string.Format(_loc["AiFunction_CurrentTime"], DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
    }

    /// <summary>
    /// 列出已配置的知识库
    /// </summary>
    public Task<string> ListVaultsAsync()
    {
        var vaults = _vaultSettings.GetVaults();
        if (vaults.Count == 0)
            return Task.FromResult<string>(_loc["AiFunction_NoVaultsConfigured"]);

        var lines = vaults.Select(v => $"- {v.Name} ({v.Path})");
        return Task.FromResult<string>(_loc["AiFunction_VaultsList"] + string.Join("\n", lines));
    }

    /// <summary>
    /// 创建笔记到知识库
    /// </summary>
    public async Task<string> CreateNoteAsync(
        [Description("笔记标题，简洁概括主题，如\"桂枝汤的功效与主治\"")] string title,
        [Description("笔记的 Markdown 内容，支持标题、列表、引用等格式")] string content)
    {
        try
        {
            var activeVault = _vaultSettings.GetActiveVault();
            if (activeVault == null)
                return _loc["AiFunction_VaultNotConfiguredSave"];

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
                return _loc["AiFunction_ContentEmpty"];

            var safeTitle = GenerateSafeFileName(title);
            var notesRoot = Path.Combine(activeVault.Path, "notes");
            var notePath = Path.Combine(notesRoot, _loc["AiGeneratedDir"], $"{safeTitle}.md");
            var noteDir = Path.GetDirectoryName(notePath) ?? throw new InvalidOperationException($"Cannot get directory: {notePath}");
            Directory.CreateDirectory(noteDir);

            var sourceInfo = _loc["AiFunction_NoteSourceInfo"] +
                string.Format(_loc["AiFunction_NoteTimeInfo"], DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            var fullContent = $"# {title}\n\n{sourceInfo}{content}";
            await File.WriteAllTextAsync(notePath, fullContent);

            // 自动为该笔记生成 Anki 记忆卡片
            try
            {
                var relativePath = Path.GetRelativePath(notesRoot, notePath);
                relativePath = relativePath.Substring(0, relativePath.Length - 3); // 去掉 .md
                _ = Task.Run(async () => await _cardGenerator.GenerateWithAiAsync(relativePath));
                _logger.LogInformation("[AI Function] create_note: 笔记已保存，已触发卡片生成任务：{Path}", relativePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AI Function] create_note: 自动触发卡片生成失败");
            }

            return string.Format(_loc["AiFunction_NoteSaved"], title, activeVault.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI Function] create_note 失败");
            return string.Format(_loc["AiFunction_SaveNoteFailed"], ex.Message);
        }
    }

    /// <summary>
    /// 获取系统健康状态
    /// </summary>
    public async Task<string> GetSystemStatusAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var report = await _healthService.GetHealthReportAsync(cts.Token);

            var lines = report.Components.Select(c =>
            {
                var icon = c.Status switch
                {
                    "healthy" => "✅",
                    "warning" => "⚠️",
                    "critical" => "❌",
                    _ => "❓"
                };
                return $"{icon} **{c.Name}**: {c.Status} ({c.Message})";
            });

            var summary = report.Status switch
            {
                "healthy" => _loc["AiFunction_StatusHealthy"],
                "warning" => _loc["AiFunction_StatusWarning"],
                "critical" => _loc["AiFunction_StatusCritical"],
                _ => _loc["AiFunction_StatusUnknown"]
            };

            return string.Format(_loc["AiFunction_StatusWithScore"], summary, report.HealthScore) + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI Function] get_system_status 失败");
            return string.Format(_loc["AiFunction_GetStatusFailed"], ex.Message);
        }
    }

    private static string GenerateSafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var invalidSet = new HashSet<char>(invalid);
        return string.Concat(title.Where(c => !invalidSet.Contains(c)).Take(50));
    }
}
