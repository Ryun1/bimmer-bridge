using System.Net;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class FirewallAddressTests
{
    private static readonly IPAddress Gw = IPAddress.Parse("192.168.64.1");

    [Theory]
    [InlineData("192.168.64.1")]
    [InlineData("192.168.64.1-192.168.64.1")]
    [InlineData("192.168.64.1/32")]
    [InlineData("192.168.64.1/255.255.255.255")]
    [InlineData("  192.168.64.1  ")]
    [InlineData("\t192.168.64.1-192.168.64.1\r\n")]
    [InlineData(" 192.168.64.1/32 ")]
    public void Accepted(string reported)
    {
        Assert.True(FirewallAddress.IsExactly(reported, Gw));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Any")]
    [InlineData("any")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LocalSubnet")]
    [InlineData("DefaultGateway")]
    [InlineData("DHCP")]
    [InlineData("DNS")]
    [InlineData("WINS")]
    [InlineData("Intranet")]
    [InlineData("192.168.64.1-192.168.64.2")]
    [InlineData("192.168.64.0-192.168.64.1")]
    [InlineData("192.168.64.0-192.168.64.255")]
    [InlineData("192.168.64.1/24")]
    [InlineData("192.168.64.1/31")]
    [InlineData("192.168.64.1/0")]
    [InlineData("192.168.64.1/255.255.255.0")]
    [InlineData("192.168.64.1/255.255.255.254")]
    [InlineData("192.168.64.1,10.0.0.1")]
    [InlineData("10.0.0.1,192.168.64.1")]
    [InlineData("192.168.64.1,192.168.64.1")]
    [InlineData("192.168.64.1/32,192.168.64.1/32")]
    [InlineData("192.168.64.2")]
    [InlineData("192.168.64.2/32")]
    [InlineData("192.168.64.2-192.168.64.2")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.64.1")]
    [InlineData("192.168.064.1")]
    [InlineData("192.168.64")]
    [InlineData("192.168.64.1.")]
    [InlineData("192.168.64.1-")]
    [InlineData("-192.168.64.1")]
    [InlineData("192.168.64.1--192.168.64.1")]
    [InlineData("192.168.64.1-192.168.64.1-192.168.64.1")]
    [InlineData("192.168.64.1/")]
    [InlineData("192.168.64.1/32/32")]
    [InlineData("192.168.64.1/ 32")]
    [InlineData("192.168.64.1 /32")]
    [InlineData("192.168.64.1 - 192.168.64.1")]
    [InlineData("192.168.64.1-192.168.64.1/32")]
    [InlineData("192.168.64.1/032")]
    [InlineData("192.168.64.1/+32")]
    [InlineData("0x7f000001")]
    [InlineData("garbage")]
    public void Rejected(string reported)
    {
        Assert.False(FirewallAddress.IsExactly(reported, Gw));
    }

    [Fact]
    public void Null_Rejected()
    {
        Assert.False(FirewallAddress.IsExactly(null, Gw));
    }

    [Fact]
    public void Ipv6Expected_Rejected()
    {
        Assert.False(FirewallAddress.IsExactly("::1", IPAddress.IPv6Loopback));
    }
}
