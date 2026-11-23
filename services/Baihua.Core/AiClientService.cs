using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;
using Baihua.Core.Localization;
using OpenAI;
using System.ClientModel;
using Microsoft.Extensions.Logging;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Core.Models;

namespace Baihua.Core.Services;
    /// <summary>
    /// 统一的 AI 客户端服务：基于 Microsoft.Extensions.AI 抽象层，
    /// 为任意 OpenAI 兼容提供商创建 IChatClient 和 IEmbeddingGenerator。
    /// </summary>
    public partial class AiClientService
    {
        private readonly AiSettingsService _aiSettings;
        private readonly LocalAiAutoStarter _autoStarter;
        // 一服务一数据库：Family（shim 模式）不注册 AIDbContext → 为 null，AI 调用指标不落 ai.db
        //（AI 服务在 shim 转发时自行记录到自己的 ai.db）；AI 服务进程内为真实 factory。
        private readonly IDbContextFactory<AIDbContext>? _dbFactory;
        private readonly AiMetricsService _metrics;
        private readonly IDistributedCache _cache;
        private readonly AnthropicAiClient _anthropicClient;
        private readonly ILogger<AiClientService> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;
        private readonly ConcurrentDictionary<string, IChatClient> _chatClientCache = new();

        public AiClientService(
            AiSettingsService aiSettings,
            LocalAiAutoStarter autoStarter,
            AiMetricsService metrics,
            IDistributedCache cache,
            AnthropicAiClient anthropicClient,
            ILogger<AiClientService> logger,
            IStringLocalizer<SharedResources> loc,
            IDbContextFactory<AIDbContext>? dbFactory = null)
        {
            _aiSettings = aiSettings;
            _autoStarter = autoStarter;
            _dbFactory = dbFactory;
            _metrics = metrics;
            _cache = cache;
            _anthropicClient = anthropicClient;
            _logger = logger;
            _loc = loc;
        }
}
