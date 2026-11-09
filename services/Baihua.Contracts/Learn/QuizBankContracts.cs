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

/// <summary>
/// 题库学习统计（统一口径）。
/// <para>Web 端按家庭成员统计（LearnerId 有值）；移动端离线判定，使用本地 quiz_attempts 同口径统计。</para>
/// <para>AnsweredCount / MasteredCount / WrongCount 均按「每题最近一次作答」判定。</para>
/// </summary>
public class QuizBankStatsDto
{
    public string BankCode { get; set; } = "";

    public int? LearnerId { get; set; }
    public string? LearnerName { get; set; }

    /// <summary>题库总题数</summary>
    public int QuestionCount { get; set; }

    /// <summary>已刷题数（做过的不同题目数）</summary>
    public int AnsweredCount { get; set; }

    /// <summary>已掌握题数（最近一次作答正确）</summary>
    public int MasteredCount { get; set; }

    /// <summary>待复习错题数（最近一次作答错误）</summary>
    public int WrongCount { get; set; }

    /// <summary>累计答题次数（含重复作答）</summary>
    public int TotalAttempts { get; set; }

    /// <summary>累计答对次数</summary>
    public int CorrectAttempts { get; set; }

    /// <summary>正确率（按次数，0-100，保留 1 位小数）</summary>
    public double Accuracy { get; set; }

    /// <summary>覆盖率（AnsweredCount / QuestionCount，0-100，保留 1 位小数）</summary>
    public double Coverage { get; set; }

    /// <summary>最近一次练习时间</summary>
    public DateTime? LastAnsweredAt { get; set; }
}

/// <summary>题库统计包：整体 + 各家庭成员（家庭成员项仅包含有作答记录者）</summary>
public class QuizBankStatsBundleDto
{
    public QuizBankStatsDto Overall { get; set; } = new();
    public List<QuizBankStatsDto> Learners { get; set; } = [];
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
/// <summary>
/// 导入选项。
/// <para><see cref="OverwriteExisting"/> = true（默认）：幂等 upsert，已存在的条目/题目按包内容刷新，
/// 与 CLI 及历史批次导入行为一致。</para>
/// <para>= false（append 模式，AI 生成 / Agent 写入默认）：只追加新代码；已存在的条目/题目原样保留并计入 Skipped。
/// 用于防止 AI 生成的 code 撞车时静默覆盖人工整理的内容。</para>
/// </summary>
public class QuizBankImportOptions
{
    /// <summary>是否覆盖已存在的条目/题目（默认 true；append 模式传 false）</summary>
    public bool OverwriteExisting { get; set; } = true;

    /// <summary>
    /// append 模式下是否仍更新行业/题库的名称与描述（默认 false：只加内容、不改元信息）
    /// </summary>
    public bool UpdateMetadata { get; set; }
}

/// <summary>
/// 题库包体检结果：落库前的整体校验（AI 生成 / Agent 写入 / 手工导入共用）。
/// <para>Errors：结构或关键字段缺陷，严格模式下整体拒绝落库。</para>
/// <para>Warnings：可容忍但影响质量（缺解析、选项数不足 4、target 非标准值等）。</para>
/// </summary>
public class QuizBankValidationResultDto
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int ItemCount { get; set; }
    public int QuestionCount { get; set; }

    /// <summary>按题型统计（行业无关：key 由行业画像定义，如 formula / rule / concept）</summary>
    public Dictionary<string, int> TargetCounts { get; set; } = [];
    public List<string> ItemCodes { get; set; } = [];
    public List<string> QuestionCodes { get; set; } = [];
}
// ─────────────────────────────────────────────
// 生成 / 导入（UI 生成页；MCP 侧的导入走工具参数，不依赖这些 DTO）
// ─────────────────────────────────────────────

/// <summary>UI 生成题库的请求参数（行业 → 题库 → 主题 → 约束）</summary>
public class QuizGenerateRequestDto
{
    public string IndustryCode { get; set; } = "";
    public string IndustryName { get; set; } = "";
    public string BankCode { get; set; } = "";
    public string BankName { get; set; } = "";

    /// <summary>生成主题与要求（必填）</summary>
    public string Topic { get; set; } = "";

    /// <summary>formula（选方案）/ pattern（选证型·病机）/ mixed（混合）</summary>
    public string Target { get; set; } = "formula";

    /// <summary>题量 1-30（超过 10 自动分批调用）</summary>
    public int QuestionCount { get; set; } = 10;

    /// <summary>难度 1-5</summary>
    public int Difficulty { get; set; } = 3;

    /// <summary>参考范围（书名篇章 / 知识库 id / 目录），可空</summary>
    public string? ReferenceScope { get; set; }

    /// <summary>same_scope（同范围易混干扰项，默认）/ random</summary>
    public string DistractorStrategy { get; set; } = "same_scope";

    /// <summary>题号前缀（默认 ai；追加到已有题库时建议用唯一前缀）</summary>
    public string CodePrefix { get; set; } = "ai";

    /// <summary>自定义题型说明（target 不属于行业画像时，用于提示词描述该题型问什么）</summary>
    public string? TargetDescription { get; set; }

    /// <summary>可选：指定 AI 提供商（留空用主 provider）</summary>
    public string? ProviderId { get; set; }

    /// <summary>可选：指定模型（留空用该 provider 的主模型）</summary>
    public string? Model { get; set; }
}

/// <summary>生成预览结果：成功时含题库包与校验结果，不落库；解析失败时保留原始响应供排查</summary>
public class QuizGeneratePreviewDto
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Prompt { get; set; } = "";
    public QuizBankPackage? Package { get; set; }
    public QuizBankValidationResultDto? Validation { get; set; }
    public string? RawResponse { get; set; }
    public string? ModelUsed { get; set; }
    public int BatchCount { get; set; }
}

/// <summary>UI 确认导入请求</summary>
public class QuizBankImportRequestDto
{
    public QuizBankPackage Package { get; set; } = new();

    /// <summary>是否覆盖已存在的条目/题目（默认 false：append，只追加新代码）</summary>
    public bool OverwriteExisting { get; set; }
}
// ─────────────────────────────────────────────
// 行业画像（题型与术语随行业变化，避免中医专用硬编码）
// ─────────────────────────────────────────────

/// <summary>行业画像中的题型选项</summary>
public class QuizTargetOptionDto
{
    /// <summary>题型代码（写入 QuizQuestionDto.Target，如 formula / rule / output）</summary>
    public string Code { get; set; } = "";

    /// <summary>UI 显示文案（如「选方剂」）</summary>
    public string Label { get; set; } = "";

    /// <summary>给 AI 的题型说明（问什么、怎么问）</summary>
    public string Description { get; set; } = "";

    /// <summary>选项承载的对象类型（如「方剂名」「法规条款名」）</summary>
    public string OptionKind { get; set; } = "";
}

/// <summary>
/// 行业画像：驱动生成页的行业/题型下拉、提示词术语与校验告警。
/// 未知行业使用「通用知识」画像；tcm 等已登记行业保持既有题型，兼容老数据。
/// </summary>
public class QuizIndustryProfileDto
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>条目类型建议（写入 QuizItemDto.ItemType）</summary>
    public string ItemType { get; set; } = "concept";

    /// <summary>条目称谓（提示词与 UI 用词，如「方剂/证型」「法规条款」）</summary>
    public string ItemName { get; set; } = "知识条目";

    /// <summary>生成主题输入框的占位提示</summary>
    public string TopicPlaceholder { get; set; } = "";

    /// <summary>参考范围输入框的占位提示</summary>
    public string? ReferenceScopePlaceholder { get; set; }

    /// <summary>content 建议字段（供提示词说明）</summary>
    public List<string> ContentFields { get; set; } = [];

    /// <summary>可选题型（至少一个）</summary>
    public List<QuizTargetOptionDto> Targets { get; set; } = [];

    /// <summary>是否内置画像（数据库中已有但未登记的行业为 false）</summary>
    public bool BuiltIn { get; set; } = true;
}