namespace RatioMaster.Models;

using System.Collections.Generic;
using RatioMaster.Services;

internal sealed record ClientFamily(string Name, IReadOnlyList<string> Versions);

/// <summary>
/// Emulation profiles for BitTorrent clients. Every profile is pure emulation — RatioMaster builds the
/// announce itself, so no real client ever needs to be installed or running.
///
/// <para>Two rules govern what belongs here, both learned the hard way from the legacy table this replaced:
/// a profile must be <b>byte-accurate</b> (peer_id prefix, tail alphabet, key case, User-Agent and query
/// order all matching what the real client emits), and it must be <b>plausible</b> — a client version that
/// has been dead for fifteen years is a red flag to a tracker, which is the exact opposite of what an
/// emulation profile is for. Families that could not satisfy both were removed rather than kept as
/// decoration: BitTorrent 6.0.3 (its peer_id was Mainline-style while its User-Agent was µTorrent-style — a
/// combination no real client ever emitted), Vuze/Azureus 4.2.0.8 (abandoned in 2017, and its User-Agent
/// advertised Windows XP + Java 1.6), KTorrent 2.2.1 (a 2007 build), and BitComet (faithfully emulated, but
/// BitComet is banned outright by most private trackers, so the profile caused the very rejection it was
/// meant to avoid).</para>
///
/// <para>All tails are printable ASCII. That is deliberate: a binary tail has to be percent-encoded for the
/// announce, and the encoded form used to leak into the peer-wire handshake, so the peer_id we announced and
/// the one we presented on the wire disagreed. Printable tails make the two identical by construction.</para>
/// </summary>
internal static class ClientCatalog
{
    private static readonly RandomStringGenerator Gen = new();

    /// <summary>Client families in UI order. The first family/version is the default.</summary>
    internal static readonly IReadOnlyList<ClientFamily> Families =
    [
        new("qBittorrent", ["5.1.0", "5.0.3", "4.6.7", "4.6.5"]),
        new("uTorrent", ["3.6.0", "3.5.5"]),
        new("Transmission", ["4.0.6"]),
        new("Deluge", ["2.1.1"]),
    ];

    internal const string DefaultFamily = "qBittorrent";
    internal const string DefaultVersion = "5.1.0";

    internal static ClientProfile Create(string family, string version) => (family + " " + version) switch
    {
        // ── qBittorrent (libtorrent) — the default; prefixes follow lt::generate_fingerprint("qB", …) ──
        "qBittorrent 5.1.0" => QBittorrent("5.1.0", "-qB5100-"),
        "qBittorrent 5.0.3" => QBittorrent("5.0.3", "-qB5030-"),
        "qBittorrent 4.6.7" => QBittorrent("4.6.7", "-qB4670-"),
        "qBittorrent 4.6.5" => QBittorrent("4.6.5", "-qB4650-"),

        // ── µTorrent ──
        "uTorrent 3.6.0" => UTorrent("3.6.0", "3600", "-UT3600-"),
        "uTorrent 3.5.5" => UTorrent("3.5.5", "3550", "-UT3550-"),

        // ── Transmission ──
        "Transmission 4.0.6" => Transmission("4.0.6", "-TR4060-"),

        // ── Deluge (libtorrent under the hood, like qBittorrent) ──
        "Deluge 2.1.1" => Deluge("2.1.1", "-DE211s-"),

        _ => QBittorrent(DefaultVersion, "-qB5100-"),
    };

    private static ClientProfile QBittorrent(string version, string peerPrefix) => new()
    {
        Name = "qBittorrent " + version,
        HttpProtocol = "HTTP/1.1",
        HashUpperCase = false,
        // libtorrent formats the key with "&key=%08X" — 8 digits, zero-padded, UPPERCASE. This used to be
        // generated lowercase, which is a one-glance giveaway for any tracker that logs the raw query.
        Key = Id("hex", 8, upper: true),
        Headers = "Host: {host}\r\nUser-Agent: qBittorrent/" + version + "\r\nAccept-Encoding: gzip\r\n",
        PeerID = peerPrefix + Id("alphanumeric", 12),
        Query = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&corrupt=0&key={key}{event}&numwant={numwant}&compact=1&no_peer_id=1&supportcrypto=1&redundant=0",
        DefNumWant = 200,
    };

    private static ClientProfile UTorrent(string version, string ua, string peerPrefix) => new()
    {
        Name = "uTorrent " + version,
        HttpProtocol = "HTTP/1.1",
        HashUpperCase = false,
        Key = Id("hex", 8, upper: true),
        Headers = "Host: {host}\r\nUser-Agent: uTorrent/" + ua + "\r\nAccept-Encoding: gzip\r\n",
        // The whole 12-char tail is random. The old profiles hardcoded the last two characters per version,
        // which made them identical across every announce AND every user — a stable fingerprint to cluster on.
        PeerID = peerPrefix + Id("alphanumeric", 12),
        Query = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&corrupt=0&key={key}{event}&numwant={numwant}&compact=1&no_peer_id=1",
        DefNumWant = 200,
    };

    private static ClientProfile Transmission(string version, string peerPrefix) => new()
    {
        Name = "Transmission " + version,
        HttpProtocol = "HTTP/1.1",
        HashUpperCase = false,
        // libtransmission emits the key with "%x" — lowercase (unlike libtorrent's %08X).
        Key = Id("hex", 8, lower: true),
        // Only gzip is advertised: TrackerResponse can decode gzip/x-gzip but NOT deflate, and a tracker
        // taking us up on a deflate offer would return a body we cannot read, failing every announce.
        Headers = "User-Agent: Transmission/" + version + "\r\nHost: {host}\r\nAccept: */*\r\nAccept-Encoding: gzip\r\n",
        PeerID = peerPrefix + TransmissionTail(),
        Query = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&numwant={numwant}&key={key}&compact=1&supportcrypto=1{event}",
        DefNumWant = 80,
    };

    private static ClientProfile Deluge(string version, string peerPrefix) => new()
    {
        Name = "Deluge " + version,
        HttpProtocol = "HTTP/1.1",
        HashUpperCase = false,
        Key = Id("hex", 8, upper: true),
        Headers = "Host: {host}\r\nUser-Agent: Deluge/" + version + " libtorrent/2.0.7.0\r\nAccept-Encoding: gzip\r\n",
        PeerID = peerPrefix + Id("alphanumeric", 12),
        // Deluge drives libtorrent, so it sends libtorrent's announce — the same one qBittorrent sends.
        Query = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&corrupt=0&key={key}{event}&numwant={numwant}&compact=1&no_peer_id=1&supportcrypto=1&redundant=0",
        DefNumWant = 200,
    };

    /// <summary>
    /// Transmission's peer_id tail is not plain random: <c>tr_peerIdInit</c> draws 11 characters from a
    /// base-36 lowercase pool and then picks a 12th so that the sum of all twelve values is a multiple of 36.
    /// A tracker can verify that checksum, so a tail drawn from the usual mixed-case 62-character pool is
    /// trivially detectable as forged.
    /// </summary>
    private static string TransmissionTail()
    {
        const string Pool = "0123456789abcdefghijklmnopqrstuvwxyz";
        string head = Gen.Generate(11, Pool.ToCharArray());
        int total = 0;
        foreach (char c in head)
        {
            total += Pool.IndexOf(c);
        }

        return head + Pool[(36 - (total % 36)) % 36];
    }

    private static string Id(string kind, int length, bool upper = false, bool lower = false)
    {
        string text = kind switch
        {
            "numeric" => Gen.Generate(length, "0123456789".ToCharArray()),
            "hex" => Gen.Generate(length, "0123456789ABCDEF".ToCharArray()),
            _ => Gen.Generate(length),
        };

        if (upper)
        {
            return text.ToUpperInvariant();
        }

        if (lower)
        {
            return text.ToLowerInvariant();
        }

        return text;
    }
}
