namespace RatioMaster.Engine;

using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

internal static class NetInfo
{
    /// <summary>First IPv4 address of this machine, computed at runtime (never hardcoded).</summary>
    internal static string GetLocalIp()
    {
        try
        {
            foreach (IPAddress address in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                {
                    return address.ToString();
                }
            }
        }
        catch
        {
            // fall through
        }

        return "127.0.0.1";
    }

    /// <summary>
    /// Every public address of the active interfaces, IPv4 first: what libtorrent lists for private torrents
    /// from its per-interface listen sockets (loopback, private, link-local and unique-local are left out).
    /// </summary>
    internal static IReadOnlyList<IPAddress> GetGlobalInterfaceAddresses()
    {
        List<IPAddress> result = [];
        try
        {
            foreach (UnicastIPAddressInformation unicast in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses))
            {
                if (IsGlobal(unicast.Address) && !result.Contains(unicast.Address)) result.Add(unicast.Address);
            }
        }
        catch (NetworkInformationException) { }
        catch (PlatformNotSupportedException) { }
        return result.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
    }

    /// <summary>The source address the system would use for public traffic, when it is itself public (Transmission 4.1).</summary>
    internal static IReadOnlyList<IPAddress> GetGlobalSourceAddresses()
    {
        List<IPAddress> result = [];
        foreach ((AddressFamily family, IPAddress probe) in new[]
        {
            (AddressFamily.InterNetwork, IPAddress.Parse("198.41.0.4")),
            (AddressFamily.InterNetworkV6, IPAddress.Parse("2001:503:ba3e::2:30")),
        })
        {
            try
            {
                // Connecting a UDP socket only selects a route; no packet is sent.
                using Socket socket = new(family, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(probe, 53));
                if (socket.LocalEndPoint is IPEndPoint local && IsGlobal(local.Address)) result.Add(local.Address);
            }
            catch (SocketException) { }
        }
        return result;
    }

    /// <summary>
    /// Globally reachable unicast, after the IANA special-purpose registries: private, shared, link-local,
    /// documentation, benchmarking (also the fake-IP range of TUN-mode VPNs), 6to4 relay and reserved blocks are not.
    /// </summary>
    internal static bool IsGlobal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(bytes[0] is 0 or 10 or 127 || bytes[0] >= 224
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2 || bytes[1] == 88 && bytes[2] == 99)
                || bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)
                || bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xE0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] == 0x0d && bytes[3] == 0xb8 || bytes[2] == 0 && bytes[3] == 0x02 && bytes[4] == 0 && bytes[5] == 0))
            && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast && !address.IsIPv6Teredo;
    }
}
