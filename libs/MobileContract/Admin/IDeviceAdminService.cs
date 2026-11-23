using MobileContract.Devices;

namespace MobileContract.Admin;

/// <summary>
/// 设备管理接口 —— 仅由服务端管理后台（WebUI.Family）使用。
/// 负责查看、授权、撤销设备以及获取统计信息。
/// </summary>
public interface IDeviceAdminService
{
    /// <summary>获取待审批的设备列表</summary>
    Task<IReadOnlyList<PendingDeviceDto>> GetPendingDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>获取已授权设备列表</summary>
    Task<IReadOnlyList<AuthorizedDeviceDto>> GetAuthorizedDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>获取所有设备（含历史）</summary>
    Task<IReadOnlyList<DeviceDto>> GetAllDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>授权待审批设备</summary>
    Task<bool> AuthorizeDeviceAsync(AuthorizeDeviceRequest request, CancellationToken cancellationToken = default);

    /// <summary>拒绝待审批设备</summary>
    Task<bool> RejectDeviceAsync(RejectDeviceRequest request, CancellationToken cancellationToken = default);

    /// <summary>撤销已授权设备</summary>
    Task<bool> RevokeDeviceAsync(RevokeDeviceRequest request, CancellationToken cancellationToken = default);

    /// <summary>获取移动端使用统计</summary>
    Task<MobileStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
