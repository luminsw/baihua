using BaihuaSdk.Push;
using BaihuaSdk.Services;
using MobileContract.Services;
using Xunit;

namespace BaihuaSdk.Tests.Services;

public class ContractAlignmentTests
{
    [Fact]
    public void PairingServiceImpl_Implements_IPairingService()
    {
        Assert.True(typeof(IPairingService).IsAssignableFrom(typeof(PairingServiceImpl)));
    }

    [Fact]
    public void PairingServiceImpl_Implements_IDeviceRegistrationService()
    {
        Assert.True(typeof(IDeviceRegistrationService).IsAssignableFrom(typeof(PairingServiceImpl)));
    }

    [Fact]
    public void SyncServiceImpl_Implements_ISyncService()
    {
        Assert.True(typeof(ISyncService).IsAssignableFrom(typeof(SyncServiceImpl)));
    }

    [Fact]
    public void PushPollingServiceImpl_Implements_IPushPollingService()
    {
        Assert.True(typeof(IPushPollingService).IsAssignableFrom(typeof(PushPollingServiceImpl)));
    }
}
