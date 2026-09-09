namespace RatioMaster.Models;

using System;
using System.Collections.Generic;
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
    internal ClientUrlEncoding UrlEncoding { get; init; } = ClientUrlEncoding.Libtorrent;
    internal bool HashUpperCase { get; init; }
    internal bool PeerIdUpperCase { get; init; }
    internal int KeyLength { get; init; } = 8;
    internal bool KeyUpperCase { get; init; } = true;
    internal int KeyRefreshSeconds { get; init; }
    internal int NumWant { get; init; } = 200;
    internal bool SupportsV2 { get; init; }

    internal ClientProfile Create()
    {
        RandomStringGenerator generator = new();
        int length = 20 - PeerIdPrefix.Length;
        string suffix = PeerIdTail switch
        {
            PeerIdTailKind.Libtorrent => generator.Generate(length, RandomStringGenerator.LibtorrentAlphabet.AsSpan()),
            PeerIdTailKind.Binary => generator.GenerateBytes(length, PeerIdByteMinimum, PeerIdByteMaximum + 1),
            PeerIdTailKind.Transmission => generator.GenerateTransmissionTail(),
            _ => throw new InvalidOperationException("Unknown peer ID generator."),
        };
        ClientProfile profile = new()
        {
            Name = Name, Family = Family, Version = Version, HttpProtocol = HttpProtocol,
            Headers = Headers, Query = Query, PeerID = PeerIdPrefix + suffix,
            PeerIdLiteralPrefix = PeerIdLiteralPrefix, UrlEncoding = UrlEncoding,
            HashUpperCase = HashUpperCase, PeerIdUpperCase = PeerIdUpperCase,
            KeyLength = KeyLength, KeyUpperCase = KeyUpperCase,
            KeyRefreshInterval = TimeSpan.FromSeconds(KeyRefreshSeconds), DefNumWant = NumWant,
            SupportsV2 = SupportsV2,
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
}

/// <summary>Built-in rules and an optional validated user catalogue. No client process is accessed.</summary>
internal static class ClientCatalog
{
    internal const int MaximumUserFileBytes = 1024 * 1024;
    private const string LibtorrentQuery = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&corrupt=0&key={key}{event}&numwant={numwant}&compact=1&no_peer_id=1&supportcrypto=1&redundant=0";
    private const string UTorrentQuery = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&corrupt=0&key={key}{event}&numwant={numwant}&compact=1&no_peer_id=1";
    private static readonly ClientCatalogSnapshot BuiltIn = new(CreateBuiltIn(), "qBittorrent 5.1.0");
    private static ClientCatalogSnapshot current = BuiltIn;

    internal static IReadOnlyList<ClientFamily> Families => Volatile.Read(ref current).Families;
    internal static string DefaultFamily => Volatile.Read(ref current).Default.Family;
    internal static string DefaultVersion => Volatile.Read(ref current).Default.Version;
    internal static string? LoadError { get; private set; }
    internal static string? UserProfilesPath { get; private set; }

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
        UserProfilesPath = path;
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
            or ArgumentException or InvalidOperationException)
        {
            LoadError = "Client catalogue ignored: " + error.Message;
        }
        Volatile.Write(ref current, selected);
        return LoadError;
    }

    internal static ClientCatalogSnapshot ParseUserProfiles(ReadOnlyMemory<byte> json) =>
        ClientCatalogLoader.Parse(json, BuiltIn);

    private static IReadOnlyList<ClientProfileDefinition> CreateBuiltIn() =>
    [
        QBittorrent("5.1.0", "-qB5100-"),
        QBittorrent("5.0.3", "-qB5030-"),
        QBittorrent("4.6.7", "-qB4670-"),
        QBittorrent("4.6.5", "-qB4650-"),
        QBittorrent("5.2.3", "-qB5230-"),
        QBittorrent("5.1.4", "-qB5140-"),
        UTorrent("uTorrent", "3.6.0", "-UT360S-", 46828, "uTorrent/360(113358572)(46828)"),
        UTorrent("uTorrent", "3.5.5", "-UT355S-", 46074, "uTorrent/355(111916026)(46074)"),
        UTorrent("uTorrent", "3.5.4", "-UT354S-", 44498, "uTorrent/354(111783378)(44498)"),
        new()
        {
            Family = "Transmission", Version = "4.0.6", PeerIdPrefix = "-TR4060-",
            PeerIdTail = PeerIdTailKind.Transmission, UrlEncoding = ClientUrlEncoding.Rfc3986,
            HashUpperCase = true, PeerIdUpperCase = true, NumWant = 80,
            Headers = "User-Agent: Transmission/4.0.6\r\nHost: {host}\r\nAccept: */*\r\nAccept-Encoding: gzip\r\n",
            Query = "info_hash={infohash}&peer_id={peerid}&port={port}&uploaded={uploaded}&downloaded={downloaded}&left={left}&numwant={numwant}&key={key}&compact=1&supportcrypto=1{event}",
        },
        new()
        {
            Family = "Deluge", Version = "2.1.1", PeerIdPrefix = "-DE211s-", SupportsV2 = true,
            Headers = "Host: {host}\r\nUser-Agent: Deluge/2.1.1 libtorrent/2.0.7.0\r\nAccept-Encoding: gzip\r\nConnection: close\r\n",
            Query = LibtorrentQuery,
        },
        UTorrent("BitTorrent", "7.10.3 (44429)", "-BT7a3S-", 44429, "BitTorrent/7103(256355725)(44429)"),
    ];

    private static ClientProfileDefinition QBittorrent(string version, string prefix) => new()
    {
        Family = "qBittorrent", Version = version, PeerIdPrefix = prefix, SupportsV2 = true,
        Headers = "Host: {host}\r\nUser-Agent: qBittorrent/" + version + "\r\nAccept-Encoding: gzip\r\nConnection: close\r\n",
        Query = LibtorrentQuery,
    };

    private static ClientProfileDefinition UTorrent(string family, string version, string prefix, ushort build, string userAgent) => new()
    {
        Family = family, Version = version,
        PeerIdPrefix = prefix + (char)(build & 255) + (char)(build >> 8),
        PeerIdLiteralPrefix = prefix, PeerIdTail = PeerIdTailKind.Binary, PeerIdByteMinimum = 1,
        UrlEncoding = ClientUrlEncoding.Alphanumeric, KeyRefreshSeconds = 600,
        Headers = "Host: {host}\r\nUser-Agent: " + userAgent + "\r\nAccept-Encoding: gzip\r\nConnection: Close\r\n",
        Query = UTorrentQuery,
    };
}
