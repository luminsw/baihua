using Baihua.Core.Models;
using Baihua.Core.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.OpenClaw;
using Baihua.Core.Localization;
using Baihua.Data.Entities;
using Baihua.Family.Models;
using Baihua.Family.Services.AI;
using Microsoft.Extensions.Localization;
using Baihua.AI.Provider;

namespace Baihua.Family.Services;
    /// <summary>
    /// 本地模型部署服务：协调模型下载、部署和 AI Provider 自动配置
    /// </summary>
    public partial class LocalModelDeploymentService
    {
        private readonly ILogger<LocalModelDeploymentService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly LocalAiAutoStarter _autoStarter;
        private readonly IAiConfigService _aiConfigService;
        private readonly AiProviderRegistryClient _providerRegistry;
        private readonly LocalModelSettingsService _localModelSettings;
        private readonly AiSettingsService _aiSettings;
        private readonly IMemoryCache _cache;
        private readonly IStringLocalizer<SharedResources> _loc;
    private readonly ILocalModelTool _openVino;

        // 内存中的部署任务状态
        private static readonly ConcurrentDictionary<string, DeployTaskStatusDto> _tasks = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCancellations = new();

        // 缓存配置
        private const string ToolsCacheKey = "local_tools";
        private const string RunningModelsCacheKey = "running_models";
        private const string DownloadedModelsCacheKey = "downloaded_models";

        public LocalModelDeploymentService(
            ILogger<LocalModelDeploymentService> logger,
            IHttpClientFactory httpClientFactory,
            LocalAiAutoStarter autoStarter,
            IAiConfigService aiConfigService,
            AiProviderRegistryClient providerRegistry,
            LocalModelSettingsService localModelSettings,
            AiSettingsService aiSettings,
            IMemoryCache cache,
            ILocalModelTool openVino,
            IStringLocalizer<SharedResources> loc)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _autoStarter = autoStarter;
            _aiConfigService = aiConfigService;
            _providerRegistry = providerRegistry;
            _localModelSettings = localModelSettings;
            _aiSettings = aiSettings;
            _cache = cache;
            _openVino = openVino;
            _loc = loc;
        }

        #region Task Management

        public DeployTaskStatusDto? GetRunnerTaskStatus(string taskId)
        {
            return _tasks.TryGetValue(taskId, out var status) ? status : null;
        }

        public bool CancelTask(string taskId)
        {
            if (_taskCancellations.TryRemove(taskId, out var cts))
            {
                cts.Cancel();
                if (_tasks.TryGetValue(taskId, out var task))
                {
                    task.Status = "failed";
                    task.ErrorMessage = _loc["Common_UserCancelled"];
                    task.CompletedAt = DateTime.Now;
                }
                return true;
            }
            return false;
        }

        public void CleanupOldTasks(TimeSpan maxAge)
        {
            var cutoff = DateTime.Now - maxAge;
            var keysToRemove = _tasks
                .Where(kv => kv.Value.Status is "completed" or "failed" && kv.Value.CompletedAt < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _tasks.TryRemove(key, out _);
                _taskCancellations.TryRemove(key, out _);
            }
        }

        #endregion

}
