using Baihua.Core.Models;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Controllers.AI.Stages;
using Baihua.Data;
using Baihua.Modules.Family.Models;
using Baihua.Modules.Family.Services;

namespace Baihua.Modules.Family.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class MasterController : ControllerBase
{
    private readonly AiClientService _aiClientService;
    private readonly AiSettingsService _aiSettings;
    private readonly MasterPromptBuilder _promptBuilder;
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly VaultSettingsService _vaultSettings;
    private readonly VaultNoteIndexer _vaultNoteIndexer;
    private readonly UserActivityService _activityService;
    private readonly IStringLocalizer<SharedResources> _loc;
    private readonly ILogger<MasterController> _logger;
    private readonly StageStrategyFactory _stageStrategyFactory;

    public MasterController(
        AiClientService aiClientService,
        AiSettingsService aiSettings,
        MasterPromptBuilder promptBuilder,
        IDbContextFactory<FamilyDbContext> dbFactory,
        VaultSettingsService vaultSettings,
        VaultNoteIndexer vaultNoteIndexer,
        UserActivityService activityService,
        IStringLocalizer<SharedResources> loc,
        ILogger<MasterController> logger,
        StageStrategyFactory stageStrategyFactory)
    {
        _aiClientService = aiClientService;
        _aiSettings = aiSettings;
        _promptBuilder = promptBuilder;
        _dbFactory = dbFactory;
        _vaultSettings = vaultSettings;
        _vaultNoteIndexer = vaultNoteIndexer;
        _activityService = activityService;
        _loc = loc;
        _logger = logger;
        _stageStrategyFactory = stageStrategyFactory;
    }

    /// <summary>
    /// 按最大长度截断文本，超过时追加"..."
    /// </summary>
    private static string TruncateText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>
    /// 解析 AI 提供商和模型。providerId 和 model 均可为空（自动选择主提供商/主模型）。
    /// </summary>
    private (AiProviderConfig Provider, string Model) ResolveProviderAndModel(string? providerId, string? model)
    {
        var providers = _aiSettings.GetAiProviders();
        var provider = string.IsNullOrEmpty(providerId)
            ? (string.IsNullOrEmpty(model)
                ? providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
                : providers.FirstOrDefault(p =>
                    p.Models.Any(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
                  ?? providers.FirstOrDefault(p => p.IsMain)
                  ?? providers.FirstOrDefault())
            : providers.FirstOrDefault(p => p.Id == providerId);

        if (provider == null)
            throw new Exception(_loc["AiProvider_NotFound"]);

        var modelOptions = provider.GetModelOptions();
        var resolvedModel = !string.IsNullOrEmpty(model)
            ? model
            : modelOptions.FirstOrDefault(m => m.IsMain)?.Name
              ?? modelOptions.FirstOrDefault()?.Name
              ?? "Qwen/Qwen2.5-14B-Instruct";

        return (provider, resolvedModel);
    }

    /// <summary>
    /// 递归展开异常链，返回最深层的错误信息（通常包含 SQLite/网络等具体原因）
    /// </summary>
    private static string UnwrapExceptionMessage(Exception ex)
    {
        var messages = new List<string>();
        var current = ex;
        while (current != null)
        {
            var msg = current.Message.Trim();
            if (!string.IsNullOrEmpty(msg) && !messages.Contains(msg))
                messages.Add(msg);
            current = current.InnerException;
        }
        return string.Join(" → ", messages);
    }

    /// <summary>
    /// 获取阶段序号
    /// </summary>
    private static int GetStageOrder(string stageName)
    {
        return stageName switch
        {
            "入道" => 1,
            "筑基" => 2,
            "精进" => 3,
            "磨砺" => 4,
            "出师" => 5,
            _ => 0
        };
    }

    /// <summary>
    /// 构建 vault 知识库上下文（用于 AI 对话增强）
    /// </summary>
    private async Task<string?> BuildVaultContextAsync(FamilyDbContext db, string masterId, string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return null;

        var focusedVaults = await db.VaultFocusStates
            .Where(v => v.MasterId == masterId && v.State == "focused")
            .ToListAsync();

        if (focusedVaults.Count == 0) return null;

        var vaultIds = focusedVaults.Select(v => v.VaultId).ToList();
        var results = new List<string>();

        foreach (var vaultId in vaultIds)
        {
            try
            {
                var searchResults = await _vaultNoteIndexer.SearchAsync(vaultId, userMessage);
                if (searchResults.Count == 0) continue;

                var context = string.Join("\n", searchResults.Take(3).Select(r =>
                    $"📄 **{r.Title}**\n{r.Preview}"));
                results.Add(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "知识库检索失败：{VaultId}", vaultId);
            }
        }

        if (results.Count == 0) return null;

        return $"以下是关联知识库中的相关内容，请结合这些内容回答：\n\n{string.Join("\n---\n", results)}";
    }
}
