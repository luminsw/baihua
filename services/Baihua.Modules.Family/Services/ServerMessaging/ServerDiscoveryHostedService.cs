using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Baihua.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services.ServerMessaging;

/// <summary>
/// 百花服务器局域网发现（UDP 广播）：
/// - 定时（30s）向 255.255.255.255:{UdpPort} 广播本机身份 {serverId, name, httpPort, baseUrl}
/// - 监听 {UdpPort} 接收其它百花服务器广播 → 登记为对端（Source=lan）
/// 注：k8s Pod 网络下广播源为 Pod IP，跨机器可能不可达；可靠登记请用 WebUI 手动添加，
/// 或配置 BAIHUA_SERVER_PUBLIC_BASE_URL 后广播携带公网/节点入口地址。
/// </summary>
public class ServerDiscoveryHostedService : BackgroundService
{
    private readonly ServerMessageService _messageService;
    private readonly ServerAddressService _serverAddressService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServerDiscoveryHostedService> _logger;

    private const int DefaultUdpPort = 45678;
    private const int BroadcastIntervalSec = 30;

    public ServerDiscoveryHostedService(
        ServerMessageService messageService,
        ServerAddressService serverAddressService,
        IConfiguration configuration,
        ILogger<ServerDiscoveryHostedService> logger)
    {
        _messageService = messageService;
        _serverAddressService = serverAddressService;
        _configuration = configuration;
        _logger = logger;
    }

    private int UdpPort => _configuration.GetValue<int?>("BAIHUA_SERVER_DISCOVERY_PORT") ?? DefaultUdpPort;
    private int HttpPort => _configuration.GetValue<int?>("BAIHUA_SERVER_DISCOVERY_HTTP_PORT") ?? 80;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_configuration.GetValue<bool?>("BAIHUA_SERVER_DISCOVERY_DISABLED") == true)
        {
            _logger.LogInformation("[ServerDiscovery] 已禁用（BAIHUA_SERVER_DISCOVERY_DISABLED=true）");
            return;
        }

        // 监听线程
        _ = Task.Run(() => ListenLoopAsync(stoppingToken), stoppingToken);

        // 广播循环
        _logger.LogInformation("[ServerDiscovery] 启动：UDP {Port}，每 {Interval}s 广播一次", UdpPort, BroadcastIntervalSec);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BroadcastAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ServerDiscovery] 广播失败");
            }
            await Task.Delay(TimeSpan.FromSeconds(BroadcastIntervalSec), stoppingToken);
        }
    }

    private async Task BroadcastAsync(CancellationToken ct)
    {
        var serverId = _serverAddressService.GetServerInstanceId();
        var name = _serverAddressService.GetSettings().DisplayName;
        var publicBase = _messageService.GetOwnPublicBaseUrl();
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "baihua-server",
            ["serverId"] = serverId,
            ["name"] = name,
            ["httpPort"] = HttpPort
        };
        if (!string.IsNullOrEmpty(publicBase))
            payload["baseUrl"] = publicBase;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, UdpPort), ct);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ServerDiscovery] 绑定 UDP {Port} 失败（端口被占用？），发现功能不可用", UdpPort);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                await HandleBroadcastAsync(text, result.RemoteEndPoint.Address, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ServerDiscovery] 接收广播异常");
            }
        }
    }

    private async Task HandleBroadcastAsync(string text, IPAddress remoteIp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "baihua-server")
                return;
            var serverId = root.TryGetProperty("serverId", out var id) ? id.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(serverId)) return;

            var ownId = _serverAddressService.GetServerInstanceId();
            if (serverId == ownId) return; // 跳过自己
            if (IsLocalAddress(remoteIp)) return; // 本机广播回环（或本机其它实例），跳过自我登记

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "百花服务器" : "百花服务器";
            var httpPort = root.TryGetProperty("httpPort", out var p) ? p.GetInt32() : 80;
            var baseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = $"http://{remoteIp}:{httpPort}";

            var peer = await _messageService.UpsertDiscoveredPeerAsync(serverId, name, baseUrl, "lan", ct);
            _logger.LogInformation("[ServerDiscovery] 发现百花服务器: {Name} ({ServerId}) @ {BaseUrl}",
                peer.Name, serverId, baseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ServerDiscovery] 解析广播失败");
        }
    }

    /// <summary>广播源是否为本机地址（回环或本机网卡 IP）——防止把自己的广播登记成对端。</summary>
    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.Equals(address))
                        return true;
                }
            }
        }
        catch
        {
            // 网络接口查询失败时保守处理：不拦截，由算力池侧的自我登记判断兜底
        }
        return false;
    }
}
