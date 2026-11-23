namespace Baihua.Data.Entities;

/// <summary>
/// 登记的其它百花服务器（互联消息对端）。
/// 来源：manual（WebUI 手动登记）/ lan（UDP 局域网发现）/ remote（对方推送消息时自动登记）。
/// </summary>
public class ServerPeer
{
    public Guid Id { get; set; }
    /// <summary>对方服务器实例 ID（ServerInstanceId）。</summary>
    public string ServerId { get; set; } = "";
    /// <summary>显示名（对方 WebUI 配置的 DisplayName）。</summary>
    public string Name { get; set; } = "";
    /// <summary>对方 HTTP 入口地址，如 http://192.168.3.14/（80 或其它端口）。</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>共享口令（可选；留空时发送用本机 BAIHUA_SERVER_MSG_TOKEN）。</summary>
    public string? Token { get; set; }
    public string Source { get; set; } = "manual";
    public DateTime? LastSeenUtc { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}
