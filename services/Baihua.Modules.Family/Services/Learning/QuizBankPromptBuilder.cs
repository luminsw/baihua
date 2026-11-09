using System.Text;
using Baihua.Contracts.Learn;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 题库生成提示词构建器：把「行业画像 + 题库 + 主题 + 约束」展开成一份可直接交给 AI 的完整提示词。
///
/// 行业无关：题型、条目称谓、选项承载对象全部来自 <see cref="QuizIndustryProfileCatalog"/>，
/// 中医只是其中一个画像（formula/pattern）。未登记行业自动使用通用知识画像。
///
/// 用途：
/// - MCP 工具 <c>baihua_learn_prompt_template</c> 返回给 Agent；
/// - UI 生成页复用同一份模板，保证两个入口产出的结构完全一致。
/// </summary>
public static class QuizBankPromptBuilder
{
    /// <summary>提示词参数</summary>
    public sealed class Options
    {
        public string IndustryCode { get; set; } = "";
        public string IndustryName { get; set; } = "";
        public string BankCode { get; set; } = "";
        public string BankName { get; set; } = "";
        /// <summary>生成主题与要求（用户提示词）</summary>
        public string Topic { get; set; } = "";
        /// <summary>题型代码：行业画像内某个 target，或 mixed（混合），或自定义</summary>
        public string Target { get; set; } = "formula";
        /// <summary>自定义题型说明（target 不在画像内时使用）</summary>
        public string? TargetDescription { get; set; }
        public int QuestionCount { get; set; } = 10;
        public int Difficulty { get; set; } = 3;
        /// <summary>参考范围（书名篇章 / 知识库 id / 目录），可空</summary>
        public string? ReferenceScope { get; set; }
        /// <summary>same_scope / random</summary>
        public string DistractorStrategy { get; set; } = "same_scope";
        /// <summary>代码前缀，用于避免与既有题号冲突</summary>
        public string CodePrefix { get; set; } = "ai";
        /// <summary>行业画像（为空时按 IndustryCode 自动解析）</summary>
        public QuizIndustryProfileDto? Profile { get; set; }
    }

    public static string Build(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var profile = options.Profile
            ?? QuizIndustryProfileCatalog.Resolve(options.IndustryCode, options.IndustryName);

        var industryCode = Normalize(options.IndustryCode, profile.Code, "industry");
        var industryName = Normalize(options.IndustryName, profile.Name, industryCode);
        var bankCode = Normalize(options.BankCode, "bank");
        var bankName = Normalize(options.BankName, bankCode);
        var topic = string.IsNullOrWhiteSpace(options.Topic)
            ? "（未指定，按该行业的基础知识生成）"
            : options.Topic.Trim();
        var codePrefix = SanitizeCode(options.CodePrefix, "ai");
        var questionCount = Math.Clamp(options.QuestionCount, 1, 50);
        var difficulty = Math.Clamp(options.Difficulty, 1, 5);
        var target = NormalizeTarget(options.Target, profile);

        var referenceBlock = string.IsNullOrWhiteSpace(options.ReferenceScope)
            ? "- 参考范围：未指定（必须基于该行业公认的基础知识，不得编造）"
            : $"- 参考范围：{options.ReferenceScope.Trim()}（内容必须限定在此范围内，范围外内容不要生成）";

        var targetCodes = string.Join(" / ", profile.Targets.Select(t => t.Code));

        var sb = new StringBuilder();
        sb.AppendLine("你是「百花题库」的内容生成助手。请按下面的规范生成一个可以直接导入的单选题题库包（JSON）。");
        sb.AppendLine();
        sb.AppendLine("## 生成任务");
        sb.AppendLine($"- 行业：{industryName}（代码 {industryCode}）");
        sb.AppendLine($"- 题库：{bankName}（代码 {bankCode}）");
        sb.AppendLine($"- 主题/要求：{topic}");
        sb.AppendLine($"- 题型：{DescribeTarget(target, profile, options.TargetDescription)}");
        sb.AppendLine($"- 题目数量：{questionCount}");
        sb.AppendLine($"- 难度：{difficulty}（1 最易，5 最难）");
        sb.AppendLine($"- 干扰项来源：{DescribeDistractor(options.DistractorStrategy)}");
        sb.AppendLine(referenceBlock);
        sb.AppendLine();
        sb.AppendLine("## 输出格式（严格 JSON，单个对象；不要 Markdown 代码块，不要任何解释文字）");
        sb.AppendLine(BuildSchema(profile, codePrefix, targetCodes));
        sb.AppendLine();
        sb.AppendLine("## 硬性规则");
        sb.AppendLine("1. 只输出一个 JSON 对象；第一个字符是 `{`，最后一个字符是 `}`，前后不得有任何文字或代码块标记。");
        sb.AppendLine($"2. 条目代码必须为 `{codePrefix}_item_1`、`{codePrefix}_item_2` ……；题目代码必须为 `{codePrefix}_q_1`、`{codePrefix}_q_2` ……；同一包内全部唯一。");
        sb.AppendLine($"3. items 与 questions 一一对应：每个 {profile.ItemName} 至少一道题；itemCode 引用必须指向本包 items 中的条目（或库内已有条目）。");
        sb.AppendLine("4. 每题恰好 4 个选项，key 依次为 A/B/C/D；`isCorrect` 为 true 的恰好一个。");
        sb.AppendLine("5. 干扰项必须是同一知识范围内真实存在的其他对象（易混淆），不得使用明显离谱的假名或重复内容。");
        sb.AppendLine("6. 每个选项都要有 explanation：正确项写「选它的理由」，干扰项写「不选它的理由（与正确项的关键差异）」。");
        sb.AppendLine("7. analysis 必填：说明正确答案并给出简明推理。");
        sb.AppendLine("8. discrimination 至少一条：写出本题正确项与主要干扰项的关键区分点。");
        sb.AppendLine("9. stem 必须自足：只看题干（含 stemDetail）就能判断出唯一答案，不得依赖未提供的外部信息。");
        sb.AppendLine("10. 不得编造不存在的出处、条文或数据；sourceRef 不确定时留空字符串。");
        sb.AppendLine("11. difficulty 必须是 1-5 的整数；questionType 固定为 single_choice。");
        sb.AppendLine("12. 所有文本使用简体中文；专有名词保持行业通用写法。");
        sb.AppendLine($"13. {DescribeTargetRule(target, profile, options.TargetDescription)}");
        sb.AppendLine();
        sb.AppendLine("## 结构示例（仅示意字段与层级，内容请按主题替换）");
        sb.AppendLine(BuildExample(profile));
        return sb.ToString();
    }

    private static string Normalize(string? value, string fallback, string? secondFallback = null)
        => !string.IsNullOrWhiteSpace(value) ? value.Trim()
            : !string.IsNullOrWhiteSpace(fallback) ? fallback
            : (secondFallback ?? "");

    private static string SanitizeCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            .ToArray();
        return chars.Length == 0 ? fallback : new string(chars);
    }

    /// <summary>题型归一：空 → 画像第一个题型；其余（含 mixed / 自定义）原样保留</summary>
    private static string NormalizeTarget(string? target, QuizIndustryProfileDto profile)
    {
        var t = (target ?? "").Trim();
        if (t.Length == 0)
            return profile.Targets.Count > 0 ? profile.Targets[0].Code : "single_answer";
        return t;
    }

    private static string DescribeTarget(
        string target, QuizIndustryProfileDto profile, string? customDescription)
    {
        if (string.Equals(target, "mixed", StringComparison.OrdinalIgnoreCase))
        {
            var labels = string.Join("、", profile.Targets.Select(t => t.Label));
            return $"混合（在以下题型之间混合，逐题在 target 中标注：{labels}）";
        }

        var matched = profile.Targets.FirstOrDefault(
            t => string.Equals(t.Code, target, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
            return $"{matched.Label}（{matched.Description}）";

        return !string.IsNullOrWhiteSpace(customDescription)
            ? $"{target}（{customDescription.Trim()}）"
            : $"{target}（自定义题型，按该字段字面含义出题）";
    }

    private static string DescribeTargetRule(
        string target, QuizIndustryProfileDto profile, string? customDescription)
    {
        if (string.Equals(target, "mixed", StringComparison.OrdinalIgnoreCase))
        {
            var kinds = profile.Targets.Count > 0
                ? string.Join(" 或 ", profile.Targets.Select(t => t.OptionKind))
                : "答案内容";
            return $"mixed 模式下逐题设置 target；同一题的 options 必须同类型（{kinds}），不得混写。";
        }

        var matched = profile.Targets.FirstOrDefault(
            t => string.Equals(t.Code, target, StringComparison.OrdinalIgnoreCase));
        if (matched is not null && !string.IsNullOrWhiteSpace(matched.OptionKind))
            return $"target={matched.Code} 时，options 的 content 必须是{matched.OptionKind}。";

        return "target 为自定义题型时，options 的 content 必须与题干所问的对象类型一致。";
    }

    private static string DescribeDistractor(string? strategy)
        => string.Equals(strategy, "random", StringComparison.OrdinalIgnoreCase)
            ? "随机选取的其他对象（仍需写明不选的理由）"
            : "同范围内真实存在的其他对象（易混淆，需写出不选它的理由）";

    private static bool HasComposition(QuizIndustryProfileDto profile)
        => profile.ContentFields.Any(f => f.StartsWith("composition", StringComparison.OrdinalIgnoreCase));

    private static string BuildSchema(
        QuizIndustryProfileDto profile, string codePrefix, string targetCodes)
    {
        var contentExtra = HasComposition(profile)
            ? """
                    "composition": [ { "name": "组成项", "ancientDose": "原始量", "modernDose": "现代量", "role": "君/臣/佐/使" } ],
                    "decoction": "煎服法/制法",
            """
            : "";

        return $$"""
        {
          "formatVersion": 1,
          "industry": { "code": "行业代码", "name": "行业名称" },
          "bank": { "code": "题库代码", "name": "题库名称", "version": "v1", "description": "题库说明" },
          "items": [
            {
              "itemCode": "{{codePrefix}}_item_1",
              "itemType": "{{profile.ItemType}}",
              "name": "{{profile.ItemName}}名称",
              "summary": "一句话摘要",
              "content": {
        {{contentExtra}}        "source": "出处（不确定留空）",
                "pathogenesis": "原理 / 说明",
                "treatment": "要点 / 结论",
                "indications": [ "适用情形" ],
                "contraindications": [ "例外 / 禁忌" ],
                "discriminations": [ { "versus": "易混对象", "point": "关键区分点" } ],
                "notes": "补充说明"
              }
            }
          ],
          "questions": [
            {
              "questionCode": "{{codePrefix}}_q_1",
              "itemCode": "{{codePrefix}}_item_1",
              "questionType": "single_choice",
              "target": "{{targetCodes}}",
              "stem": "完整题干",
              "stemDetail": { "patient": "对象概况（可空）", "signs": [ "关键条件/特征" ] },
              "options": [
                { "key": "A", "content": "选项内容", "isCorrect": false, "explanation": "不选它的理由" },
                { "key": "B", "content": "选项内容", "isCorrect": true, "explanation": "选它的理由" },
                { "key": "C", "content": "选项内容", "isCorrect": false, "explanation": "不选它的理由" },
                { "key": "D", "content": "选项内容", "isCorrect": false, "explanation": "不选它的理由" }
              ],
              "analysis": "正确答案 + 推理",
              "discrimination": [ { "versus": "主要干扰项", "point": "关键区分点" } ],
              "difficulty": 3,
              "sourceRef": "出处（不确定留空）"
            }
          ]
        }
        """;
    }

    private static string BuildExample(QuizIndustryProfileDto profile)
    {
        var firstTarget = profile.Targets.FirstOrDefault();
        var targetCode = firstTarget?.Code ?? "single_answer";
        var optionKind = firstTarget?.OptionKind ?? "答案内容";
        var compositionExtra = HasComposition(profile)
            ? """
                    "composition": [ { "name": "组成项", "ancientDose": "原始量", "modernDose": "现代量", "role": "君" } ],
                    "decoction": "煎服法",
            """
            : "";

        return $$"""
        {
          "formatVersion": 1,
          "industry": { "code": "demo", "name": "示例行业" },
          "bank": { "code": "demo_bank", "name": "示例题库", "version": "v1", "description": "结构示意" },
          "items": [
            {
              "itemCode": "ai_item_1",
              "itemType": "{{profile.ItemType}}",
              "name": "示例{{profile.ItemName}}",
              "summary": "一句话摘要",
              "content": {
        {{compositionExtra}}        "source": "",
                "indications": [ "适用情形一" ],
                "discriminations": [ { "versus": "易混对象", "point": "区分点" } ]
              }
            }
          ],
          "questions": [
            {
              "questionCode": "ai_q_1",
              "itemCode": "ai_item_1",
              "questionType": "single_choice",
              "target": "{{targetCode}}",
              "stem": "根据下列条件，最合适的选择是？",
              "stemDetail": { "patient": "对象概况", "signs": [ "条件一", "条件二" ] },
              "options": [
                { "key": "A", "content": "易混{{optionKind}}甲", "isCorrect": false, "explanation": "与条件二不符" },
                { "key": "B", "content": "正确{{optionKind}}", "isCorrect": true, "explanation": "同时满足条件一与条件二" },
                { "key": "C", "content": "易混{{optionKind}}乙", "isCorrect": false, "explanation": "缺少条件一" },
                { "key": "D", "content": "易混{{optionKind}}丙", "isCorrect": false, "explanation": "作用方向相反" }
              ],
              "analysis": "条件一与条件二同时指向正确对象，故选 B。",
              "discrimination": [ { "versus": "易混{{optionKind}}甲", "point": "条件二是否具备" } ],
              "difficulty": 3,
              "sourceRef": ""
            }
          ]
        }
        """;
    }
}