using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.Services;

public class DefaultPromptProvider
{
    private readonly string _dataDir;
    private readonly ILogger<DefaultPromptProvider> _logger;
    private Dictionary<string, PromptTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public DefaultPromptProvider(ILogger<DefaultPromptProvider> logger)
    {
        _dataDir = AppDomain.CurrentDomain.BaseDirectory;
        _logger = logger;
        LoadTemplates();
    }

    public PromptTemplate GetTemplateByName(string? industry) => GetTemplate(industry);

    public PromptTemplate GetTemplate(object? scene = null)
    {
        var key = scene?.ToString() ?? "";
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(key) && _templates.TryGetValue(key, out var t))
                return t;
            if (_templates.TryGetValue("通用", out var def))
                return def;
            return new PromptTemplate();
        }
    }

    public List<PromptTemplate> GetAllTemplates()
    {
        lock (_lock) { return _templates.Values.ToList(); }
    }

    public void SaveTemplate(PromptTemplate template)
    {
        lock (_lock)
        {
            _templates[template.DisplayName] = template;
            PersistTemplates();
        }
    }

    public bool DeleteTemplate(string displayName)
    {
        if (string.Equals(displayName, "通用", StringComparison.OrdinalIgnoreCase))
            return false;
        lock (_lock)
        {
            var removed = _templates.Remove(displayName);
            if (removed) PersistTemplates();
            return removed;
        }
    }

    private void LoadTemplates()
    {
        var filePath = ResolveTemplatePath();
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var list = JsonSerializer.Deserialize<List<PromptTemplate>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (list != null)
                {
                    _templates = list.Where(t => !string.IsNullOrEmpty(t.DisplayName))
                                     .ToDictionary(t => t.DisplayName, StringComparer.OrdinalIgnoreCase);
                    _logger.LogDebug("Loaded {Count} prompt templates from {Path}", _templates.Count, filePath);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load prompt templates from {Path}, using defaults", filePath);
            }
        }

        SeedDefaults();
        PersistTemplates();
    }

    private void PersistTemplates()
    {
        var filePath = ResolveTemplatePath();
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_templates.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist prompt templates to {Path}", filePath);
        }
    }

    private string ResolveTemplatePath()
    {
        var baseDir = Baihua.Contracts.BaihuaPaths.Db;
        var binDebug = Path.Combine("bin", "Debug");
        var binRelease = Path.Combine("bin", "Release");
        if (baseDir.Contains(binDebug) || baseDir.Contains(binRelease))
        {
            var idx = baseDir.IndexOf(binDebug);
            if (idx < 0) idx = baseDir.IndexOf(binRelease);
            if (idx > 0) baseDir = baseDir.Substring(0, idx);
        }
        return Path.Combine(baseDir, "prompt-templates.json");
    }

    private void SeedDefaults()
    {
        _templates = new Dictionary<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["通用"] = new()
            {
                ChatSystemPrompt = "你是一个知识管理助手，请根据用户的知识库内容回答问题。",
                SplitSystemPrompt = "你是一位专业的内容拆分专家。你的任务是将长文本拆分为「原子笔记」——每个笔记只聚焦一个核心概念，内容高度结构化，拒绝冗长描述。",
                SplitUserPrompt = "请将以下内容拆分为原子笔记。每条笔记必须遵循以下原则：\n\n1. 聚焦单一主题：一条笔记只讲一个核心概念\n2. 高度结构化：必须包含核心定义（1-3句话）、关键要点（3-5条列表）、关联概念（1-2个）、记忆锚点（口诀/类比）、典型场景/案例\n3. 无冗余：不讨论历史沿革、文化背景、个人经验\n4. 使用 Markdown 格式\n5. 语言专业、清晰、客观\n\n原始内容：",
                SupplementUserPrompt = "请检查是否有遗漏的核心概念，补充为新的原子笔记。保持同样的格式和原则。",
                DisplayName = "通用",
                DefaultCategories = ["通用"],
                ConfigurationAdvice = "",
                KnowledgeBuildPlaceholder = "输入任意主题，AI 将生成结构化知识笔记",
                KnowledgeBuildDescription = "AI 根据主题生成结构化知识笔记"
            },
            ["中医"] = new()
            {
                ChatSystemPrompt = @"你是一位资深中医师，精通中医基础理论、辨证论治和中药方剂。请遵循以下原则：
1. 辨证论治：先辨病机，再定治法，后选方药
2. 理法方药：理（病机）→ 法（治法）→ 方（方剂）→ 药（药物），层层递进
3. 整体观念：重视脏腑关系、气血津液、经络联系
4. 三因制宜：因人、因时、因地制宜
5. 治病求本：分清标本缓急，急则治标，缓则治本
回答时请用专业但清晰的语言，适当引用经典原文佐证。",
                SplitSystemPrompt = "你是一位中医知识拆分专家。将中医内容拆分为「原子笔记」，每条笔记只聚焦一个证型、一个方剂或一个病机概念。内容必须包含：核心定义、辨证要点、治法方药、类证鉴别、临证备要。",
                SplitUserPrompt = "请将以下中医内容拆分为原子笔记。每条笔记遵循：\n\n1. 聚焦单一证型/方剂/病机\n2. 结构：核心定义 → 辨证要点（主症、舌脉）→ 治法 → 代表方剂（组成、用法）→ 类证鉴别 → 临证备要\n3. 方剂标注出处（如《伤寒论》）\n4. 用 Markdown 格式，标题用「###」\n5. 语言精炼，避免泛泛而谈\n\n原始内容：",
                SupplementUserPrompt = "请检查是否有遗漏的证型或方剂，补充为新的原子笔记。",
                DisplayName = "中医",
                DefaultCategories = ["病机", "证治", "方剂", "中药", "经络", "养生"],
                ConfigurationAdvice = "中医知识库建议按「病机/证治/方剂」三级目录组织",
                KnowledgeBuildPlaceholder = "例如：脾胃病证治、风寒束肺、麻黄汤",
                KnowledgeBuildDescription = "AI 生成中医辨证论治知识笔记，含证型、方剂、鉴别要点"
            },
            ["计算机"] = new()
            {
                ChatSystemPrompt = @"你是一位资深软件工程师，精通计算机科学和软件工程。请遵循以下原则：
1. 概念精确：使用行业标准术语，避免模糊表述
2. 代码示例：涉及编程概念时给出简洁的代码示例
3. 分层讲解：先讲 What（是什么），再讲 Why（为什么），最后讲 How（怎么做）
4. 最佳实践：指出常见陷阱和业界最佳实践
5. 关联知识：说明前置知识和相关概念",
                SplitSystemPrompt = "你是一位计算机知识拆分专家。将技术内容拆分为「原子笔记」，每条笔记只聚焦一个概念或技术点。内容必须包含：核心定义、关键要点、代码示例、常见陷阱、关联概念。",
                SplitUserPrompt = "请将以下技术内容拆分为原子笔记。每条笔记遵循：\n\n1. 聚焦单一概念/技术点\n2. 结构：核心定义 → 关键要点（3-5条）→ 代码示例（如适用）→ 常见陷阱 → 关联概念\n3. 代码示例用 ``` 包裹并标注语言\n4. 用 Markdown 格式\n5. 语言精确，避免口语化\n\n原始内容：",
                SupplementUserPrompt = "请检查是否有遗漏的核心概念，补充为新的原子笔记。",
                DisplayName = "计算机",
                DefaultCategories = ["编程语言", "算法", "系统设计", "数据库", "网络", "DevOps"],
                ConfigurationAdvice = "计算机知识库建议按「领域/技术栈/主题」三级目录组织",
                KnowledgeBuildPlaceholder = "例如：设计模式、REST API、Docker 入门",
                KnowledgeBuildDescription = "AI 生成计算机技术知识笔记，含概念、代码示例、最佳实践"
            },
            ["笔记"] = new()
            {
                ChatSystemPrompt = "你是一位知识整理专家，擅长将零散信息组织为结构化笔记。请用清晰、简洁的语言回答，注重逻辑层次和可读性。",
                SplitSystemPrompt = "你是一位笔记整理专家。将内容拆分为「原子笔记」，每条笔记只聚焦一个知识点。内容必须包含：核心定义、关键要点、实际应用、关联知识。",
                SplitUserPrompt = "请将以下内容拆分为原子笔记。每条笔记遵循：\n\n1. 聚焦单一知识点\n2. 结构：核心定义 → 关键要点 → 实际应用 → 关联知识\n3. 用 Markdown 格式\n4. 语言简洁清晰\n\n原始内容：",
                SupplementUserPrompt = "请检查是否有遗漏的知识点，补充为新的原子笔记。",
                DisplayName = "笔记",
                DefaultCategories = ["笔记"],
                ConfigurationAdvice = "",
                KnowledgeBuildPlaceholder = "输入任意主题，AI 将生成结构化笔记",
                KnowledgeBuildDescription = "AI 根据主题生成结构化知识笔记"
            }
        };
    }

    public class PromptTemplate
    {
        public string ChatSystemPrompt { get; set; } = "你是一个知识管理助手，请根据用户的知识库内容回答问题。";
        public string SplitSystemPrompt { get; set; } = "你是一位专业的内容拆分专家。你的任务是将长文本拆分为「原子笔记」——每个笔记只聚焦一个核心概念，内容高度结构化，拒绝冗长描述。";
        public string SplitUserPrompt { get; set; } = "";
        public string SupplementUserPrompt { get; set; } = "";
        public string DisplayName { get; set; } = "通用";
        public List<string> DefaultCategories { get; set; } = ["通用"];
        public string ConfigurationAdvice { get; set; } = "";
        public string KnowledgeBuildPlaceholder { get; set; } = "";
        public string KnowledgeBuildDescription { get; set; } = "";
    }
}
