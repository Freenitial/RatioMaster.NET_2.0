#if DEBUG
namespace RatioMaster;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RatioMaster.Engine;
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>Offline profile fixtures, byte encoding checks, declarative catalogue validation and request goldens.</summary>
internal static class ProfileSelfTest
{
    private const string GoldenResource = "RatioMaster.Tests.RequestGoldens.txt";

    internal static bool Run()
    {
        bool passed = true;
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("  [PASS] Profiles: " + name); }
            catch (Exception error) { passed = false; Console.WriteLine("  [FAIL] Profiles: " + name + " — " + error.Message); }
        }
        Check("catalogue order, default and same-family migration of retired versions", Catalogue);
        Check("uTorrent and BitTorrent build bytes, User-Agent, check bytes and escaping", BinaryBuilds);
        Check("all byte values survive each percent-encoding policy", EncodingRoundTrips);
        Check("Transmission request headers, key case, checksum and default port", Transmission);
        Check("libtorrent punctuation alphabet, Deluge builds and Latin-1 boundary", Libtorrent);
        Check("first-run port ranges follow each client and stay within them", PortRanges);
        Check("peer-wire reserved bits and extension handshakes per family", PeerWireProfiles);
        Check("profile snapshots and concurrent generation remain independent", Isolation);
        Check("external profiles merge, inherit and select their default", ExternalProfiles);
        Check("invalid catalogue types, identities, headers and queries are rejected", InvalidProfiles);
        Check("catalogue depth and size limits", Limits);
        Check("an invalid clients.json is reported and the built-in catalogue kept", InvalidFile);
        Check("every built-in profile matches its byte-exact request and handshake fixtures", Goldens);
        return passed;
    }

    private static readonly string[] ExpectedCatalogue =
    [
        "qBittorrent 5.2.4", "qBittorrent 5.2.3", "qBittorrent 5.1.4", "qBittorrent 5.1.0", "qBittorrent 5.0.3",
        "qBittorrent 4.6.7", "qBittorrent 4.6.5", "uTorrent 3.6.0", "Transmission 4.1.3", "Transmission 4.0.6",
        "Deluge 2.2.0", "Deluge 2.2.0 (libtorrent 1.2.19)", "Deluge 2.1.1", "BitTorrent 7.11.0 (47255)",
    ];

    private static void Catalogue()
    {
        Require(ClientCatalog.DefaultFamily == "qBittorrent" && ClientCatalog.DefaultVersion == "5.2.4", "Unexpected built-in default.");
        string[] actual = ClientCatalog.Families.SelectMany(family => family.Versions.Select(version => family.Name + " " + version)).ToArray();
        Require(actual.SequenceEqual(ExpectedCatalogue), "Unexpected built-in catalogue: " + string.Join(", ", actual));
        Require(!ClientCatalog.TryCreate("missing", "0", out ClientProfile fallback), "Unknown profile reported present.");
        Require(fallback.Name == "qBittorrent 5.2.4", "Unknown profile did not use the default.");
        Require(ClientCatalog.Resolve("uTorrent", "3.5.5") == ("uTorrent", "3.6.0", ProfileResolution.SameFamily), "Retired uTorrent must move to the current uTorrent.");
        Require(ClientCatalog.Resolve("BitTorrent", "7.10.3 (44429)") == ("BitTorrent", "7.11.0 (47255)", ProfileResolution.SameFamily), "Retired BitTorrent must stay BitTorrent.");
        Require(ClientCatalog.Resolve("Vuze", "5.7.6.0").Resolution == ProfileResolution.Default, "An unknown family uses the default.");
        foreach (ClientFamily family in ClientCatalog.Families)
        foreach (string version in family.Versions)
        {
            ClientProfile profile = ClientCatalog.Create(family.Name, version);
            Require(profile.PeerID.Length == 20 && profile.PeerID.All(value => value <= 255), profile.Name + " peer ID is not 20 raw bytes.");
            Require(profile.Key.Length == 8 && profile.Key.All(value => "0123456789ABCDEF".Contains(value)), "Invalid built-in key.");
            Require(profile.DescribeIdentityMismatch(profile.PeerID) is null, profile.Name + " generated an identity it would reject.");
            Require(!profile.Headers.Contains("Connection:", StringComparison.Ordinal) || family.Name != "Transmission", "Transmission sends no Connection header.");
        }
    }

    private static void BinaryBuilds()
    {
        foreach ((string family, string version, string prefixHex, string agent) in new[]
        {
            ("uTorrent", "3.6.0", "2D5554333630532D96B8", "uTorrent/360(113358998)(47254)"),
            ("BitTorrent", "7.11.0 (47255)", "2D4254376230532D97B8", "BitTorrent/7110(258062487)(47255)"),
        })
        {
            for (int iteration = 0; iteration < 32; iteration++)
            {
                ClientProfile profile = ClientCatalog.Create(family, version);
                Require(Convert.ToHexString(Encoding.Latin1.GetBytes(profile.PeerID[..10])) == prefixHex, profile.Name + " build bytes differ.");
                Require(profile.PeerID[10..18].All(value => value is >= (char)1 and <= (char)255), "Binary tail contains an out-of-range byte.");
                Require(ClientIdentity.HasUTorrentChecksum(profile.PeerID), "The last two bytes must be the µTorrent check bytes.");
                Require(profile.Headers.Contains("User-Agent: " + agent + "\r\n", StringComparison.Ordinal), "Missing build-specific User-Agent.");
                Require(profile.Headers.EndsWith("Connection: Close\r\n", StringComparison.Ordinal), "Incorrect Connection form.");
                Require(profile.KeyRefreshInterval == TimeSpan.FromMinutes(10), "Incorrect generated-key refresh policy.");
            }
            ClientProfile sample = ClientCatalog.Create(family, version);
            string raw = sample.PeerID[..10] + "\0-_.!~*()ÿ";
            string encoded = sample.EncodePeerId(raw);
            Require(encoded.StartsWith(sample.PeerID[..8], StringComparison.Ordinal), "Literal peer prefix was escaped.");
            Require(encoded.EndsWith("%00-_.%21~%2a%28%29%ff", StringComparison.Ordinal), "µTorrent keeps -._~ and escapes the rest in lower case.");
            Require(Decode(encoded).SequenceEqual(Encoding.Latin1.GetBytes(raw)), "Binary announce identity differs from handshake bytes.");
            string tampered = sample.PeerID[..18] + (char)(sample.PeerID[18] ^ 1) + sample.PeerID[19];
            Require(sample.DescribeIdentityMismatch(tampered) == "checksum", "A wrong check byte must be detected.");
        }
    }

    private static void EncodingRoundTrips()
    {
        RandomStringGenerator generator = new();
        string input = new(Enumerable.Range(0, 256).Select(value => (char)value).ToArray());
        foreach (string policy in new[] { ClientEncoding.LibtorrentUnreserved, ClientEncoding.Rfc3986Unreserved, string.Empty, "-", ".-*_" })
        foreach (bool upper in new[] { false, true })
            Require(Decode(generator.UrlEncode(input, upper, policy)).SequenceEqual(Encoding.Latin1.GetBytes(input)), "Percent encoding lost a byte.");
        string punctuation = "-_.!~*()";
        Require(generator.UrlEncode(punctuation, false, ClientEncoding.UnreservedFor(ClientUrlEncoding.Libtorrent)) == punctuation, "libtorrent safe punctuation changed.");
        Require(generator.UrlEncode(punctuation, true, ClientEncoding.UnreservedFor(ClientUrlEncoding.Rfc3986)) == "-_.%21~%2A%28%29", "RFC3986 punctuation mismatch.");
        Require(generator.UrlEncode(punctuation, false, ClientEncoding.UnreservedFor(ClientUrlEncoding.Alphanumeric)) == "%2d%5f%2e%21%7e%2a%28%29", "Alphanumeric policy mismatch.");
    }

    private static void Transmission()
    {
        foreach (string version in new[] { "4.1.3", "4.0.6" })
        {
            for (int iteration = 0; iteration < 64; iteration++)
            {
                ClientProfile profile = ClientCatalog.Create("Transmission", version);
                string prefix = "-TR" + version.Replace(".", "", StringComparison.Ordinal) + "0-";
                Require(profile.PeerID.StartsWith(prefix, StringComparison.Ordinal), "Transmission prefix differs.");
                Require(profile.PeerID[8..].All(ClientIdentity.Base36.Contains), "Transmission alphabet differs.");
                Require(ClientIdentity.HasTransmissionChecksum(profile.PeerID), "Transmission checksum failed.");
                Require(profile.HashUpperCase && profile.Unreserved == ClientEncoding.Rfc3986Unreserved, "Transmission hash policy differs.");
                Require(profile.KeyRefreshInterval == TimeSpan.Zero && !profile.SupportsV2, "Unsupported Transmission capability.");
            }
            ClientProfile sample = ClientCatalog.Create("Transmission", version);
            Require(sample.Headers == "Host: {host}\r\nUser-Agent: Transmission/" + version + "\r\nAccept: */*\r\nAccept-Encoding: deflate, gzip\r\n",
                "Transmission must send libcurl's header order without a Connection header.");
            Require(sample.PortRanges.Count == 1 && sample.PortRanges[0] == new PortRange(51413, 51413), "Transmission listens on 51413 by default.");
            Require(sample.GlobalAddresses == (version == "4.1.3" ? GlobalAddressMode.Always : GlobalAddressMode.None), "Transmission address parameters differ.");
        }
    }

    private static void Libtorrent()
    {
        const string expectedAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_.!~*()";
        Require(RandomStringGenerator.LibtorrentAlphabet == expectedAlphabet, "libtorrent generator excludes expected punctuation.");
        foreach ((string family, string version, string prefix, string agent, bool v2) in new[]
        {
            ("qBittorrent", "5.2.4", "-qB5240-", "qBittorrent/5.2.4", true),
            ("Deluge", "2.2.0", "-DE220s-", "Deluge/2.2.0 libtorrent/2.0.7.0", true),
            ("Deluge", "2.2.0 (libtorrent 1.2.19)", "-DE220s-", "Deluge/2.2.0 libtorrent/1.2.19.0", false),
            ("Deluge", "2.1.1", "-DE211s-", "Deluge/2.1.1 libtorrent/2.0.6.0", true),
        })
        {
            ClientProfile profile = ClientCatalog.Create(family, version);
            Require(profile.PeerID.StartsWith(prefix, StringComparison.Ordinal) && profile.PeerID[8..].All(expectedAlphabet.Contains), "Unexpected libtorrent identity for " + profile.Name);
            Require(profile.Headers.Contains("User-Agent: " + agent + "\r\n", StringComparison.Ordinal), "Unexpected User-Agent for " + profile.Name);
            Require(profile.SupportsV2 == v2, "v2 capability must follow the libtorrent branch of " + profile.Name);
            Require(profile.GlobalAddresses == GlobalAddressMode.PrivateTorrents, "libtorrent lists its public addresses for private torrents.");
        }
        Throws(() => new RandomStringGenerator().UrlEncode("€", false, ClientEncoding.LibtorrentUnreserved), "Unicode replacement must not alter identity bytes.");
        Require(new RandomStringGenerator().GenerateBytes(3, 255, 256) == "ÿÿÿ", "Maximum random byte cannot be produced.");
        Require(new RandomStringGenerator().GenerateBytes(3, 0, 1) == "\0\0\0", "Minimum random byte cannot be produced.");
    }

    private static void PortRanges()
    {
        foreach ((string family, string version, int minimum, int maximum) in new[]
        {
            ("qBittorrent", "5.2.4", 1024, 65535), ("uTorrent", "3.6.0", 10000, 64999), ("Transmission", "4.1.3", 51413, 51413),
            ("Deluge", "2.2.0", 49152, 65524), ("BitTorrent", "7.11.0 (47255)", 10000, 64999),
        })
        {
            ClientProfile profile = ClientCatalog.Create(family, version);
            for (int iteration = 0; iteration < 200; iteration++)
            {
                int port = PortRange.Pick(profile.PortRanges);
                Require(port >= minimum && port <= maximum, profile.Name + " drew a port outside its range: " + port);
            }
        }
        Require(PortRange.TryParse("10000-64999, 51413", out IReadOnlyList<PortRange> ranges) && ranges.Count == 2 && PortRange.Format(ranges) == "10000-64999,51413", "Port ranges must round-trip.");
        foreach (string invalid in new[] { "", "0", "70000", "5-3", "a-b", "1-2-3" })
            Require(!PortRange.TryParse(invalid, out _), "Accepted invalid port range: " + invalid);

        ClientProfile utorrent = ClientCatalog.Create("uTorrent", "3.6.0");
        string direct = "direct-test-" + Guid.NewGuid().ToString("N");
        int first = ListeningPortBook.Resolve(utorrent, direct, _ => true);
        Require(ListeningPortBook.Resolve(ClientCatalog.Create("uTorrent", "3.6.0"), direct, _ => true) == first, "Tabs of one client must share its port.");
        Require(ListeningPortBook.Resolve(utorrent, direct + "-proxy", _ => true) is int other && other >= 10000 && other <= 64999, "Another exit draws its own port.");
        int busy = 0;
        int chosen = ListeningPortBook.Resolve(ClientCatalog.Create("qBittorrent", "5.2.4"), direct, port => ++busy > 3);
        Require(busy == 4 && chosen >= 1024, "Unavailable ports must be skipped when the port is first drawn.");
        Require(ListeningPortBook.Resolve(ClientCatalog.Create("Transmission", "4.1.3"), direct, _ => false) == 51413, "A fixed default port is kept as the real client would.");
    }

    private static void PeerWireProfiles()
    {
        IPAddress remote = IPAddress.Parse("203.0.113.7");
        foreach (ClientFamily family in ClientCatalog.Families)
        foreach (string version in family.Versions)
        {
            ClientProfile profile = ClientCatalog.Create(family.Name, version);
            PeerWireDefinition wire = profile.PeerWire;
            Require(wire.SupportsExtensions && wire.SupportsFast && wire.SupportsDht, profile.Name + " must offer extensions, fast and DHT like the real client.");
            foreach (bool isPrivate in new[] { false, true })
            {
                byte[] handshake = PeerWire.ExtensionHandshake(Torrent(profile, isPrivate), remote, seeding: true);
                string text = Encoding.Latin1.GetString(handshake);
                Require(text.StartsWith('d') && text.EndsWith('e'), "Extension handshake must be a bencoded dictionary.");
                Require(text.Contains("6:yourip4:" + Encoding.Latin1.GetString(remote.GetAddressBytes()), StringComparison.Ordinal), "yourip must carry the peer's address.");
                Require(text.Contains("ut_metadata", StringComparison.Ordinal) != isPrivate && text.Contains("ut_pex", StringComparison.Ordinal) != isPrivate,
                    "Metadata exchange and PEX are offered for public torrents only.");
                Require(text.Contains("4:reqqi" + wire.RequestQueue + "e", StringComparison.Ordinal), "reqq must follow the client.");
            }
        }
        Require(ClientCatalog.Create("Deluge", "2.2.0 (libtorrent 1.2.19)").PeerWire.Reserved[5] == 0x18, "libtorrent 1.2 also announces merkle support.");
        Require(ClientCatalog.Create("qBittorrent", "5.2.4").PeerWire.HybridUpgradeBit, "libtorrent 2.0 offers the v2 upgrade on hybrids.");
    }

    internal static PeerTorrent Torrent(ClientProfile profile, bool isPrivate) => new()
    {
        InfoHashes = [new byte[20]],
        PeerId = Encoding.Latin1.GetBytes(profile.PeerID),
        Wire = profile.PeerWire,
        AdvertisedPort = 6881,
        IsPrivate = isPrivate,
        Metadata = new byte[4321],
        PieceCount = 10,
        Realistic = true,
        Progress = () => 1,
    };

    private static void Isolation()
    {
        ClientProfile original = ClientCatalog.Create("uTorrent", "3.6.0");
        ClientProfile snapshot = original.Snapshot();
        string savedPeer = snapshot.PeerID;
        original.PeerID = "changed";
        original.Headers = "changed";
        Require(snapshot.PeerID == savedPeer && snapshot.Headers != "changed", "Profile snapshot shares mutable state.");
        Parallel.For(0, 128, _ =>
        {
            ClientProfile instance = ClientCatalog.Create("uTorrent", "3.6.0");
            Require(instance.PeerID.Length == 20 && instance.Headers != "changed", "Concurrent profile creation is corrupted.");
        });
    }

    private static void ExternalProfiles()
    {
        ClientCatalogSnapshot result = Parse("""
            {"format":1,"default":"Example 1.0","clients":[
              {"family":"qBittorrent","version":"5.2.4","numWant":0},
              {"family":"Example","version":"1.0","base":"qBittorrent 5.2.4","peerIdPrefixHex":"2D4558313030302D",
               "headers":["Host: {host}","User-Agent: Example/1.0","Accept-Encoding: gzip, deflate;q=0.8, br, identity"],
               "keyUpperCase":false,"keyRefreshSeconds":120,"unreserved":"-.","portRanges":"20000-20009",
               "globalAddresses":"none","handshakeReservedHex":"0000000000100004","extensionStyle":"transmission",
               "extensionClient":"Example 1.0","extensionRequestQueue":64,"encryptionPrefersRc4":true},
              {"family":"Example","version":"2.0","base":"uTorrent 3.6.0","urlEncoding":"alphanumeric"}
            ]}
            """);
        Require(result.Definitions.Count == ExpectedCatalogue.Length + 2 && result.Default.Name == "Example 1.0", "Merge/default selection failed.");
        Require(result.Find("qBittorrent", "5.2.4")!.NumWant == 0, "Explicit zero was lost.");
        ClientProfile created = result.Default.Create();
        Require(created.PeerID.StartsWith("-EX1000-", StringComparison.Ordinal) && created.Key.All(value => "0123456789abcdef".Contains(value)), "Inherited profile generation failed.");
        Require(created.KeyRefreshInterval == TimeSpan.FromSeconds(120), "User rotation duration was lost.");
        Require(created.Unreserved == "-." && created.PortRanges[0] == new PortRange(20000, 20009) && created.GlobalAddresses == GlobalAddressMode.None, "New fields were not applied.");
        Require(created.PeerWire.Style == ExtensionHandshakeStyle.Transmission && created.PeerWire.ClientName == "Example 1.0"
            && created.PeerWire.RequestQueue == 64 && created.PeerWire.PrefersRc4 && !created.PeerWire.SupportsDht, "Peer-wire fields were not applied.");
        ClientProfile second = result.Find("Example", "2.0")!.Create();
        Require(second.Unreserved.Length == 0 && ClientIdentity.HasUTorrentChecksum(second.PeerID), "A based profile keeps its identity rules and accepts a named encoding.");
        Require(ClientCatalog.DefaultFamily == "qBittorrent", "Parsing changed the active catalogue.");
    }

    private static void InvalidProfiles()
    {
        foreach (string fields in new[]
        {
            "\"unknown\":true", "\"keyLength\":9", "\"keyLength\":1.5", "\"keyRefreshSeconds\":-1",
            "\"keyRefreshSeconds\":1", "\"numWant\":-1", "\"supportsV2\":\"true\"",
            "\"peerIdPrefixHex\":\"GG\"", "\"peerIdPrefixHex\":\"\"", "\"peerIdTail\":\"custom-code\"",
            "\"peerIdTail\":\"transmission\",\"peerIdPrefixHex\":\"41\"", "\"peerIdByteMaximum\":256",
            "\"peerIdTail\":\"utorrent\",\"peerIdPrefixHex\":\"41414141414141414141414141414141414141\"",
            "\"headers\":[\"Host: evil.invalid\",\"User-Agent: Test\"]",
            "\"headers\":[\"Host: {host}\",\"Host: {host}\",\"User-Agent: Test\"]",
            "\"headers\":[\"Host: {host}\",\"User-Agent: Test\\r\\nInjected: yes\"]",
            "\"headers\":[\"Host: {host}\",\"User-Agent: Test\",\"Content-Length: 0\"]",
            "\"headers\":[\"Host: {host}\",\"User-Agent: Test\",\"Accept-Encoding: unsupported\"]",
            "\"query\":\"info_hash={unknown}\"", "\"base\":\"No such profile\"",
            "\"unreserved\":\"-/\"", "\"unreserved\":\"--\"", "\"urlEncoding\":\"rfc3986\",\"unreserved\":\"-\"",
            "\"portRanges\":\"0-10\"", "\"portRanges\":\"5000-4000\"", "\"globalAddresses\":\"sometimes\"",
            "\"handshakeReservedHex\":\"00\"", "\"handshakeReservedHex\":\"0000000000000005\"", "\"extensionStyle\":\"unknown\"",
            "\"extensionRequestQueue\":0", "\"extensionClient\":\"bad\\u0001name\"", "\"keepAliveSeconds\":10",
            "\"intervalFloorSeconds\":4000", "\"defaultMinIntervalSeconds\":-1",
        })
            Throws(() => Parse("{\"format\":1,\"clients\":[{\"family\":\"qBittorrent\",\"version\":\"5.2.4\"," + fields + "}]}"), "Accepted invalid profile: " + fields);
        Throws(() => Parse("{\"format\":1,\"format\":1,\"clients\":[]}"), "Duplicate JSON property accepted.");
        Throws(() => Parse("{\"format\":2,\"clients\":[]}"), "Unknown format accepted.");
        Throws(() => Parse("{\"format\":1,\"default\":\"Missing\",\"clients\":[]}"), "Unknown default accepted.");
        string query = ClientCatalog.Create("qBittorrent", "5.2.4").Query.Replace("numwant={numwant}", "numwant=200");
        Throws(() => Parse("{\"format\":1,\"clients\":[{\"family\":\"qBittorrent\",\"version\":\"5.2.4\",\"query\":\"" + query + "\"}]}"), "Literal numwant accepted.");
        foreach (string invalid in new[]
        {
            ClientCatalog.Create("qBittorrent", "5.2.4").Query + "&event=stopped",
            ClientCatalog.Create("qBittorrent", "5.2.4").Query.Replace("{event}&", "{event}suffix&", StringComparison.Ordinal),
        })
            Throws(() => Parse("{\"format\":1,\"clients\":[{\"family\":\"qBittorrent\",\"version\":\"5.2.4\",\"query\":\"" + invalid + "\"}]}"),
                "Ambiguous announce event accepted: " + invalid);
    }

    private static void Limits()
    {
        // Valid documents just inside each limit pass, so the rejections below can only come from the limits.
        const string empty = "{\"format\":1,\"clients\":[]}";
        Parse(empty + new string(' ', ClientCatalog.MaximumUserFileBytes - empty.Length));
        ThrowsExactly<InvalidDataException>(() => Parse(empty + new string(' ', ClientCatalog.MaximumUserFileBytes - empty.Length + 1)), "Oversized catalogue accepted.");
        string nested = "{\"format\":1,\"clients\":[{\"family\":\"X\",\"version\":\"1\",\"base\":\"qBittorrent 5.2.4\",\"headers\":" + new string('[', 13) + new string(']', 13) + "}]}";
        ThrowsExactly<InvalidDataException>(() => Parse(nested), "Sixteen nesting levels must reach profile validation.");
        ThrowsExactly<JsonException>(() => Parse(nested.Replace("[[", "[[[", StringComparison.Ordinal).Replace("]]", "]]]", StringComparison.Ordinal)), "Excessive nesting accepted.");
        string entries = string.Join(",", Enumerable.Range(0, 129).Select(index => "{\"family\":\"X\",\"version\":\"" + index + "\",\"base\":\"qBittorrent 5.2.4\"}"));
        Throws(() => Parse("{\"format\":1,\"clients\":[" + entries + "]}"), "Excessive profile count accepted.");
    }

    // ══════════════ Byte-exact fixtures ══════════════

    /// <summary>
    /// The request and handshake each built-in profile produces for one fixed identity. Any difference with the
    /// stored fixtures is a change of fingerprint and must be deliberate (regenerate with --write-request-goldens).
    /// </summary>
    internal static string RenderGoldens()
    {
        StringBuilder output = new();
        byte[] infoHash = [0x2D, 0x2E, 0x5F, 0x7E, 0x21, 0x2A, 0x28, 0x29, 0x27, 0x00, 0xFF, 0x41, 0x7A, 0x30, 0x20, 0x2B, 0x25, 0x26, 0x3D, 0x2F];
        foreach (ClientFamily family in ClientCatalog.Families)
        foreach (string version in family.Versions)
        {
            ClientProfile profile = ClientCatalog.Create(family.Name, version);
            string peerId = FixedPeerId(profile);
            string key = profile.KeyUpperCase ? "0A1B2C3D"[..profile.KeyLength] : "0a1b2c3d"[..profile.KeyLength];
            output.Append("### ").Append(profile.Name).Append('\n');
            foreach ((string eventName, string numWant) in new[] { ("started", profile.DefNumWant.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("stopped", "0") })
            {
                string url = RatioEngine.BuildAnnounceUrl(profile, "https://tracker.example:2710/a%2Fb/announce.php?passkey=x%2Fy", infoHash, peerId, "6881",
                    eventName, 1, 2, 3, key, numWant, eventName == "stopped" ? "opaque&id" : string.Empty, []);
                Require(TrackerClient.TryParseUrl(url, out _, out string host, out int port, out string target), "Fixture URL must parse.");
                output.Append(TrackerClient.BuildRequest(target, TrackerClient.Authority(host, port, 443), profile).Replace("\r\n", "\\r\\n\n"));
            }
            PeerWireDefinition wire = profile.PeerWire;
            output.Append("reserved ").Append(Convert.ToHexString(wire.Reserved.AsSpan())).Append('\n');
            foreach (bool isPrivate in new[] { false, true })
                output.Append(isPrivate ? "ltep private " : "ltep public ")
                    .Append(Printable(PeerWire.ExtensionHandshake(Torrent(profile, isPrivate), IPAddress.Parse("203.0.113.7"), seeding: !isPrivate))).Append('\n');
            output.Append('\n');
        }
        return output.ToString();
    }

    private static string FixedPeerId(ClientProfile profile) => profile.PeerIdTail switch
    {
        PeerIdTailKind.Transmission => profile.PeerIdPrefix + "abcdefghij0" + ClientIdentity.Base36[(36 - "abcdefghij0".Sum(value => ClientIdentity.Base36.IndexOf(value)) % 36) % 36],
        PeerIdTailKind.UTorrent => Checked(profile.PeerIdPrefix + "\u0001-._~!ÿ\u0080"[..(18 - profile.PeerIdPrefix.Length)]),
        PeerIdTailKind.Binary => profile.PeerIdPrefix + new string('\u0080', 20 - profile.PeerIdPrefix.Length),
        _ => profile.PeerIdPrefix + "-_.!~*()Ab9z0123456789"[..(20 - profile.PeerIdPrefix.Length)],
    };

    private static string Checked(string firstEighteen) => firstEighteen + ClientIdentity.UTorrentCheckBytes(firstEighteen);

    private static string Printable(byte[] bytes) => string.Concat(bytes.Select(value => value is >= 32 and < 127 && value != (byte)'\\'
        ? ((char)value).ToString() : "\\x" + value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));

    private static void Goldens()
    {
        using Stream? stream = typeof(ProfileSelfTest).Assembly.GetManifestResourceStream(GoldenResource);
        Require(stream is not null, "The request fixtures are not embedded.");
        using StreamReader reader = new(stream!, Encoding.UTF8);
        string expected = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        string actual = RenderGoldens();
        if (expected == actual) return;
        string[] left = expected.Split('\n'), right = actual.Split('\n');
        int line = Enumerable.Range(0, Math.Min(left.Length, right.Length)).FirstOrDefault(index => left[index] != right[index], Math.Min(left.Length, right.Length));
        throw new Exception("Fixture differs at line " + (line + 1) + ": expected '" + (line < left.Length ? left[line] : "<end>")
            + "' but produced '" + (line < right.Length ? right[line] : "<end>") + "'.");
    }

    internal static int WriteGoldens(string path)
    {
        File.WriteAllText(path, RenderGoldens(), new UTF8Encoding(false));
        Console.WriteLine("Wrote request fixtures to " + path);
        return 0;
    }

    private static ClientCatalogSnapshot Parse(string json) => ClientCatalog.ParseUserProfiles(Encoding.UTF8.GetBytes(json));

    private static byte[] Decode(string encoded)
    {
        using MemoryStream result = new();
        for (int index = 0; index < encoded.Length; index++)
        {
            char value = encoded[index];
            if (value != '%') result.WriteByte(checked((byte)value));
            else { result.WriteByte(Convert.ToByte(encoded.Substring(index + 1, 2), 16)); index += 2; }
        }
        return result.ToArray();
    }

    private static void InvalidFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RatioMaster-catalogue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "clients.json");
            foreach (string content in new[]
            {
                "{\"format\":1,\"unknown\":true,\"clients\":[]}",
                "{\"format\":1,\"clients\":[{\"family\":\"uTorrent\",\"version\":\"3.5.5\",\"numWant\":50}]}",
                "{\"format\":1,\"clients\":[{\"family\":\"X\",\"version\":\"1\",\"base\":\"uTorrent 3.5.5\"}]}",
            })
            {
                File.WriteAllText(path, content);
                Require(ClientCatalog.LoadUserProfiles(path) is { Length: > 0 } && ClientCatalog.DefaultFamily == "qBittorrent"
                    && ClientCatalog.Families.Any(family => family.Name == "Transmission"), "An invalid catalogue must be reported, never thrown: " + content);
            }
            File.WriteAllText(path, "{\"format\":1,\"clients\":[{\"family\":\"qBittorrent\",\"version\":\"5.2.4\",\"supportsV2\":false}]}");
            Require(ClientCatalog.LoadUserProfiles(path) is null && !ClientCatalog.Create("qBittorrent", "5.2.4").PeerWire.HybridUpgradeBit,
                "A construction without v2 must not offer the v2 upgrade.");
        }
        finally
        {
            ClientCatalog.LoadUserProfiles(Path.Combine(directory, "missing.json"));
            Directory.Delete(directory, recursive: true);
        }
        Require(ClientCatalog.LoadError is null && ClientCatalog.Create("qBittorrent", "5.2.4").PeerWire.HybridUpgradeBit, "A missing file restores the built-in catalogue.");
    }

    private static void ThrowsExactly<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException) { return; }
        throw new Exception(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
#endif
