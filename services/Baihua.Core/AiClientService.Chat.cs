using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using OpenAI;
using System.ClientModel;
using Microsoft.Extensions.Logging;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Core.Models;

namespace Baihua.Core.Services;

public partial class AiClientService
{
        public async Task<bool> EnsureProviderReadyAsync(AiProviderConfig provider)
        {
            if (!IsLocalProvider(provider))
                return true;

            return await _autoStarter.TryEnsureRunningAsync(provider.Id, provider.AiBaseUrl);
        }

        /// <summary>
        /// 调用 GetResponseAsync，如果连接失败则尝试自动启动本地服务后重试一次
        /// </summary>
        /// <param name="provider">AI 提供商配置</param>
        /// <param name="model">模型名称</param>
        /// <param name="messages">聊天消息列表</param>
        /// <param name="options">聊天选项</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="operation">操作类型，用于指标统计：chat, benchmark, task, split, index, openclaw 等</param>
        /// <param name="useCache">是否使用分布式缓存（转发代理场景传 false：避免缓存哈希长消息与无限缓存内存膨胀）</param>
        public async Task<ChatResponse> GetChatResponseWithAutoStartAsync(
            AiProviderConfig provider, string model,
            IList<ChatMessage> messages, ChatOptions options, CancellationToken ct,
            string operation = "chat", bool useCache = true)
        {
            var sw = Stopwatch.StartNew();
            ChatResponse? response = null;
            try
            {
                var client = useCache ? CreateChatClientWithCache(provider, model) : CreateChatClient(provider, model);
                try
                {
                    response = await client.GetResponseAsync(messages, options, ct);
                }
                // 一服务一数据库：Family（shim 模式）不直连本地模型，连接失败也不自动启动本地服务
                //（本地模型由 AI 服务进程内启动/路由）；此重试仅适用于 AI 服务进程内直连场景。
                catch (Exception ex) when (IsConnectionFailure(ex) && IsLocalProvider(provider) && !_aiSettings.RouteInferenceViaShim)
                {
                    _logger.LogWarning("本地 AI 服务连接失败，尝试自动启动: {Provider}", provider.Id);
                    var started = await _autoStarter.TryEnsureRunningAsync(provider.Id, provider.AiBaseUrl);
                    if (!started)
                        throw new Exception(_loc["AiClient_LocalServiceStartFailed", provider.Name]);

                    client = useCache ? CreateChatClientWithCache(provider, model) : CreateChatClient(provider, model);
                    // 克隆消息列表，避免外界修改导致重试使用脏上下文
                    var retryMessages = messages.Select(m => new ChatMessage(m.Role, m.Contents)).ToList();
                    response = await client.GetResponseAsync(retryMessages, options, ct);
                }

                // Anthropic fallback：若返回为空，尝试 Anthropic 协议。
                // 仅 AI 服务进程内可用（Family shim 模式不持有 key，
                // shim 转发时由 AI 服务侧自行处理 Anthropic 协议回退）。
                if (IsEmptyResponse(response) && !string.IsNullOrWhiteSpace(provider.AnthropicBaseUrl) && !_aiSettings.RouteInferenceViaShim)
                {
                    _logger.LogWarning("AI ({Provider}/{Model}) OpenAI 响应为空，尝试 Anthropic fallback", provider.Name, model);
                    var apiKey = _aiSettings.GetAiApiKey(provider.Id);
                    response = await _anthropicClient.GetChatResponseAsync(
                        provider.AnthropicBaseUrl, apiKey, model, messages, options, ct);
                    _logger.LogInformation("AI ({Provider}/{Model}) Anthropic fallback 成功，内容长度={Length}",
                        provider.Name, model, response.Text?.Length ?? 0);
                }

                sw.Stop();
                await RecordMetricAsync(provider, model, operation, sw.ElapsedMilliseconds, response, true, null);
                return response;
            }
            catch (Exception ex)
            {
                sw.Stop();
                await RecordMetricAsync(provider, model, operation, sw.ElapsedMilliseconds, response, false, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 获取支持 Function Calling 的流式响应
        /// 先通过非流式请求检测 function call，执行后再流式输出最终回复
        /// </summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseWithToolsAsync(
            AiProviderConfig provider, string model,
            IList<ChatMessage> messages, ChatOptions options,
            IList<AITool> tools,
            Action<FunctionCallContent>? onToolExecuting = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = CreateChatClient(provider, model);
            var toolOptions = new ChatOptions
            {
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                TopP = options.TopP,
                Tools = tools
            };

            // 第一轮：非流式请求，检测 function call
            var response = await client.GetResponseAsync(messages, toolOptions, ct);

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            // Qwen 等模型在启用工具调用时可能返回空内容（不调用工具也不回复）
            // 自动回退：禁用 tools 后重试一次
            if (functionCalls.Count == 0 && string.IsNullOrWhiteSpace(response.Text))
            {
                _logger.LogWarning("AI ({Provider}/{Model}) 流式响应为空，尝试禁用工具调用后重试", provider.Name, model);
                var retryOptions = new ChatOptions
                {
                    Temperature = options.Temperature,
                    MaxOutputTokens = options.MaxOutputTokens,
                    TopP = options.TopP
                };
                // 克隆消息列表，避免外界修改导致重试使用脏上下文
                var retryMessages = messages.Select(m => new ChatMessage(m.Role, m.Contents)).ToList();
                response = await client.GetResponseAsync(retryMessages, retryOptions, ct);
                functionCalls = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();
                _logger.LogInformation("AI ({Provider}/{Model}) 流式重试结果：HasContent={HasContent}, HasFunctionCall={HasFunctionCall}",
                    provider.Name, model, !string.IsNullOrWhiteSpace(response.Text), functionCalls.Count > 0);
            }

            if (functionCalls.Count > 0)
            {
                // 通知调用方有哪些 tool call
                foreach (var fc in functionCalls)
                {
                    onToolExecuting?.Invoke(fc);
                }

                // 添加 AI 的 function call 消息
                var assistantContents = response.Messages.SelectMany(m => m.Contents).ToList();
                messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

                // 执行每个 function call
                foreach (var fc in functionCalls)
                {
                    var tool = tools.FirstOrDefault(t => t is AIFunction f && f.Name == fc.Name);
                    if (tool is AIFunction aiFunc)
                    {
                        try
                        {
                            var result = await aiFunc.InvokeAsync(new AIFunctionArguments(fc.Arguments), ct);
                            messages.Add(new ChatMessage(ChatRole.Tool,
                                new[] { new FunctionResultContent(fc.CallId, result?.ToString() ?? "") }));
                        }
                        catch (Exception ex)
                        {
                            messages.Add(new ChatMessage(ChatRole.Tool,
                                new[] { new FunctionResultContent(fc.CallId, _loc["Ai_Core_FuncExecuteFailed", ex.Message]) }));
                        }
                    }
                    else
                    {
                        messages.Add(new ChatMessage(ChatRole.Tool,
                            new[] { new FunctionResultContent(fc.CallId, _loc["Ai_Core_FuncNotFound", fc.Name]) }));
                    }
                }

                // 重新获取流式响应（不带 tools，避免无限循环）
                var finalOptions = new ChatOptions
                {
                    Temperature = options.Temperature,
                    MaxOutputTokens = options.MaxOutputTokens,
                    TopP = options.TopP
                };

                await foreach (var update in client.GetStreamingResponseAsync(messages, finalOptions, ct))
                {
                    yield return update;
                }
            }
            else
            {
                // 没有 function call，将非流式响应模拟为流式输出
                var text = response.Text ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text);
                }
            }
        }

}
