using BaihuaSdk.Services;
using BaihuaSdk.Signing;
using MobileContract.Pairing;
using Moq;
using System.Net;
using System.Text.Json;
using Xunit;

namespace BaihuaSdk.Tests.Services;

public class PairingServiceTests
{
    // ---- QR Code 解析测试 ----

    [Fact]
    public void ParseQrCode_NewFormatBaseUrl_ReturnsContent()
    {
        var json = """{"serverId":"abc","baseUrl":"http://192.168.1.1:8788","hostName":"百花"}""";
        var result = PairingServiceImpl.ParseQrCode(json);
        Assert.NotNull(result);
        Assert.Equal("abc", result!.ServerId);
        Assert.Equal("http://192.168.1.1:8788", result.BaseUrl);
        Assert.Equal("百花", result.HostName);
    }

    [Fact]
    public void ParseQrCode_OldFormatServerUrl_ReturnsContent()
    {
        var json = """{"serverUrl":"http://old.example.com:8788","hostName":"老版本服务器"}""";
        var result = PairingServiceImpl.ParseQrCode(json);
        Assert.NotNull(result);
        Assert.Equal("http://old.example.com:8788", result!.ServerUrl);
    }

    [Fact]
    public void ParseQrCode_DualAddress_ReturnsContent()
    {
        var json = """{"serverId":"s1","httpUrl":"http://192.168.1.1:8788","httpsUrl":"https://example.com","hostName":"双栈服务器"}""";
        var result = PairingServiceImpl.ParseQrCode(json);
        Assert.NotNull(result);
        Assert.Equal("http://192.168.1.1:8788", result!.HttpUrl);
        Assert.Equal("https://example.com", result.HttpsUrl);
    }

    [Fact]
    public void ParseQrCode_NoHostName_ReturnsNull()
    {
        var json = """{"serverId":"abc","baseUrl":"http://example.com"}""";
        Assert.Null(PairingServiceImpl.ParseQrCode(json));
    }

    [Fact]
    public void ParseQrCode_NoUrl_ReturnsNull()
    {
        var json = """{"hostName":"only name"}""";
        Assert.Null(PairingServiceImpl.ParseQrCode(json));
    }

    [Fact]
    public void ParseQrCode_InvalidJson_ReturnsNull()
    {
        Assert.Null(PairingServiceImpl.ParseQrCode("not json"));
    }

    [Fact]
    public void GetServerAddresses_BaseUrl_Preferred()
    {
        var content = new QrCodeContent
        {
            ServerId = "svr-1",
            BaseUrl = "http://192.168.1.1:8788",
            HttpUrl = "http://old", HttpsUrl = "https://old",
            HostName = "测试服务器", DeviceId = "dev-1"
        };
        var addrs = PairingServiceImpl.GetServerAddresses(content);
        Assert.Equal("svr-1", addrs.ServerId);
        Assert.Equal("http://192.168.1.1:8788", addrs.HttpUrl);
        Assert.Equal("http://192.168.1.1:8788", addrs.HttpsUrl);
    }

    [Fact]
    public void GetServerAddresses_FallsBackToDeviceId()
    {
        var content = new QrCodeContent
        {
            BaseUrl = "http://example.com",
            HostName = "test",
            DeviceId = "fallback-device-id"
        };
        var addrs = PairingServiceImpl.GetServerAddresses(content);
        Assert.Equal("fallback-device-id", addrs.ServerId);
    }

    [Fact]
    public void GetServerAddresses_NoId_GeneratesTimestamp()
    {
        var content = new QrCodeContent
        {
            BaseUrl = "http://example.com",
            HostName = "test"
        };
        var addrs = PairingServiceImpl.GetServerAddresses(content);
        Assert.StartsWith("server-", addrs.ServerId);
    }

    // ---- 设备注册测试（Mock HttpClient） ----

    [Fact]
    public async Task RegisterDeviceAsync_SuccessAuthorized_ReturnsSuccess()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        var response = new
        {
            authorized = true,
            sharedSecret = "secret-123",
            requestId = "req-456",
            deviceName = "TestDevice"
        };
        handler.SetupResponse("/mg/register-device", HttpStatusCode.OK,
            JsonSerializer.Serialize(response));

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.True(result.Success);
        Assert.True(result.Authorized);
        Assert.Equal("secret-123", result.SharedSecret);
        Assert.Equal("req-456", result.RequestId);
        Assert.Equal("TestDevice", result.DeviceName);
    }

    [Fact]
    public async Task RegisterDeviceAsync_SuccessUnauthorized_ReturnsUnauthorized()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/register-device", HttpStatusCode.OK,
            """{"authorized":false,"requestId":"req-1","deviceId":"device-1"}""");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.True(result.Success);
        Assert.False(result.Authorized);
        Assert.Equal("req-1", result.RequestId);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ServerError_ReturnsFailure()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/register-device", HttpStatusCode.InternalServerError, "Server error");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RegisterDeviceAsync_NotFound_ReturnsFailure()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/register-device", HttpStatusCode.NotFound, "Not found");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RegisterDeviceAsync_MalformedJson_ReturnsFailure()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/register-device", HttpStatusCode.OK, "not valid json");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.False(result.Success);
    }

    // ---- 真实服务端响应格式契约测试（防止再次与 PairController 格式脱轨） ----

    [Fact]
    public async Task RegisterDeviceAsync_ServerRealUnauthorizedResponse_ParsesAsNotAuthorized()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/register-device", HttpStatusCode.OK,
            """{"message":"设备已注册，请在 WebUI 中授权","deviceId":"d1","deviceName":"Honor","serverName":"family-pc","ipAddress":"192.168.1.100","requestId":"req-abc","authorized":false,"accessToken":null}""");

        var service = new PairingServiceImpl(client, signerMock.Object, "d1", "Honor");
        var result = await service.RegisterDeviceAsync("http://192.168.1.100:8788");

        Assert.True(result.Success);
        Assert.False(result.Authorized);
        Assert.Equal("req-abc", result.RequestId);
        Assert.Equal("family-pc", result.DeviceName);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ServerRealAuthorizedResponse_ParsesSharedSecret()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/register-device", HttpStatusCode.OK,
            """{"message":"设备已授权","deviceId":"d1","deviceName":"Honor","serverName":"family-pc","ipAddress":"192.168.1.100","requestId":"d1","authorized":true,"accessToken":"token-xyz","sharedSecret":"secret-123"}""");

        var service = new PairingServiceImpl(client, signerMock.Object, "d1", "Honor");
        var result = await service.RegisterDeviceAsync("http://192.168.1.100:8788");

        Assert.True(result.Success);
        Assert.True(result.Authorized);
        Assert.Equal("secret-123", result.SharedSecret);
        Assert.Equal("token-xyz", result.AccessToken);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ServerRealMismatchResponse_ParsesAsNotAuthorized()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/register-device", HttpStatusCode.OK,
            """{"message":"设备标识已变更，请在 WebUI 中重新授权","deviceId":"d1","deviceName":"Honor","serverName":"family-pc","ipAddress":"192.168.1.100","requestId":"req-mismatch","authorized":false}""");

        var service = new PairingServiceImpl(client, signerMock.Object, "d1", "Honor");
        var result = await service.RegisterDeviceAsync("http://192.168.1.100:8788");

        Assert.True(result.Success);
        Assert.False(result.Authorized);
        Assert.Equal("req-mismatch", result.RequestId);
    }

    [Fact]
    public void ParseQrCode_EmptyString_ReturnsNull()
    {
        Assert.Null(PairingServiceImpl.ParseQrCode(""));
    }

    [Fact]
    public void ParseQrCode_Null_ReturnsNull()
    {
        Assert.Null(PairingServiceImpl.ParseQrCode(null!));
    }

    // ---- IPairingService 测试 ----

    private static (PairingServiceImpl Service, MockHttpMessageHandler Handler, Mock<IRequestSigner> Signer) CreatePairingService()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");
        return (service, handler, signerMock);
    }

    [Fact]
    public async Task GetPairCodeAsync_ReturnsPairCode()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/pair/code", HttpStatusCode.OK,
            """{"pairCode":"123456","deviceId":"server-1"}""");

        var result = await service.GetPairCodeAsync();

        Assert.Equal("123456", result.PairCode);
        Assert.Equal("server-1", result.DeviceId);
    }

    [Fact]
    public async Task GetPairCodeAsync_ServerError_ReturnsEmpty()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/pair/code", HttpStatusCode.InternalServerError, "error");

        var result = await service.GetPairCodeAsync();

        Assert.Null(result.PairCode);
        Assert.Equal("", result.DeviceId);
    }

    [Fact]
    public async Task RefreshPairCodeAsync_ReturnsNewPairCode()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/pair/code/refresh", HttpStatusCode.OK,
            """{"pairCode":"654321","message":"配对码已刷新"}""");

        var result = await service.RefreshPairCodeAsync();

        Assert.Equal("654321", result.PairCode);
        Assert.Equal("device-1", result.DeviceId);
    }

    [Fact]
    public async Task PairDeviceAsync_Authorized_ReturnsToken()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/pair", HttpStatusCode.OK,
            """{"requestId":"req-1","accessToken":"token-abc","expiresIn":86400,"status":"authorized","message":"已授权"}""");

        var result = await service.PairDeviceAsync(new PairRequest
        {
            PairCode = "123456",
            DeviceName = "MyDevice"
        });

        Assert.Equal("authorized", result.Status);
        Assert.Equal("token-abc", result.AccessToken);
        Assert.Equal("req-1", result.RequestId);
        Assert.Equal(86400, result.ExpiresIn);
    }

    [Fact]
    public async Task PairDeviceAsync_Pending_ReturnsRequestId()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/pair", HttpStatusCode.OK,
            """{"requestId":"req-2","status":"pending","message":"等待授权"}""");

        var result = await service.PairDeviceAsync(new PairRequest
        {
            PairCode = "123456"
        });

        Assert.Equal("pending", result.Status);
        Assert.Equal("req-2", result.RequestId);
    }

    [Fact]
    public async Task PairDeviceAsync_InvalidPairCode_ReturnsFailed()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/pair", HttpStatusCode.BadRequest,
            """{"error":"配对码错误"}""");

        var result = await service.PairDeviceAsync(new PairRequest
        {
            PairCode = "wrong"
        });

        Assert.Equal("failed", result.Status);
        Assert.Equal("配对码错误", result.Message);
    }

    [Fact]
    public async Task PairDeviceAsync_WithoutInitialize_Throws()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PairDeviceAsync(new PairRequest { PairCode = "123456" }));
    }

    [Fact]
    public async Task CheckPairStatusAsync_ThrowsNotSupported()
    {
        var (service, _, _) = CreatePairingService();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.CheckPairStatusAsync("req-1"));
    }

    [Fact]
    public async Task VerifyTokenAsync_ThrowsNotSupported()
    {
        var (service, _, _) = CreatePairingService();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.VerifyTokenAsync(new VerifyTokenRequest { Token = "token" }));
    }

    [Fact]
    public async Task GetAuthConfigAsync_Authorized_ReturnsSharedSecret()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/auth/config", HttpStatusCode.OK,
            """{"success":true,"sharedSecret":"secret-abc","message":"已授权"}""");

        var result = await service.GetAuthConfigAsync(new AuthConfigRequest { DeviceId = "device-1" });

        Assert.True(result.Success);
        Assert.Equal("secret-abc", result.SharedSecret);
    }

    [Fact]
    public async Task GetAuthConfigAsync_Unauthorized_ReturnsFailure()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/auth/config", HttpStatusCode.OK,
            """{"success":false,"message":"设备未授权"}""");

        var result = await service.GetAuthConfigAsync(new AuthConfigRequest { DeviceId = "device-1" });

        Assert.False(result.Success);
        Assert.Null(result.SharedSecret);
    }

    [Fact]
    public async Task GetAuthConfigAsync_Http401_ReturnsFailure()
    {
        var (service, handler, _) = CreatePairingService();
        handler.SetupResponse("/mg/auth/config", HttpStatusCode.Unauthorized, "");

        var result = await service.GetAuthConfigAsync(new AuthConfigRequest { DeviceId = "device-1" });

        Assert.False(result.Success);
        Assert.Null(result.SharedSecret);
    }
}
