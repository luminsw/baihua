using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Ai;

namespace Baihua.Modules.Family.Controllers
{
    public partial class AIController
    {
        private async Task<string> CallAiApiAsync(string query, string model, string providerId = "")
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, query)
            };
            return await CallAiApiAsync(messages, model, providerId);
        }

        private async Task<string> CallAiApiAsync(List<ChatMessage> messages, string model, string providerId = "", bool enableTools = false, CancellationToken ct = default)
        {
            var providers = _aiSettings.GetAiProviders();
            var provider = string.IsNullOrEmpty(providerId)
                ? providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
                : providers.FirstOrDefault(p => p.Id == providerId);

            if (provider == null)
                throw new Exception(_loc["AiProvider_NotFound"]);

            var apiEndpoint = provider.AiBaseUrl.TrimEnd('/') + "/chat/completions";

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                // 确保消息列表有 System 提示
                if (!messages.Any(m => m.Role == ChatRole.System))
                {
                    messages.Insert(0, new ChatMessage(ChatRole.System, GetSystemPrompt(provider.Id)));
                }

                var options = Baihua.Core.Services.AiClientService.BuildChatOptions();
                IList<AITool>? tools = null;
                if (enableTools)
                {
                    tools = _aiFunctionService.GetAllTools();
                    options.Tools = tools;
                }

                var response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                    provider, model, messages, options, linkedCts.Token);

                // 检查是否有 Function Call
                if (enableTools && tools != null)
                {
                    var functionCalls = response.Messages
                        .SelectMany(m => m.Contents)
                        .OfType<FunctionCallContent>()
                        .ToList();

                    if (functionCalls.Count > 0)
                    {
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
                                    var result = await aiFunc.InvokeAsync(new AIFunctionArguments(fc.Arguments), linkedCts.Token);
                                    messages.Add(new ChatMessage(ChatRole.Tool,
                                        new[] { new FunctionResultContent(fc.CallId, result?.ToString() ?? "") }));
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "函数 {Name} 执行失败", fc.Name);
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

                        // 重新调用 AI 获取最终回复（不再传 Tools，避免无限循环）
                        var finalOptions = Baihua.Core.Services.AiClientService.BuildChatOptions();
                        var finalResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                            provider, model, messages, finalOptions, linkedCts.Token);
                        return finalResponse.Text ?? "";
                    }
                }

                var content = response.Text;

                // Qwen 等模型在启用工具调用时可能返回空内容（不调用工具也不回复）
                // 自动回退：禁用 tools 后重试一次
                if (string.IsNullOrWhiteSpace(content) && enableTools)
                {
                    _logger.LogWarning("AI ({Provider}/{Model}) 返回内容为空，尝试禁用工具调用后重试", provider.Name, model);
                    var retryOptions = Baihua.Core.Services.AiClientService.BuildChatOptions();
                    var retryResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                        provider, model, messages, retryOptions, linkedCts.Token, operation: "chat-retry-no-tools");
                    content = retryResponse.Text;
                }

                if (string.IsNullOrWhiteSpace(content))
                    throw new Exception(_loc["Ai_Core_EmptyResponse"]);

                return content;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AI API 请求失败");
                // 尝试提取更友好的错误信息
                throw new Exception(ParseAiErrorMessage(ex));
            }
        }

        /// <summary>
        /// 从异常中提取友好的错误信息
        /// </summary>
        private string ParseAiErrorMessage(Exception ex)
        {
            var msg = ex.Message;

            // OpenAI SDK 会将 HTTP 错误包装在 ClientResultException 中
            if (msg.Contains("Model disabled", StringComparison.OrdinalIgnoreCase))
                return _loc["Ai_Core_ModelDisabled"];

            if (msg.Contains("api key", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                return _loc["Ai_Core_ApiKeyInvalid"];

            if (msg.Contains("balance", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
                return _loc["Ai_Core_BalanceInsufficient"];

            return _loc["Ai_Core_RequestFailed", msg];
        }

        private static bool IsLocalProvider(AiProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(provider?.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        /// <summary>
        /// 解析提供商和模型
        /// </summary>
        private (AiProviderConfig Provider, string Model) ResolveProviderAndModel(string? providerId, string? model)
        {
            var providers = _aiSettings.GetAiProviders();
            var provider = string.IsNullOrEmpty(providerId)
                ? (string.IsNullOrEmpty(model)
                    ? providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
                    : providers.FirstOrDefault(p =>
                        p.Models.Any(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
                      ?? providers.FirstOrDefault(p => p.IsMain)
                      ?? providers.FirstOrDefault())
                : providers.FirstOrDefault(p => p.Id == providerId);

            if (provider == null)
                throw new Exception(_loc["Ai_Core_ProviderNotFound"]);

            var modelOptions = provider.GetModelOptions();
            var resolvedModel = !string.IsNullOrEmpty(model)
                ? model
                : modelOptions.FirstOrDefault(m => m.IsMain)?.Name 
                  ?? modelOptions.FirstOrDefault()?.Name 
                  ?? "Qwen/Qwen2.5-14B-Instruct";

            return (provider, resolvedModel);
        }
    }
}
