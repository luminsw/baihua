using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Baihua.Core.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Baihua.Family.Tests;

/// <summary>
/// DeviceWebSocketHub 推送语义测试：
/// - 带 deviceId = 定向推送（只发给该设备的所有连接）
/// - 不带 deviceId = 全量广播（sync_updated / pair_request 等）
/// 这是"撤销谁谁收到"的服务端根基：撤销/授权/拒绝事件必须定向，不能广播给所有移动端。
/// </summary>
public class DeviceWebSocketHubTests
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static async Task<System.Net.WebSockets.WebSocket> AcceptServerSideWsAsync(TcpClient tcp, CancellationToken ct)
    {
        var stream = tcp.GetStream();
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) throw new InvalidOperationException("client closed before handshake");
            header.Append((char)one[0]);
        }
        var keyLine = header.ToString().Split("\r\n")
            .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        var key = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WsGuid)));
        var resp = "HTTP/1.1 101 Switching Protocols\r\n" +
                   "Upgrade: websocket\r\n" +
                   "Connection: Upgrade\r\n" +
                   $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), ct);
        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
    }

    private sealed class TestDevice : IAsyncDisposable
    {
        public required TcpListener Listener { get; init; }
        public required ClientWebSocket Client { get; init; }
        public required string DeviceId { get; init; }
        public required Task HubLoop { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { Client.Abort(); } catch { }
            try { Listener.Stop(); } catch { }
            await Task.CompletedTask;
        }
    }

    /// <summary>建立一条设备连接（服务端 accept + 握手 + hub 注册），返回客户端句柄。</summary>
    private static async Task<TestDevice> ConnectDeviceAsync(DeviceWebSocketHub hub, string deviceId, string deviceName, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = new ClientWebSocket();
        client.Options.Proxy = null; // 回环直连，禁用系统代理（避免代理配置导致连接失败）
        var uri = new Uri($"ws://127.0.0.1:{port}/ws/devices?deviceName={Uri.EscapeDataString(deviceName)}&deviceId={deviceId}");
        var connectTask = client.ConnectAsync(uri, ct);
        var tcp = await listener.AcceptTcpClientAsync(ct);
        var serverWs = await AcceptServerSideWsAsync(tcp, ct);
        await connectTask; // 确保客户端握手完成
        var hubLoop = Task.Run(() => hub.AcceptAsync(serverWs, deviceName, deviceId), CancellationToken.None);

        return new TestDevice { Listener = listener, Client = client, DeviceId = deviceId, HubLoop = hubLoop };
    }

    private static async Task<string?> TryReceiveAsync(ClientWebSocket ws, int timeoutMs)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), cts.Token);
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException)
        {
            return null; // 超时未收到 = 未推送
        }
    }

    private static async Task WaitForConnectionsAsync(DeviceWebSocketHub hub, int count, CancellationToken ct)
    {
        // hub.AcceptAsync 在 Task.Run 中异步注册连接：广播前必须等连接进入 _connections
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (hub.ConnectedCount < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }
    }

    [Fact]
    public async Task BroadcastAsync_with_deviceId_delivers_only_to_that_device()
    {
        var hub = new DeviceWebSocketHub(NullLogger<DeviceWebSocketHub>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var devA = await ConnectDeviceAsync(hub, "devA", "望月·书轩", cts.Token);
        await using var devB = await ConnectDeviceAsync(hub, "devB", "安卓机", cts.Token);
        await WaitForConnectionsAsync(hub, 2, cts.Token);

        // 撤销 devA：只推给 devA，devB 不应收到
        await hub.BroadcastAsync("revoked", "望月·书轩", type: "Revoked", deviceId: "devA");

        var msgA = await TryReceiveAsync(devA.Client, 3000);
        Assert.NotNull(msgA);
        Assert.Contains("\"action\":\"revoked\"", msgA);
        Assert.Contains("\"deviceId\":\"devA\"", msgA);

        var msgB = await TryReceiveAsync(devB.Client, 800);
        Assert.Null(msgB); // B 完全收不到
    }

    [Fact]
    public async Task BroadcastAsync_without_deviceId_delivers_to_all()
    {
        var hub = new DeviceWebSocketHub(NullLogger<DeviceWebSocketHub>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var devA = await ConnectDeviceAsync(hub, "devA", "设备A", cts.Token);
        await using var devB = await ConnectDeviceAsync(hub, "devB", "设备B", cts.Token);
        await WaitForConnectionsAsync(hub, 2, cts.Token);

        // 同步通知：全量广播（所有移动端都要收到）
        await hub.BroadcastAsync("sync_updated", "某设备", type: "SyncRequest", vaultId: "v1", vaultName: "笔记");

        var msgA = await TryReceiveAsync(devA.Client, 3000);
        var msgB = await TryReceiveAsync(devB.Client, 3000);
        Assert.NotNull(msgA);
        Assert.NotNull(msgB);
        Assert.Contains("SyncRequest", msgA!);
        Assert.Contains("SyncRequest", msgB!);
    }

    [Fact]
    public async Task BroadcastAsync_targeting_unknown_deviceId_falls_back_to_broadcast()
    {
        // 定向目标没有任何连接匹配（目标离线/旧客户端不带 deviceId）→ 降级全量广播，
        // 消息仍带 deviceId，新客户端按 deviceId 过滤忽略非本机，旧客户端靠兼容逻辑收到
        var hub = new DeviceWebSocketHub(NullLogger<DeviceWebSocketHub>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var devA = await ConnectDeviceAsync(hub, "devA", "设备A", cts.Token);
        await WaitForConnectionsAsync(hub, 1, cts.Token);

        await hub.BroadcastAsync("revoked", "未知", deviceId: "ghost-device");

        var msgA = await TryReceiveAsync(devA.Client, 3000);
        Assert.NotNull(msgA); // 降级后仍能收到（自己按 deviceId 判断是否本机）
        Assert.Contains("\"deviceId\":\"ghost-device\"", msgA);
    }

    [Fact]
    public async Task BroadcastAsync_delivers_to_legacy_client_without_deviceId_when_target_missing()
    {
        // 旧客户端：WS 连接不带 deviceId。定向推送（目标=它的设备 id）找不到匹配连接
        // → 降级全量 → 旧客户端能收到授权通知（否则旧客户端永远收不到 authorized）
        var hub = new DeviceWebSocketHub(NullLogger<DeviceWebSocketHub>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var legacy = await ConnectDeviceAsync(hub, "", "旧版安卓", cts.Token); // deviceId 为空 = 旧客户端
        await WaitForConnectionsAsync(hub, 1, cts.Token);

        await hub.BroadcastAsync("authorized", "旧版安卓", type: "Authorized", deviceId: "legacy-android-id");

        var msg = await TryReceiveAsync(legacy.Client, 3000);
        Assert.NotNull(msg);
        Assert.Contains("\"action\":\"authorized\"", msg);
    }

    [Fact]
    public async Task BroadcastAsync_targeting_online_device_does_not_leak_to_others()
    {
        // 定向目标在线（有匹配连接）→ 严格定向，其他连接（含旧客户端无 deviceId）都不收到
        var hub = new DeviceWebSocketHub(NullLogger<DeviceWebSocketHub>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var devA = await ConnectDeviceAsync(hub, "devA", "设备A", cts.Token);
        await using var legacy = await ConnectDeviceAsync(hub, "", "旧版安卓", cts.Token);
        await WaitForConnectionsAsync(hub, 2, cts.Token);

        await hub.BroadcastAsync("revoked", "设备A", deviceId: "devA");

        var msgA = await TryReceiveAsync(devA.Client, 3000);
        Assert.NotNull(msgA);
        var msgLegacy = await TryReceiveAsync(legacy.Client, 800);
        Assert.Null(msgLegacy);
    }
}
