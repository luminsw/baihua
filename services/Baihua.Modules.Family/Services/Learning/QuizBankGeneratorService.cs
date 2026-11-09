using System.Text;
using Microsoft.Extensions.AI;
using Baihua.Contracts.Learn;
using Baihua.Core.Models;
using Baihua.Core.Services;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// 题库生成服务（UI 生成页后端）：复用 <see cref="QuizBankPromptBuilder"/> 产出的模板，
/// 调用户已配置的 AI provider，解析 JSON 并整体校验，**不落库**——由用户在预览页确认后另行导入。
///
/// 设计要点：
/// - 行业无关：不含任何中医语义；
/// - 分批：单批 10 题，避免长输出被截断；每批使用不同题号前缀（ai_b1/ai_b2…）防止 code 冲突；
/// - 解析失败重试一次（追加「只输出 JSON」约束），仍失败则保留原始响应供人工修正；
/// - 生成结果必须经 <see cref="QuizBankPackageValidator"/> 校验，Errors 由预览页展示并阻止导入。
/// </summary>
public class QuizBankGeneratorService : IQuizBankGenerator
{
    /// <summary>单批题量：10 题约 6k tokens 输出，兼顾质量与截断风险</summary>
    public const int BatchSize = 10;

    /// <summary>
    /// 单批最大输出 tokens（每批最多 10 题，约 6-8k 输出）。
    /// 自适应分批后每批只有 6 题，同样够用——统一上限避免无谓的超长输出配额。
    /// </summary>
    private const int MaxOutputTokens = 8000;

    /// <summary>自适应模式回退时的总题量上限（规划失败按 10 题/批分 3 批）</summary>
    private const int AutoMaxQuestions = 30;

    /// <summary>知识点规划阶段最大输出 tokens</summary>
    private const int PlanMaxOutputTokens = 2000;

    /// <summary>知识点规划阶段超时（分钟）</summary>
    private const int PlanTimeoutMinutes = 3;

    /// <summary>最多采纳的知识点数（控制总题量与批次数）</summary>
    private const int MaxPlanTopics = 15;

    /// <summary>每批覆盖的知识点数</summary>
    private const int TopicsPerBatch = 3;

    /// <summary>按知识点分批时每批题量</summary>
    private const int QuestionsPerBatch = 6;

    /// <summary>固定题量单批 AI 调用硬超时（分钟）</summary>
    private const int BatchTimeoutMinutes = 4;

    /// <summary>解析失败时的重试约束</summary>
    private const string RetrySuffix =
        "\n\n上一次输出无法解析为 JSON。请严格只输出一个 JSON 对象：不要 Markdown 代码块，不要前后解释文字，不要省略或截断任何字段。";

    private readonly AiClientService _ai;
    private readonly AiSettingsService _aiSettings;
    private readonly ILogger<QuizBankGeneratorService> _logger;

    public QuizBankGeneratorService(
        AiClientService ai, AiSettingsService aiSettings, ILogger<QuizBankGeneratorService> logger)
    {
        _ai = ai;
        _aiSettings = aiSettings;
        _logger = logger;
    }

    public Task<QuizGeneratePreviewDto> PreviewAsync(
        QuizGenerateRequestDto request, CancellationToken ct = default)
        => PreviewAsync(request, null, ct);

    /// <summary>
    /// 生成题库预览（不落库）。成功时返回题库包与校验结果；解析失败返回 Success=false 与原始响应。
    /// <paramref name="progress"/> 每批完成时上报，供后台任务展示进度。
    /// </summary>
    public async Task<QuizGeneratePreviewDto> PreviewAsync(
        QuizGenerateRequestDto request, IProgress<QuizGenerateProgress>? progress, CancellationToken ct = default)
    {
        var topic = request.Topic?.Trim() ?? "";
        if (topic.Length == 0) return Fail("生成主题不能为空");

        var isAutoCount = string.Equals(request.QuestionCountMode, "auto", StringComparison.OrdinalIgnoreCase);
        if (!isAutoCount && request.QuestionCount is < 1 or > 30) return Fail("题量须在 1-30 之间");

        // 普通用户不填代码：留空时自动生成唯一值（行业通常复用画像 code）
        var industryCode = EnsureCode(request.IndustryCode, "ind");
        var bankCode = EnsureCode(request.BankCode, "bank");
        var codePrefix = EnsureCode(request.CodePrefix, "ai");

        var provider = string.IsNullOrWhiteSpace(request.ProviderId)
            ? _aiSettings.GetMainAiProvider()
            : _aiSettings.GetAiProvider(request.ProviderId);
        if (provider is null) return Fail("未配置 AI 模型，请先在 AI 设置中配置主模型");

        var model = string.IsNullOrWhiteSpace(request.Model) ? provider.GetMainModel() : request.Model.Trim();
        if (string.IsNullOrWhiteSpace(model)) return Fail("所选 AI 提供商未配置模型");

        var profile = QuizIndustryProfileCatalog.Resolve(industryCode, request.IndustryName);
        var merged = new QuizBankPackage
        {
            FormatVersion = 1,
            Industry = new QuizIndustryInfo
            {
                Code = industryCode,
                Name = string.IsNullOrWhiteSpace(request.IndustryName) ? industryCode : request.IndustryName.Trim(),
            },
            Bank = new QuizBankInfo
            {
                Code = bankCode,
                Name = string.IsNullOrWhiteSpace(request.BankName) ? bankCode : request.BankName.Trim(),
                Version = "v1",
                Description = $"AI 生成：{topic}",
            },
        };

        var promptUsed = "";
        var fallbackPrompt = "";
        var raw = new StringBuilder();
        var totalBatches = 1;
        var duplicateWarnings = new List<string>();

        try
        {
            // 自适应：先规划知识点（数量决定总题量），再按知识点分批出题；固定题量直接按 10 题分批
            List<BatchSpec> batchSpecs;
            if (isAutoCount)
            {
                // 规划阶段最长 3 分钟，期间必须给用户可见进度，否则 UI 一直停在 0%
                progress?.Report(new QuizGenerateProgress(0, 1, "正在规划知识点…"));

                var planPrompt = QuizBankPromptBuilder.BuildPlanPrompt(
                    string.IsNullOrWhiteSpace(request.IndustryName) ? industryCode : request.IndustryName.Trim(),
                    topic, request.ReferenceScope);
                var planText = await CompleteAsync(provider, model, planPrompt,
                    PlanMaxOutputTokens, PlanTimeoutMinutes, ct);
                raw.AppendLine(planText);

                var plan = QuizTopicPlanParser.Parse(planText);
                var topics = plan is { Topics.Count: > 0 }
                    ? plan.Topics.Take(MaxPlanTopics).ToList()
                    : null;

                if (topics is null)
                {
                    _logger.LogInformation("知识点规划解析失败，回退到 3 批 × 10 题");
                    batchSpecs = SplitBatches(AutoMaxQuestions, BatchSize)
                        .Select(n => new BatchSpec(n, null, "auto"))
                        .ToList();
                }
                else
                {
                    batchSpecs = Chunk(topics, TopicsPerBatch)
                        .Select(g => new BatchSpec(QuestionsPerBatch, g, "fixed"))
                        .ToList();
                }
            }
            else
            {
                batchSpecs = SplitBatches(request.QuestionCount, BatchSize)
                    .Select(n => new BatchSpec(n, null, "fixed"))
                    .ToList();
            }

            totalBatches = batchSpecs.Count;

            for (var i = 0; i < batchSpecs.Count; i++)
            {
                var spec = batchSpecs[i];
                var batchPrefix = batchSpecs.Count == 1
                    ? codePrefix
                    : $"{codePrefix}_b{i + 1}";

                var prompt = QuizBankPromptBuilder.Build(new QuizBankPromptBuilder.Options
                {
                    IndustryCode = industryCode,
                    IndustryName = request.IndustryName,
                    BankCode = bankCode,
                    BankName = request.BankName,
                    Topic = topic,
                    Target = request.Target,
                    QuestionCount = spec.Count,
                    QuestionCountMode = spec.CountMode,
                    Difficulty = request.Difficulty,
                    ReferenceScope = request.ReferenceScope,
                    DistractorStrategy = request.DistractorStrategy,
                    CodePrefix = batchPrefix,
                    TargetDescription = request.TargetDescription,
                    BatchIndex = i + 1,
                    BatchCount = batchSpecs.Count,
                    Topics = spec.Topics,
                    Profile = profile,
                });
                if (i == 0) promptUsed = prompt;

                var text = await CompleteAsync(provider, model, prompt,
                    MaxOutputTokens,
                    BatchTimeoutMinutes, ct);
                raw.AppendLine(text);

                var package = QuizBankResponseParser.Parse(text);
                if (package is null)
                {
                    _logger.LogInformation("题库生成第 {Batch} 批解析失败，追加约束重试一次", i + 1);
                    text = await CompleteAsync(provider, model, prompt + RetrySuffix,
                        MaxOutputTokens,
                        BatchTimeoutMinutes, ct);
                    raw.AppendLine(text);
                    package = QuizBankResponseParser.Parse(text);
                }

                if (package is null)
                {
                    // 保留最后一份提示词，供用户改用「粘贴题库包」方式人工修正
                    fallbackPrompt = prompt + RetrySuffix;
                    return Fail(
                        $"AI 返回内容无法解析为题库包（第 {i + 1}/{totalBatches} 批）。已保留原始响应，可改用「粘贴题库包」方式修正后导入。",
                        promptUsed, raw.ToString(), model, totalBatches, fallbackPrompt);
                }

                var skipped = MergeBatch(merged, package);
                if (skipped > 0)
                {
                    _logger.LogWarning("第 {Batch} 批有 {Count} 条代码与已生成内容重复，已丢弃", i + 1, skipped);
                    duplicateWarnings.Add($"第 {i + 1} 批有 {skipped} 条代码与已生成内容重复，已自动丢弃");
                }

                progress?.Report(new QuizGenerateProgress(
                    i + 1, totalBatches, $"已完成第 {i + 1}/{totalBatches} 批"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "题库生成单批超时");
            return Fail(
                $"{ex.Message}。请减少题量、改用固定题量分批生成，或稍后重试。",
                promptUsed, raw.ToString(), model, totalBatches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "题库生成调用 AI 失败（Provider={ProviderId}, Model={Model}）", provider.Id, model);
            return Fail($"AI 生成失败：{ex.Message}", promptUsed, raw.ToString(), model, totalBatches);
        }

        var validation = QuizBankPackageValidator.Validate(merged, profile);
        validation.Warnings.AddRange(duplicateWarnings);
        return new QuizGeneratePreviewDto
        {
            Success = true,
            Prompt = promptUsed,
            Package = merged,
            Validation = validation,
            RawResponse = Truncate(raw.ToString(), 20000),
            ModelUsed = model,
            BatchCount = totalBatches,
        };
    }

    private async Task<string> CompleteAsync(
        AiProviderConfig provider, string model, string prompt, int maxOutputTokens,
        int timeoutMinutes, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
        // 结构化输出求确定性（低温），避免同一参数每次生成风格漂移
        var options = new ChatOptions { MaxOutputTokens = maxOutputTokens, Temperature = 0.3f };

        // 单批硬超时：provider 不返回时 HTTP 长连接可能无限等待（曾出现任务永久 running）
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        try
        {
            var responseTask = _ai.GetChatResponseWithAutoStartAsync(
                provider, model, messages, options, batchCts.Token, operation: "quiz", useCache: false);
            // 硬超时兜底：shim/底层 SDK 在部分链路不响应取消令牌，单靠 CancelAfter 可能一直挂
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(timeoutMinutes), CancellationToken.None);
            var winner = await Task.WhenAny(responseTask, timeoutTask);

            if (winner != responseTask)
            {
                batchCts.Cancel();
                _ = responseTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"单批生成超过 {timeoutMinutes} 分钟未返回");
            }

            var response = await responseTask;
            return response.Text ?? "";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"单批生成超过 {timeoutMinutes} 分钟未返回");
        }
    }

    /// <summary>单个生成批次：题量 + （自适应两阶段时）本批覆盖的知识点 + 题量模式</summary>
    private sealed record BatchSpec(int Count, List<QuizTopicDto>? Topics, string CountMode);

    /// <summary>
    /// 把一批生成结果并入总包：按 code 去重，只保留第一次出现的条目/题目。
    /// <para>每批都要求不同的代码前缀，但 AI 偶尔会忽略前缀换批重复同一题号；
    /// 直接 AddRange 会产生重复题号，导入时后者被 append 模式跳过、overwrite 模式静默覆盖，
    /// 用户看到的是「题少了」却没有任何提示。</para>
    /// </summary>
    /// <returns>被丢弃的重复条数（条目 + 题目）</returns>
    internal static int MergeBatch(QuizBankPackage merged, QuizBankPackage batch)
    {
        var skipped = 0;

        var itemCodes = merged.Items.Select(i => i.ItemCode).ToHashSet(StringComparer.Ordinal);
        foreach (var item in batch.Items ?? [])
        {
            if (!string.IsNullOrWhiteSpace(item.ItemCode) && !itemCodes.Add(item.ItemCode))
            {
                skipped++;
                continue;
            }
            merged.Items.Add(item);
        }

        var questionCodes = merged.Questions.Select(q => q.QuestionCode).ToHashSet(StringComparer.Ordinal);
        foreach (var question in batch.Questions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(question.QuestionCode) && !questionCodes.Add(question.QuestionCode))
            {
                skipped++;
                continue;
            }
            merged.Questions.Add(question);
        }

        return skipped;
    }

    /// <summary>按 size 分组（用于把知识点清单切成每批可覆盖的组）</summary>
    private static List<List<QuizTopicDto>> Chunk(List<QuizTopicDto> topics, int size)
    {
        var groups = new List<List<QuizTopicDto>>();
        for (var i = 0; i < topics.Count; i += size)
        {
            groups.Add(topics.GetRange(i, Math.Min(size, topics.Count - i)));
        }
        return groups;
    }

    /// <summary>把总题量拆成每批不超过 <paramref name="size"/> 的批次（公开以便回归测试与复用）</summary>
    public static List<int> SplitBatches(int total, int size)
    {
        var list = new List<int>();
        var remaining = Math.Max(total, 0);
        while (remaining > 0)
        {
            var n = Math.Min(size, remaining);
            list.Add(n);
            remaining -= n;
        }
        return list;
    }

    /// <summary>普通用户无需关心代码：留空时自动生成唯一代码（前缀 + 6 位随机）</summary>
    public static string EnsureCode(string? value, string prefix)
        => string.IsNullOrWhiteSpace(value)
            ? $"{prefix}_{Guid.NewGuid().ToString("N")[..6]}"
            : value.Trim();

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    private static QuizGeneratePreviewDto Fail(
        string error, string prompt = "", string? raw = null, string? model = null,
        int batchCount = 0, string? promptFallback = null)
        => new()
        {
            Success = false,
            Error = error,
            Prompt = promptFallback is null ? prompt : (prompt.Length > 0 ? prompt : promptFallback),
            RawResponse = raw is null ? null : Truncate(raw, 20000),
            ModelUsed = model,
            BatchCount = batchCount,
        };
}