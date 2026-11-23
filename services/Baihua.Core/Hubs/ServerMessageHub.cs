using Microsoft.AspNetCore.SignalR;

namespace Baihua.Core.Hubs;

/// <summary>
/// 服务器互联消息推送 Hub（/hubs/server-messages）。
/// 对端消息落库后向 WebUI 广播 NewMessage，实现实时显示（轮询仅作兜底）。
/// </summary>
public class ServerMessageHub : Hub
{
}
