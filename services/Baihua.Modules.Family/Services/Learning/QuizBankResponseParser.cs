using System.Text.Json;
using Baihua.Contracts.Learn;

namespace Baihua.Modules.Family.Services;

/// <summary>
/// AI 响应 → 题库包解析器。
///
/// 模型输出常见噪声：Markdown 代码块围栏、前后解释文字、多个 JSON 对象、被截断的 JSON。
/// 本解析器优先做「括号平衡扫描」提取第一个完整 JSON 对象（能正确处理字符串内的花括号/转义），
/// 失败再回退到首个 `{` 到最后一个 `}` 的粗暴截取。
/// </summary>
public static class QuizBankResponseParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>从 AI 原始文本解析题库包；失败返回 null（调用方决定是否重试/展示原文）。</summary>
    public static QuizBankPackage? Parse(string? raw)
    {
        var json = ExtractJson(raw);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<QuizBankPackage>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>提取文本中的第一个完整 JSON 对象；找不到返回 null。</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = StripCodeFence(text.Trim());

        var balanced = ExtractFirstBalancedObject(trimmed);
        if (balanced is not null) return balanced;

        // 回退：首个 { 到最后一个 }（应对括号不平衡但内容仍可解析的情况）
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }

    /// <summary>去掉 Markdown 代码块围栏（```json ... ```）</summary>
    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return text;

        var body = text[(firstNewline + 1)..];
        var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
        if (fenceEnd >= 0) body = body[..fenceEnd];
        return body.Trim();
    }

    /// <summary>扫描第一个括号平衡的 JSON 对象（正确处理字符串与转义）</summary>
    private static string? ExtractFirstBalancedObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }

        return null;
    }
}