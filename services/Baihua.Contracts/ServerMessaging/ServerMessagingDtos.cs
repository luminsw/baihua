namespace Baihua.Contracts.ServerMessaging;

/// <summary>登记的其它百花服务器（互联消息对端）。</summary>
public class ServerPeerDto
{
    public Guid Id { get; set; }
    /// <summary>对方服务器实例 ID（ServerInstanceId）。</summary>
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    /// <summary>来源：manual（手动登记）/ lan（局域网发现）/ remote（对方推送消息时自动登记）。</summary>
    public string Source { get; set; } = "manual";
    /// <summary>是否配置了共享口令（不回传明文）。</summary>
    public bool HasToken { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

/// <summary>手动登记对端服务器。</summary>
public class ServerPeerSaveRequest
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    /// <summary>可选：与对方约定的共享口令（留空则用本机 BAIHUA_SERVER_MSG_TOKEN）。</summary>
    public string? Token { get; set; }
}

/// <summary>与某对端的一条消息（双向合并展示）。</summary>
public class ServerMessageDto
{
    public Guid Id { get; set; }
    /// <summary>in = 收到，out = 发出。</summary>
    public string Direction { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime SentAtUtc { get; set; }
    public bool IsRead { get; set; }
}

public class ServerMessageSendRequest
{
    public Guid PeerId { get; set; }
    public string Content { get; set; } = "";
}

public class ServerMessageSendResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public ServerMessageDto? Message { get; set; }
}

/// <summary>对端服务器推送消息到本机的请求体（/mg/server-msg/inbox）。</summary>
public class ServerMessageInboxRequest
{
    /// <summary>发送方服务器实例 ID。</summary>
    public string FromServerId { get; set; } = "";
    public string FromName { get; set; } = "";
    /// <summary>发送方 HTTP 入口地址（供接收方回发消息）。</summary>
    public string FromBaseUrl { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime SentAtUtc { get; set; }
}
