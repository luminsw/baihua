using System.Net.WebSockets;
using BaihuaSdk.Push;

namespace BaihuaSdk.Tests.Push;

/// <summary>
/// 测试专用子类，重写 WebSocket 创建逻辑以注入 <see cref="MockWebSocket"/>。
/// </summary>
public sealed class TestablePushWebSocketService : PushWebSocketService
{
    private readonly Func<MockWebSocket> _socketFactory;

    public MockWebSocket? LastMockSocket { get; private set; }

    public TestablePushWebSocketService(HttpClient httpClient, Func<MockWebSocket>? socketFactory = null)
        : base(httpClient)
    {
        _socketFactory = socketFactory ?? (() => new MockWebSocket());
    }

    protected override Task<WebSocket> CreateAndConnectWebSocketAsync(string wsUrl, CancellationToken ct)
    {
        LastMockSocket = _socketFactory();
        return Task.FromResult<WebSocket>(LastMockSocket);
    }
}
