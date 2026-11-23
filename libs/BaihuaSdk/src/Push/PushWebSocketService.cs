using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BaihuaSdk.Push;

/// <summary>
/// WebSocket 实时推送服务。
/// 连接百花服务器 WebSocket 端点，接收同步推送通知。
/// 支持自动重连和 HTTP 轮询降级。
/// 与 Kotlin PushWebSocketService.kt 逻辑对齐。
/// </summary>
public class PushWebSocketService : IDisposable
{
    private const int ReconnectDelayMs = 5000;
    private const int MaxReconnectAttempts = 10;
    private const int PollIntervalMs = 60000;

    private readonly HttpClient _httpClient;
    private Func<string, string, string, CancellationToken, Task>? _onSyncRequest;

    private WebSocket? _webSocket;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _pollCts;
    private volatile bool _disposed;
    private int _reconnectAttempts;

    private string _serverBaseUrl = "";
    private string _deviceName = "";
    private string? _pendingPollUrl;

    /// <summary>设备 id（ANDROID_ID 等），用于服务端定向推送授权/撤销事件；不设置时服务端降级为全量广播。</summary>
    public string? DeviceId { get; set; }

    /// <summary>收到同步推送时触发（vaultId, vaultName, action）</summary>
    public Func<string, string, string, CancellationToken, Task>? OnSyncRequest
    {
        get => _onSyncRequest;
        set => _onSyncRequest = value;
    }

    /// <summary>连接状态变化时触发</summary>
    public event Action<bool>? ConnectionStateChanged;

    /// <summary>日志回调</summary>
    public Action<string>? OnLog { get; set; }

    /// <summary>设备被 WebUI 授权时触发</summary>
    public event EventHandler? Authorized;

    /// <summary>是否已连接到服务器</summary>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public PushWebSocketService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>连接到百花服务器的 WebSocket 推送端点</summary>
    public async Task ConnectAsync(string baseUrl, string deviceName, CancellationToken ct = default)
    {
        if (_disposed) return;

        await DisconnectAsync();
        _serverBaseUrl = baseUrl.TrimEnd('/');
        _deviceName = deviceName;
        _reconnectAttempts = 0;

        var wsUrl = _serverBaseUrl.Replace("https://", "wss://")
            .Replace("http://", "ws://")
            + $"/ws/devices?deviceName={Uri.EscapeDataString(deviceName)}";
        if (!string.IsNullOrEmpty(DeviceId))
            wsUrl += $"&deviceId={Uri.EscapeDataString(DeviceId)}";

        Log($"PushWebSocket connecting to {wsUrl}");
        _pendingPollUrl = null;

        _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ConnectLoopAsync(wsUrl, _connectCts.Token);
    }

    /// <summary>断开 WebSocket 连接</summary>
    public async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;

        StopPolling();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                    "Client disconnect", CancellationToken.None);
            }
            catch { /* ignore */ }
        }
        _webSocket?.Dispose();
        _webSocket = null;
    }

    /// <summary>彻底销毁（释放所有资源）</summary>
    public void Dispose()
    {
        _disposed = true;
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        StopPolling();
        _webSocket?.Dispose();
    }

    // ---- internal ----

    /// <summary>创建并连接 WebSocket。子类可重写以支持测试替身。</summary>
    protected virtual async Task<WebSocket> CreateAndConnectWebSocketAsync(string wsUrl, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        return ws;
    }

    private async Task ConnectLoopAsync(string wsUrl, CancellationToken ct)
    {
        try
        {
            using var ws = await CreateAndConnectWebSocketAsync(wsUrl, ct);
            _webSocket = ws;
            StopPolling(); // WebSocket 重连成功，停止轮询
            Log("PushWebSocket connected");
            _reconnectAttempts = 0;
            ConnectionStateChanged?.Invoke(true);

            // Read messages in a loop
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log($"PushWebSocket closed by server");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleMessage(text);
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (WebSocketException ex)
        {
            Log($"PushWebSocket error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"PushWebSocket failure: {ex.Message}");
        }

        _webSocket = null;
        ConnectionStateChanged?.Invoke(false);

        if (!_disposed && !ct.IsCancellationRequested)
            _ = ScheduleReconnectAsync(ct);
    }

    private void HandleMessage(string text)
    {
        Log($"PushWebSocket message: {text}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "SyncRequest")
            {
                var vaultId = root.TryGetProperty("vaultId", out var v) ? (v.GetString() ?? "") : "";
                var vaultName = root.TryGetProperty("vaultName", out var vn) ? (vn.GetString() ?? vaultId) : vaultId;
                var action = root.TryGetProperty("action", out var a) ? (a.GetString() ?? "sync") : "sync";

                if (!string.IsNullOrEmpty(vaultId))
                    _ = OnSyncRequest?.Invoke(vaultId, vaultName, action, CancellationToken.None);
            }
            else if (type == "Authorized")
            {
                Authorized?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Log($"PushWebSocket message parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// 调度 WebSocket 重连。返回 <see cref="Task"/>，调用方负责决定如何处理（通常丢弃）。
    /// 使用显式 Task 而非 async void，避免未观察异常和测试困难。
    /// </summary>
    private async Task ScheduleReconnectAsync(CancellationToken ct)
    {
        if (_disposed) return;

        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Log("PushWebSocket max reconnect attempts reached, falling back to HTTP polling");
            StartHttpPolling();
            return;
        }

        _reconnectAttempts++;
        Log($"PushWebSocket reconnecting (attempt {_reconnectAttempts})");

        try
        {
            await Task.Delay(ReconnectDelayMs, ct);
            if (!_disposed && !ct.IsCancellationRequested && !string.IsNullOrEmpty(_serverBaseUrl))
            {
                var wsUrl = _serverBaseUrl.Replace("https://", "wss://")
                    .Replace("http://", "ws://")
                    + $"/ws/devices?deviceName={Uri.EscapeDataString(_deviceName)}";
                if (!string.IsNullOrEmpty(DeviceId))
                    wsUrl += $"&deviceId={Uri.EscapeDataString(DeviceId)}";
                await ConnectLoopAsync(wsUrl, ct);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    // ---- HTTP polling fallback ----

    private void StartHttpPolling()
    {
        if (_disposed || string.IsNullOrEmpty(_serverBaseUrl)) return;

        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();

        // TODO: push-pending 端点尚未实现，轮询降级暂不可用
        _pendingPollUrl = $"{_serverBaseUrl}/mg/devices/push-pending?deviceName={Uri.EscapeDataString(_deviceName)}&wait=false";
        _ = PollLoopAsync(_pollCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!_disposed && _webSocket == null && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_disposed || _webSocket != null || ct.IsCancellationRequested) break;

            try
            {
                var resp = await _httpClient.GetStringAsync(_pendingPollUrl, ct);
                if (!string.IsNullOrEmpty(resp))
                    HandleMessage(resp);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { /* best effort */ }
        }
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
