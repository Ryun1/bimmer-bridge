using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EdiabasAgent.Core;

namespace EdiabasAgent.Net;

/// <summary>Reads the machine's adapters into the Core's adapter model.</summary>
internal static class AdapterProbe
{
    public static IReadOnlyList<AdapterInfo> Read()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = ni.GetIPProperties();
                var addresses = props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => new Ipv4Assignment(u.Address, u.PrefixLength))
                    .ToList();
                var gateways = props.GatewayAddresses
                    .Select(g => g.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                    .ToList();

                list.Add(new AdapterInfo(
                    ni.Name,
                    ni.OperationalStatus == OperationalStatus.Up,
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                    addresses,
                    gateways));
            }
            catch (NetworkInformationException)
            {
                // An adapter that cannot be read is simply not a candidate.
            }
        }

        return list;
    }

    public static BindSelection Resolve(AgentConfig config) => BindAddressSelector.Select(config.BindAddress, Read());
}
