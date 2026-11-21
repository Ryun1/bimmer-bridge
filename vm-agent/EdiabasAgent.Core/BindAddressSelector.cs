using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EdiabasAgent.Core;

/// <summary>An IPv4 address assigned to an adapter, with its prefix length.</summary>
public sealed record Ipv4Assignment(IPAddress Address, int PrefixLength);

/// <summary>A network adapter as the selector sees it. Filled by the agent's AdapterProbe.</summary>
public sealed record AdapterInfo(
    string Name,
    bool IsUp,
    bool IsLoopback,
    IReadOnlyList<Ipv4Assignment> Addresses,
    IReadOnlyList<IPAddress> Gateways);

/// <summary>The outcome of choosing a bind address.</summary>
public sealed record BindSelection(
    bool Available,
    IPAddress? Address,
    IPAddress? Gateway,
    string? AdapterName,
    string? Warning,
    string Reason)
{
    public static BindSelection NotAvailable(string reason) => new(false, null, null, null, null, reason);

    /// <summary>True when both selections would bind the same way (same address and gateway).</summary>
    public bool SameBindingAs(BindSelection? other) =>
        other is not null
        && Available == other.Available
        && Equals(Address, other.Address)
        && Equals(Gateway, other.Gateway);
}

/// <summary>
/// Pure choice of the single IPv4 address the agent binds to. It never returns a wildcard;
/// in <c>auto</c> mode it never returns a loopback, link-local or public address either.
/// When nothing suitable exists the answer is "not available yet" - there is no fallback.
/// </summary>
public static partial class BindAddressSelector
{
    [GeneratedRegex(@"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DottedQuad();

    /// <summary>
    /// Strictly parses a literal IPv4 unicast address in dotted-quad form (no leading zeros,
    /// no shorthand like <c>127.1</c>, no hex). Rejects 0.0.0.0/8, multicast, reserved and broadcast.
    /// </summary>
    public static bool TryParseLiteral(string? text, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrEmpty(text) || text.Length > 15 || !DottedQuad().IsMatch(text))
        {
            return false;
        }

        var parsed = IPAddress.Parse(text);
        var b = parsed.GetAddressBytes();
        if (b[0] == 0 || b[0] >= 224)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>RFC 1918 private IPv4: 10/8, 172.16/12, 192.168/16.</summary>
    public static bool IsRfc1918(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>True when <paramref name="other"/> is inside <paramref name="address"/>/<paramref name="prefixLength"/>.</summary>
    public static bool InSameSubnet(IPAddress address, int prefixLength, IPAddress other)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork
            || other.AddressFamily != AddressFamily.InterNetwork
            || prefixLength is < 0 or > 32)
        {
            return false;
        }

        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (ToUInt(address) & mask) == (ToUInt(other) & mask);
    }

    public static BindSelection Select(string? configured, IReadOnlyList<AdapterInfo> adapters)
    {
        if (string.Equals(configured, AgentConfig.AutoBind, StringComparison.OrdinalIgnoreCase))
        {
            return SelectAuto(adapters);
        }

        if (!TryParseLiteral(configured, out var literal))
        {
            return BindSelection.NotAvailable("configured bindAddress is not a valid literal IPv4 address");
        }

        foreach (var adapter in adapters.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            if (!adapter.IsUp)
            {
                continue;
            }

            var match = adapter.Addresses.FirstOrDefault(a => a.Address.Equals(literal));
            if (match is null)
            {
                continue;
            }

            var gateway = adapter.Gateways
                .Where(g => g.AddressFamily == AddressFamily.InterNetwork
                            && !g.Equals(literal)
                            && InSameSubnet(match.Address, match.PrefixLength, g))
                .OrderBy(ToUInt)
                .FirstOrDefault();

            return new BindSelection(
                true,
                literal,
                gateway,
                adapter.Name,
                gateway is null
                    ? $"adapter '{adapter.Name}' has no IPv4 gateway in {literal}/{match.PrefixLength}; every request will be rejected by the remote-address check"
                    : null,
                $"literal {literal} found on up adapter '{adapter.Name}'");
        }

        return BindSelection.NotAvailable($"no up adapter currently has {literal}");
    }

    private static BindSelection SelectAuto(IReadOnlyList<AdapterInfo> adapters)
    {
        var candidates = new List<(string Name, IPAddress Address, IPAddress Gateway)>();

        foreach (var adapter in adapters)
        {
            if (!adapter.IsUp || adapter.IsLoopback)
            {
                continue;
            }

            foreach (var assignment in adapter.Addresses)
            {
                var addr = assignment.Address;
                if (addr.AddressFamily != AddressFamily.InterNetwork || !IsRfc1918(addr) || IPAddress.IsLoopback(addr))
                {
                    continue;
                }

                foreach (var gw in adapter.Gateways)
                {
                    if (gw.AddressFamily == AddressFamily.InterNetwork
                        && IsRfc1918(gw)
                        && !gw.Equals(addr)
                        && InSameSubnet(addr, assignment.PrefixLength, gw))
                    {
                        candidates.Add((adapter.Name ?? "", addr, gw));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            return BindSelection.NotAvailable(
                "no up, non-loopback adapter has a private IPv4 address with an IPv4 gateway in its subnet");
        }

        var ordered = candidates
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => ToUInt(c.Address))
            .ThenBy(c => ToUInt(c.Gateway))
            .ToList();
        var chosen = ordered[0];

        string? warning = null;
        if (ordered.Count > 1)
        {
            var all = string.Join(", ", ordered.Select(c => $"'{c.Name}' {c.Address} via {c.Gateway}"));
            warning = $"{ordered.Count} candidate addresses ({all}); chose the first by adapter name. Set bindAddress to a literal address to remove the ambiguity.";
        }

        return new BindSelection(true, chosen.Address, chosen.Gateway, chosen.Name, warning,
            $"auto: '{chosen.Name}' {chosen.Address} via gateway {chosen.Gateway}");
    }

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length != 4 ? 0u : ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
