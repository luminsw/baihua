using Baihua.Contracts.Devices;
using Baihua.Web.Localization;
using Microsoft.Extensions.Localization;

namespace Baihua.Web.Services;

/// <summary>
/// 设备管理服务
/// </summary>
public class DevicesService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DevicesService> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public DevicesService(IHttpClientFactory httpClientFactory, ILogger<DevicesService> logger, IStringLocalizer<SharedResources> loc)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loc = loc;
    }

    /// <summary>
    /// 获取待授权设备列表
    /// </summary>
    public async Task<List<PendingDeviceDto>> GetPendingDevicesAsync()
    {
        try
        {
            _logger.LogInformation("[DevicesService] 获取待授权设备列表...");
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.GetAsync("api/devices/pending");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[DevicesService] 获取待授权设备失败，状态码: {StatusCode}", response.StatusCode);
                return new List<PendingDeviceDto>();
            }
            
            var content = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("[DevicesService] 待授权设备API响应: {Content}", content);
            
            var devices = await response.Content.ReadFromJsonAsync<List<PendingDeviceDto>>();
            _logger.LogInformation("[DevicesService] 获取到 {Count} 个待授权设备", devices?.Count ?? 0);
            return devices ?? new List<PendingDeviceDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DevicesService] 获取待授权设备失败");
            return new List<PendingDeviceDto>();
        }
    }

    /// <summary>
    /// 获取已授权设备列表
    /// </summary>
    public async Task<List<AuthorizedDeviceDto>> GetAuthorizedDevicesAsync()
    {
        try
        {
            _logger.LogInformation("[DevicesService] 获取已授权设备列表...");
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.GetAsync("api/devices/authorized");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[DevicesService] 获取已授权设备失败，状态码: {StatusCode}", response.StatusCode);
                return new List<AuthorizedDeviceDto>();
            }
            
            var content = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("[DevicesService] 已授权设备API响应: {Content}", content);
            
            var devices = await response.Content.ReadFromJsonAsync<List<AuthorizedDeviceDto>>();
            _logger.LogInformation("[DevicesService] 获取到 {Count} 个已授权设备", devices?.Count ?? 0);
            return devices ?? new List<AuthorizedDeviceDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DevicesService] 获取已授权设备失败");
            return new List<AuthorizedDeviceDto>();
        }
    }

    /// <summary>
    /// 授权设备
    /// </summary>
    public async Task<(bool success, string? message)> AuthorizeDeviceAsync(string requestId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsJsonAsync("api/devices/authorize", new { requestId });
            
            if (response.IsSuccessStatusCode)
            {
                return (true, _loc["Devices_DeviceAuthorized"].Value);
            }
            
            var error = await response.Content.ReadAsStringAsync();
            return (false, _loc["Devices_AuthorizeFailed", error].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "授权设备失败，RequestId: {RequestId}", requestId);
            return (false, _loc["Devices_AuthorizeFailed", ex.Message].Value);
        }
    }

    /// <summary>
    /// 拒绝设备配对请求
    /// </summary>
    public async Task<(bool success, string? message)> RejectDeviceAsync(string requestId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsJsonAsync("api/devices/reject", new { requestId });
            
            if (response.IsSuccessStatusCode)
            {
                return (true, _loc["Devices_RequestRejected"].Value);
            }
            
            var error = await response.Content.ReadAsStringAsync();
            return (false, _loc["Devices_RejectFailed", error].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "拒绝设备失败，RequestId: {RequestId}", requestId);
            return (false, _loc["Devices_RejectFailed", ex.Message].Value);
        }
    }

    /// <summary>
    /// 撤销设备授权
    /// </summary>
    public async Task<(bool success, string? message)> RevokeDeviceAsync(string deviceId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FamilyApi");
            var response = await client.PostAsJsonAsync("api/devices/revoke", new { deviceId });
            
            if (response.IsSuccessStatusCode)
            {
                return (true, _loc["Devices_DeviceRevoked"].Value);
            }
            
            var error = await response.Content.ReadAsStringAsync();
            return (false, _loc["Devices_RevokeFailed", error].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销设备授权失败，DeviceId: {DeviceId}", deviceId);
            return (false, _loc["Devices_RevokeFailed", ex.Message].Value);
        }
    }

}
