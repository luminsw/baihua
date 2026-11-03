using System.Text.Json.Serialization;

namespace Baihua.Contracts.Learn;

/// <summary>
/// 题库包文件（百花导出 / 花阁上传 / 花记下载的交换格式）。
/// 行业无关：中医（经方）只是第一个实例，任何行业都可按此结构组装题库。
/// JSON 序列化遵循仓库规范：camelCase + 大小写不敏感。
/// </summary>
public class QuizBankPackage
{
    public int FormatVersion { get; set; } = 1;

    public QuizIndustryInfo Industry { get; set; } = new();

    public QuizBankInfo Bank { get; set; } = new();

    public List<QuizItemDto> Items { get; set; } = [];

    public List<QuizQuestionDto> Questions { get; set; } = [];
}

public class QuizIndustryInfo
{
    /// <summary>行业代码（如 tcm = 中医）</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
}

public class QuizBankInfo
{
    /// <summary>题库代码（如 jingfang = 经方）</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public string Version { get; set; } = "";

    public string Description { get; set; } = "";
}

/// <summary>
/// 知识条目：题库的核心知识单元。中医实例以「药方」为核心（itemType = formula），
/// 亦可承载证型 / 概念等；内容字段行业无关，中医语义全在 content 内。
/// </summary>
public class QuizItemDto
{
    /// <summary>条目代码（库内唯一，如 f_xiexin_tang）</summary>
    public string ItemCode { get; set; } = "";

    /// <summary>条目类型：formula / pattern / concept …（行业自定义）</summary>
    public string ItemType { get; set; } = "concept";

    public string Name { get; set; } = "";

    public string Summary { get; set; } = "";

    public QuizItemContentDto? Content { get; set; }
}

/// <summary>
/// 知识条目内容（中医实例的完整结构；其他行业可自定义字段，未知字段容忍）。
/// </summary>
public class QuizItemContentDto
{
    /// <summary>出处（原文条文，含书名篇章）</summary>
    public string? Source { get; set; }

    /// <summary>组成（药味：名称/古量/今量/君臣佐使）</summary>
    public List<QuizHerbDoseDto>? Composition { get; set; }

    /// <summary>煎服法全文（含方后注）</summary>
    public string? Decoction { get; set; }

    /// <summary>病机（证型病机描述）</summary>
    public string? Pathogenesis { get; set; }

    /// <summary>治法</summary>
    public string? Treatment { get; set; }

    /// <summary>主治证型名称列表</summary>
    public List<string>? Indications { get; set; }

    /// <summary>禁忌</summary>
    public List<string>? Contraindications { get; set; }

    /// <summary>与相似方/相似证的鉴别区分点</summary>
    public List<QuizDiscriminationDto>? Discriminations { get; set; }

    /// <summary>补充说明（炮制、今代、安全红线等）</summary>
    public string? Notes { get; set; }
}

public class QuizHerbDoseDto
{
    public string Name { get; set; } = "";

    /// <summary>古量原文（如 三两（去皮））</summary>
    public string? AncientDose { get; set; }

    /// <summary>今量（如 9g）</summary>
    public string? ModernDose { get; set; }

    /// <summary>君臣佐使（可空）</summary>
    public string? Role { get; set; }
}

/// <summary>区分点：与易混淆对象的精准鉴别（临床最少问诊、精确选方的教学核心）</summary>
public class QuizDiscriminationDto
{
    /// <summary>对照对象（相似方名/证型名）</summary>
    public string Versus { get; set; } = "";

    /// <summary>关键区分点</summary>
    public string Point { get; set; } = "";
}

/// <summary>选择题（单选为 MVP；结构预留多选）</summary>
public class QuizQuestionDto
{
    /// <summary>题目代码（题库内唯一，如 q_053_f）</summary>
    public string QuestionCode { get; set; } = "";

    /// <summary>关联知识条目（如方剂 itemCode，可空）</summary>
    public string? ItemCode { get; set; }

    /// <summary>题型：single_choice（MVP 仅此一种）</summary>
    public string QuestionType { get; set; } = "single_choice";

    /// <summary>提问目标：formula（选方）/ pattern（选证型·病机）/ 其他行业自定义</summary>
    public string Target { get; set; } = "formula";

    /// <summary>题干：完整病例描述（中医实例为四诊合参的病情）</summary>
    public string Stem { get; set; } = "";

    /// <summary>结构化题干（患者概况 + 体征症状要点，便于 UI 渲染）</summary>
    public QuizStemDetailDto? StemDetail { get; set; }

    /// <summary>选项（含正确项与干扰项；正确项唯一由数据声明，判定零 AI）</summary>
    public List<QuizOptionDto> Options { get; set; } = [];

    /// <summary>解析：正确结果 + 完整分析（作答后展示）</summary>
    public string Analysis { get; set; } = "";

    /// <summary>区分点：与本题干扰项的关键鉴别（作答后展示）</summary>
    public List<QuizDiscriminationDto>? Discrimination { get; set; }

    /// <summary>难度 1-5</summary>
    public int Difficulty { get; set; } = 3;

    /// <summary>条文出处（如 金匮要略·黄疸病脉证并治）</summary>
    public string? SourceRef { get; set; }
}

public class QuizOptionDto
{
    /// <summary>选项键（A/B/C/D…）</summary>
    public string Key { get; set; } = "";

    /// <summary>选项内容（方名 / 证型名 / 行业自定义）</summary>
    public string Content { get; set; } = "";

    /// <summary>是否正确项（数据静态声明；判定＝比对所选 key，纯确定性）</summary>
    public bool IsCorrect { get; set; }

    /// <summary>该选项的解析（正确项＝选它的理由；干扰项＝不选它的理由/与正确项的差异）</summary>
    public string? Explanation { get; set; }
}

public class QuizStemDetailDto
{
    /// <summary>患者概况（如「王某，男，38岁」；其他行业可作题设背景）</summary>
    public string? Patient { get; set; }

    /// <summary>体征/症状/条件要点（四诊合参的清单化展示）</summary>
    public List<string> Signs { get; set; } = [];
}

// ─────────────────────────────────────────────
// 服务端 DTO（刷题 API；UI 阶段使用，契约先行）
// ─────────────────────────────────────────────

/// <summary>刷题会话中的单题视图（不含正确项标记——作答前不泄题）</summary>
public class QuizQuestionViewDto
{
    public string QuestionCode { get; set; } = "";
    public string? ItemCode { get; set; }
    public string Target { get; set; } = "formula";
    public string Stem { get; set; } = "";
    public QuizStemDetailDto? StemDetail { get; set; }
    public int Difficulty { get; set; }
    public string? SourceRef { get; set; }
    public List<QuizOptionViewDto> Options { get; set; } = [];
}

public class QuizOptionViewDto
{
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>作答提交</summary>
public class QuizAnswerSubmitDto
{
    public string QuestionCode { get; set; } = "";
    public string SelectedKey { get; set; } = "";
    public int? LearnerId { get; set; }
    public int? TimeCostMs { get; set; }
    public string? SessionId { get; set; }
}

/// <summary>作答结果（含正确答案 + 解析 + 区分点）</summary>
public class QuizAnswerResultDto
{
    public bool IsCorrect { get; set; }
    public string CorrectKey { get; set; } = "";
    public string CorrectContent { get; set; } = "";
    public List<QuizOptionDto> Options { get; set; } = [];
    public string Analysis { get; set; } = "";
    public List<QuizDiscriminationDto>? Discrimination { get; set; }
}

/// <summary>题库概览</summary>
public class QuizBankOverviewDto
{
    public string IndustryCode { get; set; } = "";
    public string IndustryName { get; set; } = "";
    public string BankCode { get; set; } = "";
    public string BankName { get; set; } = "";
    public string Version { get; set; } = "";
    public int ItemCount { get; set; }
    public int QuestionCount { get; set; }
}

/// <summary>导入结果</summary>
public class QuizBankImportResultDto
{
    public string BankCode { get; set; } = "";
    public int ItemsImported { get; set; }
    public int QuestionsImported { get; set; }
    public int ItemsSkipped { get; set; }
    public int QuestionsSkipped { get; set; }
    public List<string> Warnings { get; set; } = [];
}