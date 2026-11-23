using System.Net.Http.Json;
using Baihua.Contracts.Capability;

namespace Baihua.Web.Services;

/// <summary>
/// 前端能力评估服务：从后端获取机器能力信息，控制功能可见性
/// </summary>
public class CapabilityService
{
    private readonly HttpClient _httpClient;
    private CapabilityInfo? _cachedInfo;
    private readonly object _lock = new();

    public CapabilityService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("FamilyApi");
    }

    /// <summary>
    /// 获取机器能力信息（缓存）
    /// </summary>
    public async Task<CapabilityInfo> GetCapabilityAsync()
    {
        if (_cachedInfo != null)
            return _cachedInfo;

        lock (_lock)
        {
            if (_cachedInfo != null)
                return _cachedInfo;
        }

        try
        {
            var info = await _httpClient.GetFromJsonAsync<CapabilityInfo>("api/capability");
            if (info != null)
            {
                lock (_lock)
                {
                    _cachedInfo = info;
                }
                return info;
            }
        }
        catch
        {
            // 后端不可用时，默认允许所有功能（避免前端功能被意外隐藏）
        }

        // 默认返回全部可用（降级策略：宁可显示太多也不要隐藏必要功能）
        return new CapabilityInfo
        {
            Level = MachineCapability.HighEndGpu,
            IsIntelGpu = true,
            OpenVinoAvailable = true,
            AvailableFeatures = new List<string>()
        };
    }

    /// <summary>
    /// 检查指定功能是否可用
    /// </summary>
    public async Task<bool> CanShowAsync(string featureName)
    {
        var info = await GetCapabilityAsync();
        return info.AvailableFeatures.Contains(featureName);
    }

    /// <summary>
    /// 刷新能力信息
    /// </summary>
    public async Task RefreshAsync()
    {
        lock (_lock)
        {
            _cachedInfo = null;
        }
        await GetCapabilityAsync();
    }

    /// <summary>
    /// 获取能力等级（同步版本，用于已加载后的快速检查）
    /// </summary>
    public MachineCapability? GetCachedLevel()
    {
        lock (_lock)
        {
            return _cachedInfo?.Level;
        }
    }
}
