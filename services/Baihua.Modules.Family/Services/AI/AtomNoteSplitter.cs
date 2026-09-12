using Baihua.Core.Models;
using Baihua.Core.Services;
using Baihua.Core;
using Baihua.Core.Localization;
using Microsoft.Extensions.Localization;
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
        private static readonly object _suppHistoryLock = new();
        private static List<string>? _supplementHistory;
        private string SupplementHistoryFile => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "supplement.history.json");
        private const int MaxSupplementHistoryEntries = 1000;
        private readonly AiClientService _aiClientService;
        private readonly TaskManager _taskManager;
        private readonly AiSettingsService _aiSettings;
        private readonly VaultSettingsService _vaultSettings;
        private readonly LocalAiAutoStarter _localAiAutoStarter;
        private readonly DefaultPromptProvider _scenePromptService;
        private readonly AnkiCardGenerator _cardGenerator;
        private readonly NoteParser _noteParser;
        private readonly ILogger<AtomNoteSplitter> _logger;
        private readonly IStringLocalizer<SharedResources> _loc;

        public AtomNoteSplitter(
            AiClientService aiClientService,
            TaskManager taskManager,
            AiSettingsService aiSettings,
            VaultSettingsService vaultSettings,
            LocalAiAutoStarter localAiAutoStarter,
            DefaultPromptProvider scenePromptService,
            AnkiCardGenerator cardGenerator,
            NoteParser noteParser,
            ILogger<AtomNoteSplitter> logger,
            IStringLocalizer<SharedResources> loc)
        {
            _aiClientService = aiClientService;
            _loc = loc;
            _taskManager = taskManager;
            _aiSettings = aiSettings;
            _vaultSettings = vaultSettings;
            _localAiAutoStarter = localAiAutoStarter;
            _scenePromptService = scenePromptService;
            _cardGenerator = cardGenerator;
            _noteParser = noteParser;
            _logger = logger;
        }

        private static string ComputeTargetsHash(List<string> targets)
        {
            targets.Sort(StringComparer.OrdinalIgnoreCase);
            var concat = string.Join("|", targets);
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(concat);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private static bool IsTransient(Exception ex)
        {
            if (ex is HttpRequestException) return true;
            if (ex is TaskCanceledException) return true; // timeout
            if (ex is System.ClientModel.ClientResultException) return true;
            if (ex.InnerException != null) return IsTransient(ex.InnerException);
            return false;
        }

        private static bool IsConnectionFailure(Exception ex)
        {
            var message = ex.Message + (ex.InnerException?.Message ?? "");
            return message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("积极拒绝", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("无法连接", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
                || ex.InnerException is System.Net.Sockets.SocketException;
        }

}
