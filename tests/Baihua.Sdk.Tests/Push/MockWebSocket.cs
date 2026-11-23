using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace BaihuaSdk.Tests.Push;

/// <summary>
/// 用于单元测试的 WebSocket 替身。
/// 通过 <see cref="EnqueueTextMessage"/> 模拟服务端向客户端推送消息。
/// </summary>
public sealed class MockWebSocket : WebSocket
{
    private readonly Channel<Message> _incoming = Channel.CreateUnbounded<Message>();
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;

    public void EnqueueTextMessage(string text)
    {
        _incoming.Writer.TryWrite(new Message(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true));
    }

    public void EnqueueClose(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        _closeStatus = status;
        _incoming.Writer.TryWrite(new Message(Array.Empty<byte>(), WebSocketMessageType.Close, true));
    }

    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _incoming.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _closeStatus = closeStatus;
        _state = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _state = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        Message msg;
        try
        {
            msg = await _incoming.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            _state = WebSocketState.Closed;
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        }

        if (msg.MessageType == WebSocketMessageType.Close)
        {
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        var data = msg.Data;
        var count = Math.Min(data.Length, buffer.Count);
        if (count > 0 && buffer.Array != null)
        {
            Array.Copy(data, 0, buffer.Array, buffer.Offset, count);
        }

        return new WebSocketReceiveResult(count, msg.MessageType, msg.EndOfMessage);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private readonly record struct Message(byte[] Data, WebSocketMessageType MessageType, bool EndOfMessage);
}
