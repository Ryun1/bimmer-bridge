using System.Net;
using EdiabasAgent.Core;

namespace EdiabasAgent.Tests;

public class BindAddressSelectorTests
{
    private static AdapterInfo Adapter(
        string name, string address, int prefix, string? gateway, bool up = true, bool loopback = false)
    {
        return new AdapterInfo(
            name,
            up,
            loopback,
            [new Ipv4Assignment(IPAddress.Parse(address), prefix)],
            gateway is null ? [] : [IPAddress.Parse(gateway)]);
    }

    private static readonly AdapterInfo Utm = Adapter("Ethernet", "192.168.64.3", 24, "192.168.64.1");
    private static readonly AdapterInfo Loop = Adapter("Loopback Pseudo-Interface 1", "127.0.0.1", 8, null, loopback: true);

    // ---- literal ----

    [Fact]
    public void Literal_Present_Returned()
    {
        var r = BindAddressSelector.Select("192.168.64.3", [Loop, Utm]);
        Assert.True(r.Available, r.Reason);
        Assert.Equal(IPAddress.Parse("192.168.64.3"), r.Address);
        Assert.Equal(IPAddress.Parse("192.168.64.1"), r.Gateway);
        Assert.Equal("Ethernet", r.AdapterName);
    }

    [Fact]
    public void Literal_Absent_NotAvailable()
    {
        var r = BindAddressSelector.Select("192.168.64.9", [Loop, Utm]);
        Assert.False(r.Available);
        Assert.Null(r.Address);
        Assert.False(string.IsNullOrEmpty(r.Reason));
    }

    [Fact]
    public void Literal_OnDownAdapter_NotAvailable()
    {
        var down = Adapter("Ethernet", "192.168.64.3", 24, "192.168.64.1", up: false);
        var r = BindAddressSelector.Select("192.168.64.3", [down]);
        Assert.False(r.Available);
    }

    [Fact]
    public void Literal_NoGatewayInSubnet_AvailableWithNullGateway()
    {
        var a = Adapter("Ethernet", "192.168.64.3", 24, "10.0.0.1");
        var r = BindAddressSelector.Select("192.168.64.3", [a]);
        Assert.True(r.Available);
        Assert.Null(r.Gateway);
        Assert.NotNull(r.Warning);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("::")]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("255.255.255.255")]
    public void Literal_Wildcard_NeverReturned(string configured)
    {
        // Even if an adapter oddly reports the wildcard, the selector refuses it.
        var weird = Adapter("Weird", "0.0.0.0", 0, "0.0.0.0");
        var r = BindAddressSelector.Select(configured, [weird, Utm]);
        Assert.False(r.Available);
        Assert.Null(r.Address);
    }

    // ---- auto ----

    [Fact]
    public void Auto_OneCandidate()
    {
        var r = BindAddressSelector.Select("auto", [Loop, Utm]);
        Assert.True(r.Available, r.Reason);
        Assert.Equal(IPAddress.Parse("192.168.64.3"), r.Address);
        Assert.Equal(IPAddress.Parse("192.168.64.1"), r.Gateway);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void Auto_CaseInsensitive()
    {
        Assert.True(BindAddressSelector.Select("AUTO", [Utm]).Available);
    }

    [Fact]
    public void Auto_ZeroCandidates_NotAvailable()
    {
        var r = BindAddressSelector.Select("auto", [Loop]);
        Assert.False(r.Available);
        Assert.Null(r.Address);
        Assert.Null(r.Gateway);
        Assert.False(string.IsNullOrEmpty(r.Reason));
    }

    [Fact]
    public void Auto_NoAdapters_NotAvailable()
    {
        Assert.False(BindAddressSelector.Select("auto", []).Available);
    }

    [Fact]
    public void Auto_ManyCandidates_FirstByNameWithWarning()
    {
        var b = Adapter("Ethernet 2", "10.0.5.7", 24, "10.0.5.1");
        var a = Adapter("Ethernet", "192.168.64.3", 24, "192.168.64.1");
        var r1 = BindAddressSelector.Select("auto", [b, a]);
        var r2 = BindAddressSelector.Select("auto", [a, b]);
        Assert.True(r1.Available);
        Assert.Equal(IPAddress.Parse("192.168.64.3"), r1.Address);
        Assert.Equal(r1.Address, r2.Address);
        Assert.Equal(r1.Gateway, r2.Gateway);
        Assert.NotNull(r1.Warning);
        Assert.NotNull(r2.Warning);
    }

    [Fact]
    public void Auto_GatewayOutsideSubnet_NotCandidate()
    {
        var a = Adapter("Ethernet", "192.168.64.3", 24, "192.168.65.1");
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Fact]
    public void Auto_NoGateway_NotCandidate()
    {
        var a = Adapter("Ethernet", "192.168.64.3", 24, null);
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Fact]
    public void Auto_GatewayEqualsAddress_NotCandidate()
    {
        var a = Adapter("Ethernet", "192.168.64.3", 24, "192.168.64.3");
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Theory]
    [InlineData("8.8.4.4", 24, "8.8.4.1")]          // public
    [InlineData("100.64.0.5", 10, "100.64.0.1")]    // CGNAT, not RFC 1918
    [InlineData("172.32.0.5", 16, "172.32.0.1")]    // just outside 172.16/12
    [InlineData("169.254.10.20", 16, "169.254.0.1")] // link-local
    [InlineData("127.0.0.5", 8, "127.0.0.1")]       // loopback range on a non-loopback adapter
    [InlineData("0.0.0.0", 0, "0.0.0.0")]
    public void Auto_NonPrivate_NotCandidate(string address, int prefix, string gateway)
    {
        var a = Adapter("Ethernet", address, prefix, gateway);
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Fact]
    public void Auto_PrivateAddressButPublicGateway_NotCandidate()
    {
        // Prefix so wide that a public gateway is "in subnet".
        var a = Adapter("Ethernet", "10.0.0.5", 4, "8.8.8.8");
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Theory]
    [InlineData("10.1.2.3", 8, "10.0.0.1")]
    [InlineData("172.16.5.5", 12, "172.31.0.1")]
    [InlineData("192.168.1.10", 24, "192.168.1.1")]
    public void Auto_Rfc1918_Candidate(string address, int prefix, string gateway)
    {
        var r = BindAddressSelector.Select("auto", [Adapter("Ethernet", address, prefix, gateway)]);
        Assert.True(r.Available, r.Reason);
        Assert.Equal(IPAddress.Parse(address), r.Address);
    }

    [Fact]
    public void Auto_Loopback_NotCandidate()
    {
        // A loopback adapter with a private-looking address and gateway is still excluded.
        var a = Adapter("Loopback", "192.168.64.3", 24, "192.168.64.1", loopback: true);
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Fact]
    public void Auto_DownAdapter_NotCandidate()
    {
        var a = Adapter("Ethernet", "192.168.64.3", 24, "192.168.64.1", up: false);
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Fact]
    public void Auto_IPv6_Ignored()
    {
        var a = new AdapterInfo("Ethernet", true, false,
            [new Ipv4Assignment(IPAddress.Parse("fd00::5"), 64)],
            [IPAddress.Parse("fd00::1")]);
        Assert.False(BindAddressSelector.Select("auto", [a]).Available);
    }

    [Fact]
    public void Auto_AdapterWithSeveralAddresses_PicksTheOneMatchingTheGateway()
    {
        var a = new AdapterInfo("Ethernet", true, false,
            [new Ipv4Assignment(IPAddress.Parse("169.254.3.3"), 16), new Ipv4Assignment(IPAddress.Parse("192.168.64.3"), 24)],
            [IPAddress.Parse("192.168.64.1")]);
        var r = BindAddressSelector.Select("auto", [a]);
        Assert.True(r.Available);
        Assert.Equal(IPAddress.Parse("192.168.64.3"), r.Address);
    }

    [Fact]
    public void Auto_Property_NeverYieldsWildcardLoopbackLinkLocalOrPublic()
    {
        var rng = new Random(12345);
        string[] names = ["Ethernet", "Ethernet 2", "Wi-Fi", "Loopback", "vEthernet", ""];
        for (var i = 0; i < 20000; i++)
        {
            var count = rng.Next(0, 4);
            var adapters = new List<AdapterInfo>();
            for (var j = 0; j < count; j++)
            {
                var addrs = new List<Ipv4Assignment>();
                var addrCount = rng.Next(0, 3);
                for (var k = 0; k < addrCount; k++)
                {
                    addrs.Add(new Ipv4Assignment(RandomAddress(rng), rng.Next(0, 33)));
                }
                var gws = new List<IPAddress>();
                var gwCount = rng.Next(0, 3);
                for (var k = 0; k < gwCount; k++)
                {
                    // Bias gateways towards the adapter's own subnet so candidates actually occur.
                    if (addrs.Count > 0 && rng.Next(2) == 0)
                    {
                        var b = addrs[0].Address.GetAddressBytes();
                        b[3] = (byte)rng.Next(256);
                        gws.Add(new IPAddress(b));
                    }
                    else
                    {
                        gws.Add(RandomAddress(rng));
                    }
                }
                adapters.Add(new AdapterInfo(names[rng.Next(names.Length)], rng.Next(4) != 0, rng.Next(5) == 0, addrs, gws));
            }

            var r = BindAddressSelector.Select("auto", adapters);
            if (!r.Available)
            {
                Assert.Null(r.Address);
                continue;
            }

            var addr = r.Address!;
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, addr.AddressFamily);
            Assert.False(addr.Equals(IPAddress.Any));
            Assert.False(addr.Equals(IPAddress.Broadcast));
            Assert.False(IPAddress.IsLoopback(addr));
            var bytes = addr.GetAddressBytes();
            Assert.False(bytes[0] == 169 && bytes[1] == 254, "link-local");
            Assert.True(BindAddressSelector.IsRfc1918(addr), $"public address {addr}");
            Assert.NotNull(r.Gateway);
            Assert.True(BindAddressSelector.IsRfc1918(r.Gateway!));
        }
    }

    private static IPAddress RandomAddress(Random rng)
    {
        // Mix of interesting ranges and fully random addresses.
        return rng.Next(8) switch
        {
            0 => IPAddress.Parse("0.0.0.0"),
            1 => new IPAddress(new byte[] { 127, 0, 0, (byte)rng.Next(256) }),
            2 => new IPAddress(new byte[] { 169, 254, (byte)rng.Next(256), (byte)rng.Next(256) }),
            3 => new IPAddress(new byte[] { 192, 168, 64, (byte)rng.Next(256) }),
            4 => new IPAddress(new byte[] { 10, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256) }),
            5 => new IPAddress(new byte[] { 172, (byte)rng.Next(10, 40), (byte)rng.Next(256), (byte)rng.Next(256) }),
            6 => IPAddress.Broadcast,
            _ => new IPAddress(new byte[] { (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256) }),
        };
    }
}
