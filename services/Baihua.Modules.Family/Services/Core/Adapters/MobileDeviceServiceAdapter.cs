using Baihua.Core;
using Baihua.Core.Localization;
using Microsoft.Extensions.Localization;
using MobileContract.Admin;
using MobileContract.Devices;
using MobileContract.Pairing;
using MobileContract.Services;
using MPushSyncRequest = MobileContract.Devices.PushSyncRequest;

namespace Baihua.Modules.Family.Services.Adapters;

/// <summary>
/// 设备服务适配器 — 将 MobileGateway 的 DeviceService 适配到 MobileContract 接口
/// </summary>
public class MobileDeviceServiceAdapter :
    IDeviceAdminService,
    IPairingService,
    IPushAdminService
{
    private readonly DeviceService _deviceService;
    private readonly ILogger<MobileDeviceServiceAdapter> _logger;
    private readonly IStringLocalizer<SharedResources> _loc;

    public MobileDeviceServiceAdapter(DeviceService deviceService, ILogger<MobileDeviceServiceAdapter> logger, IStringLocalizer<SharedResources> loc)
    {
        _deviceService = deviceService;
        _logger = logger;
        _loc = loc;
    }

    #region IPairingService

    /// <inheritdoc />
    public void Initialize(string serverUrl)
    {
        // 服务端适配器无需设置服务器地址
    }

    public Task<PairCodeResponse> GetPairCodeAsync(CancellationToken cancellationToken = default)
    {
        var code = _deviceService.GetPairCode();
        // TODO: 优化 DeviceService 以支持获取过期时间
        return Task.FromResult(new PairCodeResponse
        {
            DeviceId = "", // DeviceService 中未存储 server deviceId
            PairCode = code
        });
    }

    public Task<PairCodeResponse> RefreshPairCodeAsync(CancellationToken cancellationToken = default)
    {
        var code = _deviceService.RefreshPairCode();
        return Task.FromResult(new PairCodeResponse
        {
            PairCode = code
        });
    }

    public Task<PairResponse> PairDeviceAsync(PairRequest request, CancellationToken cancellationToken = default)
    {
        var info = _deviceService.SubmitPairRequest(
            request.DeviceName ?? "",
            request.PairCode ?? "",
            requestId: null);

        return Task.FromResult(new PairResponse
        {
            RequestId = info.RequestId,
            Status = "pending",
            Message = _loc["Pair_RequestSubmitted"]
        });
    }

    public Task<PairResponse> CheckPairStatusAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var result = _deviceService.GetRequestResult(requestId);
        if (string.IsNullOrEmpty(result))
        {
            return Task.FromResult(new PairResponse
            {
                RequestId = requestId,
                Status = "pending",
                Message = _loc["Pair_WaitingAuth"]
            });
        }

        // result 格式可能是 "authorized:{token}" 或 "rejected"
        if (result.StartsWith("authorized:"))
        {
            var token = result["authorized:".Length..];
            return Task.FromResult(new PairResponse
            {
                RequestId = requestId,
                Status = "authorized",
                AccessToken = token,
                ExpiresIn = 86400 * 30,
                Message = _loc["Pair_AuthSuccess"]
            });
        }

        return Task.FromResult(new PairResponse
        {
            RequestId = requestId,
            Status = "rejected",
            Message = _loc["Pair_AuthRejected"]
        });
    }

    public Task<bool> VerifyTokenAsync(VerifyTokenRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_deviceService.ValidateAccessToken(request.Token));
    }

    public Task<AuthConfigResponse> GetAuthConfigAsync(AuthConfigRequest request, CancellationToken cancellationToken = default)
    {
        // AuthConfig 在 AuthController 中直接读取配置，DeviceService 不处理
        // 这里返回空，实际应由专门的 IAuthConfigService 处理
        _logger.LogWarning("GetAuthConfigAsync 未在 DeviceService 中实现，应由上层控制器直接处理");
        return Task.FromResult(new AuthConfigResponse());
    }

    #endregion

    #region IDeviceAdminService

    public Task<IReadOnlyList<PendingDeviceDto>> GetPendingDevicesAsync(CancellationToken cancellationToken = default)
    {
        var pending = _deviceService.GetPendingRequests();
        var result = pending.Select(p => new PendingDeviceDto
        {
            RequestId = p.RequestId,
            DeviceName = p.DeviceName,
            RequestTime = p.RequestTime,
            IpAddress = p.IpAddress
        }).ToList();

        return Task.FromResult<IReadOnlyList<PendingDeviceDto>>(result);
    }

    public Task<IReadOnlyList<AuthorizedDeviceDto>> GetAuthorizedDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = _deviceService.GetAuthorizedDevices();
        var result = devices
            .Where(d => d.Status == DeviceStatus.Authorized)
            .Select(MapToAuthorizedDeviceDto)
            .ToList();

        return Task.FromResult<IReadOnlyList<AuthorizedDeviceDto>>(result);
    }

    public Task<IReadOnlyList<DeviceDto>> GetAllDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = _deviceService.GetAllDevices();
        var result = devices.Select(MapToDeviceDto).ToList();
        return Task.FromResult<IReadOnlyList<DeviceDto>>(result);
    }

    public Task<bool> AuthorizeDeviceAsync(AuthorizeDeviceRequest request, CancellationToken cancellationToken = default)
    {
        var (success, _, _) = _deviceService.AuthorizeDevice(request.RequestId ?? "");
        return Task.FromResult(success);
    }

    public Task<bool> RejectDeviceAsync(RejectDeviceRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_deviceService.RejectRequest(request.RequestId ?? ""));
    }

    public Task<bool> RevokeDeviceAsync(RevokeDeviceRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_deviceService.RevokeDevice(request.DeviceId ?? ""));
    }

    public Task<MobileStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = _deviceService.GetMobileStats();
        // MobileStats 类型来自 Baihua.Contracts.Devices，与 MobileContract.Devices.MobileStats 同名不同命名空间
        // 需要显式映射到 MobileContract 的类型
        var result = new MobileStats
        {
            TotalDevices = stats.TotalDevices,
            TotalSyncs = stats.TotalSyncs,
            TotalSyncFiles = stats.TotalSyncFiles,
            ActiveDevices7Days = stats.ActiveDevices7Days,
            ActiveDevices30Days = stats.ActiveDevices30Days,
            Devices = stats.Devices?.Select(d => new DeviceStat
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                IpAddress = d.IpAddress,
                SyncCount = d.SyncCount,
                FirstSyncTime = d.FirstSyncTime,
                LastSyncTime = d.LastSyncTime,
                AuthorizedTime = d.AuthorizedTime
            }).ToList() ?? new List<DeviceStat>()
        };

        return Task.FromResult(result);
    }

    #endregion

    #region IPushAdminService

    public Task<bool> PushSyncAsync(string deviceId, string? vaultId, string action, CancellationToken cancellationToken = default)
    {
        // 服务器→移动端推送已移除，返回 false
        return Task.FromResult(false);
    }

    #endregion

    #region 映射辅助方法

    private static AuthorizedDeviceDto MapToAuthorizedDeviceDto(DeviceInfo info)
    {
        return new AuthorizedDeviceDto
        {
            DeviceId = info.DeviceId,
            DeviceName = info.DeviceName,
            AuthorizedTime = info.AuthorizedTime ?? DateTimeOffset.MinValue,
            LastSyncTime = info.LastSyncTime,
            IpAddress = info.IpAddress,
            SyncCount = info.SyncCount,
            FirstSyncTime = info.FirstSyncTime
        };
    }

    private static DeviceDto MapToDeviceDto(DeviceInfo info)
    {
        return new DeviceDto
        {
            DeviceId = info.DeviceId,
            DeviceName = info.DeviceName,
            Status = info.Status.ToString().ToLowerInvariant(),
            FirstRequestTime = info.FirstRequestTime,
            AuthorizedTime = info.AuthorizedTime.HasValue ? new DateTimeOffset(info.AuthorizedTime.Value) : null,
            LastSyncTime = info.LastSyncTime.HasValue ? new DateTimeOffset(info.LastSyncTime.Value) : null,
            IpAddress = info.IpAddress,
            SyncCount = info.SyncCount,
            FirstSyncTime = info.FirstSyncTime.HasValue ? new DateTimeOffset(info.FirstSyncTime.Value) : null
        };
    }

    #endregion
}
