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
        private static bool IsEmptyResponse(ChatResponse? response)
        {
            if (response == null) return true;
            var text = response.Text ?? "";
            return string.IsNullOrWhiteSpace(text);
        }

        private static bool IsLocalProvider(AiProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(provider?.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        private static bool IsConnectionFailure(Exception ex)
        {
            var message = ex.Message + (ex.InnerException?.Message ?? "");
            return message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("积极拒绝", StringComparison.OrdinalIgnoreCase)
                || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("无法连接", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no connection could be made", StringComparison.OrdinalIgnoreCase)
                || ex.InnerException is System.Net.Sockets.SocketException;
        }

        /// <summary>
        /// 构建标准 ChatOptions
        /// </summary>
        public static ChatOptions BuildChatOptions(float temperature = 0.7f, int maxOutputTokens = 2000, float topP = 0.95f)
        {
            return new ChatOptions
            {
                Temperature = temperature,
                MaxOutputTokens = maxOutputTokens,
                TopP = topP,
            };
        }

        /// <summary>
        /// 记录 AI 调用指标到数据库
        /// </summary>
        private async Task RecordMetricAsync(
            AiProviderConfig provider, string model, string operation,
            long latencyMs, ChatResponse? response, bool isSuccess, string? errorMessage)
        {
            try
            {
                int? inputTokens = null;
                int? outputTokens = null;
                int? totalTokens = null;
                double? tps = null;

                if (response?.Usage is { } usage)
                {
                    inputTokens = (int?)usage.InputTokenCount;
                    outputTokens = (int?)usage.OutputTokenCount;
                    totalTokens = (int?)usage.TotalTokenCount;
                    if (latencyMs > 0 && outputTokens > 0)
                        tps = outputTokens.Value / (latencyMs / 1000.0);
                }

                // 1. 记录到 .NET Metrics（通过 OpenTelemetry OTLP 推送到 OpenObserve）
                _metrics.RecordAiRequest(
                    provider.Id, model, operation,
                    latencyMs, isSuccess,
                    inputTokens, outputTokens, tps);

                // 2. 记录到 SQLite（保留本地历史查询能力；仅 AI 服务进程内直写 ai.db，
                //    Family shim 模式由 AI 服务转发时自行记录，避免 Family 写 ai.db）
                if (_dbFactory != null && !_aiSettings.RouteInferenceViaShim)
                {
                    using var db = await _dbFactory.CreateDbContextAsync();
                    db.AiUsageMetrics.Add(new AiUsageMetric
                    {
                        CalledAt = DateTime.UtcNow,
                        ProviderId = provider.Id,
                        ProviderName = provider.Name ?? provider.Id,
                        ModelId = model,
                        Operation = operation,
                        LatencyMs = latencyMs,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        TotalTokens = totalTokens,
                        TokensPerSecond = tps,
                        IsSuccess = isSuccess,
                        ErrorMessage = errorMessage,
                    });
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "记录 AI 调用指标失败（不影响主流程）");
            }
        }
}
