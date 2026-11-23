using BaihuaSdk.Models;
using BaihuaSdk.Push;
using MobileContract.Services;

namespace BaihuaSdk.Services;

/// <summary>
/// 授权等待器（纯 WebSocket 方案）。
/// 封装「注册设备 → 连接 WebSocket → 等待授权推送」的完整流程。
/// WebSocket 自带重连机制（PushWebSocketService 内部处理），无需轮询兜底。
/// </summary>
public class AuthorizationWatcher : IDisposable
{
    private readonly IDeviceRegistrationService _deviceRegistration;
    private readonly PushWebSocketService _pushService;

    private CancellationTokenSource? _watchCts;

    public AuthorizationWatcher(IDeviceRegistrationService deviceRegistration, PushWebSocketService pushService)
    {
        _deviceRegistration = deviceRegistration;
        _pushService = pushService;
    }

    /// <summary>WebSocket 连接状态变化。</summary>
    public event Action<bool>? WebSocketConnectionStateChanged;

    /// <summary>收到授权通知（通过 WebSocket 推送）。</summary>
    public event Action? Authorized;

    /// <summary>
    /// 等待设备被授权（纯 WebSocket，无轮询）。
    /// </summary>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="deviceName">设备名称，用于 WebSocket 连接标识。</param>
    /// <param name="timeout">整体等待超时时间，默认 2 分钟。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<AuthorizationResult> WaitForAuthorizationAsync(
        string serverUrl,
        string deviceName,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        // 1. 立即检查一次是否已授权
        var immediate = await CheckAuthorizationAsync(serverUrl, ct);
        if (immediate.IsAuthorized) return immediate;

        StopWatching();
        _watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchCt = _watchCts.Token;

        var authorizedTcs = new TaskCompletionSource<AuthorizationResult>();
        var timeoutResolved = timeout ?? TimeSpan.FromMinutes(2);

        // 2. 监听 WebSocket 授权推送
        EventHandler pushAuthorizedHandler = (s, e) =>
            OnPushAuthorized(s, e, serverUrl, authorizedTcs, watchCt);
        _pushService.Authorized += pushAuthorizedHandler;

        // 3. 监听 WebSocket 连接状态
        void OnConnectionStateChange(bool connected)
        {
            WebSocketConnectionStateChanged?.Invoke(connected);
            // WebSocket 断开后 PushWebSocketService 会自动重连（最多 10 次）。
            // 重连成功后若已授权，会通过 Authorized 事件回调。
        }
        _pushService.ConnectionStateChanged += OnConnectionStateChange;

        try
        {
            // 4. 启动 WebSocket 连接
            await _pushService.ConnectAsync(serverUrl, deviceName, watchCt);

            // 5. 等待授权推送（或超时/取消）
            using var timeoutCts = new CancellationTokenSource(timeoutResolved);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(watchCt, timeoutCts.Token);
            try
            {
                await authorizedTcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return AuthorizationResult.Failed("等待授权超时，请检查 WebUI 是否已批准设备。");
            }

            var result = await authorizedTcs.Task;
            if (result.IsAuthorized)
            {
                Authorized?.Invoke();
            }
            return result;
        }
        finally
        {
            _pushService.Authorized -= pushAuthorizedHandler;
            _pushService.ConnectionStateChanged -= OnConnectionStateChange;
            StopWatching();
        }
    }

    /// <summary>立即查询一次授权状态（通过 /mg/register-device）。</summary>
    public async Task<AuthorizationResult> CheckAuthorizationAsync(string serverUrl, CancellationToken ct = default)
    {
        var result = await _deviceRegistration.RegisterDeviceAsync(serverUrl, ct);
        if (result is { Success: true, Authorized: true, SharedSecret: not null })
        {
            return AuthorizationResult.Authorized(result.SharedSecret);
        }

        if (result is { Success: true, Authorized: false })
        {
            return AuthorizationResult.NotAuthorized(result.RequestId);
        }

        return AuthorizationResult.Failed(result?.ErrorMessage ?? "注册失败");
    }

    /// <summary>
    /// WebSocket 授权推送事件处理器。
    /// </summary>
    private async void OnPushAuthorized(
        object? sender,
        EventArgs e,
        string serverUrl,
        TaskCompletionSource<AuthorizationResult> authorizedTcs,
        CancellationToken ct)
    {
        try
        {
            var result = await _deviceRegistration.RegisterDeviceAsync(serverUrl, ct);
            if (result is { Success: true, Authorized: true, SharedSecret: not null })
            {
                authorizedTcs.TrySetResult(AuthorizationResult.Authorized(result.SharedSecret));
            }
        }
        catch (OperationCanceledException)
        {
            authorizedTcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            authorizedTcs.TrySetException(ex);
        }
    }

    private void StopWatching()
    {
        if (_watchCts != null)
        {
            _watchCts.Cancel();
            _watchCts.Dispose();
            _watchCts = null;
        }
    }

    public void Dispose()
    {
        StopWatching();
    }
}
