namespace RatioMaster.Engine;

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RatioMaster.BitTorrent;

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

internal static class PeerList
{
    private const int MaxPeersToShow = 5;

    internal static string FormatCompact(byte[] bytes, int addressBytes)
    {
        if (addressBytes is not (4 or 16)) throw new ArgumentOutOfRangeException(nameof(addressBytes));
        int stride = addressBytes + 2;
        int count = bytes.Length / stride;
        StringBuilder result = Prefix(count);
        for (int index = 0; index < Math.Min(count, MaxPeersToShow); index++)
        {
            int offset = index * stride;
            Peer peer = new(bytes.AsSpan(offset, addressBytes).ToArray(),
                BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + addressBytes, 2)));
            result.Append(peer).Append(';');
        }
        return result.ToString();
    }

    internal static string FormatDictionary(ValueList peers)
    {
        int count = peers.values.Count(value => value is ValueDictionary);
        StringBuilder result = Prefix(count);
        foreach (ValueDictionary peer in peers.values.OfType<ValueDictionary>().Take(MaxPeersToShow))
        {
            result.Append(new Peer(BEncode.String(peer["ip"]) ?? string.Empty,
                BEncode.String(peer["port"]) ?? "0", BEncode.String(peer["peer id"]) ?? string.Empty)).Append(';');
        }
        return result.ToString();
    }

    private static StringBuilder Prefix(int count) => new("(" + count.ToString(CultureInfo.InvariantCulture) + ") ");
}
