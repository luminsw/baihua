using System.Net;
using BaihuaSdk.Push;
using BaihuaSdk.Signing;
using MobileContract.Devices;
using Moq;
using System.Text.Json;
using Xunit;

namespace BaihuaSdk.Tests.Services;

public class PushPollingServiceTests
{
    [Fact]
    public async Task PollPendingAsync_WithPendingRequests_ReturnsRequests()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        var expected = new List<PushSyncRequest>
        {
            new() { RequestId = "r1", DeviceId = "d1", VaultId = "v1", Action = "sync" }
        };
        handler.SetupResponse("/mg/devices/push-pending", HttpStatusCode.OK,
            JsonSerializer.Serialize(expected));

        var service = new PushPollingServiceImpl(client, signerMock.Object);
        service.Initialize("http://localhost:8788");

        var result = await service.PollPendingAsync("MyPhone");

        Assert.Single(result);
        Assert.Equal("r1", result[0].RequestId);
    }

    [Fact]
    public async Task PollPendingAsync_ServerError_ReturnsEmptyList()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/devices/push-pending", HttpStatusCode.InternalServerError, "error");

        var service = new PushPollingServiceImpl(client, signerMock.Object);
        service.Initialize("http://localhost:8788");

        var result = await service.PollPendingAsync("MyPhone");

        Assert.Empty(result);
    }
}
