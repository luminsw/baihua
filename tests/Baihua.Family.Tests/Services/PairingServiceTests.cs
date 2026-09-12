using Baihua.Core.Models;
using Baihua.Core.Services;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Baihua.Data;
using Baihua.Modules.Family.Services;

namespace Baihua.Family.Tests.Services;

public class PairingServiceTests
{
    private readonly Mock<ILogger<PairingService>> _loggerMock = new();
    private readonly PairingService _service;

    public PairingServiceTests()
    {
        var config = new ConfigurationBuilder().Build();
        var serverAddressServiceMock = new Mock<ServerAddressService>(
            new Mock<IDbContextFactory<FamilyDbContext>>().Object,
            NullLogger<ServerAddressService>.Instance,
            config
        );
        _service = new PairingService(_loggerMock.Object, serverAddressServiceMock.Object);
    }

    [Fact]
    public void GenerateQRCodeContent_WithUrlAndHostName_ReturnsValidTuple()
    {
        var result = _service.GenerateQRCodeContent("http://192.168.1.100:8788", "my-server");

        Assert.Equal("http://192.168.1.100:8788", result.url);
        Assert.Equal("my-server", result.hostName);
        Assert.NotNull(result.qrCodeData);
        Assert.NotEmpty(result.qrCodeData);
    }

    [Fact]
    public void GenerateQRCodeContent_QrCodeDataContainsServerId()
    {
        var result = _service.GenerateQRCodeContent("http://localhost:8788", "localhost");

        Assert.Contains("serverId", result.qrCodeData);
    }

    [Fact]
    public void GenerateQRCodeContent_QrCodeDataContainsBaseUrl()
    {
        var result = _service.GenerateQRCodeContent("http://192.168.1.100:8788", "my-server");

        Assert.Contains("http://192.168.1.100:8788", result.qrCodeData);
    }

    [Fact]
    public void GenerateQRCodeContent_QrCodeDataContainsHostName()
    {
        var result = _service.GenerateQRCodeContent("http://localhost:8788", "my-host");

        Assert.Contains("my-host", result.qrCodeData);
    }

    [Fact]
    public void GenerateQRCodeContent_WithDeviceId_UsesProvidedDeviceId()
    {
        var deviceId = "my-device-123";
        var result = _service.GenerateQRCodeContent("http://localhost:8788", "localhost", deviceId);

        Assert.Contains($"\"serverId\":\"{deviceId}\"", result.qrCodeData);
        Assert.Contains($"\"deviceId\":\"{deviceId}\"", result.qrCodeData);
    }

    [Fact]
    public void GenerateQRCodeContent_WithoutDeviceId_GeneratesNewServerId()
    {
        var result1 = _service.GenerateQRCodeContent("http://localhost:8788", "localhost");
        var result2 = _service.GenerateQRCodeContent("http://localhost:8788", "localhost");

        Assert.NotEqual(result1.qrCodeData, result2.qrCodeData);
    }

    [Fact]
    public void GenerateQRCodeContent_EmptyHostName_StillWorks()
    {
        var result = _service.GenerateQRCodeContent("http://localhost:8788", "");

        Assert.Equal("", result.hostName);
        Assert.Contains("\"hostName\":\"\"", result.qrCodeData);
    }
}