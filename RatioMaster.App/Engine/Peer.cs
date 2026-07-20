namespace RatioMaster.Engine;

using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

internal sealed class Peer
{
    internal IPAddress? IpAddress { get; }

    internal string PeerID { get; } = string.Empty;

    internal ushort Port { get; }

    internal Peer(byte[] ip, short port)
    {
        IpAddress = new IPAddress(ip);
        Port = (ushort)IPAddress.NetworkToHostOrder(port);
    }

    /// <summary>Compact peer whose big-endian port the caller has ALREADY decoded (BEP 7 IPv6 lists) —
    /// so, unlike the short overload, this must not byte-swap again. A 16-byte <paramref name="ip"/>
    /// yields an IPv6 address, a 4-byte one an IPv4 address.</summary>
    internal Peer(byte[] ip, ushort port)
    {
        IpAddress = new IPAddress(ip);
        Port = port;
    }

    internal Peer(string ip, string port, string peerId)
    {
        // The dictionary-model peers list gives the port as a plain host-order decimal integer
        // (e.g. "51413"). Parse it as ushort (short would overflow above 32767) and DON'T byte-swap:
        // NetworkToHostOrder is only correct for the compact ctor above, which reads raw big-endian bytes.
        if (IPAddress.TryParse(ip, out IPAddress? addr) && ushort.TryParse(port, out ushort p))
        {
            IpAddress = addr;
            Port = p;
            PeerID = peerId;
        }
    }

    // IPv6 literals are bracketed ([::1]:6881) so the address' own colons can't be mistaken for the
    // port separator — the conventional way peers are written.
    private string Address => IpAddress?.AddressFamily == AddressFamily.InterNetworkV6
        ? $"[{IpAddress}]:{Port}"
        : $"{IpAddress}:{Port}";

    public override string ToString() =>
        PeerID.Length > 0 ? $"{Address}(PeerID={PeerID})" : Address;
}

internal sealed class PeerList : List<Peer>
{
    private const int MaxPeersToShow = 5;

    public override string ToString()
    {
        string result = $"({Count}) ";
        int counter = 0;
        foreach (Peer peer in this)
        {
            if (counter < MaxPeersToShow)
            {
                result += peer + ";";
            }

            counter++;
        }

        return result;
    }
}
