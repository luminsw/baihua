using Baihua.Contracts.Learn;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 题库包校验器：落库前对题库包做整体体检（AI 生成 / Agent 写入 / 手工导入共用）。
///
/// 与 <see cref="QuizBankService.ImportPackageAsync(QuizBankPackage, QuizBankImportOptions, System.Threading.CancellationToken)"/>
/// 的逐条 skip 不同，本校验器把问题分成：
/// - Errors：结构或关键字段缺陷，严格模式下整体拒绝落库；
/// - Warnings：可容忍但影响质量（缺区分点、选项数不足 4、target 与行业画像不符等）。
///
/// 行业无关：target 的合法性由传入的 <see cref="QuizIndustryProfileDto"/> 决定；
/// 不传画像时只校验非空，不假定任何行业专用取值。
/// </summary>
public static class QuizBankPackageValidator
{
    /// <summary>建议的单题选项数</summary>
    private const int RecommendedOptionCount = 4;

    public static QuizBankValidationResultDto Validate(
        QuizBankPackage? package, QuizIndustryProfileDto? profile = null)
    {
        var result = new QuizBankValidationResultDto();
        if (package is null)
        {
            result.Errors.Add("题库包为空（JSON 解析结果为 null）");
            return result;
        }

        var industryCode = package.Industry?.Code?.Trim() ?? "";
        var industryName = package.Industry?.Name?.Trim() ?? "";
        var bankCode = package.Bank?.Code?.Trim() ?? "";
        var bankName = package.Bank?.Name?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(industryCode)) result.Errors.Add("缺少行业代码 industry.code");
        if (string.IsNullOrWhiteSpace(industryName)) result.Errors.Add("缺少行业名称 industry.name");
        if (string.IsNullOrWhiteSpace(bankCode)) result.Errors.Add("缺少题库代码 bank.code");
        if (string.IsNullOrWhiteSpace(bankName)) result.Errors.Add("缺少题库名称 bank.name");

        var items = package.Items ?? [];
        var questions = package.Questions ?? [];
        result.ItemCount = items.Count;
        result.QuestionCount = questions.Count;

        if (items.Count == 0 && questions.Count == 0)
        {
            result.Errors.Add("题库包为空：items 与 questions 均为空");
        }
        else if (items.Count == 0)
        {
            result.Warnings.Add("items 为空：题目必须引用库内已有条目，否则无法按条目关联");
        }

        ValidateItems(items, result);
        ValidateQuestions(questions, profile, result);

        return result;
    }

    private static void ValidateItems(List<QuizItemDto> items, QuizBankValidationResultDto result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var label = $"items[{i}]";

            if (item is null)
            {
                result.Errors.Add($"{label} 为空");
                continue;
            }

            var code = item.ItemCode?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                result.Errors.Add($"{label} 缺少 itemCode");
            }
            else
            {
                result.ItemCodes.Add(code);
                if (!seen.Add(code)) result.Errors.Add($"itemCode 重复：{code}");
            }

            if (string.IsNullOrWhiteSpace(item.Name)) result.Errors.Add($"{label}（{code}）缺少 name");
            if (string.IsNullOrWhiteSpace(item.ItemType)) result.Warnings.Add($"{label}（{code}）缺少 itemType，将按 concept 处理");
            if (item.Content is null) result.Warnings.Add($"{label}（{code}）缺少 content，题库将只有题目没有知识条目内容");
            else if (item.Content.Composition is { Count: 0 }) result.Warnings.Add($"{label}（{code}）content.composition 为空数组");
        }
    }

    private static void ValidateQuestions(
        List<QuizQuestionDto> questions, QuizIndustryProfileDto? profile, QuizBankValidationResultDto result)
    {
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var knownTargets = profile?.Targets
            .Select(t => t.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var label = $"questions[{i}]";

            if (q is null)
            {
                result.Errors.Add($"{label} 为空");
                continue;
            }

            var code = q.QuestionCode?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                result.Errors.Add($"{label} 缺少 questionCode");
            }
            else
            {
                result.QuestionCodes.Add(code);
                if (!seenCodes.Add(code)) result.Errors.Add($"questionCode 重复：{code}");
            }

            if (string.IsNullOrWhiteSpace(q.Stem)) result.Errors.Add($"{label}（{code}）缺少 stem（题干）");
            if (string.IsNullOrWhiteSpace(q.Analysis)) result.Errors.Add($"{label}（{code}）缺少 analysis（解析）");

            if (string.IsNullOrWhiteSpace(q.Target))
            {
                result.Errors.Add($"{label}（{code}）缺少 target");
            }
            else
            {
                var target = q.Target.Trim();
                result.TargetCounts[target] = result.TargetCounts.GetValueOrDefault(target) + 1;

                // 仅当传入行业画像且题型既非画像内、也非 mixed 时提示（自定义题型是允许的）
                if (knownTargets is { Count: > 0 }
                    && !knownTargets.Contains(target)
                    && !string.Equals(target, "mixed", StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add($"{label}（{code}）target 不在行业画像内：{target}（可视为自定义题型）");
                }
            }

            if (!string.IsNullOrWhiteSpace(q.QuestionType) &&
                !string.Equals(q.QuestionType, "single_choice", StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"{label}（{code}）questionType 非 single_choice：{q.QuestionType}（当前仅支持单选）");
            }

            if (q.Difficulty is < 1 or > 5)
                result.Warnings.Add($"{label}（{code}）difficulty 超出 1-5：{q.Difficulty}");

            if (q.ItemCode is not null && string.IsNullOrWhiteSpace(q.ItemCode.Trim()))
                result.Warnings.Add($"{label}（{code}）itemCode 为空白字符串，将按未关联处理");

            if (q.Discrimination is null || q.Discrimination.Count == 0)
                result.Warnings.Add($"{label}（{code}）缺少 discrimination 区分点，教学价值降低");

            ValidateOptions(q, label, code, result);
        }
    }

    private static void ValidateOptions(
        QuizQuestionDto q, string label, string code, QuizBankValidationResultDto result)
    {
        var options = q.Options ?? [];
        var display = string.IsNullOrWhiteSpace(code) ? label : $"{label}（{code}）";

        if (options.Count < 2)
        {
            result.Errors.Add($"{display} 选项不足 2 个（当前 {options.Count}）");
            return;
        }

        if (options.Count != RecommendedOptionCount)
            result.Warnings.Add($"{display} 选项数为 {options.Count}，建议 {RecommendedOptionCount} 个");

        var correctCount = options.Count(o => o is { IsCorrect: true });
        if (correctCount != 1)
            result.Errors.Add($"{display} 正确项数量为 {correctCount}，必须恰好 1 个");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contents = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < options.Count; j++)
        {
            var o = options[j];
            if (o is null)
            {
                result.Errors.Add($"{display} 的选项[{j}] 为空");
                continue;
            }

            var key = o.Key?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key)) result.Errors.Add($"{display} 的选项[{j}] 缺少 key");
            else if (!keys.Add(key)) result.Errors.Add($"{display} 选项 key 重复：{key}");

            var content = o.Content?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(content)) result.Errors.Add($"{display} 的选项[{j}]（{key}）缺少 content");
            else if (!contents.Add(content)) result.Errors.Add($"{display} 选项内容重复：{content}");

            if (string.IsNullOrWhiteSpace(o.Explanation))
                result.Warnings.Add($"{display} 选项[{j}]（{key}）缺少 explanation");
        }
    }
}