using Baihua.Contracts.Learn;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 行业画像目录：描述「某个行业的题库长什么样」——题型（target）、条目称谓、内容字段建议、
/// 主题与参考范围的输入提示。生成页、提示词模板、校验器统一从这里取术语，避免把中医语义
/// （方剂 / 证型）硬编码到通用能力里。
///
/// 兜底规则：未登记的行业返回「通用知识」画像，仅替换 code/name，其余保持中性术语。
/// 已知行业（如 tcm）的题型保持原值，向后兼容既有 219 方 / 259 题数据。
/// </summary>
public static class QuizIndustryProfileCatalog
{
    /// <summary>通用兜底画像（任何未登记行业都用它）</summary>
    public static QuizIndustryProfileDto General { get; } = new()
    {
        Code = "general",
        Name = "通用知识",
        Description = "任意学科的基础知识选择题",
        ItemType = "concept",
        ItemName = "知识条目",
        TopicPlaceholder = "例：基础概念与常见应用场景，每题给出情境，问最合适的答案",
        ReferenceScopePlaceholder = "如 某本教材的章节 / 知识库 id",
        ContentFields = ["source（出处）", "treatment（要点或结论）", "pathogenesis（原理说明）", "indications（适用范围）", "contraindications（例外或禁忌）", "discriminations（与易混对象的区分点）", "notes（补充说明）"],
        Targets =
        [
            new QuizTargetOptionDto
            {
                Code = "single_answer",
                Label = "选正确答案",
                Description = "从若干选项中选出唯一正确答案",
                OptionKind = "答案内容（概念、数值、结论等）",
            },
            new QuizTargetOptionDto
            {
                Code = "definition",
                Label = "选定义",
                Description = "给出术语或现象，选出与之对应的定义",
                OptionKind = "定义文字",
            },
            new QuizTargetOptionDto
            {
                Code = "application",
                Label = "选应用",
                Description = "给出具体情境，选出最合适的应用或做法",
                OptionKind = "做法 / 应用方案",
            },
        ],
    };

    private static readonly Dictionary<string, QuizIndustryProfileDto> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = General,
        ["tcm"] = new QuizIndustryProfileDto
        {
            Code = "tcm",
            Name = "中医",
            Description = "中医辨证与经方选择题",
            ItemType = "formula",
            ItemName = "方剂/证型",
            TopicPlaceholder = "例：太阳病桂枝汤类方，重点考兼证与禁忌；每题给出病例四诊，问最合适的方剂",
            ReferenceScopePlaceholder = "如 伤寒论·太阳篇，或知识库 id",
            ContentFields = ["source（原文条文）", "composition（组成：药味/古量/今量/君臣佐使）", "decoction（煎服法）", "pathogenesis（病机）", "treatment（治法）", "indications（主治证型）", "contraindications（禁忌）", "discriminations（相似方/证的区分点）", "notes（补充说明）"],
            Targets =
            [
                new QuizTargetOptionDto
                {
                    Code = "formula",
                    Label = "选方剂",
                    Description = "给出病例四诊，问最合适的方剂是哪一个",
                    OptionKind = "方剂名",
                },
                new QuizTargetOptionDto
                {
                    Code = "pattern",
                    Label = "选证型 / 病机",
                    Description = "给出病例四诊，问对应的证型或病机是哪一个",
                    OptionKind = "证型名 / 病机名",
                },
            ],
        },
        ["driving"] = new QuizIndustryProfileDto
        {
            Code = "driving",
            Name = "驾照 / 交通法规",
            Description = "交通法规与安全驾驶选择题",
            ItemType = "rule",
            ItemName = "法规条款/驾驶知识点",
            TopicPlaceholder = "例：城市道路让行规则与常见违章情形；每题给出驾驶情境，问应当如何处置",
            ReferenceScopePlaceholder = "如 道路交通安全法 第某章 / 题库知识库 id",
            ContentFields = ["source（法规出处）", "treatment（规定要点）", "pathogenesis（适用条件与原理）", "indications（适用场景）", "contraindications（例外情形）", "discriminations（易混条款的区分点）", "notes（记忆口诀）"],
            Targets =
            [
                new QuizTargetOptionDto
                {
                    Code = "rule",
                    Label = "选法规条款",
                    Description = "给出驾驶情境，选出应当适用的法规条款",
                    OptionKind = "法规条款名 / 条款编号",
                },
                new QuizTargetOptionDto
                {
                    Code = "judgment",
                    Label = "选驾驶判断",
                    Description = "给出驾驶情境，选出正确的处置方式",
                    OptionKind = "驾驶操作 / 处置方式",
                },
                new QuizTargetOptionDto
                {
                    Code = "sign",
                    Label = "选交通标志信号",
                    Description = "给出道路场景描述，选出正确的标志或信号含义",
                    OptionKind = "标志 / 信号含义",
                },
            ],
        },
        ["programming"] = new QuizIndustryProfileDto
        {
            Code = "programming",
            Name = "编程",
            Description = "编程语言、框架与工程实践选择题",
            ItemType = "concept",
            ItemName = "知识点/代码片段",
            TopicPlaceholder = "例：C# 异步编程与并发陷阱；每题给出代码或运行情境，问输出结果或最合适的修复",
            ReferenceScopePlaceholder = "如 官方文档某章节 / 项目知识库 id",
            ContentFields = ["source（文档出处）", "treatment（正确做法）", "pathogenesis（原理与原因）", "indications（适用场景）", "contraindications（不适用 / 常见误用）", "discriminations（与相似 API 的差异）", "notes（示例代码要点）"],
            Targets =
            [
                new QuizTargetOptionDto
                {
                    Code = "concept",
                    Label = "选概念",
                    Description = "选出术语或机制的正确解释",
                    OptionKind = "概念解释",
                },
                new QuizTargetOptionDto
                {
                    Code = "output",
                    Label = "选输出结果",
                    Description = "给出代码或命令，选出其运行结果",
                    OptionKind = "输出结果",
                },
                new QuizTargetOptionDto
                {
                    Code = "debug",
                    Label = "选原因 / 修复",
                    Description = "给出报错或异常行为，选出原因或最合适的修复方案",
                    OptionKind = "原因 / 修复方案",
                },
                new QuizTargetOptionDto
                {
                    Code = "api",
                    Label = "选 API / 配置",
                    Description = "给出需求，选出应使用的 API、库或配置项",
                    OptionKind = "API / 库 / 配置项名称",
                },
            ],
        },
        ["english"] = new QuizIndustryProfileDto
        {
            Code = "english",
            Name = "英语",
            Description = "词汇、语法与阅读理解选择题",
            ItemType = "concept",
            ItemName = "词汇/语法点",
            TopicPlaceholder = "例：高频动词短语与从句引导词；每题给出句子，问最合适的词或形式",
            ReferenceScopePlaceholder = "如 某教材第几单元 / 词汇表 id",
            ContentFields = ["source（教材出处）", "treatment（用法要点）", "pathogenesis（语法规则）", "indications（典型例句）", "contraindications（易错情形）", "discriminations（近义词辨析）", "notes（记忆要点）"],
            Targets =
            [
                new QuizTargetOptionDto { Code = "vocabulary", Label = "选词义", Description = "选出单词或短语在句中的正确含义/用法", OptionKind = "词义或短语" },
                new QuizTargetOptionDto { Code = "grammar", Label = "选语法形式", Description = "选出句子中应填的正确语法形式", OptionKind = "语法形式" },
                new QuizTargetOptionDto { Code = "reading", Label = "选阅读理解答案", Description = "根据短文选出正确答案", OptionKind = "答案内容" },
            ],
        },
        ["math"] = new QuizIndustryProfileDto
        {
            Code = "math",
            Name = "数学",
            Description = "数学概念、公式与解题方法选择题",
            ItemType = "concept",
            ItemName = "公式/定理",
            TopicPlaceholder = "例：一元二次方程与函数图像；每题给出题目，问计算结果或解题方法",
            ReferenceScopePlaceholder = "如 某教材章节 / 知识点 id",
            ContentFields = ["source（教材出处）", "treatment（结论或公式）", "pathogenesis（推导思路）", "indications（适用条件）", "contraindications（不适用情形）", "discriminations（易混公式辨析）", "notes（解题技巧）"],
            Targets =
            [
                new QuizTargetOptionDto { Code = "result", Label = "选计算结果", Description = "给出题目，选出正确的计算结果", OptionKind = "数值 / 表达式" },
                new QuizTargetOptionDto { Code = "method", Label = "选解题方法", Description = "给出题目，选出最合适的解题方法", OptionKind = "方法名称" },
                new QuizTargetOptionDto { Code = "formula", Label = "选公式", Description = "给出情境，选出应使用的公式或定理", OptionKind = "公式 / 定理" },
            ],
        },
        ["law"] = new QuizIndustryProfileDto
        {
            Code = "law",
            Name = "法律",
            Description = "法条与案例分析选择题",
            ItemType = "concept",
            ItemName = "法条/案例",
            TopicPlaceholder = "例：合同法违约责任；每题给出案情，问适用的法条或裁判要点",
            ReferenceScopePlaceholder = "如 民法典 某编某章 / 法规知识库 id",
            ContentFields = ["source（法条出处）", "treatment（裁判要点）", "pathogenesis（法理说明）", "indications（适用情形）", "contraindications（例外 / 但书）", "discriminations（相似法条的区分）", "notes（案例要点）"],
            Targets =
            [
                new QuizTargetOptionDto { Code = "article", Label = "选法条", Description = "给出案情，选出应适用的法条", OptionKind = "法条名称 / 条款编号" },
                new QuizTargetOptionDto { Code = "case", Label = "选裁判要点", Description = "给出案情，选出正确的裁判结论", OptionKind = "裁判结论 / 要点" },
            ],
        },
        ["medicine"] = new QuizIndustryProfileDto
        {
            Code = "medicine",
            Name = "医学（现代）",
            Description = "现代医学诊断与治疗选择题",
            ItemType = "concept",
            ItemName = "疾病/诊疗要点",
            TopicPlaceholder = "例：常见内科急症识别与处理；每题给出病史与检查，问最可能的诊断或首选处理",
            ReferenceScopePlaceholder = "如 某诊疗指南 / 教材章节",
            ContentFields = ["source（指南出处）", "treatment（处理原则）", "pathogenesis（病理生理）", "indications（适应证）", "contraindications（禁忌证）", "discriminations（鉴别诊断要点）", "notes（临床提醒）"],
            Targets =
            [
                new QuizTargetOptionDto { Code = "diagnosis", Label = "选诊断", Description = "给出病史与检查，选出最可能的诊断", OptionKind = "疾病名称" },
                new QuizTargetOptionDto { Code = "treatment", Label = "选治疗方案", Description = "选出首选或最合适的治疗方案", OptionKind = "治疗方案" },
                new QuizTargetOptionDto { Code = "drug", Label = "选药物", Description = "选出应使用的药物", OptionKind = "药物名称" },
                new QuizTargetOptionDto { Code = "exam", Label = "选检查", Description = "选出最有价值的检查", OptionKind = "检查项目" },
            ],
        },
    };

    /// <summary>全部内置画像（按 code 排序）</summary>
    public static IReadOnlyList<QuizIndustryProfileDto> GetAll()
        => BuiltIns.Values.OrderBy(p => p.Code, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 解析行业画像：已登记返回内置；未登记返回通用画像的副本（替换 code/name，保留传入名称）。
    /// </summary>
    public static QuizIndustryProfileDto Resolve(string? code, string? name = null)
    {
        var normalized = string.IsNullOrWhiteSpace(code) ? "general" : code.Trim();
        if (BuiltIns.TryGetValue(normalized, out var profile))
            return profile;

        return new QuizIndustryProfileDto
        {
            Code = normalized,
            Name = string.IsNullOrWhiteSpace(name) ? normalized : name.Trim(),
            Description = General.Description,
            ItemType = General.ItemType,
            ItemName = General.ItemName,
            TopicPlaceholder = General.TopicPlaceholder,
            ReferenceScopePlaceholder = General.ReferenceScopePlaceholder,
            ContentFields = [.. General.ContentFields],
            Targets = General.Targets.Select(t => new QuizTargetOptionDto
            {
                Code = t.Code,
                Label = t.Label,
                Description = t.Description,
                OptionKind = t.OptionKind,
            }).ToList(),
            BuiltIn = false,
        };
    }

    /// <summary>把内置画像与数据库中已有但未登记的行业合并（生成页行业下拉用）</summary>
    public static List<QuizIndustryProfileDto> MergeWithExisting(
        IEnumerable<(string Code, string Name)> existingIndustries)
    {
        var list = GetAll().ToList();
        var known = new HashSet<string>(list.Select(p => p.Code), StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name) in existingIndustries)
        {
            if (string.IsNullOrWhiteSpace(code) || !known.Add(code.Trim())) continue;
            list.Add(Resolve(code, name));
        }
        return list.OrderBy(p => p.BuiltIn ? 0 : 1).ThenBy(p => p.Code, StringComparer.Ordinal).ToList();
    }
}