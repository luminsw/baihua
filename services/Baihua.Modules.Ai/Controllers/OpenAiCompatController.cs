using System.Text.Json;
using System.Text.Json.Serialization;
using Baihua.Core.Models;
using Baihua.Core.Security;
using Baihua.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace Baihua.Modules.Ai.Controllers;

/// <summary>
/// OpenAI 兼容推理端点（/mg/ai/v1/*）。
/// 供局域网内其他百花服务器的算力池自动注册为本机 AI 提供方后跨机调用：
/// 本机 AiClientService 用 OpenAI 协议请求 /mg/ai/v1/chat/completions，
/// 本端点按模型名路由到本机配置的 AI 提供方（含本地 Ollama/llama.cpp/OpenVINO）。
/// 鉴权：Authorization: Bearer 与 BAIHUA_AI_EXTERNAL_TOKEN 比对（未配置则局域网内免鉴权）。
/// </summary>
[ApiController]
[Route("mg/ai/v1")]
public class OpenAiCompatController : ControllerBase
{
    private readonly AiSettingsService _aiSettings;
    private readonly AiClientService _aiClientService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiCompatController> _logger;

    public OpenAiCompatController(
        AiSettingsService aiSettings,
        AiClientService aiClientService,
        IConfiguration configuration,
        ILogger<OpenAiCompatController> logger)
    {
        _aiSettings = aiSettings;
        _aiClientService = aiClientService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>模型列表（OpenAI /v1/models 兼容）</summary>
    [HttpGet("models")]
    public ActionResult<object> ListModels()
    {
        if (!Authorize()) return Unauthorized(new { error = "invalid token" });

        var models = _aiSettings.GetAiProviders()
            .Where(p => p.Models is { Count: > 0 })
            .SelectMany(p => p.Models.Select(m => new { id = m.Name, @object = "model", owned_by = p.Id }))
            .ToList();
        return Ok(new { @object = "list", data = models });
    }

    /// <summary>聊天补全（OpenAI /v1/chat/completions 兼容，非流式 + 流式 SSE）</summary>
    [HttpPost("chat/completions")]
    public async Task ChatCompletions([FromBody] JsonElement body, CancellationToken ct)
    {
        if (!Authorize())
        {
            Response.StatusCode = 401;
            await Response.WriteAsJsonAsync(new { error = new { message = "invalid token", type = "invalid_request_error" } });
            return;
        }

        try
        {
            var modelName = body.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            var stream = body.TryGetProperty("stream", out var s) && s.GetBoolean();
            // 采样参数透传（OpenAI 协议）：本地小模型/DSH 插件需要按任务收紧输出预算，
            // 此前 shim 固定使用 BuildChatOptions 默认值，max_tokens/temperature/top_p 全部被忽略。
            var maxTokens = body.TryGetProperty("max_tokens", out var mt) && mt.TryGetInt32(out var mtv) ? mtv : (int?)null;
            var temperature = body.TryGetProperty("temperature", out var tt) && tt.TryGetDouble(out var ttv) ? (float)ttv : (float?)null;
            var topP = body.TryGetProperty("top_p", out var tpp) && tpp.TryGetDouble(out var tpv) ? (float)tpv : (float?)null;
            var messages = ParseMessages(body);

            if (messages.Count == 0)
            {
                await WriteErrorAsync("messages is required");
                return;
            }

            var (provider, resolvedModel) = ResolveProviderAndModel(modelName);
            if (provider == null)
            {
                await WriteErrorAsync($"no provider found for model {modelName}");
                return;
            }

            var ready = await _aiClientService.EnsureProviderReadyAsync(provider);
            if (!ready)
            {
                await WriteErrorAsync($"local model provider {provider.Name} is not running");
                return;
            }

            var tools = ParseTools(body);
            if (stream)
            {
                await StreamResponseAsync(provider, resolvedModel, messages, ct, maxTokens, temperature, topP);
            }
            else
            {
                await NonStreamResponseAsync(provider, resolvedModel, messages, tools, ct, maxTokens, temperature, topP);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI 兼容端点调用失败");
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsJsonAsync(new { error = new { message = ex.Message, type = "server_error" } });
            }
        }
    }

    private bool Authorize()
    {
        var expected = _configuration["BAIHUA_AI_EXTERNAL_TOKEN"] ?? "";

        // 回环 + 管理允许网段免鉴权（本机 DSH 等信任面），否则才要求 token（跨机安全）
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp != null)
        {
            var allowed = AdminNetworkPolicy.ParseNets(
                Environment.GetEnvironmentVariable(AdminNetworkPolicy.AdminAllowedNetsEnv));
            if (AdminNetworkPolicy.IsAllowed(remoteIp, allowed)) return true;
        }

        if (string.IsNullOrEmpty(expected)) return true; // 未配置则局域网内信任
        var auth = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        var token = auth["Bearer ".Length..].Trim();
        return string.Equals(token, expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// 解析消息 content：兼容 OpenAI 两种格式——
    ///   字符串（纯文本，百花 Web 聊天用）
    ///   数组（多模态：{type:text} / {type:image_url, image_url:{url:data:... 或 http...}}，图生文用）
    /// </summary>
    private static List<AIContent> ParseContent(JsonElement content)
    {
        var list = new List<AIContent>();
        if (content.ValueKind == JsonValueKind.String)
        {
            var s = content.GetString() ?? "";
            if (s.Length > 0) list.Add(new TextContent(s));
            return list;
        }
        if (content.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var part in content.EnumerateArray())
        {
            var type = part.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "text":
                    var text = part.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    if (text.Length > 0) list.Add(new TextContent(text));
                    break;
                case "image_url":
                    var url = part.TryGetProperty("image_url", out var iu) && iu.TryGetProperty("url", out var u)
                        ? u.GetString() ?? ""
                        : "";
                    // M.E.AI 的 DataContent(Uri, mediaType)：OpenAI 适配器会识别为图片输入
                    // （image_url 支持 data:base64 与 http(s) 两种；OVMS VL 模型接收 base64）
                    if (url.Length > 0) list.Add(new DataContent(new Uri(url), MimeFromUrl(url)));
                    break;
            }
        }
        return list;
    }

    /// <summary>从 image_url（data URL 或 http URL）推断 MIME 类型（DataContent 需要）。</summary>
    private static string MimeFromUrl(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var semi = url.IndexOf(';');
            var comma = url.IndexOf(',');
            if (semi > 5 && (comma < 0 || semi < comma)) return url[5..semi]; // data:image/png;base64,...
            if (comma > 5) return url[5..comma];
        }
        var ext = System.IO.Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
    }

    private static List<ChatMessage> ParseMessages(JsonElement body)
    {
        var list = new List<ChatMessage>();
        if (!body.TryGetProperty("messages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in arr.EnumerateArray())
        {
            var role = item.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            var content = item.TryGetProperty("content", out var c) ? c : default;
            switch (role.ToLowerInvariant())
            {
                case "system":
                    list.Add(new ChatMessage(ChatRole.System, ParseContent(content)));
                    break;
                case "assistant":
                    if (item.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
                    {
                        var contents = new List<AIContent>();
                        contents.AddRange(ParseContent(content));
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            var id = tc.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                            var fn = tc.TryGetProperty("function", out var f) ? f : default;
                            var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "" : "";
                            IDictionary<string, object?>? argDict = null;
                            if (!string.IsNullOrWhiteSpace(argsJson))
                            {
                                try { argDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson); }
                                catch { argDict = null; }
                            }
                            contents.Add(new FunctionCallContent(id, name, argDict));
                        }
                        list.Add(new ChatMessage(ChatRole.Assistant, contents));
                    }
                    else
                    {
                        list.Add(new ChatMessage(ChatRole.Assistant, ParseContent(content)));
                    }
                    break;
                case "tool":
                    var callId = item.TryGetProperty("tool_call_id", out var tid) ? tid.GetString() ?? "" : "";
                    var toolContent = item.TryGetProperty("content", out var tc2) ? tc2.GetString() ?? "" : "";
                    list.Add(new ChatMessage(ChatRole.Tool, new[] { new FunctionResultContent(callId, toolContent) }));
                    break;
                default:
                    list.Add(new ChatMessage(ChatRole.User, ParseContent(content)));
                    break;
            }
        }
        return list;
    }

    /// <summary>解析 OpenAI 协议 tools 数组为 M.E.AI 工具声明（无实现体，只透传给模型用于 function call）。</summary>
    private static List<AITool> ParseTools(JsonElement body)
    {
        var tools = new List<AITool>();
        if (!body.TryGetProperty("tools", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return tools;
        foreach (var t in arr.EnumerateArray())
        {
            try
            {
                if (!t.TryGetProperty("type", out var type) || type.GetString() != "function") continue;
                if (!t.TryGetProperty("function", out var fn)) continue;
                var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var description = fn.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var parameters = fn.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object
                    ? p
                    : JsonSerializer.SerializeToElement(new { type = "object", properties = new Dictionary<string, object>(), required = new List<string>() });
                tools.Add(AIFunctionFactory.CreateDeclaration(name, description, parameters, null));
            }
            catch (Exception ex)
            {
                // 单个工具解析失败不影响其余工具
                System.Diagnostics.Debug.WriteLine($"ParseTools skipped tool: {ex.Message}");
            }
        }
        return tools;
    }

    private (AiProviderConfig? Provider, string Model) ResolveProviderAndModel(string modelName)
    {
        var providers = _aiSettings.GetAiProviders();
        AiProviderConfig? provider = null;
        if (!string.IsNullOrEmpty(modelName))
        {
            provider = providers.FirstOrDefault(p =>
                p.Models.Any(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)));
        }
        provider ??= providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault();

        var resolvedModel = !string.IsNullOrEmpty(modelName)
            ? modelName
            : provider?.GetMainModel() ?? "";

        return (provider, resolvedModel);
    }

    private async Task NonStreamResponseAsync(
        AiProviderConfig provider, string model, List<ChatMessage> messages, List<AITool> tools, CancellationToken ct,
        int? maxTokens = null, float? temperature = null, float? topP = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var options = AiClientService.BuildChatOptions(temperature ?? 0.7f, maxTokens ?? 2000, topP ?? 0.95f);
        if (tools.Count > 0)
            options.Tools = tools;
        // useCache: false —— AI 服务是转发代理，不缓存响应（Family 侧已有缓存层）
        var result = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, useCache: false);
        sw.Stop();

        // Function Calling 透传：把模型的 tool_calls 原样返回给调用方（Family 侧负责执行工具）。
        // OpenAI 协议要求 arguments 为 JSON 字符串。
        object? toolCalls = null;
        var functionCall = result.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault();
        if (functionCall != null)
        {
            var argsJson = functionCall.Arguments is { Count: > 0 }
                ? JsonSerializer.Serialize(functionCall.Arguments)
                : "";
            toolCalls = new[]
            {
                new
                {
                    id = functionCall.CallId,
                    type = "function",
                    function = new { name = functionCall.Name, arguments = argsJson }
                }
            };
        }

        Response.ContentType = "application/json";
        await Response.WriteAsJsonAsync(new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}"[..24],
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = result.Text ?? "", tool_calls = toolCalls },
                    finish_reason = functionCall != null ? "tool_calls" : "stop"
                }
            },
            usage = new
            {
                // 真实用量透传（此前恒为 0）：DSH 插件/算力池需要它来做 token 统计
                prompt_tokens = result.Usage?.InputTokenCount ?? 0,
                completion_tokens = result.Usage?.OutputTokenCount ?? 0,
                total_tokens = result.Usage?.TotalTokenCount ?? 0,
                // 扩展字段：实测性能
                elapsed_ms = sw.ElapsedMilliseconds
            }
        });
    }

    private async Task StreamResponseAsync(
        AiProviderConfig provider, string model, List<ChatMessage> messages, CancellationToken ct,
        int? maxTokens = null, float? temperature = null, float? topP = null)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var client = _aiClientService.CreateChatClient(provider, model);
        await foreach (var update in client.GetStreamingResponseAsync(messages, AiClientService.BuildChatOptions(temperature ?? 0.7f, maxTokens ?? 2000, topP ?? 0.95f), linkedCts.Token))
        {
            var text = update.Text;
            if (string.IsNullOrEmpty(text)) continue;
            var chunk = new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}"[..24],
                @object = "chat.completion.chunk",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model,
                choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } }
            };
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
            await Response.Body.FlushAsync(ct);
        }

        await Response.WriteAsync("data: [DONE]\n\n");
        await Response.Body.FlushAsync(ct);
    }

    private async Task WriteErrorAsync(string message)
    {
        Response.StatusCode = 400;
        await Response.WriteAsJsonAsync(new { error = new { message, type = "invalid_request_error" } });
    }
}
