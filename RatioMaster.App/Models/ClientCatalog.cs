namespace RatioMaster.Models;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using RatioMaster.Services;

internal sealed record ClientFamily(string Name, IReadOnlyList<string> Versions);

internal enum PeerIdTailKind
{
    Libtorrent,
    Binary,
    Transmission,

    /// <summary>Random bytes ending with the two µTorrent check bytes.</summary>
    UTorrent,
}

/// <summary>When the announce carries the machine's own public addresses as ipv4=/ipv6= parameters.</summary>
internal enum GlobalAddressMode
{
    None,

    /// <summary>libtorrent: private torrents only, every non-local address the client listens on.</summary>
    PrivateTorrents,

    /// <summary>Transmission 4.1: every announce, the source address the system routes public traffic from.</summary>
    Always,
}

/// <summary>Declarative client rules; identities are generated independently for each requested profile.</summary>
internal sealed record ClientProfileDefinition
{
    internal required string Family { get; init; }
    internal required string Version { get; init; }
    internal string Name => Family + " " + Version;
    internal string HttpProtocol { get; init; } = "HTTP/1.1";
    internal required string Headers { get; init; }
    internal required string Query { get; init; }
    internal required string PeerIdPrefix { get; init; }
    internal string PeerIdLiteralPrefix { get; init; } = string.Empty;
    internal PeerIdTailKind PeerIdTail { get; init; } = PeerIdTailKind.Libtorrent;
    internal int PeerIdByteMinimum { get; init; }
    internal int PeerIdByteMaximum { get; init; } = 255;
    internal string Unreserved { get; init; } = ClientEncoding.LibtorrentUnreserved;
    internal bool HashUpperCase { get; init; }
    internal bool PeerIdUpperCase { get; init; }
    internal int KeyLength { get; init; } = 8;
    internal bool KeyUpperCase { get; init; } = true;
    internal int KeyRefreshSeconds { get; init; }
    internal int IntervalFloorSeconds { get; init; }
    internal int DefaultMinIntervalSeconds { get; init; }
    internal int NumWant { get; init; } = 200;
    internal bool SupportsV2 { get; init; }
    internal GlobalAddressMode GlobalAddresses { get; init; }
    internal IReadOnlyList<PortRange> PortRanges { get; init; } = ClientEncoding.DefaultPorts;
    internal PeerWireDefinition PeerWire { get; init; } = new();

    internal ClientProfile Create()
    {
        RandomStringGenerator generator = new();
        int length = 20 - PeerIdPrefix.Length;
        string suffix = PeerIdTail switch
        {
            PeerIdTailKind.Libtorrent => generator.Generate(length, RandomStringGenerator.LibtorrentAlphabet.AsSpan()),
            PeerIdTailKind.Binary => generator.GenerateBytes(length, PeerIdByteMinimum, PeerIdByteMaximum + 1),
            PeerIdTailKind.Transmission => generator.GenerateTransmissionTail(),
            PeerIdTailKind.UTorrent => generator.GenerateUTorrentTail(PeerIdPrefix, PeerIdByteMinimum, PeerIdByteMaximum + 1),
            _ => throw new InvalidOperationException("Unknown peer ID generator."),
        };
        ClientProfile profile = new()
        {
            Name = Name, Family = Family, Version = Version, HttpProtocol = HttpProtocol,
            Headers = Headers, Query = Query, PeerID = PeerIdPrefix + suffix, PeerIdPrefix = PeerIdPrefix,
            PeerIdLiteralPrefix = PeerIdLiteralPrefix, PeerIdTail = PeerIdTail, Unreserved = Unreserved,
            HashUpperCase = HashUpperCase, PeerIdUpperCase = PeerIdUpperCase,
            KeyLength = KeyLength, KeyUpperCase = KeyUpperCase,
            KeyRefreshInterval = TimeSpan.FromSeconds(KeyRefreshSeconds), DefNumWant = NumWant,
            IntervalFloorSeconds = IntervalFloorSeconds, DefaultMinIntervalSeconds = DefaultMinIntervalSeconds,
            SupportsV2 = SupportsV2, GlobalAddresses = GlobalAddresses, PortRanges = PortRanges, PeerWire = PeerWire,
        };
        profile.Key = ClientCatalog.GenerateKey(profile);
        return profile;
    }
}

internal sealed class ClientCatalogSnapshot
{
    internal IReadOnlyList<ClientProfileDefinition> Definitions { get; }
    internal IReadOnlyList<ClientFamily> Families { get; }
    internal ClientProfileDefinition Default { get; }

    internal ClientCatalogSnapshot(IReadOnlyList<ClientProfileDefinition> definitions, string defaultName)
    {
        Definitions = Array.AsReadOnly(definitions.ToArray());
        Families = Array.AsReadOnly(definitions.GroupBy(profile => profile.Family, StringComparer.Ordinal)
            .Select(group => new ClientFamily(group.Key, Array.AsReadOnly(group.Select(profile => profile.Version).ToArray())))
            .ToArray());
        Default = definitions.Single(profile => profile.Name == defaultName);
    }

    internal ClientProfileDefinition? Find(string family, string version) =>
        Definitions.FirstOrDefault(profile => profile.Family == family && profile.Version == version);

    /// <summary>The first listed (newest) version of a family; retired versions migrate there, not to another client.</summary>
    internal ClientProfileDefinition? Successor(string family) =>
        Definitions.FirstOrDefault(profile => profile.Family == family);
}

/// <summary>Built-in rules and an optional validated user catalogue. No client process is accessed.</summary>
internal static class ClientCatalog
{
    internal const int MaximumUserFileBytes = 1024 * 1024;

    // libtorrent 1.2/2.0 http_tracker_connection: fixed order, %08X key, corrupt and redundant always present.
    private const string LibtorrentQuery = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&corrupt=0&key={key}{event}&numwant={numwant}&compact=1&no_peer_id=1&supportcrypto=1&redundant=0";
    private const string UTorrentQuery = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&corrupt=0&key={key}{event}&numwant={numwant}&compact=1&no_peer_id=1";

    // Transmission announcer-http.cc (4.0.6 and 4.1.3): corrupt only when nonzero, key formatted {:08X}.
    private const string TransmissionQuery = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&numwant={numwant}&key={key}&compact=1&supportcrypto=1{event}";
    internal const string DefaultProfileName = "qBittorrent 5.2.4";
    private static readonly ClientCatalogSnapshot BuiltIn = new(CreateBuiltIn(), DefaultProfileName);
    private static ClientCatalogSnapshot current = BuiltIn;

    internal static IReadOnlyList<ClientFamily> Families => Volatile.Read(ref current).Families;
    internal static string DefaultFamily => Volatile.Read(ref current).Default.Family;
    internal static string DefaultVersion => Volatile.Read(ref current).Default.Version;
    internal static string? LoadError { get; private set; }

    internal static ClientProfile Create(string family, string version)
    {
        ClientCatalogSnapshot snapshot = Volatile.Read(ref current);
        return (snapshot.Find(family, version) ?? snapshot.Default).Create();
    }

    internal static bool TryCreate(string family, string version, out ClientProfile profile)
    {
        ClientCatalogSnapshot snapshot = Volatile.Read(ref current);
        ClientProfileDefinition? definition = snapshot.Find(family, version);
        profile = (definition ?? snapshot.Default).Create();
        return definition is not null;
    }

    /// <summary>Resolves a saved family/version: exact match, else the newest version of the same family, else the default.</summary>
    internal static (string Family, string Version, ProfileResolution Resolution) Resolve(string family, string version)
    {
        ClientCatalogSnapshot snapshot = Volatile.Read(ref current);
        if (snapshot.Find(family, version) is { } exact) return (exact.Family, exact.Version, ProfileResolution.Exact);
        if (snapshot.Successor(family) is { } successor) return (successor.Family, successor.Version, ProfileResolution.SameFamily);
        return (snapshot.Default.Family, snapshot.Default.Version, ProfileResolution.Default);
    }

    internal static ClientProfileDefinition Definition(string family, string version)
    {
        ClientCatalogSnapshot snapshot = Volatile.Read(ref current);
        return snapshot.Find(family, version) ?? snapshot.Default;
    }

    internal static string GenerateKey(ClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.KeyLength is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(profile));
        return new RandomStringGenerator().Generate(profile.KeyLength,
            (profile.KeyUpperCase ? "0123456789ABCDEF" : "0123456789abcdef").AsSpan());
    }

    /// <summary>Loads before creating tabs. Invalid data selects the complete built-in catalogue.</summary>
    internal static string? LoadUserProfiles(string path)
    {
        LoadError = null;
        ClientCatalogSnapshot selected = BuiltIn;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumUserFileBytes) throw new InvalidDataException("The client catalogue exceeds 1 MiB.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("The client catalogue changed while being read.");
            selected = ParseUserProfiles(bytes);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            LoadError = error.Message;
        }
        Volatile.Write(ref current, selected);
        return LoadError;
    }

    internal static ClientCatalogSnapshot ParseUserProfiles(ReadOnlyMemory<byte> json) =>
        ClientCatalogLoader.Parse(json, BuiltIn);

    internal static ImmutableArray<byte> ReservedBytes(string hex) => ImmutableArray.Create(Convert.FromHexString(hex));

    private static IReadOnlyList<ClientProfileDefinition> CreateBuiltIn() =>
    [
        QBittorrent("5.2.4"),
        QBittorrent("5.2.3"),
        QBittorrent("5.1.4"),
        QBittorrent("5.1.0"),
        QBittorrent("5.0.3"),
        QBittorrent("4.6.7"),
        QBittorrent("4.6.5"),
        UTorrent("uTorrent", "3.6.0", "-UT360S-", 47254, "uTorrent/360(113358998)(47254)", "µTorrent 3.6.0"),
        Transmission("4.1.3", requestQueue: 2000, GlobalAddressMode.Always),
        Transmission("4.0.6", requestQueue: 512, GlobalAddressMode.None),
        Deluge("2.2.0", "2.2.0", "2.0.7.0"),
        Deluge("2.2.0 (libtorrent 1.2.19)", "2.2.0", "1.2.19.0"),
        Deluge("2.1.1", "2.1.1", "2.0.6.0"),
        UTorrent("BitTorrent", "7.11.0 (47255)", "-BT7b0S-", 47255, "BitTorrent/7110(258062487)(47255)", "BitTorrent 7.11.0"),
    ];

    // libtorrent bt_peer_connection::write_handshake: DHT (7:0x01), extensions (5:0x10), fast (7:0x04);
    // 1.2 also merkle (5:0x08); 2.0 adds the v2 upgrade bit (7:0x10) for v1 connections to hybrid torrents.
    private static PeerWireDefinition Libtorrent(string userAgent, bool version2) => new()
    {
        Reserved = ReservedBytes(version2 ? "0000000000100005" : "0000000000180005"),
        HybridUpgradeBit = version2,
        Style = ExtensionHandshakeStyle.Libtorrent,
        ClientName = userAgent,
        RequestQueue = version2 ? 2000 : 500,
        KeepAliveSeconds = 60,
    };

    // libtorrent torrent::tracker_response: next announce after max(interval, min_announce_interval = 300 s);
    // http_tracker_connection: "min interval" defaults to 30 s when the tracker omits it.
    private const int LibtorrentIntervalFloor = 300;
    private const int LibtorrentDefaultMinInterval = 30;

    // qBittorrent sessionimpl.cpp: user_agent "qBittorrent/<version>", first-run port rand(1024, 65535).
    private static ClientProfileDefinition QBittorrent(string version) => new()
    {
        Family = "qBittorrent", Version = version, PeerIdPrefix = "-qB" + version.Replace(".", "", StringComparison.Ordinal) + "0-",
        SupportsV2 = true, GlobalAddresses = GlobalAddressMode.PrivateTorrents,
        IntervalFloorSeconds = LibtorrentIntervalFloor, DefaultMinIntervalSeconds = LibtorrentDefaultMinInterval,
        Headers = "Host: {host}\r\nUser-Agent: qBittorrent/" + version + "\r\nAccept-Encoding: gzip\r\nConnection: close\r\n",
        Query = LibtorrentQuery,
        PortRanges = [new PortRange(1024, 65535)],
        PeerWire = Libtorrent("qBittorrent/" + version, version2: true),
    };

    // Deluge core.py: user agent "Deluge/<version> libtorrent/<lt.__version__>", fingerprint -DE<ver>s-;
    // preferencesmanager.py: random port randrange(49152, 65525). Official Windows installers ship both
    // libtorrent branches (2.2.0: 2.0.7 and 1.2.19; 2.1.1: 2.0.6 and 1.2.15).
    private static ClientProfileDefinition Deluge(string version, string release, string libtorrent)
    {
        bool version2 = libtorrent.StartsWith("2.", StringComparison.Ordinal);
        string agent = "Deluge/" + release + " libtorrent/" + libtorrent;
        return new()
        {
            Family = "Deluge", Version = version, PeerIdPrefix = "-DE" + release.Replace(".", "", StringComparison.Ordinal) + "s-",
            SupportsV2 = version2, GlobalAddresses = GlobalAddressMode.PrivateTorrents,
            IntervalFloorSeconds = LibtorrentIntervalFloor, DefaultMinIntervalSeconds = LibtorrentDefaultMinInterval,
            Headers = "Host: {host}\r\nUser-Agent: " + agent + "\r\nAccept-Encoding: gzip\r\nConnection: close\r\n",
            Query = LibtorrentQuery,
            PortRanges = [new PortRange(49152, 65524)],
            PeerWire = Libtorrent(agent, version2),
        };
    }

    // Transmission: libcurl request (Host first, Accept */*, Accept-Encoding from a zlib-only curl build, no
    // Connection header), default peer port 51413, RFC 3986 escaping in upper-case hex. Handshake: LTEP, fast, DHT.
    // announcer.cc: the tracker interval is used as sent; without "min interval", 120 s between announces.
    private static ClientProfileDefinition Transmission(string version, int requestQueue, GlobalAddressMode addresses) => new()
    {
        Family = "Transmission", Version = version, PeerIdPrefix = "-TR" + version.Replace(".", "", StringComparison.Ordinal) + "0-",
        PeerIdTail = PeerIdTailKind.Transmission, Unreserved = ClientEncoding.Rfc3986Unreserved,
        HashUpperCase = true, PeerIdUpperCase = true, NumWant = 80, GlobalAddresses = addresses, DefaultMinIntervalSeconds = 120,
        Headers = "Host: {host}\r\nUser-Agent: Transmission/" + version + "\r\nAccept: */*\r\nAccept-Encoding: deflate, gzip\r\n",
        Query = TransmissionQuery,
        PortRanges = [new PortRange(51413, 51413)],
        PeerWire = new()
        {
            Reserved = ReservedBytes("0000000000100005"), Style = ExtensionHandshakeStyle.Transmission,
            ClientName = "Transmission " + version, RequestQueue = requestQueue, PrefersRc4 = true, KeepAliveSeconds = 100,
        },
    };

    // µTorrent and BitTorrent are closed source: the build bytes, User-Agent, check bytes and 10000-64999 port
    // range follow published analyses of these builds. Their extension handshake follows the family's BEP 10 usage.
    // Their announce timing is unknown, so only the engine's own 60-second floor applies.
    private static ClientProfileDefinition UTorrent(string family, string version, string prefix, ushort build, string userAgent, string clientName) => new()
    {
        Family = family, Version = version,
        PeerIdPrefix = prefix + (char)(build & 255) + (char)(build >> 8),
        PeerIdLiteralPrefix = prefix, PeerIdTail = PeerIdTailKind.UTorrent, PeerIdByteMinimum = 1,
        Unreserved = ClientEncoding.Rfc3986Unreserved, KeyRefreshSeconds = 600,
        Headers = "Host: {host}\r\nUser-Agent: " + userAgent + "\r\nAccept-Encoding: gzip\r\nConnection: Close\r\n",
        Query = UTorrentQuery,
        PortRanges = [new PortRange(10000, 64999)],
        PeerWire = new()
        {
            Reserved = ReservedBytes("0000000000100005"), Style = ExtensionHandshakeStyle.UTorrent,
            ClientName = clientName, RequestQueue = 255, PrefersRc4 = true,
        },
    };
}

internal enum ProfileResolution
{
    Exact,
    SameFamily,
    Default,
}
