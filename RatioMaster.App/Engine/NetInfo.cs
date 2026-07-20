namespace RatioMaster.Engine;

using System.Net;
using System.Net.Sockets;

internal static class NetInfo
{
    /// <summary>First IPv4 address of this machine, computed at runtime (never hardcoded).</summary>
    internal static string GetLocalIp()
    {
        try
        {
            foreach (IPAddress address in Dns.GetHostEntry(string.Empty).AddressList)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
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
