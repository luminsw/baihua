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
public class QuizBankGeneratorService
{
    /// <summary>单批题量：10 题约 6k tokens 输出，兼顾质量与截断风险</summary>
    private const int BatchSize = 10;

    /// <summary>单批最大输出 tokens</summary>
    private const int MaxOutputTokens = 8000;

    /// <summary>自适应题量（单批 8-30 题）输出上限</summary>
    private const int AutoMaxOutputTokens = 16000;

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

    /// <summary>
    /// 生成题库预览（不落库）。成功时返回题库包与校验结果；解析失败返回 Success=false 与原始响应。
    /// </summary>
    public async Task<QuizGeneratePreviewDto> PreviewAsync(
        QuizGenerateRequestDto request, CancellationToken ct = default)
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
        // 自适应题量只跑一批（AI 一次规划并覆盖）；固定题量按 10 题分批
        var batches = isAutoCount ? [1] : SplitBatches(request.QuestionCount, BatchSize);
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

        try
        {
            for (var i = 0; i < batches.Count; i++)
            {
                var count = batches[i];
                var batchPrefix = batches.Count == 1
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
                    QuestionCount = count,
                    QuestionCountMode = isAutoCount ? "auto" : "fixed",
                    Difficulty = request.Difficulty,
                    ReferenceScope = request.ReferenceScope,
                    DistractorStrategy = request.DistractorStrategy,
                    CodePrefix = batchPrefix,
                    TargetDescription = request.TargetDescription,
                    Profile = profile,
                });
                if (i == 0) promptUsed = prompt;

                var text = await CompleteAsync(provider, model, prompt,
                    isAutoCount ? AutoMaxOutputTokens : MaxOutputTokens, ct);
                raw.AppendLine(text);

                var package = QuizBankResponseParser.Parse(text);
                if (package is null)
                {
                    _logger.LogInformation("题库生成第 {Batch} 批解析失败，追加约束重试一次", i + 1);
                    text = await CompleteAsync(provider, model, prompt + RetrySuffix,
                        isAutoCount ? AutoMaxOutputTokens : MaxOutputTokens, ct);
                    raw.AppendLine(text);
                    package = QuizBankResponseParser.Parse(text);
                }

                if (package is null)
                {
                    // 保留最后一份提示词，供用户改用「粘贴题库包」方式人工修正
                    fallbackPrompt = prompt + RetrySuffix;
                    return Fail(
                        $"AI 返回内容无法解析为题库包（第 {i + 1}/{batches.Count} 批）。已保留原始响应，可改用「粘贴题库包」方式修正后导入。",
                        promptUsed, raw.ToString(), model, batches.Count, fallbackPrompt);
                }

                merged.Items.AddRange(package.Items ?? []);
                merged.Questions.AddRange(package.Questions ?? []);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "题库生成调用 AI 失败（Provider={ProviderId}, Model={Model}）", provider.Id, model);
            return Fail($"AI 生成失败：{ex.Message}", promptUsed, raw.ToString(), model, batches.Count);
        }

        var validation = QuizBankPackageValidator.Validate(merged, profile);
        return new QuizGeneratePreviewDto
        {
            Success = true,
            Prompt = promptUsed,
            Package = merged,
            Validation = validation,
            RawResponse = Truncate(raw.ToString(), 20000),
            ModelUsed = model,
            BatchCount = batches.Count,
        };
    }

    private async Task<string> CompleteAsync(
        AiProviderConfig provider, string model, string prompt, int maxOutputTokens, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
        // 结构化输出求确定性（低温），避免同一参数每次生成风格漂移
        var options = new ChatOptions { MaxOutputTokens = maxOutputTokens, Temperature = 0.3f };
        var response = await _ai.GetChatResponseWithAutoStartAsync(
            provider, model, messages, options, ct, operation: "quiz", useCache: false);
        return response.Text ?? "";
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