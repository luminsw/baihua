namespace MobileContract.Admin;

/// <summary>
/// 推送管理接口 —— 仅由服务端管理后台（WebUI.Family）使用。
/// 向指定移动设备发起同步推送请求。
/// </summary>
public interface IPushAdminService
{
    /// <summary>向指定设备发送同步推送请求</summary>
    Task<bool> PushSyncAsync(string deviceId, string? vaultId, string action, CancellationToken cancellationToken = default);
}
