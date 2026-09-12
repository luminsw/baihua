using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Baihua.Core.Localization;
using Baihua.Modules.Family.Services;
using System.Text.Json;
using Baihua.AI.Provider;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Localization;
using Baihua.Modules.Family.Models;
using Baihua.Contracts.Scene;
using Baihua.Contracts.Tasks;
using Baihua.Contracts.Vaults;

namespace Baihua.Modules.Family.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB 限制，防止 DoS
    public partial class TasksController : ControllerBase
    {
        private readonly Baihua.Core.Services.TaskManager _taskManager;
        private readonly Baihua.Core.Services.AiSettingsService _aiSettings;
        private readonly Baihua.Core.Services.VaultSettingsService _vaultSettings;
        private readonly Services.AtomNoteSplitter _atomNoteSplitter;
        private readonly Baihua.Core.Services.AiClientService _aiClientService;
        private readonly Baihua.Core.Services.LocalAiAutoStarter _localAiAutoStarter;

        private readonly Services.IOpenClawTaskService _openClawTaskService;
        private readonly DefaultPromptProvider _scenePromptService;
        private readonly Services.AnkiCardGenerator _cardGenerator;
        private readonly Baihua.Core.Services.VaultNoteIndexer _vaultNoteIndexer;
        private readonly Services.UserActivityService _activityService;
        private readonly Services.AiDetailSettingsService _aiDetailSettings;
        private readonly ILogger<TasksController> _logger;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly IStringLocalizer<SharedResources> _loc;

        public TasksController(
            Baihua.Core.Services.TaskManager taskManager,
            Baihua.Core.Services.AiSettingsService aiSettings,
            Baihua.Core.Services.VaultSettingsService vaultSettings,
            Services.AtomNoteSplitter atomNoteSplitter,
            Baihua.Core.Services.AiClientService aiClientService,
            Baihua.Core.Services.LocalAiAutoStarter localAiAutoStarter,

            Services.IOpenClawTaskService openClawTaskService,
            DefaultPromptProvider scenePromptService,
            Services.AnkiCardGenerator cardGenerator,
            Baihua.Core.Services.VaultNoteIndexer vaultNoteIndexer,
            Services.UserActivityService activityService,
            Services.AiDetailSettingsService aiDetailSettings,
            ILogger<TasksController> logger,
            IHostApplicationLifetime appLifetime,
            IStringLocalizer<SharedResources> loc)
        {
            _taskManager = taskManager;
            _aiSettings = aiSettings;
            _vaultSettings = vaultSettings;
            _atomNoteSplitter = atomNoteSplitter;
            _aiClientService = aiClientService;
            _localAiAutoStarter = localAiAutoStarter;

            _openClawTaskService = openClawTaskService;
            _scenePromptService = scenePromptService;
            _cardGenerator = cardGenerator;
            _vaultNoteIndexer = vaultNoteIndexer;
            _activityService = activityService;
            _aiDetailSettings = aiDetailSettings;
            _logger = logger;
            _appLifetime = appLifetime;
            _loc = loc;
        }

        private AiProviderConfig? ResolveProvider(string modelName)
        {
            var providers = _aiSettings.GetAiProviders();
            if (string.IsNullOrWhiteSpace(modelName))
                return providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault();

            return providers.FirstOrDefault(p =>
                p.Models.Any(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
                ?? providers.FirstOrDefault(p => p.IsMain)
                ?? providers.FirstOrDefault();
        }

        private static bool IsLocalProvider(AiProviderConfig? provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        /// <summary>
        /// 查找知识库，支持重试以应对可能的写入延迟
        /// </summary>
        private async Task<VaultConfig?> FindVaultWithRetryAsync(string vaultId, int retryCount = 1, int delayMs = 500)
        {
            for (int i = 0; i <= retryCount; i++)
            {
                var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
                if (vault != null) return vault;
                if (i < retryCount)
                {
                    _logger.LogWarning("知识库查找重试 {Attempt}/{Max}: VaultId={VaultId}", i + 1, retryCount, vaultId);
                    await Task.Delay(delayMs);
                }
            }
            return null;
        }

        /// <summary>
        /// 截断字符串用于错误信息
        /// </summary>
        private static string TruncateForError(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }
    }
}
