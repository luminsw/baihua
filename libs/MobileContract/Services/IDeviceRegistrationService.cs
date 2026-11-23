using MobileContract.Pairing;

namespace MobileContract.Services;

/// <summary>
/// 移动设备注册服务接口。
/// 通过 HTTP 向百花服务器注册本机设备（扫码/手动输入流程）。
/// </summary>
public interface IDeviceRegistrationService
{
    /// <summary>向指定服务器注册本机设备</summary>
    Task<RegisterDeviceResult> RegisterDeviceAsync(string serverUrl, CancellationToken cancellationToken = default);
}
