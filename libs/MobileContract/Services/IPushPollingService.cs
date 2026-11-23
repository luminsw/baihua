using MobileContract.Devices;

namespace MobileContract.Services;

/// <summary>
/// 移动端推送轮询接口。
/// 当 WebSocket 不可用时，移动端通过此接口长轮询待处理的同步推送请求。
/// </summary>
public interface IPushPollingService
{
    /// <summary>获取指定设备的待处理推送请求</summary>
    /// <param name="deviceName">设备名称（移动端使用本机设备名）</param>
    Task<IReadOnlyList<PushSyncRequest>> PollPendingAsync(
        string deviceName, bool wait = false, CancellationToken cancellationToken = default);
}
