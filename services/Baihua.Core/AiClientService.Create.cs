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
        public IChatClient CreateChatClient(string providerId, string model)
        {
            return CreateChatClient(providerId, model, tools: null);
        }

        /// <summary>
        /// 为指定提供商+模型创建 IChatClient（支持 Function Calling）
        /// 使用缓存避免每次创建新的 TCP 连接
        ///
        /// 一服务一数据库：Family 进程（AiClient__UseShim=true）统一经本机 AI 服务的
        /// OpenAI 兼容 shim（/mg/ai/v1）按模型名转发——API Key 与模型路由只存在于 AI 服务；
        /// AI 服务进程（默认 false）内部直连真实 provider，避免 shim 自指转发。
        /// </summary>
        public IChatClient CreateChatClient(string providerId, string model, IList<AITool>? tools)
        {
            var cacheKey = $"{providerId}:{model}";
            return _chatClientCache.GetOrAdd(cacheKey, _ =>
            {
                if (_aiSettings.RouteInferenceViaShim)
                {
                    var shimBase = _aiSettings.AiShimUrl;
                    // 尾斜杠关键：OpenAI SDK 在 endpoint 后追加 "chat/completions"（相对 URI 合并）
                    var shimEndpoint = new Uri($"{shimBase.TrimEnd('/')}/mg/ai/v1/");
                    var shimOptions = new OpenAIClientOptions
                    {
                        Endpoint = shimEndpoint,
                        // SDK 默认网络超时仅 100 秒（且自动重试 4 次），长 JSON 输出（题库 30 题等）会整批失败
                        NetworkTimeout = TimeSpan.FromMinutes(3),
                    };
                    // shim 鉴权：未配置 BAIHUA_AI_EXTERNAL_TOKEN 时局域网信任，占位即可
                    var shimCredential = new ApiKeyCredential("local-shim");
                    var shimClient = new OpenAIClient(shimCredential, shimOptions);
                    var routedClient = shimClient.GetChatClient(model).AsIChatClient();
                    return routedClient.AsBuilder().UseOpenTelemetry().Build();
                }

                var provider = _aiSettings.GetAiProvider(providerId)
                    ?? throw new Exception(_loc["AiClient_ProviderNotFound", providerId]);

                var apiKey = _aiSettings.GetAiApiKey(providerId);
                if (string.IsNullOrWhiteSpace(apiKey))
                    _logger.LogWarning("提供商 {ProviderId} 未配置 API Key，将以无鉴权方式请求", providerId);

                // 尾斜杠关键：OpenAI SDK 在 endpoint 后追加 "chat/completions"（相对 URI 合并），
                // 无尾斜杠时会把 endpoint 最后一个路径段（如 /v1）当文件替换掉，
                // 导致本地服务（Ollama/OpenVINO 等以 /v1 结尾的端点）404。
                var endpoint = new Uri(provider.AiBaseUrl.TrimEnd('/') + "/");
var clientOptions = new OpenAIClientOptions
                {
                    Endpoint = endpoint,
                    // 与 shim 一致：默认 100 秒对长输出偏短
                    NetworkTimeout = TimeSpan.FromMinutes(3),
                };

                // 无 API Key 时使用占位符（Ollama/LMStudio 等本地服务不需要 Key）
                var credential = string.IsNullOrWhiteSpace(apiKey)
                    ? new ApiKeyCredential("placeholder")
                    : new ApiKeyCredential(apiKey);

                var openaiClient = new OpenAIClient(credential, clientOptions);
                var client = openaiClient.GetChatClient(model).AsIChatClient();
                // OTel GenAI 遥测（span/metric 按 GenAI 语义约定，含 token 用量）
                return client.AsBuilder().UseOpenTelemetry().Build();
            });
        }

        /// <summary>
        /// 为指定提供商+模型创建 IChatClient（通过 AiProviderConfig 对象）
        /// </summary>
        public IChatClient CreateChatClient(AiProviderConfig provider, string model)
        {
            return CreateChatClient(provider.Id, model);
        }

        /// <summary>
        /// 为指定提供商+模型创建 IChatClient（通过 AiProviderConfig 对象，支持 Function Calling）
        /// </summary>
        public IChatClient CreateChatClient(AiProviderConfig provider, string model, IList<AITool>? tools)
        {
            return CreateChatClient(provider.Id, model, tools);
        }

        /// <summary>
        /// 创建带分布式缓存的 IChatClient（用于非流式请求）
        /// 使用缓存避免每次创建新的 TCP 连接
        /// </summary>
        private IChatClient CreateChatClientWithCache(string providerId, string model)
        {
            var client = CreateChatClient(providerId, model);
            return client.AsBuilder()
                .UseDistributedCache(_cache)
                .Build();
        }

        /// <summary>
        /// 创建带分布式缓存的 IChatClient（用于非流式请求）
        /// </summary>
        private IChatClient CreateChatClientWithCache(AiProviderConfig provider, string model)
        {
            return CreateChatClientWithCache(provider.Id, model);
        }

        /// <summary>
        /// 创建 Embedding 生成器（回退到旧配置）
        /// </summary>
        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
        {
            var url = _aiSettings.SemanticEmbeddingUrl;
            var model = _aiSettings.SemanticEmbeddingModel;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
                throw new Exception(_loc["AiClient_EmbeddingNotConfigured"]);

            // Embedding 可能使用与 Chat 不同的提供商
            // 尝试从 AI 提供商中匹配 Embedding URL
            var providers = _aiSettings.GetAiProviders();
            var matchedProvider = providers.FirstOrDefault(p =>
                p.AiBaseUrl.TrimEnd('/').Equals(url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

            string apiKey;
            if (matchedProvider != null)
            {
                apiKey = _aiSettings.GetAiApiKey(matchedProvider.Id);
            }
            else
            {
                // 使用主提供商的 API Key
                apiKey = _aiSettings.AiApiKey;
            }

            return CreateEmbeddingGenerator(url, model, apiKey);
        }

        /// <summary>
        /// 为指定 URL+模型+Key 创建 Embedding 生成器
        /// </summary>
        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string url, string model, string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
                throw new Exception(_loc["AiClient_EmbeddingNotConfigured"]);

            var endpoint = new Uri(url.TrimEnd('/'));
            var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };

            var credential = string.IsNullOrWhiteSpace(apiKey)
                ? new ApiKeyCredential("placeholder")
                : new ApiKeyCredential(apiKey);

            var openaiClient = new OpenAIClient(credential, clientOptions);
            return openaiClient.GetEmbeddingClient(model).AsIEmbeddingGenerator();
        }
}
