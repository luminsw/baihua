using System.Net;
using Baihua.Core.Security;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 管理 API 网络访问策略测试：默认仅 loopback，CIDR 显式放行，非法项忽略。
/// </summary>
public class AdminNetworkPolicyTests
{
    // ============ ParseNets ============

    [Fact]
    public void ParseNets_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Empty(AdminNetworkPolicy.ParseNets(null));
        Assert.Empty(AdminNetworkPolicy.ParseNets(""));
        Assert.Empty(AdminNetworkPolicy.ParseNets("   "));
    }

    [Fact]
    public void ParseNets_ValidCidrs_AreParsed()
    {
        var nets = AdminNetworkPolicy.ParseNets("172.16.0.0/12, 192.168.1.0/24");

        Assert.Equal(2, nets.Count);
        Assert.True(nets[0].Contains(IPAddress.Parse("172.17.0.5")));
        Assert.True(nets[1].Contains(IPAddress.Parse("192.168.1.42")));
        Assert.False(nets[1].Contains(IPAddress.Parse("192.168.2.1")));
    }

    [Fact]
    public void ParseNets_InvalidEntries_AreIgnored()
    {
        var nets = AdminNetworkPolicy.ParseNets("not-a-cidr, 10.0.0.0/8");

        Assert.Single(nets);
        Assert.True(nets[0].Contains(IPAddress.Parse("10.1.2.3")));
    }

    // ============ IsAllowed ============

    [Fact]
    public void IsAllowed_NullIp_ReturnsFalse()
    {
        Assert.False(AdminNetworkPolicy.IsAllowed(null, new List<System.Net.IPNetwork>()));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IsAllowed_Loopback_ReturnsTrueEvenWithoutNets(string ip)
    {
        Assert.True(AdminNetworkPolicy.IsAllowed(IPAddress.Parse(ip), new List<System.Net.IPNetwork>()));
    }

    [Fact]
    public void IsAllowed_LanIp_WithoutNets_ReturnsFalse()
    {
        // 收紧后的核心契约：局域网 IP 不再隐式放行
        Assert.False(AdminNetworkPolicy.IsAllowed(IPAddress.Parse("192.168.1.50"), new List<System.Net.IPNetwork>()));
    }

    [Fact]
    public void IsAllowed_LanIp_WithinConfiguredNet_ReturnsTrue()
    {
        var nets = AdminNetworkPolicy.ParseNets("192.168.1.0/24");
        Assert.True(AdminNetworkPolicy.IsAllowed(IPAddress.Parse("192.168.1.50"), nets));
        Assert.False(AdminNetworkPolicy.IsAllowed(IPAddress.Parse("192.168.2.50"), nets));
    }
}
