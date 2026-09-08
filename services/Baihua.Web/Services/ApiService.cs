using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Baihua.Contracts.Ai;
using Baihua.Contracts.Assistant;
using Baihua.Contracts.Budget;
using Baihua.Contracts.ComputePool;
using Baihua.Contracts.Stock;
using Baihua.Contracts.Todo;
using Microsoft.Extensions.Localization;
using Baihua.Contracts.Anki;
using Baihua.Contracts.Achievements;
using Baihua.Contracts.Benchmark;
using Baihua.Contracts.Health;
using Baihua.Contracts.LocalModels;
using Baihua.Contracts.Metrics;
using Baihua.Contracts.OpenClaw;
using Baihua.Contracts.Scene;
using Baihua.Contracts.Master;
using Baihua.Contracts.Onboarding;
using Baihua.Contracts.Generate;
using Baihua.Contracts.Medical;
using Baihua.Contracts.Draw;

namespace Baihua.Web.Services
{
    /// <summary>
    /// AI 流式响应事件
    /// </summary>
    public class ChatStreamEvent
    {
        public string Type { get; set; } = "";
        public string? Content { get; set; }
        public string? ToolName { get; set; }
        public Dictionary<string, object?>? ToolArguments { get; set; }
    }

    public interface IApiService : IBaihuaHealthApi
    {
        /// <summary>快速健康检查，后台不可用时抛出异常（3秒超时）</summary>
        Task CheckHealthFastAsync(CancellationToken cancellationToken = default);
        Task<HealthFixResultDto> FixHealthIssuesAsync(CancellationToken cancellationToken = default);
        Task<JsonElement> SetupOpenClawAsync(CancellationToken cancellationToken = default);

        Task<List<TaskInfo>> GetTasksAsync();
        Task<TaskInfo?> GetTaskAsync(string taskId);
        Task<OnboardingStatusDto> GetOnboardingStatusAsync();
        Task<bool> CompleteOnboardingAsync();
        Task<VaultGenerationResponse> CreateVaultGenerationTaskAsync(string industry, string keyword, string? model = null, int noteCount = 30, bool generateCards = false, string? detailLevel = null);
        // 家庭病历本（成员档案 / 病历记录 / AI 诊断）
        Task<List<MedicalMemberDto>> GetMedicalMembersAsync(CancellationToken cancellationToken = default);
        Task<MedicalMemberDetailDto?> GetMedicalMemberDetailAsync(int id, CancellationToken cancellationToken = default);
        Task<MedicalMemberDto> CreateMedicalMemberAsync(CreateMedicalMemberRequest request, CancellationToken cancellationToken = default);
        Task<MedicalMemberDto?> UpdateMedicalMemberAsync(int id, UpdateMedicalMemberRequest request, CancellationToken cancellationToken = default);
        Task<bool> DeleteMedicalMemberAsync(int id, CancellationToken cancellationToken = default);
        Task<MedicalRecordDto> CreateMedicalRecordAsync(int memberId, CreateMedicalRecordRequest request, CancellationToken cancellationToken = default);
        Task<MedicalRecordDto?> UpdateMedicalRecordAsync(int id, UpdateMedicalRecordRequest request, CancellationToken cancellationToken = default);
        Task<bool> DeleteMedicalRecordAsync(int id, CancellationToken cancellationToken = default);
        Task<AiDiagnoseResultDto> RunAiDiagnosisAsync(AiDiagnoseRequest request, CancellationToken cancellationToken = default);
        Task<bool> DeleteAiDiagnosisAsync(int id, CancellationToken cancellationToken = default);

        Task<List<AiProviderInfo>> GetAiProvidersAsync();

        // 本地 AI 绘图（文生图 / 文生视频，走本机 ComfyUI）
        Task<DrawStatusDto?> GetDrawStatusAsync(CancellationToken cancellationToken = default);
        Task<DrawResultDto?> GenerateDrawImageAsync(DrawImageRequest request, CancellationToken cancellationToken = default);
        Task<DrawResultDto?> GenerateDrawVideoAsync(DrawVideoRequest request, CancellationToken cancellationToken = default);
        string GetDrawFileUrl(string filename, string subfolder = "", string type = "output");
        Task<StockRecommendationResponse> GetStockRecommendationsAsync(string? strategy = null, string? industry = null, string? horizon = null, string? prompt = null, string? direction = null, bool refresh = false, CancellationToken cancellationToken = default);
        Task<List<string>> GetStockIndustriesAsync(CancellationToken cancellationToken = default);
        Task<TopicSuggestionResponse> GetTopicSuggestionsAsync(string? context = null, bool refresh = false, CancellationToken cancellationToken = default);
        Task<StockEvaluationResponse> EvaluateStockAsync(string code, bool refresh = false, CancellationToken cancellationToken = default);
        Task<List<BudgetTransaction>> GetBudgetTransactionsAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default);
        Task<BudgetTransaction> AddBudgetTransactionAsync(BudgetCreateRequest request, CancellationToken cancellationToken = default);
        Task<bool> DeleteBudgetTransactionAsync(Guid id, CancellationToken cancellationToken = default);
        Task<BudgetSummary> GetBudgetSummaryAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default);

        // 个人待办清单
        Task<List<TodoItemDto>> GetTodosAsync(CancellationToken cancellationToken = default);
        Task<TodoItemDto> AddTodoAsync(CreateTodoRequest request, CancellationToken cancellationToken = default);
        Task<TodoItemDto?> UpdateTodoAsync(int id, UpdateTodoRequest request, CancellationToken cancellationToken = default);
        Task<bool> DeleteTodoAsync(int id, CancellationToken cancellationToken = default);
        Task<List<TodoGoalDto>> GetTodoGoalsAsync(CancellationToken cancellationToken = default);
        Task<AiTodoPreviewDto> GenerateTodosAsync(GenerateTodosRequest request, CancellationToken cancellationToken = default);
        Task<TodoGoalDto> SaveGeneratedTodosAsync(SaveGeneratedTodosRequest request, CancellationToken cancellationToken = default);
        Task<bool> DeleteTodoGoalAsync(int id, CancellationToken cancellationToken = default);

        Task<AssistantSettingsDto> GetAssistantSettingsAsync(CancellationToken cancellationToken = default);
        Task SaveAssistantSettingsAsync(AssistantSettingsDto settings, CancellationToken cancellationToken = default);
        Task<AssistantAnalysisDto?> GetAssistantTodayAnalysisAsync(CancellationToken cancellationToken = default);
        Task<AssistantAnalysisDto> RunAssistantAnalysisAsync(CancellationToken cancellationToken = default);
        Task<List<AssistantAnalysisDto>> GetAssistantHistoryAsync(int days = 14, CancellationToken cancellationToken = default);
        Task<List<UserActivityDto>> GetAssistantActivitiesAsync(CancellationToken cancellationToken = default);
        Task<Dictionary<string, int>> GetAssistantActivityCountsAsync(int days = 14, CancellationToken cancellationToken = default);
        Task<string> GetGlobalDetailLevelAsync(CancellationToken cancellationToken = default);
        Task SetGlobalDetailLevelAsync(string level, CancellationToken cancellationToken = default);
        Task<SearchResponse> SearchAsync(string query, string vaultId);
        Task<IndexStatusDto> GetIndexStatusAsync(string vaultId);
        Task<bool> RebuildIndexAsync(string vaultId, CancellationToken cancellationToken = default);
        Task<AiNoteResponse> AskAIAsync(string query, bool saveToVault);
        Task<AiTaskResponse> CreateAiTaskAsync(string query, bool saveToVault, string vaultId, string? model = null, bool autoSplit = false, string? systemPrompt = null, string? industry = null);
        Task<ChatResponse> ChatAsync(string message, CancellationToken cancellationToken = default);
        IAsyncEnumerable<string> StreamChatAsync(string message, string providerId, string model, List<(bool IsUser, string Content)>? history = null, CancellationToken cancellationToken = default);
        IAsyncEnumerable<ChatStreamEvent> StreamChatWithEventsAsync(string message, string providerId, string model, List<(bool IsUser, string Content)>? history = null, string? sessionId = null, CancellationToken cancellationToken = default);

        // 直接调用 Baihua.AI（纯 AI，无 RAG/记忆/Function Calling）
        Task<ChatResponse> ChatDirectAsync(string message, string? providerId = null, string? model = null, CancellationToken cancellationToken = default);
        IAsyncEnumerable<string> StreamChatDirectAsync(string message, string? providerId = null, string? model = null, List<(bool IsUser, string Content)>? history = null, CancellationToken cancellationToken = default);        IAsyncEnumerable<string> StreamLocalChatAsync(string message, string modelPath, string modelType, List<(bool IsUser, string Content)>? history = null, string? systemPrompt = null, CancellationToken cancellationToken = default);
        IAsyncEnumerable<string> StreamChatWithVaultAsync(string message, string model, List<(bool IsUser, string Content)>? history = null, CancellationToken cancellationToken = default);


        Task<List<LocalModelInfo>> ScanLocalModelsAsync(string? directory = null);

        // 本地视觉识别（Qwen2.5-VL + OpenVINO）
        Task<VisionStatusDto> GetVisionStatusAsync(CancellationToken cancellationToken = default);
        Task<VisionStatusDto> StartVisionServerAsync(CancellationToken cancellationToken = default);
        Task<VisionStatusDto> StopVisionServerAsync(CancellationToken cancellationToken = default);
        Task<VisionResultDto> RecognizeImageAsync(byte[] imageBytes, string prompt, string model, CancellationToken cancellationToken = default);

        // AI 绘图（ComfyUI）
        Task<ComfyStatusDto> GetComfyStatusAsync(CancellationToken cancellationToken = default);
        Task<ComfyGenerateResultDto> GenerateComfyImageAsync(string prompt, string negativePrompt, int width, int height, int steps, CancellationToken cancellationToken = default);
        Task<ComfyGenerateResultDto> GenerateComfyVideoAsync(string prompt, string negativePrompt, CancellationToken cancellationToken = default);
        Task<List<ComfyHistoryItemDto>> GetComfyHistoryAsync(int limit = 50, string? kind = null, CancellationToken cancellationToken = default);

        string GetBackendBaseUrl();
        Task<bool> DeleteTaskAsync(string taskId);
        Task<bool> DeleteAllTasksAsync();
        Task<bool> CancelTaskAsync(string taskId);
        Task<AiTaskResponse> RetryAiTaskAsync(string taskId, int timeoutMinutes = 0, string? model = null);

        Task<VaultNoteResponse?> ReadVaultNoteAsync(string path, string vaultId);
        Task<bool> WriteVaultNoteAsync(string path, string content, string vaultId);
        Task<GenerateMissingNoteResponse?> GenerateMissingNoteAsync(string linkPath, string vaultId);
        Task<VaultBrowseResponse?> GetVaultBrowseAsync(string vaultId, string? path = null);
        Task<VaultNotesBatchResponse?> GetVaultNotesBatchAsync(string vaultId);

        Task<string?> GetVaultRootAsync();
        Task<bool> SetVaultRootAsync(string vaultPath);

        // AI 配置管理
        Task<List<AiConfigProvider>> GetAiConfigProvidersAsync();
        Task<AiConfigProvider?> GetAiConfigProviderAsync(string providerId);
        Task<SaveAiProviderResult> SaveAiConfigProviderAsync(SaveAiProviderRequest request);
        Task<bool> DeleteAiConfigProviderAsync(string providerId);
        Task<EnvConfigHelp?> GetAiEnvConfigHelpAsync();
        Task<List<AiProviderPreset>> GetAiProviderPresetsAsync();
        Task<AiCategoryConfigDto> GetAiCategoryConfigAsync();
        Task<bool> SaveAiCategoryConfigAsync(List<AiCategoryAssignmentDto> assignments);

        // 算力池（局域网算力总览与选用）
        Task<ComputePoolViewDto?> GetComputePoolAsync();
        Task<bool> RefreshComputePoolAsync();
        Task<(bool ok, string? error)> SelectComputeModelAsync(string serverId, string modelName);
        Task<BenchmarkRunResultDto?> RunComputeBenchmarkAsync(string serverId, string modelName);
        Task<(bool ok, string? error)> PullComputeModelAsync(string serverId, string modelName);
        Task<(bool ok, string? message, string? error)> DeployComputeModelAsync(string serverId, string modelName);
        Task<(bool ok, string? text, string? error)> RunPoolChatAsync(string modelName, string prompt);
        Task<(bool ok, string? error)> DeleteComputePeerAsync(Guid peerId);

        // Embedding 配置
        Task<EmbeddingConfigDto> GetEmbeddingConfigAsync();
        Task<SaveAiProviderResult> SaveEmbeddingConfigAsync(SaveEmbeddingConfigRequest request);

        // 每日一帖 Anki
        Task<DailyCardResultDto> GetDailyCardAsync(string vaultId);
        Task<(bool Success, DailyProgressDto? Progress)> SubmitDailyAnswerAsync(string vaultId, string cardId, string result);
        Task<DailyProgressDto> GetDailyProgressAsync(string vaultId);
        Task<bool> SaveCustomCardAsync(string vaultId, CustomCardRequestDto request);
        Task<int> GetAnkiCardCountAsync(string vaultId);
        Task<BatchGenerateResultDto?> GenerateAnkiCardsBatchAsync(string vaultId, string directory, bool recursive = true);
        Task<int> GetVaultCardCountAsync(string vaultId);
        Task<int> GetVaultNoteCountAsync(string vaultId);
        Task<GenerateCardsTaskDto?> GenerateAllCardsAsync(string vaultId);
        Task<AnkiSearchResult> SearchAnkiCardsAsync(string? query, string? vaultId, int limit = 100);
        Task<DeckListResult> GetAnkiDecksAsync(string? vaultId);

        // 成就与赛舟榜
        Task<List<LearnerDto>> GetLearnersAsync();
        Task<LearnerDto> CreateLearnerAsync(CreateLearnerRequest request);
        Task<bool> SetDefaultLearnerAsync(int id);
        Task<bool> DeleteLearnerAsync(int id);
        Task<List<AchievementDto>> GetAchievementsAsync(int learnerId);
        Task<List<AchievementDto>> CheckAchievementsAsync(int learnerId);
        Task<List<RewardProgressDto>> GetRewardProgressAsync(string? vaultId = null);
        Task<RewardConfigDto> CreateRewardAsync(CreateRewardRequest request);
        Task<List<RewardClaimDto>> TriggerRewardsAsync(string? vaultId = null);
        Task<QuizSessionDto> CreateQuizAsync(CreateQuizRequest request);
        Task<QuizResultDto> SubmitQuizAnswerAsync(SubmitAnswerRequest request);
        Task<List<LeaderboardEntryDto>> GetLeaderboardAsync(string type, string? vaultId = null);
        Task<DashboardDataDto> GetDashboardAsync(string? vaultId = null, int? learnerId = null);
        Task<CheckinDataDto> GetCheckinDataAsync(string? vaultId = null);
        Task<CheckinMakeupResultDto> MakeupCheckinAsync(CheckinMakeupRequest request);
        Task<WeeklyCompareResultDto> GetWeeklyCompareAsync(string? vaultId = null, int? learnerId = null);
        Task<List<LeaderboardEntryDto>> GetRoleLeaderboardAsync(string role, string? vaultId = null);
        Task<LeaderboardSettingsDto> GetAllFamilyTabSettingAsync();
        Task<LeaderboardSettingsDto> SetAllFamilyTabSettingAsync(bool enabled);

        // Obsidian 操作
        Task<bool> OpenInObsidianAsync(CancellationToken cancellationToken = default);
        Task<bool> OpenVaultInObsidianAsync(string path);

        // NotesMD CLI
        Task<NotesMdCliStatus?> GetNotesMdCliStatusAsync();
        Task<bool> AddVaultToNotesMdCliAsync(string path);
        Task<NotesMdBatchResult?> BatchAddVaultsToNotesMdCliAsync(List<string> paths);

        // 平台信息
        Task<PlatformInfoResponse?> GetPlatformAsync(CancellationToken cancellationToken = default);

        // 本地模型部署
        Task<HardwareInfoDto?> GetHardwareInfoAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

        Task<List<DownloadSourceDto>> GetDownloadSourcesAsync(CancellationToken cancellationToken = default);
        Task<DownloadDirectoryConfigDto?> GetDownloadConfigAsync(CancellationToken cancellationToken = default);
        Task<bool> SaveDownloadConfigAsync(DownloadDirectoryConfigDto config, CancellationToken cancellationToken = default);

        // 运行中模型管理
        // 运行中模型管理
        Task<List<RunningModelDto>> GetRunningModelsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
        Task<List<string>> GetAvailableModelsAsync(string toolId, CancellationToken cancellationToken = default);
        Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(CancellationToken cancellationToken = default);
        Task<bool> DeleteModelAsync(DeleteModelRequest request, CancellationToken cancellationToken = default);


        // 请求指标统计
        Task<MetricsSummary?> GetMetricsSummaryAsync(CancellationToken cancellationToken = default);
        Task<List<RequestMetric>> GetSlowestRequestsAsync(int count = 10, CancellationToken cancellationToken = default);
        Task<List<PathFrequency>> GetFrequentPathsAsync(int count = 10, CancellationToken cancellationToken = default);
        Task<List<RequestMetric>> GetRecentErrorsAsync(int count = 10, CancellationToken cancellationToken = default);
        Task<bool> ClearMetricsAsync(CancellationToken cancellationToken = default);

        // OpenClaw 任务
        Task<OpenClawTaskDto> CreateOpenClawTaskAsync(string prompt);
        Task<List<OpenClawTaskDto>> GetOpenClawTasksAsync();
        Task<OpenClawTaskDto?> GetOpenClawTaskAsync(int id);
        Task<string?> GetOpenClawReportAsync(int id);
        Task<bool> DeleteOpenClawTaskAsync(int id);
        Task<bool> CancelOpenClawTaskAsync(int id);
        Task<OpenClawLocalAiConfigDto> GetOpenClawLocalAiConfigAsync();
        Task<bool> SaveOpenClawLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request);
        Task<List<OpenClawLocalModelDto>> ScanOpenClawLocalModelsAsync(string provider);
        Task<LocalAiServiceStatusDto> DetectAndStartOpenClawLocalAiAsync(string provider);
        Task<OpenClawDefaultModelDto> GetOpenClawDefaultModelAsync();
        Task<bool> SetOpenClawDefaultModelAsync(string model);
        Task<ModelProfileListDto> GetModelProfilesAsync();
        Task<bool> SetModelProfileAsync(string profile);
        Task<bool> SyncLocalModelsToOpenClawAsync(string provider);

        // 模型基准测试
        Task<List<RecommendedBenchmarkModel>> GetBenchmarkModelsAsync(string? category = null);

        Task<List<BenchmarkPrompt>> GetBenchmarkPromptsAsync(string? category = null);
        Task<bool> RunBenchmarkAsync(BenchmarkModelConfig model, string[]? promptIds = null);
        Task<bool> StopBenchmarkAsync();
        Task<BenchmarkStatusDto> GetBenchmarkStatusAsync();
        Task<List<BenchmarkSession>> GetBenchmarkHistoryAsync(string? category = null);
        Task<List<BenchmarkLeaderboardEntry>> GetBenchmarkLeaderboardAsync(string? category = null);
        Task<bool> DeleteBenchmarkSessionAsync(string sessionId);
        Task<bool> ClearBenchmarkHistoryAsync();

        // AI 调用性能指标
        Task<AiMetricsSummaryDto?> GetAiMetricsSummaryAsync(int days = 7, CancellationToken cancellationToken = default);
        Task<List<AiProviderMetricsDto>> GetAiProviderMetricsAsync(int days = 7, CancellationToken cancellationToken = default);
        Task<List<AiModelMetricsDto>> GetAiModelMetricsAsync(int days = 7, CancellationToken cancellationToken = default);
        Task<List<AiMetricsTrendDto>> GetAiMetricsTrendsAsync(int days = 7, CancellationToken cancellationToken = default);
        Task<List<AiUsageMetricDto>> GetAiRecentMetricsAsync(int limit = 50, int days = 7, CancellationToken cancellationToken = default);

        // 场景管理
        Task SetSceneAsync(Baihua.Contracts.Scene.AppScene scene, CancellationToken cancellationToken = default);

        // 虚拟师父
        Task<CreateMasterResponse> CreateMasterAsync(string goal, string industry, CancellationToken cancellationToken = default);
        IAsyncEnumerable<ChatStreamEvent> StreamMasterChatAsync(string masterId, string message, string stage, List<(bool IsUser, string Content)>? history = null, CancellationToken cancellationToken = default);
        Task<StageCompleteResponse> MasterStageCompleteAsync(string masterId, string stageName, CancellationToken cancellationToken = default);
        Task<ApprenticeProfileResponse> GetMasterProfileAsync(string masterId, CancellationToken cancellationToken = default);
        Task<AssessResponse> MasterAssessAsync(string masterId, string type = "capability", CancellationToken cancellationToken = default);
        Task<List<MasterListItem>> GetMastersAsync(CancellationToken cancellationToken = default);
        Task<bool> DeleteMasterAsync(string masterId, CancellationToken cancellationToken = default);
        Task<MasterEvictResponse> EvictMasterAsync(string masterId, CancellationToken cancellationToken = default);
        Task<ApprenticeProfileResponse> UpdateMasterProfileAsync(string masterId, UpdateProfileRequest request, CancellationToken cancellationToken = default);
        Task<VaultFocusListResponse> GetVaultFocusAsync(string masterId, CancellationToken cancellationToken = default);
        Task<VaultFocusUpdateResponse> UpdateVaultFocusAsync(string masterId, VaultFocusUpdateRequest request, CancellationToken cancellationToken = default);
        Task<VaultFocusUpdateResponse> RemoveVaultFocusAsync(string masterId, string vaultId, CancellationToken cancellationToken = default);

        Task<byte[]?> SynthesizeSpeechAsync(string text, string voice, float speed = 1.0f, CancellationToken cancellationToken = default);
        Task<List<TtsVoice>?> GetTtsVoicesAsync(CancellationToken cancellationToken = default);
    }


    public partial class ApiService : IApiService

    {
        /// <summary>任务列表、提供方、删除等；后台不可达时尽快失败，避免整页卡住。</summary>
        private static readonly TimeSpan QuickCallTimeout = TimeSpan.FromSeconds(15);

        /// <summary>拆笔记、AI 等长请求，仅用 HttpClient 全局超时。</summary>
        private static readonly TimeSpan LongHttpTimeout = TimeSpan.FromMinutes(5);
        private static readonly JsonSerializerOptions _caseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient _httpClient;
        private readonly HttpClient _longHttpClient;
        private readonly HttpClient _aiHttpClient;
        private readonly HttpClient _vaultHttpClient;
        private readonly SettingsService _settingsService;
        private readonly ILogger<ApiService> _logger;
        private readonly IStringLocalizer<Baihua.Web.Localization.SharedResources> _loc;
        private readonly ApiCallMetricsService? _metricsService;
        private readonly EndToEndPerformanceService? _e2eService;

        private readonly string _fallbackBaseUrl = "http://127.0.0.1:8788";

        public ApiService(IHttpClientFactory httpClientFactory, SettingsService settingsService, ILogger<ApiService> logger, IStringLocalizer<Baihua.Web.Localization.SharedResources> loc, IServiceProvider serviceProvider)
        {
            _settingsService = settingsService;
            _logger = logger;
            _loc = loc;
            _httpClient = httpClientFactory.CreateClient("FamilyApi");
            _longHttpClient = httpClientFactory.CreateClient("FamilyApiLong");
            _aiHttpClient = httpClientFactory.CreateClient("AiApi");
            _vaultHttpClient = httpClientFactory.CreateClient("VaultApi");
            
            // 延迟获取服务避免循环依赖
            _metricsService = serviceProvider.GetService<ApiCallMetricsService>();
            _e2eService = serviceProvider.GetService<EndToEndPerformanceService>();
            
            EnsurePrimaryBaseAddress();
        }
        
        public async Task<SystemHealthReportDto> GetFullHealthAsync(CancellationToken cancellationToken = default)
        {
            // 完整健康报告在后端有 25s 预算；这里加一个额外超时上限避免前端长时间等待。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var response = await GetWithFallbackAsync("/api/health/full", linked.Token);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<SystemHealthReportDto>(linked.Token)
                   ?? new SystemHealthReportDto();
        }

        public async Task CheckHealthFastAsync(CancellationToken cancellationToken = default)
        {
            // 快速健康检查：3秒超时，后台不可用时抛出异常
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var response = await GetWithFallbackAsync("/api/health/simple", linked.Token);
            response.EnsureSuccessStatusCode();
        }

        public async Task<HealthFixResultDto> FixHealthIssuesAsync(CancellationToken cancellationToken = default)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var response = await _httpClient.PostAsync("/api/health/fix", null, linked.Token);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<HealthFixResultDto>(linked.Token)
                   ?? new HealthFixResultDto();
        }

        public async Task<JsonElement> SetupOpenClawAsync(CancellationToken cancellationToken = default)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var response = await _httpClient.PostAsync("/api/health/setup-openclaw", null, linked.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(linked.Token);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        private string GetPrimaryBaseUrl()
        {
            // Docker 部署：compose 通过环境变量注入 FamilyApi__BaseUrl（服务名 DNS），
            // 必须优先于 settings 文件，否则容器内会回退到 127.0.0.1 导致 connection refused
            var envBase = Environment.GetEnvironmentVariable("FamilyApi__BaseUrl");
            if (!string.IsNullOrWhiteSpace(envBase))
            {
                return BaihuaEndpointHelper.NormalizeOutboundBaseUrl(envBase, _fallbackBaseUrl);
            }
            return BaihuaEndpointHelper.NormalizeOutboundBaseUrl(_settingsService.BackendUrl, _fallbackBaseUrl);
        }

        private void EnsurePrimaryBaseAddress()
        {
            var current = GetPrimaryBaseUrl();
            if (_httpClient.BaseAddress == null || _httpClient.BaseAddress.ToString().TrimEnd('/') != current)
            {
                _httpClient.BaseAddress = new Uri(current);
            }
        }

        private static bool ShouldFallback(System.Net.HttpStatusCode code)
        {
            return code == System.Net.HttpStatusCode.MethodNotAllowed ||
                   code == System.Net.HttpStatusCode.NotFound;
        }

        private async Task<HttpResponseMessage> GetWithFallbackAsync(string path, CancellationToken cancellationToken = default, HttpClient? client = null)
        {
            EnsurePrimaryBaseAddress();
            var primaryBaseUrl = GetPrimaryBaseUrl();
            var http = client ?? _httpClient;
            var response = await http.GetAsync(path, cancellationToken);
            if (!ShouldFallback(response.StatusCode) || primaryBaseUrl == _fallbackBaseUrl)
            {
                return response;
            }

            response.Dispose();
            using var fallbackClient = new HttpClient();
            fallbackClient.BaseAddress = new Uri(_fallbackBaseUrl);
            fallbackClient.Timeout = LongHttpTimeout;
            return await fallbackClient.GetAsync(path, cancellationToken);
        }

        private async Task<HttpResponseMessage> PostWithFallbackAsync(string path, HttpContent? body, CancellationToken cancellationToken = default, HttpClient? client = null)
        {
            EnsurePrimaryBaseAddress();
            var primaryBaseUrl = GetPrimaryBaseUrl();
            var http = client ?? _httpClient;
            var response = await http.PostAsync(path, body, cancellationToken);
            if (!ShouldFallback(response.StatusCode) || primaryBaseUrl == _fallbackBaseUrl)
            {
                return response;
            }

            response.Dispose();
            using var fallbackClient = new HttpClient();
            fallbackClient.BaseAddress = new Uri(_fallbackBaseUrl);
            fallbackClient.Timeout = LongHttpTimeout;
            if (body == null)
            {
                return await fallbackClient.PostAsync(path, null);
            }
            var fallbackBody = new StringContent(await body.ReadAsStringAsync(), Encoding.UTF8, "application/json");
            return await fallbackClient.PostAsync(path, fallbackBody);
        }
        #region Metrics Tracking

        /// <summary>
        /// 记录 API 调用到端到端追踪（指标由 MetricsRecordingHandler 自动记录）
        /// </summary>
        private void RecordApiCall(string endpoint, string method, long elapsedMs, bool success, int? statusCode = null, string? error = null)
        {

        }
        
        /// <summary>
        /// 包装 GET 请求并记录指标
        /// </summary>
        private async Task<HttpResponseMessage> GetWithMetricsAsync(string endpoint, CancellationToken cancellationToken = default, HttpClient? client = null)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await GetWithFallbackAsync(endpoint, cancellationToken, client);
                stopwatch.Stop();
                RecordApiCall(endpoint, "GET", stopwatch.ElapsedMilliseconds, response.IsSuccessStatusCode, (int)response.StatusCode);
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                RecordApiCall(endpoint, "GET", stopwatch.ElapsedMilliseconds, false, null, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 包装 POST 请求并记录指标
        /// </summary>
        private async Task<HttpResponseMessage> PostWithMetricsAsync(string endpoint, HttpContent? content, CancellationToken cancellationToken = default, HttpClient? client = null)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await PostWithFallbackAsync(endpoint, content, cancellationToken, client);
                stopwatch.Stop();
                RecordApiCall(endpoint, "POST", stopwatch.ElapsedMilliseconds, response.IsSuccessStatusCode, (int)response.StatusCode);
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                RecordApiCall(endpoint, "POST", stopwatch.ElapsedMilliseconds, false, null, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 包装 DELETE 请求并记录指标
        /// </summary>
        private async Task<HttpResponseMessage> DeleteWithMetricsAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await _httpClient.DeleteAsync(endpoint, cancellationToken);
                stopwatch.Stop();
                RecordApiCall(endpoint, "DELETE", stopwatch.ElapsedMilliseconds, response.IsSuccessStatusCode, (int)response.StatusCode);
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                RecordApiCall(endpoint, "DELETE", stopwatch.ElapsedMilliseconds, false, null, ex.Message);
                throw;
            }
        }

        #endregion

        public async Task<byte[]?> SynthesizeSpeechAsync(string text, string voice, float speed = 1.0f, CancellationToken cancellationToken = default)
        {
            var payload = new { text, voice, speed };
            var response = await _aiHttpClient.PostAsJsonAsync("/api/ai/tts/speech", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TTS API returned {Status}", response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        public async Task<List<TtsVoice>?> GetTtsVoicesAsync(CancellationToken cancellationToken = default)
        {
            var response = await _aiHttpClient.GetAsync("/api/ai/tts/voices", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var list = await response.Content.ReadFromJsonAsync<TtsVoiceList>(cancellationToken: cancellationToken);
            return list?.Voices;
        }

    }
}
