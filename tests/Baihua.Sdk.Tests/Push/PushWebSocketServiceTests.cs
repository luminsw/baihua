using BaihuaSdk.Push;
using Xunit;

namespace BaihuaSdk.Tests.Push;

public class PushWebSocketServiceTests
{
    private static HttpClient CreateHttpClient() => new();

    private static async Task<TestablePushWebSocketService> CreateConnectedServiceAsync(HttpClient client)
    {
        var service = new TestablePushWebSocketService(client);
        var connectedTcs = new TaskCompletionSource<bool>();
        service.ConnectionStateChanged += connected =>
        {
            if (connected) connectedTcs.TrySetResult(true);
        };

        await service.ConnectAsync("http://localhost:8788", "TestDevice");
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(service.LastMockSocket);
        return service;
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        using var httpClient = CreateHttpClient();
        using var service = new PushWebSocketService(httpClient);
        Assert.NotNull(service);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public void IsConnected_ReturnsFalse_WhenNotConnected()
    {
        using var httpClient = CreateHttpClient();
        using var service = new PushWebSocketService(httpClient);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public void ConnectionStateChanged_CanBeSubscribed()
    {
        using var httpClient = CreateHttpClient();
        bool? state = null;
        using var service = new PushWebSocketService(httpClient);
        service.ConnectionStateChanged += connected => state = connected;
        Assert.Null(state);
    }

    [Fact]
    public void OnSyncRequest_CanBeSet()
    {
        using var httpClient = CreateHttpClient();
        var service = new PushWebSocketService(httpClient)
        {
            OnSyncRequest = (vaultId, vaultName, action, ct) =>
            {
                Assert.NotEmpty(vaultId);
                return Task.CompletedTask;
            }
        };
        Assert.NotNull(service.OnSyncRequest);
    }

    [Fact]
    public void DisconnectAsync_WhenNotConnected_NoOp()
    {
        using var httpClient = CreateHttpClient();
        using var service = new PushWebSocketService(httpClient);
        var task = service.DisconnectAsync();
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var httpClient = CreateHttpClient();
        var service = new PushWebSocketService(httpClient);
        service.Dispose();
        service.Dispose(); // no exception
    }

    [Fact]
    public void Service_HasOnLogCallback()
    {
        using var httpClient = CreateHttpClient();
        string? lastLog = null;
        var service = new PushWebSocketService(httpClient)
        {
            OnLog = msg => lastLog = msg
        };
        Assert.NotNull(service.OnLog);
    }

    [Fact]
    public async Task ConnectAsync_InvokesConnectionStateChanged_True()
    {
        using var httpClient = CreateHttpClient();
        var service = new TestablePushWebSocketService(httpClient);
        var states = new List<bool>();
        service.ConnectionStateChanged += states.Add;

        await service.ConnectAsync("http://localhost:8788", "TestDevice");

        await Task.Delay(100);
        Assert.Contains(true, states);
        Assert.True(service.IsConnected);
    }

    [Fact]
    public async Task ReceiveAuthorizedMessage_InvokesAuthorizedEvent()
    {
        using var httpClient = CreateHttpClient();
        var service = await CreateConnectedServiceAsync(httpClient);

        var authorizedTcs = new TaskCompletionSource<object?>();
        service.Authorized += (sender, args) => authorizedTcs.TrySetResult(null);

        service.LastMockSocket!.EnqueueTextMessage("""{"type":"Authorized","deviceName":"TestDevice"}""");
        service.LastMockSocket!.EnqueueClose();

        await authorizedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(authorizedTcs.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReceiveSyncRequest_InvokesOnSyncRequest()
    {
        using var httpClient = CreateHttpClient();
        var service = await CreateConnectedServiceAsync(httpClient);

        var receivedTcs = new TaskCompletionSource<(string vaultId, string vaultName, string action)>();
        service.OnSyncRequest = (vaultId, vaultName, action, ct) =>
        {
            receivedTcs.TrySetResult((vaultId, vaultName, action));
            return Task.CompletedTask;
        };

        service.LastMockSocket!.EnqueueTextMessage("""{"type":"SyncRequest","vaultId":"vault-1","vaultName":"MyVault","action":"sync"}""");
        service.LastMockSocket!.EnqueueClose();

        var (vaultId, vaultName, action) = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("vault-1", vaultId);
        Assert.Equal("MyVault", vaultName);
        Assert.Equal("sync", action);
    }

    [Fact]
    public async Task ServerClose_InvokesConnectionStateChanged_False()
    {
        using var httpClient = CreateHttpClient();
        var service = await CreateConnectedServiceAsync(httpClient);

        var disconnectedTcs = new TaskCompletionSource<bool>();
        service.ConnectionStateChanged += connected =>
        {
            if (!connected) disconnectedTcs.TrySetResult(true);
        };

        service.LastMockSocket!.EnqueueClose();

        await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task ReceiveMessage_LogsMessage()
    {
        using var httpClient = CreateHttpClient();
        var service = await CreateConnectedServiceAsync(httpClient);

        var logs = new List<string>();
        service.OnLog = logs.Add;

        service.LastMockSocket!.EnqueueTextMessage("""{"type":"Authorized"}""");
        service.LastMockSocket!.EnqueueClose();

        await Task.Delay(200);
        Assert.Contains(logs, msg => msg.Contains("PushWebSocket message"));
    }
}
