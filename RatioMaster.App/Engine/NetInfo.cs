namespace RatioMaster.Engine;

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
}
