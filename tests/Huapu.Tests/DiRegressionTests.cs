using BaihuaSdk.Push;
using BaihuaSdk.Services;
using BaihuaSdk.Signing;
using BaihuaSdk.Storage;
using Moq;

namespace MobileApp.Maui.Tests;

public class DiRegressionTests
{
    // ---- PairingServiceImpl ----

    [Fact]
    public void PairingServiceImpl_CanBeConstructed_WithCorrectFactoryPattern()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        var service = new PairingServiceImpl(client, signer, "device-1", "test-device");

        Assert.NotNull(service);
    }

    [Fact]
    public void PairingServiceImpl_Ctor_RequiresAllStringParams()
    {
        var ctor = typeof(PairingServiceImpl).GetConstructors().Single();
        var stringParams = ctor.GetParameters().Where(p => p.ParameterType == typeof(string));

        Assert.Equal(3, stringParams.Count());
        Assert.Contains(stringParams, p => p.Name == "deviceId");
        Assert.Contains(stringParams, p => p.Name == "deviceName");
        Assert.Contains(stringParams, p => p.Name == "systemDeviceName");
    }

    // ---- SyncServiceImpl ----

    [Fact]
    public void SyncServiceImpl_CanBeConstructed_WithPlainDi()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        var service = new SyncServiceImpl(client, signer);

        Assert.NotNull(service);
    }

    [Fact]
    public void SyncServiceImpl_Ctor_HasNoStringParams()
    {
        var ctor = typeof(SyncServiceImpl).GetConstructors().Single();
        var stringParams = ctor.GetParameters().Where(p => p.ParameterType == typeof(string));

        Assert.Empty(stringParams);
    }

    // ---- PushWebSocketService ----

    [Fact]
    public void PushWebSocketService_CanBeConstructed_WithHttpClient()
    {
        var client = new HttpClient();
        
        using var service = new PushWebSocketService(client);
        
        Assert.NotNull(service);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public void PushWebSocketService_Ctor_HasNoStringParams()
    {
        var ctor = typeof(PushWebSocketService).GetConstructors().Single();
        var stringParams = ctor.GetParameters().Where(p => p.ParameterType == typeof(string));
        
        Assert.Empty(stringParams);
    }

    // ---- 综合：验证所有 @inject 的类型都可以构造 ----

    [Fact]
    public void AllInjectedServiceTypes_CanBeConstructed()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        var services = new object[]
        {
            new PairingServiceImpl(client, signer, "test-device", "test-name"),
            new SyncServiceImpl(client, signer),
            new PushWebSocketService(client),
        };

        foreach (var svc in services)
            Assert.NotNull(svc);
    }
}
