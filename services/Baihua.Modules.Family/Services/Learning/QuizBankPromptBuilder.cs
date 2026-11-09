using System.Text;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 题库生成提示词构建器：把「行业 + 题库 + 主题 + 约束」展开成一份可直接交给 AI 的完整提示词，
/// 其中内嵌 <see cref="Baihua.Contracts.Learn.QuizBankPackage"/> 的 JSON 结构与硬性规则。
///
/// 用途：
/// - MCP 工具 <c>baihua_learn_prompt_template</c> 返回给 Agent，Agent 按模板产出 JSON 后调导入工具；
/// - UI 生成页（阶段二）复用同一份模板，保证两个入口产出的结构完全一致。
///
/// 行业无关：模板中的行业/题型/代码前缀均为参数，不硬编码任何中医语义。
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
        /// <summary>formula / pattern / mixed</summary>
        public string Target { get; set; } = "formula";
        public int QuestionCount { get; set; } = 10;
        public int Difficulty { get; set; } = 3;
        /// <summary>参考范围（书名篇章 / 知识库 id / 目录），可空</summary>
        public string? ReferenceScope { get; set; }
        /// <summary>same_scope / random</summary>
        public string DistractorStrategy { get; set; } = "same_scope";
        /// <summary>代码前缀，用于避免与既有题号冲突</summary>
        public string CodePrefix { get; set; } = "ai";
    }

    public static string Build(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var industryCode = Normalize(options.IndustryCode, "industry");
        var industryName = Normalize(options.IndustryName, industryCode);
        var bankCode = Normalize(options.BankCode, "bank");
        var bankName = Normalize(options.BankName, bankCode);
        var topic = string.IsNullOrWhiteSpace(options.Topic) ? "（未指定，按该行业的基础知识生成）" : options.Topic.Trim();
        var codePrefix = SanitizeCode(options.CodePrefix, "ai");
        var questionCount = Math.Clamp(options.QuestionCount, 1, 50);
        var difficulty = Math.Clamp(options.Difficulty, 1, 5);
        var target = NormalizeTarget(options.Target);

        var referenceBlock = string.IsNullOrWhiteSpace(options.ReferenceScope)
            ? "- 参考范围：未指定（必须基于该行业公认的基础知识，不得编造）"
            : $"- 参考范围：{options.ReferenceScope.Trim()}（内容必须限定在此范围内，范围外内容不要生成）";

        var sb = new StringBuilder();
        sb.AppendLine("你是「百花题库」的内容生成助手。请按下面的规范生成一个可以直接导入的单选题题库包（JSON）。");
        sb.AppendLine();
        sb.AppendLine("## 生成任务");
        sb.AppendLine($"- 行业：{industryName}（代码 {industryCode}）");
        sb.AppendLine($"- 题库：{bankName}（代码 {bankCode}）");
        sb.AppendLine($"- 主题/要求：{topic}");
        sb.AppendLine($"- 题型：{DescribeTarget(target)}");
        sb.AppendLine($"- 题目数量：{questionCount}");
        sb.AppendLine($"- 难度：{difficulty}（1 最易，5 最难）");
        sb.AppendLine($"- 干扰项来源：{DescribeDistractor(options.DistractorStrategy)}");
        sb.AppendLine(referenceBlock);
        sb.AppendLine();
        sb.AppendLine("## 输出格式（严格 JSON，单个对象；不要 Markdown 代码块，不要任何解释文字）");
        sb.AppendLine(SchemaJson);
        sb.AppendLine();
        sb.AppendLine("## 硬性规则");
        sb.AppendLine($"1. 只输出一个 JSON 对象；第一个字符是 `{{`，最后一个字符是 `}}`，前后不得有任何文字或代码块标记。");
        sb.AppendLine($"2. 条目代码必须为 `{codePrefix}_item_1`、`{codePrefix}_item_2` ……；题目代码必须为 `{codePrefix}_q_1`、`{codePrefix}_q_2` ……；同一包内全部唯一。");
        sb.AppendLine("3. items 与 questions 一一对应：每个 item 至少一道题；itemCode 引用必须指向本包 items 中的条目（或库内已有条目）。");
        sb.AppendLine($"4. 每题恰好 {RecommendedOptionCount} 个选项，key 依次为 A/B/C/D；`isCorrect` 为 true 的恰好一个。");
        sb.AppendLine("5. 干扰项必须是同一知识范围内真实存在的其他对象（易混淆），不得使用明显离谱的假名或重复内容。");
        sb.AppendLine("6. 每个选项都要有 explanation：正确项写「选它的理由」，干扰项写「不选它的理由（与正确项的关键差异）」。");
        sb.AppendLine("7. analysis 必填：说明正确答案并给出简明推理。");
        sb.AppendLine("8. discrimination 至少一条：写出本题正确项与主要干扰项的关键区分点。");
        sb.AppendLine("9. stem 必须自足：只看题干（含 stemDetail）就能判断出唯一答案，不得依赖未提供的外部信息。");
        sb.AppendLine("10. 不得编造不存在的出处、条文或数据；sourceRef 不确定时留空字符串。");
        sb.AppendLine("11. difficulty 必须是 1-5 的整数；questionType 固定为 single_choice。");
        sb.AppendLine("12. 所有文本使用简体中文；专有名词保持行业通用写法。");
        sb.AppendLine($"13. {DescribeTargetRule(target)}");
        sb.AppendLine();
        sb.AppendLine("## 结构示例（仅示意字段与层级，内容请按主题替换）");
        sb.AppendLine(ExampleJson);
        return sb.ToString();
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string SanitizeCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            .ToArray();
        return chars.Length == 0 ? fallback : new string(chars);
    }

    private static string NormalizeTarget(string? target)
    {
        var t = (target ?? "").Trim().ToLowerInvariant();
        return t is "formula" or "pattern" or "mixed" ? t : "formula";
    }

    private static string DescribeTarget(string target) => target switch
    {
        "pattern" => "选证型/病机（给出病例或情境，问对应的证型/病机是哪一个）",
        "mixed" => "混合（约一半选方案，一半选证型/病机，逐题在 target 中标注）",
        _ => "选方案（给出病例或情境，问最合适的方剂/方案是哪一个）",
    };

    private static string DescribeTargetRule(string target) => target switch
    {
        "pattern" => "target=pattern 时，options 的 content 必须是证型名/病机名，不要写方剂名或方案名。",
        "mixed" => "mixed 模式下逐题设置 target；同一题的 options 必须同类型（全是方案名或全是证型名）。",
        _ => "target=formula 时，options 的 content 必须是方剂名/方案名，不要写证型名。",
    };

    private static string DescribeDistractor(string? strategy)
        => string.Equals(strategy, "random", StringComparison.OrdinalIgnoreCase)
            ? "随机选取的其他对象（仍需写明不选的理由）"
            : "同范围内真实存在的其他对象（易混淆，需写出不选它的理由）";

    private const int RecommendedOptionCount = 4;

    private const string SchemaJson = """
    {
      "formatVersion": 1,
      "industry": { "code": "行业代码", "name": "行业名称" },
      "bank": { "code": "题库代码", "name": "题库名称", "version": "v1", "description": "题库说明" },
      "items": [
        {
          "itemCode": "前缀_item_1",
          "itemType": "formula | pattern | concept",
          "name": "条目名称",
          "summary": "一句话摘要",
          "content": {
            "source": "出处（不确定留空）",
            "composition": [ { "name": "组成项", "ancientDose": "原始量", "modernDose": "现代量", "role": "角色" } ],
            "decoction": "制法/用法说明",
            "pathogenesis": "病机/原理",
            "treatment": "治法/原则",
            "indications": [ "适用情形" ],
            "contraindications": [ "禁忌" ],
            "discriminations": [ { "versus": "对照对象", "point": "关键区分点" } ],
            "notes": "补充说明"
          }
        }
      ],
      "questions": [
        {
          "questionCode": "前缀_q_1",
          "itemCode": "前缀_item_1",
          "questionType": "single_choice",
          "target": "formula | pattern",
          "stem": "完整题干",
          "stemDetail": { "patient": "对象概况（可空）", "signs": [ "关键条件/症状/特征" ] },
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

    private const string ExampleJson = """
    {
      "formatVersion": 1,
      "industry": { "code": "demo", "name": "示例行业" },
      "bank": { "code": "demo_bank", "name": "示例题库", "version": "v1", "description": "结构示意" },
      "items": [
        {
          "itemCode": "ai_item_1",
          "itemType": "concept",
          "name": "示例条目",
          "summary": "一句话摘要",
          "content": {
            "source": "",
            "indications": [ "适用情形一" ],
            "discriminations": [ { "versus": "易混条目", "point": "区分点" } ]
          }
        }
      ],
      "questions": [
        {
          "questionCode": "ai_q_1",
          "itemCode": "ai_item_1",
          "questionType": "single_choice",
          "target": "formula",
          "stem": "根据下列条件，最合适的选择是？",
          "stemDetail": { "patient": "对象概况", "signs": [ "条件一", "条件二" ] },
          "options": [
            { "key": "A", "content": "易混对象甲", "isCorrect": false, "explanation": "与条件二不符" },
            { "key": "B", "content": "正确对象", "isCorrect": true, "explanation": "同时满足条件一与条件二" },
            { "key": "C", "content": "易混对象乙", "isCorrect": false, "explanation": "缺少条件一" },
            { "key": "D", "content": "易混对象丙", "isCorrect": false, "explanation": "作用方向相反" }
          ],
          "analysis": "条件一与条件二同时指向正确对象，故选 B。",
          "discrimination": [ { "versus": "易混对象甲", "point": "条件二是否具备" } ],
          "difficulty": 3,
          "sourceRef": ""
        }
      ]
    }
    """;
}