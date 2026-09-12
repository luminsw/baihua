using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Ai;
using Baihua.Contracts.Master;
using Baihua.Core.Localization;
using System.Reflection;
using System.Text.Json;

namespace Baihua.Modules.Family.Services;

public class MasterPromptBuilder
{
    private readonly IStringLocalizer<SharedResources> _loc;

    public MasterPromptBuilder(IStringLocalizer<SharedResources> loc)
    {
        _loc = loc;
    }

    private static readonly Dictionary<string, string> IndustryMasterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["中医"] = "MasterQiBo",
        ["医学"] = "MasterQiBo",
        ["计算机"] = "MasterTuLing",
        ["IT"] = "MasterTuLing",
        ["会计"] = "MasterSuanSheng",
        ["财务"] = "MasterSuanSheng",
        ["教资"] = "MasterFuZi",
        ["教育"] = "MasterFuZi",
        ["法律"] = "MasterTingWei",
        ["建筑"] = "MasterLuBan",
    };

    private StagePersona GetStagePersona(string stageName)
    {
        return stageName switch
        {
            "入道" => new StagePersona(_loc["MasterPrompt_RuDao_Role"], _loc["MasterPrompt_RuDao_Style"], _loc["MasterPrompt_RuDao_Prompt"]),
            "筑基" => new StagePersona(_loc["MasterPrompt_ZhuJi_Role"], _loc["MasterPrompt_ZhuJi_Style"], _loc["MasterPrompt_ZhuJi_Prompt"]),
            "精进" => new StagePersona(_loc["MasterPrompt_JingJin_Role"], _loc["MasterPrompt_JingJin_Style"], _loc["MasterPrompt_JingJin_Prompt"]),
            "磨砺" => new StagePersona(_loc["MasterPrompt_MoLi_Role"], _loc["MasterPrompt_MoLi_Style"], _loc["MasterPrompt_MoLi_Prompt"]),
            "出师" => new StagePersona(_loc["MasterPrompt_ChuShi_Role"], _loc["MasterPrompt_ChuShi_Style"], _loc["MasterPrompt_ChuShi_Prompt"]),
            _ => new StagePersona(_loc["MasterPrompt_RuDao_Role"], _loc["MasterPrompt_RuDao_Style"], _loc["MasterPrompt_RuDao_Prompt"])
        };
    }

    private static readonly string[] SafetyBlockedKeywords =
    [
        "真实诊断", "开处方", "开药方", "真实法律建议", "代理诉讼",
        "医疗诊断", "处方药", "手术方案", "法律代理"
    ];

    public string ResolveMasterName(string industry)
    {
        foreach (var (key, locKey) in IndustryMasterKeys)
        {
            if (industry.Contains(key, StringComparison.OrdinalIgnoreCase))
                return _loc["MasterPrompt_" + locKey];
        }
        return _loc["Prompt_DefaultMasterName"];
    }

    public List<ChatMessage> BuildMessages(
        string goal,
        string industry,
        string masterName,
        string currentStage,
        string? coreProfile,
        string? stageSummary,
        List<ChatHistoryItem>? recentHistory,
        string userMessage)
    {
        var messages = new List<ChatMessage>();

        var systemPrompt = BuildSystemPrompt(goal, industry, masterName, currentStage, coreProfile, stageSummary);
        messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        if (recentHistory != null)
        {
            foreach (var item in recentHistory.TakeLast(20))
            {
                var role = item.Role == "assistant" ? ChatRole.Assistant : ChatRole.User;
                messages.Add(new ChatMessage(role, item.Content));
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, userMessage));

        return messages;
    }

    public bool ContainsBlockedContent(string input)
    {
        return SafetyBlockedKeywords.Any(k => input.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildSafetyRefusal()
    {
        return _loc["Prompt_SafetyRefusal"];
    }

    public string BuildSystemPrompt(
        string goal,
        string industry,
        string masterName,
        string currentStage,
        string? coreProfile,
        string? stageSummary)
    {
        var persona = GetStagePersona(currentStage);

        var sb = new System.Text.StringBuilder();

        sb.AppendLine(_loc["Prompt_SectionMasterRole"]);
        sb.AppendLine(string.Format(_loc["Prompt_YouAreTemplate"], masterName, industry, persona.RoleName));
        sb.AppendLine(string.Format(_loc["Prompt_StyleTemplate"], persona.Style));
        sb.AppendLine();
        sb.AppendLine(persona.Prompt);
        sb.AppendLine();
        sb.AppendLine(_loc["Prompt_SectionApprenticeGoal"]);
        sb.AppendLine(string.Format(_loc["Prompt_GoalTemplate"], goal));
        sb.AppendLine(string.Format(_loc["Prompt_CurrentStageTemplate"], currentStage));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(coreProfile))
        {
            sb.AppendLine(_loc["Prompt_SectionCoreProfile"]);
            sb.AppendLine(coreProfile);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(stageSummary))
        {
            sb.AppendLine(_loc["Prompt_SectionStageSummary"]);
            sb.AppendLine(stageSummary);
            sb.AppendLine();
        }

        sb.AppendLine(_loc["Prompt_SectionTeachingPrinciples"]);
        sb.AppendLine(_loc["Prompt_Principle1"]);
        sb.AppendLine(_loc["Prompt_Principle2"]);
        sb.AppendLine(_loc["Prompt_Principle3"]);
        sb.AppendLine(_loc["Prompt_Principle4"]);
        sb.AppendLine(_loc["Prompt_Principle5"]);
        sb.AppendLine();
        sb.AppendLine(_loc["Prompt_SectionSafety"]);
        sb.AppendLine(_loc["Prompt_SafetyBoundary"]);

        return sb.ToString();
    }

    public List<StageInfo> GetDefaultStages()
    {
        return
        [
            new() { Name = "入道", DisplayName = _loc["Prompt_StageNameRuDao"], Description = _loc["Prompt_StageDescRuDao"], Order = 1 },
            new() { Name = "筑基", DisplayName = _loc["Prompt_StageNameZhuJi"], Description = _loc["Prompt_StageDescZhuJi"], Order = 2 },
            new() { Name = "精进", DisplayName = _loc["Prompt_StageNameJingJin"], Description = _loc["Prompt_StageDescJingJin"], Order = 3 },
            new() { Name = "磨砺", DisplayName = _loc["Prompt_StageNameMoLi"], Description = _loc["Prompt_StageDescMoLi"], Order = 4 },
            new() { Name = "出师", DisplayName = _loc["Prompt_StageNameChuShi"], Description = _loc["Prompt_StageDescChuShi"], Order = 5 },
        ];
    }

    public ExamOutline? MatchExamOutline(string goal, string industry)
    {
        var outlines = LoadAllOutlines();
        if (outlines.Count == 0) return null;

        var text = $"{goal} {industry}".ToLowerInvariant();

        foreach (var outline in outlines)
        {
            if (outline.Keywords == null) continue;
            foreach (var kw in outline.Keywords)
            {
                if (!string.IsNullOrEmpty(kw) && text.Contains(kw.ToLowerInvariant()))
                    return outline;
            }
        }

        return null;
    }

    public List<StageInfo> GetStagesForOutline(ExamOutline? outline)
    {
        if (outline?.Stages == null || outline.Stages.Count == 0)
            return GetDefaultStages();

        return outline.Stages.Select((s, i) => new StageInfo
        {
            Name = s.Name,
            DisplayName = s.Name,
            Description = s.Description ?? "",
            Order = i + 1
        }).ToList();
    }

    public string? GetOutlineContext(ExamOutline? outline, string currentStage)
    {
        if (outline == null) return null;

        var stageInfo = outline.Stages?.FirstOrDefault(s => s.Name == currentStage);
        if (stageInfo == null) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format(_loc["MasterPrompt_OutlineHeader"], outline.Name));

        if (stageInfo.KeyPoints != null && stageInfo.KeyPoints.Count > 0)
        {
            sb.AppendLine(_loc["MasterPrompt_StageKeyPoints"]);
            foreach (var kp in stageInfo.KeyPoints)
                sb.AppendLine($"- {kp}");
        }

        if (!string.IsNullOrEmpty(stageInfo.TransitionCriteria))
            sb.AppendLine(string.Format(_loc["MasterPrompt_TransitionCriteria"], stageInfo.TransitionCriteria));

        if (stageInfo.Milestones != null && stageInfo.Milestones.Count > 0)
        {
            sb.AppendLine(_loc["MasterPrompt_Milestones"]);
            foreach (var m in stageInfo.Milestones)
                sb.AppendLine($"- {m}");
        }

        return sb.ToString();
    }

    private static List<ExamOutline> LoadAllOutlines()
    {
        var result = new List<ExamOutline>();
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "Baihua.Data.ExamOutlines.";

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix) || !name.EndsWith(".json")) continue;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var outline = JsonSerializer.Deserialize<ExamOutline>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (outline != null) result.Add(outline);
            }
            catch { }
        }

        return result;
    }

    private record StagePersona(string RoleName, string Style, string Prompt);
}

public class ExamOutline
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string>? Keywords { get; set; }
    public List<ExamStageOutline>? Stages { get; set; }
}

public class ExamStageOutline
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? KeyPoints { get; set; }
    public List<string>? Milestones { get; set; }
    public string? TransitionCriteria { get; set; }
}
