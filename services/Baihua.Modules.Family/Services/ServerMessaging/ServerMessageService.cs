using Baihua.Contracts.ServerMessaging;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Core.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Baihua.Modules.Family.Services.ServerMessaging;

/// <summary>
/// 百花服务器互联消息服务：
/// - 对端登记（手动 / 局域网发现 / 对方推送时自动）
/// - 消息发送（HTTP 推送到对端 /mg/server-msg/inbox，X-Server-Token 鉴权）
/// - 消息接收（校验口令后落库）与双向列表
/// </summary>
public class ServerMessageService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ServerAddressService _serverAddressService;
    private readonly Microsoft.AspNetCore.SignalR.IHubContext<Baihua.Core.Hubs.ServerMessageHub> _hub;
    private readonly ILogger<ServerMessageService> _logger;

    /// <summary>本机共享口令（BAIHUA_SERVER_MSG_TOKEN）。未配置则接收端不做口令校验。 </summary>
    public string LocalToken => _configuration["BAIHUA_SERVER_MSG_TOKEN"] ?? "";

    public ServerMessageService(
        IDbContextFactory<FamilyDbContext> dbFactory,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ServerAddressService serverAddressService,
        Microsoft.AspNetCore.SignalR.IHubContext<Baihua.Core.Hubs.ServerMessageHub> hub,
        ILogger<ServerMessageService> logger)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _serverAddressService = serverAddressService;
        _hub = hub;
        _logger = logger;
    }

    // ---------- Peers ----------

    public async Task<List<ServerPeer>> ListPeersAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ServerPeers.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<ServerPeer?> GetPeerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ServerPeers.FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>手动登记对端（按 BaseUrl 去重，重复登记更新名称/口令）。</summary>
    public async Task<ServerPeer> AddPeerAsync(ServerPeerSaveRequest request, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var baseUrl = request.BaseUrl.Trim().TrimEnd('/');
        var existing = await db.ServerPeers.FirstOrDefaultAsync(p => p.BaseUrl == baseUrl, ct);
        if (existing != null)
        {
            existing.Name = request.Name.Trim();
            existing.Token = string.IsNullOrWhiteSpace(request.Token) ? existing.Token : request.Token.Trim();
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var peer = new ServerPeer
        {
            Id = Guid.NewGuid(),
            ServerId = "",
            Name = request.Name.Trim(),
            BaseUrl = baseUrl,
            Token = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim(),
            Source = "manual",
            AddedAtUtc = DateTime.UtcNow
        };
        db.ServerPeers.Add(peer);
        await db.SaveChangesAsync(ct);
        return peer;
    }

    public async Task<bool> DeletePeerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var peer = await db.ServerPeers.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (peer == null) return false;
        db.ServerPeers.Remove(peer);
        var msgs = db.ServerMessages.Where(m => m.PeerId == id);
        db.ServerMessages.RemoveRange(msgs);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// 以对端 /mg/capabilities 返回的权威身份校正登记（对端改名/重装后 ServerId 变化时调用）。
    /// 若另一条登记已持有该 ServerId（同一台机器的旧登记残留），删除本条旧登记并返回那条，
    /// 避免算力池里同一台机器出现两个节点。
    /// </summary>
    public async Task<ServerPeer?> ReconcilePeerIdentityAsync(Guid peerId, string serverId, string name, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var peer = await db.ServerPeers.FirstOrDefaultAsync(p => p.Id == peerId, ct);
        if (peer == null) return null;
        var oldServerId = peer.ServerId;

        var owner = await db.ServerPeers.FirstOrDefaultAsync(p => p.Id != peerId && p.ServerId == serverId, ct);
        if (owner != null)
        {
            db.ServerPeers.Remove(peer);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[ServerMsg] 对端 {PeerId} 身份校正：旧登记 {Old} 已被新登记 {New} 取代，删除旧登记",
                peerId, oldServerId, serverId);
            return owner;
        }

        peer.ServerId = serverId;
        if (!string.IsNullOrWhiteSpace(name)) peer.Name = name;
        peer.LastSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[ServerMsg] 对端 {PeerId} 身份校正：{Old} → {New}（{Name}）", peerId, oldServerId, serverId, name);
        return peer;
    }

    /// <summary>发现/收到消息时登记对端（按 ServerId 或 BaseUrl 去重，Source=lan/remote）。</summary>
    public async Task<ServerPeer> UpsertDiscoveredPeerAsync(string serverId, string name, string baseUrl, string source, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var normBase = string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.TrimEnd('/');
        // 优先按 ServerId 匹配；手动登记的 peer ServerId 为空，按 BaseUrl 匹配以合并到同一条会话
        var existing = await db.ServerPeers.FirstOrDefaultAsync(p =>
            (!string.IsNullOrWhiteSpace(serverId) && p.ServerId == serverId) ||
            (normBase.Length > 0 && p.BaseUrl == normBase), ct);
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(serverId)) existing.ServerId = serverId;
            if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
            if (normBase.Length > 0) existing.BaseUrl = normBase;
            existing.LastSeenUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var peer = new ServerPeer
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            Name = name,
            BaseUrl = normBase,
            Source = source,
            LastSeenUtc = DateTime.UtcNow,
            AddedAtUtc = DateTime.UtcNow
        };
        db.ServerPeers.Add(peer);
        await db.SaveChangesAsync(ct);
        return peer;
    }

    // ---------- Messages ----------

    /// <summary>发送消息到对端服务器；成功后在本地记录 Outgoing。</summary>
    public async Task<ServerMessageSendResult> SendMessageAsync(Guid peerId, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new ServerMessageSendResult { Success = false, Error = "消息内容不能为空" };

        var peer = await GetPeerAsync(peerId, ct);
        if (peer == null)
            return new ServerMessageSendResult { Success = false, Error = "对端服务器不存在" };

        var baseUrl = peer.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
            return new ServerMessageSendResult { Success = false, Error = "对端服务器地址无效" };

        var token = !string.IsNullOrWhiteSpace(peer.Token) ? peer.Token! : LocalToken;
        var ownId = _serverAddressService.GetServerInstanceId();
        var ownName = _serverAddressService.GetSettings().DisplayName;

        var request = new ServerMessageInboxRequest
        {
            FromServerId = ownId,
            FromName = ownName,
            FromBaseUrl = GetOwnPublicBaseUrl(),
            Content = content,
            SentAtUtc = DateTime.UtcNow
        };

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/mg/server-msg/inbox");
            if (!string.IsNullOrEmpty(token))
                httpReq.Headers.TryAddWithoutValidation("X-Server-Token", token);
            httpReq.Content = JsonContent.Create(request);

            using var resp = await client.SendAsync(httpReq, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[ServerMsg] Send failed to {Peer} ({BaseUrl}): HTTP {Status} {Body}",
                    peer.Name, baseUrl, (int)resp.StatusCode, body[..Math.Min(body.Length, 300)]);
                return new ServerMessageSendResult { Success = false, Error = $"对方返回 HTTP {(int)resp.StatusCode}" };
            }

            var msg = new ServerMessage
            {
                Id = Guid.NewGuid(),
                PeerId = peer.Id,
                PeerServerId = peer.ServerId,
                PeerName = peer.Name,
                Direction = "out",
                Content = content,
                SentAtUtc = request.SentAtUtc,
                IsRead = true
            };
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.ServerMessages.Add(msg);
            await db.SaveChangesAsync(ct);

            return new ServerMessageSendResult { Success = true, Message = MapMessage(msg) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ServerMsg] Send exception to {BaseUrl}", baseUrl);
            return new ServerMessageSendResult { Success = false, Error = $"发送失败: {ex.Message}" };
        }
    }

    /// <summary>接收对端消息：校验口令 → 登记对端 → 落库 Incoming。</summary>
    public async Task<(bool ok, string? error)> ReceiveMessageAsync(ServerMessageInboxRequest request, string? tokenHeader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return (false, "消息内容为空");

        // 口令校验：本机配置了口令则必须匹配
        var localToken = LocalToken;
        if (!string.IsNullOrEmpty(localToken))
        {
            if (string.IsNullOrEmpty(tokenHeader) || !string.Equals(tokenHeader, localToken, StringComparison.Ordinal))
                return (false, "口令校验失败");
        }

        // 登记/更新对端（按 ServerId 去重）
        if (!string.IsNullOrWhiteSpace(request.FromServerId))
        {
            var peer = await UpsertDiscoveredPeerAsync(
                request.FromServerId, request.FromName ?? "百花服务器",
                request.FromBaseUrl ?? "", "remote", ct);
            var msg = new ServerMessage
            {
                Id = Guid.NewGuid(),
                PeerId = peer.Id,
                PeerServerId = peer.ServerId,
                PeerName = peer.Name,
                Direction = "in",
                Content = request.Content,
                SentAtUtc = request.SentAtUtc == default ? DateTime.UtcNow : request.SentAtUtc,
                IsRead = false
            };
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.ServerMessages.Add(msg);
            await db.SaveChangesAsync(ct);

            // 实时推送：WebUI 服务器互联页收到后立即刷新（轮询仅兜底）
            try
            {
                await _hub.Clients.All.SendAsync("NewMessage", new
                {
                    peerId = peer.Id,
                    peerServerId = peer.ServerId,
                    fromName = peer.Name,
                    content = request.Content,
                    sentAtUtc = msg.SentAtUtc
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "服务器互联消息推送失败");
            }

            return (true, null);
        }

        return (false, "缺少发送方标识");
    }

    /// <summary>与某对端的双向消息列表（按时间升序）。</summary>
    public async Task<List<ServerMessage>> ListMessagesAsync(Guid peerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ServerMessages
            .Where(m => m.PeerId == peerId)
            .OrderBy(m => m.SentAtUtc)
            .ToListAsync(ct);
    }

    public static ServerMessageDto MapMessage(ServerMessage m) => new()
    {
        Id = m.Id,
        Direction = m.Direction,
        Content = m.Content,
        SentAtUtc = m.SentAtUtc,
        IsRead = m.IsRead
    };

    public static ServerPeerDto MapPeer(ServerPeer p) => new()
    {
        Id = p.Id,
        ServerId = p.ServerId,
        Name = p.Name,
        BaseUrl = p.BaseUrl,
        Source = p.Source,
        HasToken = !string.IsNullOrWhiteSpace(p.Token),
        LastSeenUtc = p.LastSeenUtc,
        AddedAtUtc = p.AddedAtUtc
    };

    /// <summary>
    /// 本机局域网/公网入口地址（供广播与回发）。自动探测，无需手动配置：
    /// 1. BAIHUA_SERVER_PUBLIC_BASE_URL 显式配置优先；
    /// 2. k8s 下行 API 注入的 BAIHUA_HOST_IP（节点 IP，入口为 traefik :80）；
    /// 3. native：自动探测本机 IP + Kestrel 监听端口。
    /// </summary>
    public string GetOwnPublicBaseUrl()
    {
        var configured = _configuration["BAIHUA_SERVER_PUBLIC_BASE_URL"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim().TrimEnd('/');

        // k8s：下行 API（status.hostIP）注入的节点 IP → http://<节点IP>/（traefik :80 入口）
        var hostIp = _configuration["BAIHUA_HOST_IP"];
        if (!string.IsNullOrWhiteSpace(hostIp))
            return $"http://{hostIp}";

        // native：自动探测本机 IP + Kestrel 监听端口
        return _serverAddressService.GetLocalPublicBaseUrl();
    }
}
