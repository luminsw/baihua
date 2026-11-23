using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Baihua.Core.WebSocket;

public class DeviceWebSocketHub
{
    private readonly ConcurrentDictionary<string, WebSocketConnection> _connections = new();
    private readonly ILogger<DeviceWebSocketHub> _logger;

    public DeviceWebSocketHub(ILogger<DeviceWebSocketHub> logger)
    {
        _logger = logger;
    }

    public int ConnectedCount => _connections.Count;

    public async Task AcceptAsync(System.Net.WebSockets.WebSocket webSocket, string? deviceName = null,
        string? deviceId = null, string? serverId = null, string? serverName = null)
    {
        var connectionId = Guid.NewGuid().ToString("N")[..8];
        var connection = new WebSocketConnection(connectionId, webSocket, deviceName, deviceId);
        _connections[connectionId] = connection;
        _logger.LogInformation("WebSocket 客户端连接: {ConnectionId}, deviceName={DeviceName}", connectionId, deviceName);

        // 握手：告知客户端本服务器的身份（serverId），供客户端校验是否为本地已添加的服务器。
        // 防止换网络后遇到 IP 相同的另一台服务器，被误认为是已添加的服务器。
        if (!string.IsNullOrEmpty(serverId))
        {
            try
            {
                var welcome = new Dictionary<string, object?>
                {
                    ["action"] = "server_info",
                    ["serverId"] = serverId,
                    ["serverName"] = serverName ?? "",
                    ["timestamp"] = DateTime.UtcNow.ToString("o")
                };
                var welcomeBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(welcome));
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(welcomeBytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "发送 server_info 握手消息失败: {ConnectionId}", connectionId);
            }
        }

        try
        {
            var buffer = new byte[4096];
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationToken.None);
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            _logger.LogInformation("WebSocket 客户端断开: {ConnectionId}", connectionId);
        }
    }

    public async Task BroadcastAsync(string action, string? deviceName = null, string? requestId = null,
        string? type = null, string? vaultId = null, string? vaultName = null, string? deviceId = null)
    {
        if (_connections.IsEmpty) return;

        // deviceId 非空 = 定向推送（只发给该设备的所有连接，如授权/拒绝/撤销）；
        // 为空 = 全量广播（如 sync_updated 同步通知、pair_request 配对通知，所有移动端都要收到）
        var targeted = !string.IsNullOrEmpty(deviceId);
        // 优雅降级：定向推送但没有任何连接携带该 deviceId 时（旧客户端 WS 不带 deviceId、
        // 或目标设备离线），降级为全量广播——消息仍带 deviceId，新客户端会按 deviceId 过滤
        // 忽略非本机事件，旧客户端靠兼容逻辑处理，避免事件丢失（如授权通知到达不了旧客户端）
        if (targeted && !_connections.Values.Any(c => c.DeviceId == deviceId))
        {
            _logger.LogInformation("WS 定向推送无匹配连接 deviceId={DeviceId}，降级为全量广播", deviceId);
            targeted = false;
        }

        var msg = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["deviceName"] = deviceName,
            ["requestId"] = requestId,
            ["deviceId"] = deviceId,
            ["timestamp"] = DateTime.UtcNow.ToString("o")
        };
        if (!string.IsNullOrEmpty(type)) msg["type"] = type;
        if (!string.IsNullOrEmpty(vaultId)) msg["vaultId"] = vaultId;
        if (!string.IsNullOrEmpty(vaultName)) msg["vaultName"] = vaultName;

        var message = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(message);

        var dead = new List<string>();

        foreach (var kvp in _connections)
        {
            if (targeted && kvp.Value.DeviceId != deviceId) continue;
            try
            {
                if (kvp.Value.Socket.State == WebSocketState.Open)
                {
                    await kvp.Value.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                else
                {
                    dead.Add(kvp.Key);
                }
            }
            catch
            {
                dead.Add(kvp.Key);
            }
        }

        foreach (var id in dead)
        {
            _connections.TryRemove(id, out _);
        }
    }

    private record WebSocketConnection(string Id, System.Net.WebSockets.WebSocket Socket, string? DeviceName, string? DeviceId);
}