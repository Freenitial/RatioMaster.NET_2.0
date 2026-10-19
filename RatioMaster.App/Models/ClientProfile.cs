namespace RatioMaster.Models;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using RatioMaster.Services;

/// <summary>Named percent-encoding policies; each maps to the punctuation a client leaves unescaped.</summary>
internal enum ClientUrlEncoding
{
    Libtorrent,
    Rfc3986,
    Alphanumeric,
}

/// <summary>How the emulated client fills its BEP 10 extension handshake.</summary>
internal enum ExtensionHandshakeStyle
{
    /// <summary>No extension protocol: the LTEP reserved bit must also be clear.</summary>
    None,

    /// <summary>libtorrent 1.2/2.0 (qBittorrent, Deluge): upload_only, holepunch, donthave, share_mode, complete_ago.</summary>
    Libtorrent,

    /// <summary>Transmission 4.x: encryption flag, advertised port, upload_only and a two-entry m dictionary.</summary>
    Transmission,

    /// <summary>µTorrent/BitTorrent: closed source; layout follows the published BEP 10 conventions of the family.</summary>
    UTorrent,
}

/// <summary>Inclusive port interval a client draws its listening port from on first run.</summary>
internal readonly record struct PortRange(int Minimum, int Maximum)
{
    internal int Count => Maximum - Minimum + 1;

    internal static bool TryParse(string text, out IReadOnlyList<PortRange> ranges)
    {
        List<PortRange> parsed = [];
        ranges = parsed;
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2
                || !int.TryParse(bounds[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minimum)
                || !int.TryParse(bounds[^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int maximum)
                || minimum is < 1 or > 65535 || maximum < minimum || maximum > 65535) return false;
            parsed.Add(new PortRange(minimum, maximum));
        }
        return parsed.Count is > 0 and <= 16;
    }

    internal static string Format(IReadOnlyList<PortRange> ranges) =>
        string.Join(",", ranges.Select(range => range.Minimum == range.Maximum ? range.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : range.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + range.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    internal static bool Contains(IReadOnlyList<PortRange> ranges, int port) => ranges.Any(range => port >= range.Minimum && port <= range.Maximum);

    /// <summary>Uniform over the union of the intervals, so a wide interval is not under-sampled.</summary>
    internal static int Pick(IReadOnlyList<PortRange> ranges)
    {
        int total = ranges.Sum(range => range.Count);
        int index = System.Security.Cryptography.RandomNumberGenerator.GetInt32(total);
        foreach (PortRange range in ranges)
        {
            if (index < range.Count) return range.Minimum + index;
            index -= range.Count;
        }
        return ranges[^1].Maximum;
    }
}

/// <summary>Peer-wire behaviour of one client construction: handshake flags and extension handshake.</summary>
internal sealed record PeerWireDefinition
{
    /// <summary>The eight reserved handshake bytes (BEP 3/5/6/10); immutable, as every profile shares them.</summary>
    internal ImmutableArray<byte> Reserved { get; init; } = ImmutableArray.Create(new byte[8]);

    /// <summary>libtorrent 2.0 raises byte 7 bit 0x10 on hybrid torrents to offer the v2 upgrade (BEP 52).</summary>
    internal bool HybridUpgradeBit { get; init; }

    internal ExtensionHandshakeStyle Style { get; init; } = ExtensionHandshakeStyle.None;

    /// <summary>The "v" value of the extension handshake; empty omits the key.</summary>
    internal string ClientName { get; init; } = string.Empty;

    /// <summary>The "reqq" value: requests the client accepts in flight.</summary>
    internal int RequestQueue { get; init; } = 250;

    /// <summary>Selects RC4 when an encrypted peer also offers plaintext (Transmission), or plaintext (libtorrent).</summary>
    internal bool PrefersRc4 { get; init; }

    /// <summary>Idle seconds before a keep-alive: libtorrent peer_timeout / 2 = 60, Transmission KeepaliveIntervalSecs = 100.</summary>
    internal int KeepAliveSeconds { get; init; } = 120;

    internal bool SupportsExtensions => (Reserved[5] & 0x10) != 0;
    internal bool SupportsFast => (Reserved[7] & 0x04) != 0;
    internal bool SupportsDht => (Reserved[7] & 0x01) != 0;
}

/// <summary>Announce formatting and one generated identity; PeerID contains raw Latin-1 bytes.</summary>
internal sealed class ClientProfile
{
    internal string Name { get; set; } = string.Empty;
    internal string Family { get; set; } = string.Empty;
    internal string Version { get; set; } = string.Empty;
    internal string HttpProtocol { get; set; } = "HTTP/1.1";
    internal bool HashUpperCase { get; set; }
    internal bool PeerIdUpperCase { get; set; }
    internal string PeerIdPrefix { get; set; } = string.Empty;
    internal string PeerIdLiteralPrefix { get; set; } = string.Empty;
    internal PeerIdTailKind PeerIdTail { get; set; } = PeerIdTailKind.Libtorrent;

    /// <summary>Punctuation this client leaves unescaped in info_hash, peer_id and key; letters and digits always are.</summary>
    internal string Unreserved { get; set; } = ClientEncoding.LibtorrentUnreserved;
    internal string Key { get; set; } = string.Empty;
    internal int KeyLength { get; set; } = 8;
    internal bool KeyUpperCase { get; set; } = true;
    internal TimeSpan KeyRefreshInterval { get; set; }

    /// <summary>Shortest regular announce interval the client accepts from a tracker (libtorrent: min_announce_interval).</summary>
    internal int IntervalFloorSeconds { get; set; }

    /// <summary>Minimum interval the client assumes when the tracker sends none; manual updates wait for it.</summary>
    internal int DefaultMinIntervalSeconds { get; set; }
    internal string Headers { get; set; } = string.Empty;
    internal string PeerID { get; set; } = string.Empty;
    internal string Query { get; set; } = string.Empty;
    internal int DefNumWant { get; set; } = 200;
    internal bool SupportsV2 { get; set; }
    internal GlobalAddressMode GlobalAddresses { get; set; }
    internal IReadOnlyList<PortRange> PortRanges { get; set; } = ClientEncoding.DefaultPorts;
    internal PeerWireDefinition PeerWire { get; set; } = new();

    internal ClientProfile Snapshot() => (ClientProfile)MemberwiseClone();

    internal string EncodePeerId(string raw)
    {
        RandomStringGenerator encoder = new();
        if (PeerIdLiteralPrefix.Length > 0 && raw.StartsWith(PeerIdLiteralPrefix, StringComparison.Ordinal))
            return PeerIdLiteralPrefix + encoder.UrlEncode(raw[PeerIdLiteralPrefix.Length..], PeerIdUpperCase, Unreserved);
        return encoder.UrlEncode(raw, PeerIdUpperCase, Unreserved);
    }

    /// <summary>
    /// Explains why a peer ID could not have been produced by this construction, or returns null.
    /// Used to warn about custom identities that a tracker comparing peer ID and User-Agent would find inconsistent.
    /// </summary>
    internal string? DescribeIdentityMismatch(string peerId)
    {
        if (peerId.Length != 20) return "length";
        if (!peerId.StartsWith(PeerIdPrefix, StringComparison.Ordinal)) return "prefix";
        return PeerIdTail switch
        {
            PeerIdTailKind.UTorrent when !ClientIdentity.HasUTorrentChecksum(peerId) => "checksum",
            PeerIdTailKind.Transmission when !ClientIdentity.HasTransmissionChecksum(peerId) => "checksum",
            _ => null,
        };
    }
}

/// <summary>Encoding vocabulary shared by built-in and user-defined profiles.</summary>
internal static class ClientEncoding
{
    internal const string LibtorrentUnreserved = "-_.!~*()";
    internal const string Rfc3986Unreserved = "-_.~";

    /// <summary>Punctuation any profile may leave unescaped; everything else is always percent-encoded.</summary>
    internal const string AllowedUnreserved = "-_.!~*()'";

    internal static readonly IReadOnlyList<PortRange> DefaultPorts = [new PortRange(1024, 65535)];

    internal static string UnreservedFor(ClientUrlEncoding policy) => policy switch
    {
        ClientUrlEncoding.Libtorrent => LibtorrentUnreserved,
        ClientUrlEncoding.Rfc3986 => Rfc3986Unreserved,
        _ => string.Empty,
    };
}
