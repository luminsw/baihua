using System.Net.Http.Json;
using Baihua.Contracts.Onboarding;

namespace Baihua.Web.Services;

/// <summary>
/// Onboarding 服务 - 管理首次使用引导和初始化任务
/// </summary>
public class OnboardingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OnboardingService> _logger;
    private OnboardingStatusDto? _cachedStatus;
    private InitTasksResponse? _cachedTasks;
    private DateTime _lastStatusFetch = DateTime.MinValue;
    private DateTime _lastTasksFetch = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(10);

    public OnboardingService(IHttpClientFactory httpClientFactory, ILogger<OnboardingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// 获取 Onboarding 状态
    /// </summary>
    public async Task<OnboardingStatusDto> GetStatusAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedStatus != null && DateTime.UtcNow - _lastStatusFetch < _cacheDuration)
        {
            return _cachedStatus;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.GetFromJsonAsync<OnboardingStatusDto>("api/onboarding/status");
            _cachedStatus = response ?? new OnboardingStatusDto();
            _lastStatusFetch = DateTime.UtcNow;
            return _cachedStatus;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 Onboarding 状态失败");
            throw;
        }
    }

    /// <summary>
    /// 标记 Onboarding 完成
    /// </summary>
    public async Task<bool> CompleteOnboardingAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsync("api/onboarding/complete", null);
            if (response.IsSuccessStatusCode)
            {
                _cachedStatus = null;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "完成 Onboarding 失败");
            return false;
        }
    }

    /// <summary>
    /// 获取初始化任务列表
    /// </summary>
    public async Task<InitTasksResponse> GetTasksAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedTasks != null && DateTime.UtcNow - _lastTasksFetch < _cacheDuration)
        {
            return _cachedTasks;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.GetFromJsonAsync<InitTasksResponse>("api/onboarding/tasks");
            _cachedTasks = response ?? new InitTasksResponse();
            _lastTasksFetch = DateTime.UtcNow;
            return _cachedTasks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取初始化任务列表失败");
            throw;
        }
    }

    /// <summary>
    /// 标记任务完成
    /// </summary>
    public async Task<bool> CompleteTaskAsync(string taskId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsync($"api/onboarding/tasks/{taskId}/complete", null);
            if (response.IsSuccessStatusCode)
            {
                _cachedTasks = null;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "完成任务失败: {TaskId}", taskId);
            return false;
        }
    }


    /// <summary>
    /// 创建示例知识库
    /// </summary>
    public async Task<CreateSampleVaultResponse?> CreateSampleVaultAsync(string vaultName, string vaultType)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsJsonAsync("api/onboarding/create-sample-vault", new
            {
                vaultName,
                vaultType
            });

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<CreateSampleVaultResponse>();
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("创建示例知识库失败: {Error}", error);
            return new CreateSampleVaultResponse { Success = false, Message = error };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建示例知识库失败");
            return new CreateSampleVaultResponse { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// 清除缓存
    /// </summary>
    public void ClearCache()
    {
        _cachedStatus = null;
        _cachedTasks = null;
    }
}
