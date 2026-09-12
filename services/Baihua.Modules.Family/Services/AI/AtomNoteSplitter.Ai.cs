using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using Baihua.AI.Provider;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Scene;

namespace Baihua.Modules.Family.Services;

public partial class AtomNoteSplitter
{

        private async Task<string> SendRequestWithRetryAsync(string providerId, string model, string userPrompt, bool isSupplement, CancellationToken cancellationToken, Baihua.Contracts.Scene.AppScene? scene = null)
        {
            _logger.LogDebug("发送 AI 请求到 provider {ProviderId} model {Model} (补充={IsSupplement})", providerId, model, isSupplement);

            var maxAttempts = _aiSettings.AiRequestMaxAttempts;
            var initialBackoff = _aiSettings.AiRequestInitialBackoffMs;
            var maxBackoff = _aiSettings.AiRequestMaxBackoffMs;
            var rand = new Random();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None, cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes));

                    var chatClient = _aiClientService.CreateChatClient(providerId, model);
                    var messages = new List<ChatMessage>
                    {
                        new(ChatRole.System, GetSystemPrompt(scene)),
                        new(ChatRole.User, userPrompt)
                    };
                    var options = AiClientService.BuildChatOptions();

                    var response = await chatClient.GetResponseAsync(messages, options, cts.Token);
                    var aiContent = response.Text;
                    _logger.LogDebug("解析到 AI 内容长度：{Len}", aiContent?.Length ?? 0);
                    return aiContent ?? throw new Exception("AI returned empty content");
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
                {
                    // 首次失败且是连接类错误时，尝试自动启动本地 AI 服务
                    if (attempt == 1 && IsConnectionFailure(ex))
                    {
                        try
                        {
                            var provider = _aiSettings.GetAiProviders().FirstOrDefault(p => p.Id == providerId);
                            if (provider != null)
                            {
                                await _localAiAutoStarter.TryEnsureRunningAsync(provider.Id, provider.AiBaseUrl);
                            }
                        }
                        catch { /* 自动启动失败不影响原有重试逻辑 */ }
                    }

                    var backoff = Math.Min(maxBackoff, initialBackoff * (int)Math.Pow(2, attempt - 1));
                    var delayMs = backoff + rand.Next(0, Math.Min(500, backoff));
                    _logger.LogWarning(ex, "调用 AI 第 {Attempt} 次失败，{Delay}ms 后重试...", attempt, delayMs);
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw;
                }
            }

            throw new Exception("AI request still failed after multiple retries");
        }

        private static string NormalizeRelPath(string relPath)
        {
            if (string.IsNullOrWhiteSpace(relPath)) return string.Empty;
            // 使用正斜杠，去掉前后斜杠，删除多余空格
            var p = relPath.Replace('\\', '/').Trim();
            while (p.StartsWith('/')) p = p.Substring(1);
            while (p.EndsWith('/')) p = p.Substring(0, p.Length - 1);
            // 规范化连续的斜杠
            while (p.Contains("//")) p = p.Replace("//", "/");
            return p;
        }
}
