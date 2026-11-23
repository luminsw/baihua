using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;

namespace Baihua.Core.Services;

/// <summary>
/// Anthropic API 客户端（HTTP 直调，不依赖第三方 SDK）
/// 用于 OpenAI 兼容端点返回为空时的 fallback。
/// </summary>
public class AnthropicAiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnthropicAiClient> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public AnthropicAiClient(HttpClient httpClient, ILogger<AnthropicAiClient> logger, IStringLocalizer<SharedResources> loc)
    {
        _httpClient = httpClient;
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 使用 Anthropic Messages API 获取聊天响应
    /// </summary>
    public async Task<ChatResponse> GetChatResponseAsync(
        string baseUrl, string apiKey, string model,
        IList<ChatMessage> messages, ChatOptions options, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/v1/messages";
        var requestBody = BuildRequestBody(model, messages, options);
        var json = JsonSerializer.Serialize(requestBody, AnthropicJsonContext.Default.AnthropicRequest);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(request, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Anthropic API 请求失败: {StatusCode} {Body}", (int)response.StatusCode, responseJson);
            throw new Exception(_loc["Anthropic_ApiError", responseJson]);
        }

        var anthropicResponse = JsonSerializer.Deserialize(responseJson, AnthropicJsonContext.Default.AnthropicResponse)
            ?? throw new Exception(_loc["Anthropic_ParseFailed"]);

        var text = anthropicResponse.Content?.FirstOrDefault(c => c.Type == "text")?.Text ?? "";
        var chatMessage = new ChatMessage(ChatRole.Assistant, text);

        var usage = new UsageDetails
        {
            InputTokenCount = anthropicResponse.Usage?.InputTokens,
            OutputTokenCount = anthropicResponse.Usage?.OutputTokens,
            TotalTokenCount = (anthropicResponse.Usage?.InputTokens ?? 0) + (anthropicResponse.Usage?.OutputTokens ?? 0)
        };

        return new ChatResponse(chatMessage)
        {
            Usage = usage
        };
    }

    /// <summary>
    /// 获取流式响应（将 Anthropic 的 streaming 转换为 IAsyncEnumerable）
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        string baseUrl, string apiKey, string model,
        IList<ChatMessage> messages, ChatOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/v1/messages";
        var requestBody = BuildRequestBody(model, messages, options);
        requestBody.Stream = true;
        var json = JsonSerializer.Serialize(requestBody, AnthropicJsonContext.Default.AnthropicRequest);

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Anthropic streaming API 请求失败: {StatusCode} {Body}", (int)response.StatusCode, errorBody);
            throw new Exception(_loc["Anthropic_ApiError", errorBody]);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") yield break;

            var text = TryParseStreamDelta(data);
            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(text);
                yield return new ChatResponseUpdate(ChatRole.Assistant, text);
            }
        }

        // 最后输出完整文本（作为 fallback）
        var fullText = sb.ToString();
        if (!string.IsNullOrEmpty(fullText))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, fullText);
        }
    }

    private static string? TryParseStreamDelta(string data)
    {
        try
        {
            var chunk = JsonSerializer.Deserialize(data, AnthropicJsonContext.Default.AnthropicStreamEvent);
            if (chunk?.Type == "content_block_delta" && chunk.Delta?.Type == "text_delta")
            {
                return chunk.Delta.Text;
            }
        }
        catch (JsonException)
        {
            // 忽略无法解析的行
        }
        return null;
    }

    private static AnthropicRequest BuildRequestBody(string model, IList<ChatMessage> messages, ChatOptions options)
    {
        var anthropicMessages = new List<AnthropicMessage>();
        string? systemPrompt = null;

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
            {
                systemPrompt = string.Join("", msg.Contents.OfType<TextContent>().Select(t => t.Text));
                continue;
            }

            var role = msg.Role == ChatRole.Assistant ? "assistant" : "user";
            var text = string.Join("", msg.Contents.OfType<TextContent>().Select(t => t.Text));
            anthropicMessages.Add(new AnthropicMessage { Role = role, Content = text });
        }

        return new AnthropicRequest
        {
            Model = model,
            MaxTokens = options.MaxOutputTokens ?? 4096,
            Temperature = options.Temperature,
            TopP = options.TopP,
            System = systemPrompt,
            Messages = anthropicMessages
        };
    }
}
