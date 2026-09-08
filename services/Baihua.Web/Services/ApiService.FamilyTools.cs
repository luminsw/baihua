using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Baihua.Contracts.Ai;
using Baihua.Contracts.Assistant;
using Baihua.Contracts.Budget;
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

namespace Baihua.Web.Services
{
    public partial class ApiService {
        // AI 配置管理 API
        public async Task<List<AiConfigProvider>> GetAiConfigProvidersAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.GetAsync("/api/ai/config/providers", quick.Token);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<List<AiConfigProvider>>(quick.Token);
                if (result != null)
                {
                    foreach (var provider in result)
                    {
                        var toolId = GetToolIdFromProviderUrl(provider.BaseUrl);
                        if (toolId != null)
                        {
                            try
                            {
                                using var localCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                                var availableModels = await GetAvailableModelsAsync(toolId, localCts.Token);
                                MergeModels(provider.Models, availableModels);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "动态获取本地模型列表失败，ProviderId: {ProviderId}", provider.Id);
                            }
                        }
                    }
                }
                return result ?? new List<AiConfigProvider>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 AI 配置列表失败");
                return new List<AiConfigProvider>();
            }
        }

        public async Task<AiConfigProvider?> GetAiConfigProviderAsync(string providerId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.GetAsync($"/api/ai/config/providers/{Uri.EscapeDataString(providerId)}", quick.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AiConfigProvider>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 AI 配置详情失败，ProviderId: {ProviderId}", providerId);
                return null;
            }
        }

        public async Task<SaveAiProviderResult> SaveAiConfigProviderAsync(SaveAiProviderRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var json = JsonSerializer.Serialize(request);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _aiHttpClient.PostAsync("/api/ai/config/providers", httpContent, quick.Token);
                
                if (response.IsSuccessStatusCode)
                {
                    return new SaveAiProviderResult { Success = true, Message = _loc["Api_SaveSuccess"] };
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return new SaveAiProviderResult 
                    { 
                        Success = false, 
                        Message = _loc["Api_SaveFailedWithError", error!]
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存 AI 配置失败，ProviderId: {ProviderId}", request.Id);
                return new SaveAiProviderResult
                {
                    Success = false,
                    Message = _loc["Api_SaveFailedWithError", ex.Message]
                };
            }
        }

        public async Task<bool> DeleteAiConfigProviderAsync(string providerId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.DeleteAsync($"/api/ai/config/providers/{Uri.EscapeDataString(providerId)}", quick.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除 AI 配置失败，ProviderId: {ProviderId}", providerId);
                return false;
            }
        }

        public async Task<EnvConfigHelp?> GetAiEnvConfigHelpAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.GetAsync("/api/ai/config/env-help", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<EnvConfigHelp>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取环境变量配置帮助失败");
                return null;
            }
        }

        public async Task<List<AiProviderPreset>> GetAiProviderPresetsAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.GetAsync("/api/ai/config/presets", quick.Token);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<List<AiProviderPreset>>(quick.Token);
                return result ?? new List<AiProviderPreset>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 AI 提供商预设失败");
                return new List<AiProviderPreset>();
            }
        }

        public async Task<AiCategoryConfigDto> GetAiCategoryConfigAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.GetAsync("/api/ai/config/categories", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AiCategoryConfigDto>(quick.Token)
                       ?? new AiCategoryConfigDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务分类模型配置失败");
                return new AiCategoryConfigDto();
            }
        }

        public async Task<bool> SaveAiCategoryConfigAsync(List<AiCategoryAssignmentDto> assignments)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.PutAsync("/api/ai/config/categories",
                    JsonContent.Create(new SaveAiCategoriesRequest { Assignments = assignments ?? new List<AiCategoryAssignmentDto>() }),
                    quick.Token);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存任务分类模型配置失败");
                return false;
            }
        }

        public async Task<EmbeddingConfigDto> GetEmbeddingConfigAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.GetAsync("/api/embedding/config", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<EmbeddingConfigDto>(quick.Token) ?? new EmbeddingConfigDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 Embedding 配置失败");
                return new EmbeddingConfigDto();
            }
        }

        public async Task<SaveAiProviderResult> SaveEmbeddingConfigAsync(SaveEmbeddingConfigRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _aiHttpClient.PostAsync("/api/embedding/config", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<SaveAiProviderResult>(quick.Token)
                       ?? new SaveAiProviderResult { Success = false, Message = _loc["Api_UnknownError"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存 Embedding 配置失败");
                return new SaveAiProviderResult { Success = false, Message = ex.Message };
            }
        }

        public async Task<DailyCardResultDto> GetDailyCardAsync(string vaultId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/anki/daily?vaultId={Uri.EscapeDataString(vaultId)}", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<DailyCardResultDto>(quick.Token) ?? new DailyCardResultDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取每日卡片失败");
                return new DailyCardResultDto { HasCard = false, Message = _loc["Api_FetchFailed"] };
            }
        }

        public async Task<(bool Success, DailyProgressDto? Progress)> SubmitDailyAnswerAsync(string vaultId, string cardId, string result)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var request = new DailyAnswerRequestDto { CardId = cardId, Result = result };
                var response = await PostWithMetricsAsync($"/api/anki/daily/answer?vaultId={Uri.EscapeDataString(vaultId)}", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(quick.Token);
                var success = json.GetProperty("success").GetBoolean();
                var progress = json.TryGetProperty("progress", out var p) ? JsonSerializer.Deserialize<DailyProgressDto>(p.GetRawText(), _caseInsensitiveJsonOptions) : null;
                return (success, progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提交每日卡片答案失败");
                return (false, null);
            }
        }

        public async Task<DailyProgressDto> GetDailyProgressAsync(string vaultId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/anki/daily/progress?vaultId={Uri.EscapeDataString(vaultId)}", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<DailyProgressDto>(quick.Token) ?? new DailyProgressDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取每日进度失败");
                return new DailyProgressDto();
            }
        }

        public async Task<bool> SaveCustomCardAsync(string vaultId, CustomCardRequestDto request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync($"/api/anki/custom-card?vaultId={Uri.EscapeDataString(vaultId)}", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存自定义卡片失败");
                return false;
            }
        }

        public async Task<int> GetAnkiCardCountAsync(string vaultId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/anki/card-count?vaultId={Uri.EscapeDataString(vaultId)}", quick.Token);
                response.EnsureSuccessStatusCode();
                var count = await response.Content.ReadFromJsonAsync<int>(quick.Token);
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取卡片数量失败");
                return 0;
            }
        }

        public async Task<BatchGenerateResultDto?> GenerateAnkiCardsBatchAsync(string vaultId, string directory, bool recursive = true)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var request = new BatchGenerateRequestDto { Directory = directory, Recursive = recursive };
                var response = await PostWithMetricsAsync($"/api/anki/generate-batch?vaultId={Uri.EscapeDataString(vaultId)}", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<BatchGenerateResultDto>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量生成记忆卡片失败");
                return null;
            }
        }

        public async Task<int> GetVaultCardCountAsync(string vaultId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/anki/vault-card-count?vaultId={Uri.EscapeDataString(vaultId)}", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<int>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取知识库卡片数量失败");
                return 0;
            }
        }

        public async Task<int> GetVaultNoteCountAsync(string vaultId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _vaultHttpClient.GetAsync($"/vault/note-count?vaultId={Uri.EscapeDataString(vaultId)}", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<int>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取知识库笔记数量失败");
                return 0;
            }
        }

        public async Task<AnkiSearchResult> SearchAnkiCardsAsync(string? query, string? vaultId, int limit = 100)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = $"/api/anki/search?limit={limit}";
                if (!string.IsNullOrWhiteSpace(query))
                    url += $"&q={Uri.EscapeDataString(query)}";
                if (!string.IsNullOrWhiteSpace(vaultId))
                    url += $"&vaultId={Uri.EscapeDataString(vaultId)}";
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AnkiSearchResult>(quick.Token) ?? new AnkiSearchResult { Cards = new() };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索 Anki 卡片失败");
                return new AnkiSearchResult { Cards = new() };
            }
        }

        public async Task<DeckListResult> GetAnkiDecksAsync(string? vaultId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = "/api/anki/decks";
                if (!string.IsNullOrWhiteSpace(vaultId))
                    url += $"?vaultId={Uri.EscapeDataString(vaultId)}";
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<DeckListResult>(quick.Token) ?? new DeckListResult { Decks = new() };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 Anki 牌组失败");
                return new DeckListResult { Decks = new() };
            }
        }

        public async Task<GenerateCardsTaskDto?> GenerateAllCardsAsync(string vaultId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/anki/generate-all-ai?vaultId={Uri.EscapeDataString(vaultId)}", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<GenerateCardsTaskDto>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "为知识库生成全部记忆卡片失败");
                return null;
            }
        }

        public async Task<List<LearnerDto>> GetLearnersAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync("/api/achievements/learners", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<LearnerDto>>(quick.Token) ?? new List<LearnerDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取学习者失败");
                return new List<LearnerDto>();
            }
        }

        public async Task<LearnerDto> CreateLearnerAsync(CreateLearnerRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync("/api/achievements/learners", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<LearnerDto>(quick.Token) ?? new LearnerDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建学习者失败");
                return new LearnerDto();
            }
        }

        public async Task<bool> SetDefaultLearnerAsync(int id)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync($"/api/achievements/learners/{id}/default", null, quick.Token);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置默认学习者失败");
                return false;
            }
        }

        public async Task<bool> DeleteLearnerAsync(int id)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await _httpClient.DeleteAsync($"/api/achievements/learners/{id}", quick.Token);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除学习者失败");
                return false;
            }
        }

        public async Task<List<AchievementDto>> GetAchievementsAsync(int learnerId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/achievements?learnerId={learnerId}", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<AchievementDto>>(quick.Token) ?? new List<AchievementDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取成就失败");
                return new List<AchievementDto>();
            }
        }

        public async Task<List<AchievementDto>> CheckAchievementsAsync(int learnerId)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync($"/api/achievements/check?learnerId={learnerId}", null, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<AchievementDto>>(quick.Token) ?? new List<AchievementDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查成就失败");
                return new List<AchievementDto>();
            }
        }

        // ===== FAM-31 家庭奖励 =====

        public async Task<List<RewardProgressDto>> GetRewardProgressAsync(string? vaultId = null)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = "/api/rewards/progress";
                if (!string.IsNullOrEmpty(vaultId))
                    url += $"?vaultId={Uri.EscapeDataString(vaultId)}";
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<RewardProgressDto>>(quick.Token) ?? new List<RewardProgressDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取奖励进度失败");
                return new List<RewardProgressDto>();
            }
        }

        public async Task<RewardConfigDto> CreateRewardAsync(CreateRewardRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync("/api/rewards", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<RewardConfigDto>(quick.Token) ?? new RewardConfigDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建奖励失败");
                return new RewardConfigDto();
            }
        }

        public async Task<List<RewardClaimDto>> TriggerRewardsAsync(string? vaultId = null)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = "/api/rewards/trigger";
                if (!string.IsNullOrEmpty(vaultId))
                    url += $"?vaultId={Uri.EscapeDataString(vaultId)}";
                var response = await PostWithMetricsAsync(url, null, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<RewardClaimDto>>(quick.Token) ?? new List<RewardClaimDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "触发奖励检查失败");
                return new List<RewardClaimDto>();
            }
        }

        // ===== FAM-30 亲子互考 =====

        public async Task<QuizSessionDto> CreateQuizAsync(CreateQuizRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync("/api/quiz/create", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<QuizSessionDto>(quick.Token) ?? new QuizSessionDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建互考失败");
                throw;
            }
        }

        public async Task<QuizResultDto> SubmitQuizAnswerAsync(SubmitAnswerRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync("/api/quiz/answer", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<QuizResultDto>(quick.Token) ?? new QuizResultDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提交互考答案失败");
                throw;
            }
        }

        public async Task<List<LeaderboardEntryDto>> GetLeaderboardAsync(string type, string? vaultId = null)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = $"/api/achievements/leaderboard/{type}";
                if (!string.IsNullOrEmpty(vaultId))
                    url += $"?vaultId={Uri.EscapeDataString(vaultId)}";
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<LeaderboardEntryDto>>(quick.Token) ?? new List<LeaderboardEntryDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取赛舟榜失败");
                return new List<LeaderboardEntryDto>();
            }
        }

        public async Task<DashboardDataDto> GetDashboardAsync(string? vaultId = null, int? learnerId = null)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = "/api/achievements/dashboard";
                var query = new List<string>();
                if (!string.IsNullOrEmpty(vaultId))
                    query.Add($"vaultId={Uri.EscapeDataString(vaultId)}");
                if (learnerId.HasValue)
                    query.Add($"learnerId={learnerId.Value}");
                if (query.Count > 0)
                    url += "?" + string.Join("&", query);
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<DashboardDataDto>(quick.Token) ?? new DashboardDataDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取家长看板失败");
                return new DashboardDataDto();
            }
        }

        public async Task<CheckinDataDto> GetCheckinDataAsync(string? vaultId = null)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = "/api/checkin";
                if (!string.IsNullOrEmpty(vaultId))
                    url += $"?vaultId={Uri.EscapeDataString(vaultId)}";
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<CheckinDataDto>(quick.Token) ?? new CheckinDataDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取学习打卡数据失败");
                return new CheckinDataDto();
            }
        }

        public async Task<CheckinMakeupResultDto> MakeupCheckinAsync(CheckinMakeupRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync("/api/checkin/makeup", JsonContent.Create(request), quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<CheckinMakeupResultDto>(quick.Token) ?? new CheckinMakeupResultDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "补签失败");
                return new CheckinMakeupResultDto { Success = false, Message = "补签失败" };
            }
        }

        public async Task<WeeklyCompareResultDto> GetWeeklyCompareAsync(string? vaultId = null, int? learnerId = null)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = "/api/achievements/leaderboard/compare";
                var query = new List<string>();
                if (!string.IsNullOrEmpty(vaultId)) query.Add($"vaultId={Uri.EscapeDataString(vaultId)}");
                if (learnerId.HasValue) query.Add($"learnerId={learnerId.Value}");
                if (query.Count > 0) url += "?" + string.Join("&", query);
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<WeeklyCompareResultDto>(quick.Token) ?? new WeeklyCompareResultDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取和自己比数据失败");
                return new WeeklyCompareResultDto();
            }
        }

        public async Task<List<LeaderboardEntryDto>> GetRoleLeaderboardAsync(string role, string? vaultId = null)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var url = $"/api/achievements/leaderboard/role?role={Uri.EscapeDataString(role)}";
                if (!string.IsNullOrEmpty(vaultId)) url += $"&vaultId={Uri.EscapeDataString(vaultId)}";
                var response = await GetWithMetricsAsync(url, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<LeaderboardEntryDto>>(quick.Token) ?? new List<LeaderboardEntryDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取角色排行榜失败");
                return new List<LeaderboardEntryDto>();
            }
        }

        public async Task<LeaderboardSettingsDto> GetAllFamilyTabSettingAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync("/api/achievements/leaderboard/settings/all-family-tab", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<LeaderboardSettingsDto>(quick.Token) ?? new LeaderboardSettingsDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取全家排行设置失败");
                return new LeaderboardSettingsDto();
            }
        }

        public async Task<LeaderboardSettingsDto> SetAllFamilyTabSettingAsync(bool enabled)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var body = JsonSerializer.Serialize(new LeaderboardSettingsDto { AllFamilyTabEnabled = enabled });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/achievements/leaderboard/settings/all-family-tab", content, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<LeaderboardSettingsDto>(quick.Token) ?? new LeaderboardSettingsDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置全家排行失败");
                return new LeaderboardSettingsDto();
            }
        }

        public async Task<bool> OpenInObsidianAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, 
                    new CancellationTokenSource(QuickCallTimeout).Token);
                var content = new StringContent("", Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/obsidian/open-current-vault", content, linked.Token);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方取消，向上传播
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "在 Obsidian 中打开知识库失败");
                return false;
            }
        }

        public async Task<bool> OpenVaultInObsidianAsync(string path)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var payload = new { path = path };
                var json = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/obsidian/open", httpContent, quick.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "在 Obsidian 中打开知识库失败，路径: {Path}", path);
                return false;
            }
        }

        public async Task<NotesMdCliStatus?> GetNotesMdCliStatusAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithFallbackAsync("/api/notesmd-cli/status", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<NotesMdCliStatus>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 NotesMD CLI 状态失败");
                return null;
            }
        }

        public async Task<bool> AddVaultToNotesMdCliAsync(string path)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var payload = new { path };
                var json = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/notesmd-cli/add-vault", httpContent, quick.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加知识库到 NotesMD CLI 失败，路径: {Path}", path);
                return false;
            }
        }

        public async Task<NotesMdBatchResult?> BatchAddVaultsToNotesMdCliAsync(List<string> paths)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var payload = new { paths };
                var json = JsonSerializer.Serialize(payload);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/notesmd-cli/batch-add", httpContent, quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<NotesMdBatchResult>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量添加知识库到 NotesMD CLI 失败");
                return null;
            }
        }

        public async Task<PlatformInfoResponse?> GetPlatformAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithMetricsAsync("/api/health/os", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<PlatformInfoResponse>(linked.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取平台信息失败");
                return null;
            }
        }

        // 请求指标统计 API
        public async Task<MetricsSummary?> GetMetricsSummaryAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithFallbackAsync("/api/metrics/summary", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MetricsSummary>(linked.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取请求指标摘要失败");
                return null;
            }
        }

        public async Task<List<RequestMetric>> GetSlowestRequestsAsync(int count = 10, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithFallbackAsync($"/api/metrics/slowest?count={count}", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<RequestMetric>>(linked.Token) ?? new List<RequestMetric>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取慢请求列表失败");
                return new List<RequestMetric>();
            }
        }

        public async Task<List<PathFrequency>> GetFrequentPathsAsync(int count = 10, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithFallbackAsync($"/api/metrics/frequent?count={count}", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<PathFrequency>>(linked.Token) ?? new List<PathFrequency>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取高频请求路径失败");
                return new List<PathFrequency>();
            }
        }

        public async Task<List<RequestMetric>> GetRecentErrorsAsync(int count = 10, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithFallbackAsync($"/api/metrics/errors?count={count}", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<RequestMetric>>(linked.Token) ?? new List<RequestMetric>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取最近错误请求失败");
                return new List<RequestMetric>();
            }
        }

        public async Task<bool> ClearMetricsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await _httpClient.PostAsync("/api/metrics/clear", null, linked.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空请求指标失败");
                return false;
            }
        }

        #region 本地模型部署


        public async Task<List<DownloadSourceDto>> GetDownloadSourcesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithMetricsAsync("/api/local-models/sources", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<DownloadSourceDto>>(linked.Token) ?? new List<DownloadSourceDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取下载源失败");
                return new List<DownloadSourceDto>();
            }
        }

        public async Task<DownloadDirectoryConfigDto?> GetDownloadConfigAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithMetricsAsync("/api/local-models/config", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<DownloadDirectoryConfigDto>(linked.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取下载配置失败");
                return null;
            }
        }

        public async Task<bool> SaveDownloadConfigAsync(DownloadDirectoryConfigDto config, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var json = JsonSerializer.Serialize(config);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/local-models/config", content, linked.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存下载配置失败");
                return false;
            }
        }


        public async Task<List<string>> GetAvailableModelsAsync(string toolId, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithMetricsAsync($"/api/local-models/available?toolId={Uri.EscapeDataString(toolId)}", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<string>>(linked.Token) ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取可用模型列表失败");
                return new List<string>();
            }
        }

        public async Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithMetricsAsync("/api/local-models/downloaded", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<DownloadedModelDto>>(linked.Token) ?? new List<DownloadedModelDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取已下载模型列表失败");
                return new List<DownloadedModelDto>();
            }
        }

        public async Task<bool> DeleteModelAsync(DeleteModelRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/local-models/delete", content, linked.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除模型失败");
                return false;
            }
        }


        #endregion

        #region OpenClaw 任务

        public async Task<OpenClawTaskDto> CreateOpenClawTaskAsync(string prompt)
        {
            try
            {
                var payload = new { prompt = prompt };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/openclaw/tasks", content);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<OpenClawTaskDto>()
                       ?? new OpenClawTaskDto { Prompt = prompt, Status = "unknown" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 OpenClaw 任务失败");
                return new OpenClawTaskDto { Prompt = prompt, Status = "failed", ErrorMessage = ex.Message };
            }
        }

        public async Task<List<OpenClawTaskDto>> GetOpenClawTasksAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync("/api/openclaw/tasks", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<OpenClawTaskDto>>(quick.Token) ?? new List<OpenClawTaskDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 OpenClaw 任务列表失败");
                return new List<OpenClawTaskDto>();
            }
        }

        public async Task<OpenClawTaskDto?> GetOpenClawTaskAsync(int id)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/openclaw/tasks/{id}", quick.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<OpenClawTaskDto>(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 OpenClaw 任务详情失败，Id: {Id}", id);
                return null;
            }
        }

        public async Task<string?> GetOpenClawReportAsync(int id)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/openclaw/tasks/{id}/report", quick.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(quick.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 OpenClaw 报告失败，Id: {Id}", id);
                return null;
            }
        }

        public async Task<bool> DeleteOpenClawTaskAsync(int id)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await DeleteWithMetricsAsync($"/api/openclaw/tasks/{id}", quick.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除 OpenClaw 任务失败，Id: {Id}", id);
                return false;
            }
        }

        public async Task<bool> CancelOpenClawTaskAsync(int id)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await PostWithMetricsAsync($"/api/openclaw/tasks/{id}/cancel", new StringContent(""), quick.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消 OpenClaw 任务失败，Id: {Id}", id);
                return false;
            }
        }

        public async Task<OpenClawLocalAiConfigDto> GetOpenClawLocalAiConfigAsync()
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync("/api/openclaw/local-ai-config", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<OpenClawLocalAiConfigDto>(quick.Token)
                       ?? new OpenClawLocalAiConfigDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 OpenClaw 本地 AI 配置失败");
                return new OpenClawLocalAiConfigDto();
            }
        }

        public async Task<bool> SaveOpenClawLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/openclaw/local-ai-config", content, quick.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存 OpenClaw 本地 AI 配置失败");
                return false;
            }
        }

        public async Task<List<OpenClawLocalModelDto>> ScanOpenClawLocalModelsAsync(string provider)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync($"/api/openclaw/local-ai-models?provider={Uri.EscapeDataString(provider)}", quick.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<OpenClawLocalModelDto>>(quick.Token)
                       ?? new List<OpenClawLocalModelDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描 OpenClaw 本地模型失败，Provider: {Provider}", provider);
                return new List<OpenClawLocalModelDto>();
            }
        }

        public async Task<LocalAiServiceStatusDto> DetectAndStartOpenClawLocalAiAsync(string provider)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var payload = new { provider = provider };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/openclaw/local-ai-detect", content, cts.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<LocalAiServiceStatusDto>(cts.Token)
                       ?? new LocalAiServiceStatusDto { Provider = provider, Message = _loc["Api_DetectFailed"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检测/启动本地 AI 服务失败，Provider: {Provider}", provider);
                return new LocalAiServiceStatusDto { Provider = provider, Message = _loc["Api_DetectFailedWithError", ex.Message] };
            }
        }

        public async Task<OpenClawDefaultModelDto> GetOpenClawDefaultModelAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(QuickCallTimeout);
                var response = await GetWithMetricsAsync("/api/openclaw/default-model", cts.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<OpenClawDefaultModelDto>(cts.Token)
                       ?? new OpenClawDefaultModelDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 OpenClaw 默认模型失败");
                return new OpenClawDefaultModelDto();
            }
        }

        public async Task<bool> SetOpenClawDefaultModelAsync(string model)
        {
            try
            {
                using var cts = new CancellationTokenSource(QuickCallTimeout);
                var payload = new { model = model };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/openclaw/default-model", content, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置 OpenClaw 默认模型失败，Model: {Model}", model);
                return false;
            }
        }

        public async Task<ModelProfileListDto> GetModelProfilesAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("api/openclaw/model-profiles");
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<ModelProfileListDto>() ?? new ModelProfileListDto();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取模型配置列表失败");
            }
            return new ModelProfileListDto();
        }

        public async Task<bool> SetModelProfileAsync(string profile)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync("api/openclaw/model-profiles", new SetModelProfileRequest { Profile = profile });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置模型配置失败，Profile: {Profile}", profile);
                return false;
            }
        }

        public async Task<bool> SyncLocalModelsToOpenClawAsync(string provider)
        {
            try
            {
                using var cts = new CancellationTokenSource(QuickCallTimeout);
                var payload = new SyncLocalModelsRequest { Provider = provider };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await PostWithMetricsAsync("/api/openclaw/sync-local-models", content, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步 {Provider} 模型到 OpenClaw 失败", provider);
                return false;
            }
        }

        #endregion


        #region AI 调用性能指标

        public async Task<AiMetricsSummaryDto?> GetAiMetricsSummaryAsync(int days = 7, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _aiHttpClient.GetAsync($"/api/ai/metrics/summary?days={days}", cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AiMetricsSummaryDto>(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 AI 指标总览失败");
                return null;
            }
        }

        public async Task<List<AiProviderMetricsDto>> GetAiProviderMetricsAsync(int days = 7, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _aiHttpClient.GetAsync($"/api/ai/metrics/providers?days={days}", cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<AiProviderMetricsDto>>(cancellationToken) ?? new List<AiProviderMetricsDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 Provider 指标失败");
                return new List<AiProviderMetricsDto>();
            }
        }

        public async Task<List<AiModelMetricsDto>> GetAiModelMetricsAsync(int days = 7, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _aiHttpClient.GetAsync($"/api/ai/metrics/models?days={days}", cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<AiModelMetricsDto>>(cancellationToken) ?? new List<AiModelMetricsDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取模型指标失败");
                return new List<AiModelMetricsDto>();
            }
        }

        public async Task<List<AiMetricsTrendDto>> GetAiMetricsTrendsAsync(int days = 7, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _aiHttpClient.GetAsync($"/api/ai/metrics/trends?days={days}", cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<AiMetricsTrendDto>>(cancellationToken) ?? new List<AiMetricsTrendDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 AI 指标趋势失败");
                return new List<AiMetricsTrendDto>();
            }
        }

        public async Task<List<AiUsageMetricDto>> GetAiRecentMetricsAsync(int limit = 50, int days = 7, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _aiHttpClient.GetAsync($"/api/ai/metrics/recent?limit={limit}&days={days}", cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<AiUsageMetricDto>>(cancellationToken) ?? new List<AiUsageMetricDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取最近 AI 调用记录失败");
                return new List<AiUsageMetricDto>();
            }
        }

        public async Task SetSceneAsync(Baihua.Contracts.Scene.AppScene scene, CancellationToken cancellationToken = default)
        {
            var payload = new { scene = (int)scene };
            var response = await _httpClient.PostAsJsonAsync("/api/scene", payload, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        #endregion

        #region 虚拟师父

        public async Task<CreateMasterResponse> CreateMasterAsync(string goal, string industry, CancellationToken cancellationToken = default)
        {
            var payload = new { goal, industry };
            var response = await _httpClient.PostAsJsonAsync("/api/master/create", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryGetErrorMessageAsync(response);
                throw new HttpRequestException(error ?? _loc["Api_MasterCreateFailed", (int)response.StatusCode]);
            }
            return await response.Content.ReadFromJsonAsync<CreateMasterResponse>(cancellationToken)
                   ?? new CreateMasterResponse { Success = false, Message = _loc["Api_CreateFailed"] };
        }

        private static async Task<string?> TryGetErrorMessageAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body)) return null;

                using var doc = JsonDocument.Parse(body);
                // Try both PascalCase and camelCase (server uses PascalCase, some endpoints use camelCase)
                foreach (var key in new[] { "Message", "message", "Title", "title", "error", "Error" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                        return val.GetString();
                }
            }
            catch { }
            return null;
        }

        public async IAsyncEnumerable<ChatStreamEvent> StreamMasterChatAsync(
            string masterId,
            string message,
            string stage,
            List<(bool IsUser, string Content)>? history = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var payload = new Dictionary<string, object?>
            {
                ["masterId"] = masterId,
                ["message"] = message,
                ["stage"] = stage
            };
            if (history != null && history.Count > 0)
                payload["history"] = history.Select(h => new { role = h.IsUser ? "user" : "assistant", content = h.Content }).ToList();

            var json = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/api/master/chat/stream") { Content = httpContent },
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await TryGetErrorMessageAsync(response);
                throw new HttpRequestException(error ?? _loc["Api_MasterChatFailed", (int)response.StatusCode]);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? currentEvent = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;

                if (line.StartsWith("event: "))
                {
                    currentEvent = line.Substring(7).Trim();
                }
                else if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (currentEvent == "delta")
                    {
                        var text = TryExtractContent(data);
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return new ChatStreamEvent { Type = "delta", Content = text };
                        }
                    }
                    else if (currentEvent == "done")
                    {
                        yield return new ChatStreamEvent { Type = "done" };
                        yield break;
                    }
                    else if (currentEvent == "error")
                    {
                        throw new InvalidOperationException(_loc["Api_MasterChatError", data!]);
                    }
                }
                else if (string.IsNullOrEmpty(line))
                {
                    currentEvent = null;
                }
            }
        }

        public async Task<StageCompleteResponse> MasterStageCompleteAsync(string masterId, string stageName, CancellationToken cancellationToken = default)
        {
            var payload = new { stageName };
            var response = await _httpClient.PostAsJsonAsync($"/api/master/{masterId}/stage-complete", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryGetErrorMessageAsync(response);
                throw new HttpRequestException(error ?? _loc["Api_StageCompleteFailed", (int)response.StatusCode]);
            }
            return await response.Content.ReadFromJsonAsync<StageCompleteResponse>(cancellationToken)
                   ?? new StageCompleteResponse { Success = false, Message = _loc["Api_OperationFailed"] };
        }

        public async Task<ApprenticeProfileResponse> GetMasterProfileAsync(string masterId, CancellationToken cancellationToken = default)
        {
            var response = await _httpClient.GetAsync($"/api/master/{masterId}/profile", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryGetErrorMessageAsync(response);
                throw new HttpRequestException(error ?? _loc["Api_ProfileFetchFailed", (int)response.StatusCode]);
            }
            return await response.Content.ReadFromJsonAsync<ApprenticeProfileResponse>(cancellationToken)
                   ?? new ApprenticeProfileResponse { Success = false, Message = _loc["Api_FetchFailed"] };
        }

        public async Task<AssessResponse> MasterAssessAsync(string masterId, string type = "capability", CancellationToken cancellationToken = default)
        {
            var payload = new { type };
            var response = await _httpClient.PostAsJsonAsync($"/api/master/{masterId}/assess", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryGetErrorMessageAsync(response);
                throw new HttpRequestException(error ?? _loc["Api_AssessFailedWithHttp", (int)response.StatusCode]);
            }
            return await response.Content.ReadFromJsonAsync<AssessResponse>(cancellationToken)
                   ?? new AssessResponse { Success = false, Message = _loc["Api_AssessFailed"] };
        }

        public async Task<List<MasterListItem>> GetMastersAsync(CancellationToken cancellationToken = default)
        {
            var response = await _httpClient.GetAsync("/api/master", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryGetErrorMessageAsync(response);
                throw new HttpRequestException(error ?? $"获取师父列表失败 (HTTP {(int)response.StatusCode})");
            }
            return await response.Content.ReadFromJsonAsync<List<MasterListItem>>(cancellationToken)
                   ?? new List<MasterListItem>();
        }

        public async Task<bool> DeleteMasterAsync(string masterId, CancellationToken cancellationToken = default)
        {
            var response = await _httpClient.DeleteAsync($"/api/master/{masterId}", cancellationToken);
            return response.IsSuccessStatusCode;
        }

        public async Task<MasterEvictResponse> EvictMasterAsync(string masterId, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.PostAsync($"/api/master/{masterId}/evict", null, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MasterEvictResponse>(cancellationToken)
                    ?? new MasterEvictResponse { Success = false, Message = _loc["Api_CleanupFailed"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理师父数据失败，MasterId: {MasterId}", masterId);
                return new MasterEvictResponse { Success = false, Message = _loc["Api_CleanupFailedWithError", ex.Message] };
            }
        }

        public async Task<ApprenticeProfileResponse> UpdateMasterProfileAsync(string masterId, UpdateProfileRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                var json = JsonSerializer.Serialize(request);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync($"/api/master/{masterId}/profile", httpContent, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<ApprenticeProfileResponse>(cancellationToken)
                    ?? new ApprenticeProfileResponse { Success = false, Message = _loc["Api_UpdateFailed"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新师父画像失败，MasterId: {MasterId}", masterId);
                return new ApprenticeProfileResponse { Success = false, Message = _loc["Api_UpdateFailedWithError", ex.Message] };
            }
        }

        public async Task<VaultFocusListResponse> GetVaultFocusAsync(string masterId, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync($"/api/master/{masterId}/vault-focus", cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<VaultFocusListResponse>(cancellationToken)
                    ?? new VaultFocusListResponse { Success = false, Message = _loc["Api_FetchFailed"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取知识库关联失败，MasterId: {MasterId}", masterId);
                return new VaultFocusListResponse { Success = false, Message = _loc["Api_FetchFailedWithError", ex.Message] };
            }
        }

        public async Task<VaultFocusUpdateResponse> UpdateVaultFocusAsync(string masterId, VaultFocusUpdateRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                var json = JsonSerializer.Serialize(request);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"/api/master/{masterId}/vault-focus", httpContent, cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<VaultFocusUpdateResponse>(cancellationToken)
                    ?? new VaultFocusUpdateResponse { Success = false, Message = _loc["Api_OperationFailed"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新知识库关联失败，MasterId: {MasterId}", masterId);
                return new VaultFocusUpdateResponse { Success = false, Message = $"操作失败：{ex.Message}" };
            }
        }

        public async Task<VaultFocusUpdateResponse> RemoveVaultFocusAsync(string masterId, string vaultId, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"/api/master/{masterId}/vault-focus/{vaultId}", cancellationToken);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<VaultFocusUpdateResponse>(cancellationToken)
                    ?? new VaultFocusUpdateResponse { Success = false, Message = _loc["Api_OperationFailed"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消知识库关联失败，MasterId: {MasterId}", masterId);
                return new VaultFocusUpdateResponse { Success = false, Message = $"操作失败：{ex.Message}" };
            }
        }

        // ============ AI 绘图（ComfyUI） ============


        private static async Task<ComfyGenerateResultDto> ReadComfyGenerateResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string? error = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == System.Text.Json.JsonValueKind.String)
                        error = err.GetString();
                }
                catch { }
                throw new InvalidOperationException(error ?? $"HTTP {(int)response.StatusCode}");
            }
            return System.Text.Json.JsonSerializer.Deserialize<ComfyGenerateResultDto>(body, _caseInsensitiveJsonOptions)
                ?? throw new InvalidOperationException("空响应");
        }

        #endregion
    }

    public class GenerateCardsTaskDto
    {
        public bool Success { get; set; }
        public string TaskId { get; set; } = "";
        public string Message { get; set; } = "";
        public string VaultName { get; set; } = "";
    }

    public class ComfyStatusDto
    {
        public bool Available { get; set; }
    }

    public class ComfyGenerateResultDto
    {
        public int Id { get; set; }
        public string Url { get; set; } = "";
        public double DurationSeconds { get; set; }
    }

    public class ComfyHistoryItemDto
    {
        public int Id { get; set; }
        public string Kind { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string Url { get; set; } = "";
        public bool IsSuccess { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
