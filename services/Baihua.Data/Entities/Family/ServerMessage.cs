namespace Baihua.Data.Entities;

/// <summary>
/// 服务器互联消息（双向：in = 从对端收到，out = 发给对端）。
/// </summary>
public class ServerMessage
{
    public Guid Id { get; set; }
    /// <summary>关联的 ServerPeer.Id。</summary>
    public Guid PeerId { get; set; }
    /// <summary>对端服务器实例 ID（冗余，便于按实例 ID 匹配）。</summary>
    public string PeerServerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string Direction { get; set; } = "in";
    public string Content { get; set; } = "";
    public DateTime SentAtUtc { get; set; }
    public bool IsRead { get; set; }
}
