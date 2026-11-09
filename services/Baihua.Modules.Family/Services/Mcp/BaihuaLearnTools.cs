using System.ComponentModel;
using System.Text.Json;
using Baihua.Contracts.Learn;
using ModelContextProtocol.Server;

namespace Baihua.Modules.Family.Services.Mcp;

/// <summary>
/// 百花 MCP 工具：选择题题库（行业画像 / 生成提示词 / 列举 / 导入 / 导出）。
///
/// 设计意图：生成逻辑交给 Agent（DSH / Claude Code / Cursor 等），百花只保留不随时间变化的
/// 「行业画像 + 结构模板 + 校验 + 存储」。Agent 先查 <c>baihua_learn_industries</c> 选题型，
/// 再用 <c>baihua_learn_prompt_template</c> 拿到模板，自己产出 JSON，
/// 最后经 <c>baihua_learn_import_package</c> 校验落库。
/// </summary>
[McpServerToolType]
public sealed class BaihuaLearnTools
{
    private readonly QuizBankService _quizBanks;
    private static readonly JsonSerializerOptions JsonOpts = JsonSerializerOptions.Web;

    public BaihuaLearnTools(QuizBankService quizBanks)
    {
        _quizBanks = quizBanks;
    }

    [McpServerTool(Name = "baihua_learn_industries"), Description("列出百花题库支持的行业画像（行业代码/名称/说明，以及该行业可选题型 target 的代码、显示名、说明、选项承载对象）。生成题目前先调它确定 industry_code 与 target；未登记的行业也可用，会回退到「通用知识」画像。")]
    public async Task<string> Industries()
    {
        try
        {
            var overviews = await _quizBanks.GetOverviewsAsync();
            var existing = overviews
                .Select(b => (Code: b.IndustryCode, Name: b.IndustryName))
                .Distinct();
            var profiles = QuizIndustryProfileCatalog.MergeWithExisting(existing);
            return JsonSerializer.Serialize(new { ok = true, industries = profiles }, JsonOpts);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "baihua_learn_list"), Description("列出百花题库（行业与题库的代码/名称/条目数/题目数）。生成新题目前先调它，避免 industry_code / bank_code 与既有题库冲突。")]
    public async Task<string> List()
    {
        try
        {
            var banks = await _quizBanks.GetOverviewsAsync();
            var industries = banks
                .GroupBy(b => new { b.IndustryCode, b.IndustryName })
                .Select(g => new
                {
                    code = g.Key.IndustryCode,
                    name = g.Key.IndustryName,
                    bankCount = g.Count(),
                    itemCount = g.Sum(x => x.ItemCount),
                    questionCount = g.Sum(x => x.QuestionCount),
                })
                .ToList();
            return JsonSerializer.Serialize(new { ok = true, industries, banks }, JsonOpts);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "baihua_learn_prompt_template"), Description("生成一份「题库生成提示词模板」（含行业化题型说明、JSON 结构与硬性规则）。把返回的 prompt 交给当前 AI（你自己），按模板产出题库包 JSON，再调用 baihua_learn_import_package 写入百花。参数：industry_code/industry_name（行业代码与名称，如 tcm/中医、driving/驾照、programming/编程；未登记行业自动用通用画像；industry_code 可留空自动生成）、bank_code/bank_name（题库代码与名称；bank_code 可留空自动生成）、topic（生成主题与要求，必填）、target（题型：先经 baihua_learn_industries 查询；留空=该行业第一个题型；mixed=混合；也可传自定义题型代码并配 target_description）、question_count（题量：>0 为固定题量；传 0=自适应，由 AI 按知识点覆盖范围决定 8-30 题）、difficulty（1-5，默认 3）、reference_scope（可选：参考范围，如某本书某篇章或知识库 id）、distractor_strategy（same_scope=同范围干扰项，推荐；random=随机）、code_prefix（题号前缀；留空自动生成唯一前缀）。")]
    public string PromptTemplate(
        string industry_code, string industry_name, string bank_code, string bank_name, string topic,
        string target = "", int question_count = 10, int difficulty = 3,
        string? reference_scope = null, string distractor_strategy = "same_scope",
        string code_prefix = "ai", string? target_description = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(topic))
                return JsonSerializer.Serialize(new { ok = false, error = "topic（生成主题）不能为空" }, JsonOpts);

            // 普通用户/Agent 都无需操心代码：留空时自动生成唯一值；question_count<=0 表示自适应题量
            var isAutoCount = question_count <= 0;
            var effectiveIndustryCode = string.IsNullOrWhiteSpace(industry_code)
                ? $"ind_{Guid.NewGuid().ToString("N")[..6]}" : industry_code;
            var effectiveBankCode = string.IsNullOrWhiteSpace(bank_code)
                ? $"bank_{Guid.NewGuid().ToString("N")[..6]}" : bank_code;
            var effectivePrefix = string.IsNullOrWhiteSpace(code_prefix)
                ? $"ai{Guid.NewGuid().ToString("N")[..6]}" : code_prefix;

            var prompt = QuizBankPromptBuilder.Build(new QuizBankPromptBuilder.Options
            {
                IndustryCode = effectiveIndustryCode,
                IndustryName = industry_name,
                BankCode = effectiveBankCode,
                BankName = bank_name,
                Topic = topic,
                Target = target,
                TargetDescription = target_description,
                QuestionCount = isAutoCount ? 30 : question_count,
                QuestionCountMode = isAutoCount ? "auto" : "fixed",
                Difficulty = difficulty,
                ReferenceScope = reference_scope,
                DistractorStrategy = distractor_strategy,
                CodePrefix = effectivePrefix,
            });
            return JsonSerializer.Serialize(new { ok = true, prompt }, JsonOpts);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "baihua_learn_import_package"), Description("把一份 JSON 题库包写入百花（先整体校验、再落库）。package_json 为完整题库包 JSON 字符串（结构见 baihua_learn_prompt_template）。append_only 默认 true：已存在的条目/题目原样保留、只追加新代码，避免覆盖人工整理的题库；明确要覆盖时传 false。dry_run=true 只校验并返回统计、不落库。校验会按题目所属行业的画像检查题型是否匹配（不匹配只告警，不阻断）。校验有错误时默认拒绝写入（返回 errors/warnings）；force=true 可跳过错误强制写入（危险）。返回校验结果、导入计数与 warnings。")]
    public async Task<string> ImportPackage(
        string package_json, bool append_only = true, bool dry_run = false, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(package_json))
            return JsonSerializer.Serialize(new { ok = false, error = "package_json 为空" }, JsonOpts);

        QuizBankPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<QuizBankPackage>(package_json, JsonOpts);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = $"JSON 解析失败：{ex.Message}" }, JsonOpts);
        }

        if (package is null)
            return JsonSerializer.Serialize(new { ok = false, error = "题库包解析结果为 null" }, JsonOpts);

        try
        {
            var profile = QuizIndustryProfileCatalog.Resolve(package.Industry?.Code, package.Industry?.Name);
            var validation = QuizBankPackageValidator.Validate(package, profile);
            if (!validation.IsValid && !force)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    isValid = false,
                    dryRun = dry_run,
                    validation,
                    error = "题库包校验未通过，未写入。修正 errors 后重试；确需强制写入请传 force=true。",
                }, JsonOpts);
            }

            if (dry_run)
            {
                return JsonSerializer.Serialize(new { ok = true, dryRun = true, validation }, JsonOpts);
            }

            var result = await _quizBanks.ImportPackageAsync(
                package, new QuizBankImportOptions { OverwriteExisting = !append_only });
            return JsonSerializer.Serialize(new { ok = true, dryRun = false, validation, result }, JsonOpts);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "baihua_learn_export_package"), Description("导出百花题库包（完整 JSON，含答案与解析），用于增量修改后重新导入。industry_code 与 bank_code 可先通过 baihua_learn_list 获取。")]
    public async Task<string> ExportPackage(string industry_code, string bank_code)
    {
        try
        {
            var package = await _quizBanks.ExportPackageAsync(industry_code, bank_code);
            return JsonSerializer.Serialize(new { ok = true, package }, JsonOpts);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static string Fail(Exception ex)
        => JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOpts);
}