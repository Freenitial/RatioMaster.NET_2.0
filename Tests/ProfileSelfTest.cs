#if DEBUG
namespace RatioMaster;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>Offline profile fixtures, byte encoding checks and declarative catalogue validation.</summary>
internal static class ProfileSelfTest
{
    internal static bool Run()
    {
        bool passed = true;
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("  [PASS] Profiles: " + name); }
            catch (Exception error) { passed = false; Console.WriteLine("  [FAIL] Profiles: " + name + " — " + error.Message); }
        }
        Check("preserved defaults, added versions and explicit unknown-profile fallback", Catalogue);
        Check("uTorrent and BitTorrent fixed build bytes and User-Agent fixtures", BinaryBuilds);
        Check("all byte values survive each percent-encoding policy", EncodingRoundTrips);
        Check("Transmission 4.0.6 hash encoding, key case and checksum", Transmission);
        Check("libtorrent punctuation alphabet and Latin-1 boundary", Libtorrent);
        Check("profile snapshots and concurrent generation remain independent", Isolation);
        Check("external profiles merge, inherit and select their default", ExternalProfiles);
        Check("invalid catalogue types, identities, headers and queries are rejected", InvalidProfiles);
        Check("catalogue depth and size limits", Limits);
        return passed;
    }

    private static void Catalogue()
    {
        Require(ClientCatalog.DefaultFamily == "qBittorrent" && ClientCatalog.DefaultVersion == "5.1.0", "Unexpected built-in default.");
        string[] expected =
        [
            "qBittorrent 5.1.0", "qBittorrent 5.0.3", "qBittorrent 4.6.7", "qBittorrent 4.6.5",
            "qBittorrent 5.2.3", "qBittorrent 5.1.4", "uTorrent 3.6.0", "uTorrent 3.5.5",
            "uTorrent 3.5.4", "Transmission 4.0.6", "Deluge 2.1.1", "BitTorrent 7.10.3 (44429)",
        ];
        string[] actual = ClientCatalog.Families.SelectMany(family => family.Versions.Select(version => family.Name + " " + version)).ToArray();
        Require(actual.SequenceEqual(expected), "Unexpected built-in catalogue.");
        Require(!ClientCatalog.TryCreate("missing", "0", out ClientProfile fallback), "Unknown profile reported present.");
        Require(fallback.Name == "qBittorrent 5.1.0", "Unknown profile did not use the default.");
        foreach (ClientFamily family in ClientCatalog.Families)
        foreach (string version in family.Versions)
        {
            ClientProfile profile = ClientCatalog.Create(family.Name, version);
            Require(profile.PeerID.Length == 20 && profile.PeerID.All(value => value <= 255), profile.Name + " peer ID is not 20 raw bytes.");
            Require(profile.Key.Length == 8 && profile.Key.All(value => "0123456789ABCDEF".Contains(value)), "Invalid built-in key.");
        }
    }

    private static void BinaryBuilds()
    {
        foreach ((string family, string version, string prefixHex, string agent) in new[]
        {
            ("uTorrent", "3.6.0", "2D5554333630532DECB6", "uTorrent/360(113358572)(46828)"),
            ("uTorrent", "3.5.5", "2D5554333535532DFAB3", "uTorrent/355(111916026)(46074)"),
            ("uTorrent", "3.5.4", "2D5554333534532DD2AD", "uTorrent/354(111783378)(44498)"),
            ("BitTorrent", "7.10.3 (44429)", "2D4254376133532D8DAD", "BitTorrent/7103(256355725)(44429)"),
        })
        {
            ClientProfile profile = ClientCatalog.Create(family, version);
            Require(Convert.ToHexString(Encoding.Latin1.GetBytes(profile.PeerID[..10])) == prefixHex, profile.Name + " build bytes differ.");
            Require(profile.PeerID[10..].All(value => value is >= (char)1 and <= (char)255), "Binary tail contains an out-of-range byte.");
            Require(profile.Headers.Contains("User-Agent: " + agent + "\r\n", StringComparison.Ordinal), "Missing build-specific User-Agent.");
            Require(profile.Headers.EndsWith("Connection: Close\r\n", StringComparison.Ordinal), "Incorrect Connection form.");
            Require(profile.KeyRefreshInterval == TimeSpan.FromMinutes(10), "Incorrect generated-key refresh policy.");
            string raw = profile.PeerID[..10] + "\0-_.!~*()\u00ff";
            Require(raw.Length == 20, "Bad fixture.");
            string encoded = profile.EncodePeerId(raw);
            Require(encoded.StartsWith(profile.PeerID[..8], StringComparison.Ordinal), "Literal peer prefix was escaped.");
            Require(encoded.EndsWith("%00%2d%5f%2e%21%7e%2a%28%29%ff", StringComparison.Ordinal), "Binary suffix escaped incorrectly.");
            Require(Decode(encoded).SequenceEqual(Encoding.Latin1.GetBytes(raw)), "Binary announce identity differs from handshake bytes.");
        }
    }

    private static void EncodingRoundTrips()
    {
        RandomStringGenerator generator = new();
        string input = new(Enumerable.Range(0, 256).Select(value => (char)value).ToArray());
        foreach (ClientUrlEncoding policy in Enum.GetValues<ClientUrlEncoding>())
        foreach (bool upper in new[] { false, true })
            Require(Decode(generator.UrlEncode(input, upper, policy)).SequenceEqual(Encoding.Latin1.GetBytes(input)), "Percent encoding lost a byte.");
        string punctuation = "-_.!~*()";
        Require(generator.UrlEncode(punctuation, false, ClientUrlEncoding.Libtorrent) == punctuation, "libtorrent safe punctuation changed.");
        Require(generator.UrlEncode(punctuation, true, ClientUrlEncoding.Rfc3986) == "-_.%21~%2A%28%29", "RFC3986 punctuation mismatch.");
        Require(generator.UrlEncode(punctuation, false, ClientUrlEncoding.Alphanumeric) == "%2d%5f%2e%21%7e%2a%28%29", "Alphanumeric policy mismatch.");
    }

    private static void Transmission()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            ClientProfile profile = ClientCatalog.Create("Transmission", "4.0.6");
            Require(profile.PeerID.StartsWith("-TR4060-", StringComparison.Ordinal), "Transmission prefix differs.");
            const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
            string tail = profile.PeerID[8..];
            Require(tail.All(alphabet.Contains), "Transmission alphabet differs.");
            Require(tail.Sum(value => alphabet.IndexOf(value)) % 36 == 0, "Transmission checksum failed.");
            Require(profile.HashUpperCase && profile.UrlEncoding == ClientUrlEncoding.Rfc3986, "Transmission hash policy differs.");
            Require(profile.KeyRefreshInterval == TimeSpan.Zero && !profile.SupportsV2, "Unsupported Transmission capability.");
        }
    }

    private static void Libtorrent()
    {
        const string expectedAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_.!~*()";
        Require(RandomStringGenerator.LibtorrentAlphabet == expectedAlphabet, "libtorrent generator excludes expected punctuation.");
        foreach (string family in new[] { "qBittorrent", "Deluge" })
        {
            ClientProfile profile = ClientCatalog.Create(family, family == "Deluge" ? "2.1.1" : "5.1.0");
            Require(profile.PeerID[8..].All(expectedAlphabet.Contains), "Unexpected libtorrent tail character.");
            Require(profile.SupportsV2, "libtorrent 2 profile lost v2 capability.");
        }
        Throws(() => new RandomStringGenerator().UrlEncode("\u20ac", false), "Unicode replacement must not alter identity bytes.");
        Require(new RandomStringGenerator().GenerateBytes(3, 255, 256) == "\u00ff\u00ff\u00ff", "Maximum random byte cannot be produced.");
        Require(new RandomStringGenerator().GenerateBytes(3, 0, 1) == "\0\0\0", "Minimum random byte cannot be produced.");
    }

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
              {"family":"qBittorrent","version":"5.1.0","numWant":0},
              {"family":"Example","version":"1.0","base":"qBittorrent 5.1.0","peerIdPrefixHex":"2D4558313030302D",
               "headers":["Host: {host}","User-Agent: Example/1.0","Accept-Encoding: gzip, deflate;q=0.8, br, identity"],
               "keyUpperCase":false,"keyRefreshSeconds":120}
            ]}
            """);
        Require(result.Definitions.Count == 13 && result.Default.Name == "Example 1.0", "Merge/default selection failed.");
        Require(result.Find("qBittorrent", "5.1.0")!.NumWant == 0, "Explicit zero was lost.");
        ClientProfile created = result.Default.Create();
        Require(created.PeerID.StartsWith("-EX1000-", StringComparison.Ordinal) && created.Key.All(value => "0123456789abcdef".Contains(value)), "Inherited profile generation failed.");
        Require(created.KeyRefreshInterval == TimeSpan.FromSeconds(120), "User rotation duration was lost.");
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
            "\"headers\":[\"Host: evil.invalid\",\"User-Agent: Test\"]",
            "\"headers\":[\"Host: {host}\",\"Host: {host}\",\"User-Agent: Test\"]",
            "\"headers\":[\"Host: {host}\",\"User-Agent: Test\\r\\nInjected: yes\"]",
            "\"headers\":[\"Host: {host}\",\"User-Agent: Test\",\"Content-Length: 0\"]",
            "\"headers\":[\"Host: {host}\",\"User-Agent: Test\",\"Accept-Encoding: unsupported\"]",
            "\"query\":\"info_hash={unknown}\"", "\"base\":\"No such profile\"",
        })
            Throws(() => Parse("{\"format\":1,\"clients\":[{\"family\":\"qBittorrent\",\"version\":\"5.1.0\"," + fields + "}]}"), "Accepted invalid profile: " + fields);
        Throws(() => Parse("{\"format\":1,\"format\":1,\"clients\":[]}"), "Duplicate JSON property accepted.");
        Throws(() => Parse("{\"format\":2,\"clients\":[]}"), "Unknown format accepted.");
        Throws(() => Parse("{\"format\":1,\"default\":\"Missing\",\"clients\":[]}"), "Unknown default accepted.");
        string query = ClientCatalog.Create("qBittorrent", "5.1.0").Query.Replace("numwant={numwant}", "numwant=200");
        Throws(() => Parse("{\"format\":1,\"clients\":[{\"family\":\"qBittorrent\",\"version\":\"5.1.0\",\"query\":\"" + query + "\"}]}"), "Literal numwant accepted.");
    }

    private static void Limits()
    {
        Throws(() => ClientCatalog.ParseUserProfiles(new byte[ClientCatalog.MaximumUserFileBytes + 1]), "Oversized catalogue accepted.");
        Throws(() => Parse(new string('[', 17) + new string(']', 17)), "Excessive nesting accepted.");
        string entries = string.Join(",", Enumerable.Range(0, 129).Select(index => "{\"family\":\"X\",\"version\":\"" + index + "\",\"base\":\"qBittorrent 5.1.0\"}"));
        Throws(() => Parse("{\"format\":1,\"clients\":[" + entries + "]}"), "Excessive profile count accepted.");
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
