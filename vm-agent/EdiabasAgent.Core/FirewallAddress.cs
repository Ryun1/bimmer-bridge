using System.Net;
using System.Net.Sockets;

namespace EdiabasAgent.Core;

/// <summary>
/// Interprets the <c>RemoteAddresses</c> value the Windows Firewall reports for a rule.
/// </summary>
public static class FirewallAddress
{
    /// <summary>
    /// True only when <paramref name="reported"/> denotes exactly the one IPv4 address
    /// <paramref name="expected"/>. Accepted forms (surrounding whitespace ignored):
    /// <c>A</c>, <c>A-A</c>, <c>A/32</c>, <c>A/255.255.255.255</c>. Everything else - keywords such
    /// as <c>*</c>/<c>Any</c>/<c>LocalSubnet</c>, real ranges, wider prefixes or masks, lists,
    /// IPv6, malformed text - is false.
    /// </summary>
    public static bool IsExactly(string? reported, IPAddress expected)
    {
        if (reported is null || expected.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = reported.Trim();
        if (value.Length == 0 || value.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var dash = value.Split('-');
        var slash = value.Split('/');
        if (dash.Length == 2 && slash.Length == 1)
        {
            return IsAddress(dash[0], expected) && IsAddress(dash[1], expected);
        }

        if (slash.Length == 2 && dash.Length == 1)
        {
            return IsAddress(slash[0], expected) && slash[1] is "32" or "255.255.255.255";
        }

        return dash.Length == 1 && slash.Length == 1 && IsAddress(value, expected);
    }

    private static bool IsAddress(string text, IPAddress expected) =>
        BindAddressSelector.TryParseLiteral(text, out var ip) && ip!.Equals(expected);
}
