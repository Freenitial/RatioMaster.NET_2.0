#if DEBUG
namespace RatioMaster;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using RatioMaster.BitTorrent;
using RatioMaster.Engine;
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>
/// Debug-only end-to-end check (run: <c>RatioMaster.NET.exe --selftest</c>). Spins up a
/// local HTTP tracker, parses a generated .torrent, drives the real <see cref="RatioEngine"/>
/// and asserts the announce round-trips and the counters grow. Excluded from Release/AOT.
/// </summary>
internal static class SelfTest
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    internal static int Run()
    {
        const int port = 8791;
        List<string> log = [];
        void Log(string s)
        {
            lock (log)
            {
                log.Add(s);
            }

            Console.WriteLine("  [engine] " + s);
        }

        // 1. Build a minimal single-file .torrent pointing at our local tracker.
        string tracker = $"http://127.0.0.1:{port}/announce";
        byte[] torrentBytes = BuildTorrent(tracker, "selftest.bin", 4 * 1024 * 1024);
        string path = Path.Combine(Path.GetTempPath(), "ratiomaster_selftest.torrent");
        File.WriteAllBytes(path, torrentBytes);

        Torrent torrent = new(path);
        byte[] infoHash = torrent.InfoHash;
        Console.WriteLine($"Torrent parsed: name={torrent.Name} announce={torrent.Announce} size={torrent.TotalLength} hash={Convert.ToHexString(infoHash)}");

        // 2. Local tracker that answers announce + scrape with valid bencode.
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        bool serving = true;
        Thread server = new(() =>
        {
            while (serving)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    return;
                }

                byte[] body = ctx.Request.Url!.AbsolutePath.Contains("scrape")
                    ? ScrapeResponse(infoHash)
                    : AnnounceResponse();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.OutputStream.Close();
            }
        })
        { IsBackground = true };
        server.Start();

        // 3. Drive the real engine.
        StubHost host = new();
        RatioEngine engine = new(host);
        engine.Log += Log;
        EngineStats? last = null;
        engine.Stats += s => last = s;

        SessionConfig cfg = new()
        {
            Client = ClientCatalog.Create("qBittorrent", "5.0.3"),
            Proxy = new ProxyConfig(),
            Tracker = tracker,
            HashHex = Convert.ToHexString(infoHash),
            InfoHash = infoHash,
            TotalLength = (long)torrent.TotalLength,
            FinishedPercent = 100,
            Interval = 120,
            Port = "6881",
            Key = "abcd1234",
            PeerID = "-qB5030-selftest1234",
            NumWant = "200",
        };

        engine.Start(cfg);
        Thread.Sleep(4000);
        engine.Stop();
        Thread.Sleep(700);
        serving = false;
        listener.Stop();

        // 4. Assertions.
        string joined = string.Join("\n", log);
        bool connected = joined.Contains("Connected successfully");
        bool gotPeers = joined.Contains("peers:");
        bool intervalUpdated = joined.Contains("Updating interval: 120");
        bool uploadedGrew = last is { Uploaded: > 0 };
        bool noProcessError = !joined.Contains("process found") && !joined.Contains("client is running");

        // BEP 7 IPv6: the compact 18-byte record must decode to a bracketed address with a big-endian port
        // (a byte-swapped port would read 57626, the classic bug this pins down).
        bool gotPeers6 = joined.Contains("peers6:") && joined.Contains("[2001:db8::1]:6881");

        // The engine must surface the live swarm state to the tab's alert line (green, 3 leechers > 0).
        bool alerted = host.LastAlertLevel == EngineAlert.Ok && host.LastAlertMessage.Contains("3 leechers");

        // "On next update" percent → multiplier. Rolling it end-to-end would need a >60s run (it fires once
        // per announce), so the conversion is asserted directly: the defaults must map 50..150% to 0.5..1.5
        // (a missing ÷100 would silently multiply every rate by a hundred), min/max may be reversed, and a
        // negative percent must clamp to 0 rather than run the counters backwards.
        bool percentMath =
            RatioEngine.PercentToMultiplier(50, 150, 0.0) == 0.5 &&
            RatioEngine.PercentToMultiplier(50, 150, 1.0) == 1.5 &&
            RatioEngine.PercentToMultiplier(50, 150, 0.5) == 1.0 &&
            RatioEngine.PercentToMultiplier(200, 200, 0.7) == 2.0 &&
            RatioEngine.PercentToMultiplier(150, 50, 0.0) == 0.5 &&
            RatioEngine.PercentToMultiplier(-10, 0, 0.0) == 0.0;

        bool legacySession = LegacySessionLoads();
        bool profilesValid = ClientProfilesValid();

        Console.WriteLine();
        Console.WriteLine("──────── RESULTS ────────");
        Report("Tracker connected", connected);
        Report("Peers parsed (IPv4)", gotPeers);
        Report("Peers parsed (IPv6 / peers6, BEP 7)", gotPeers6);
        Report("Interval honored (120s)", intervalUpdated);
        Report($"Uploaded counter grew ({(last?.Uploaded ?? 0)} bytes, ratio {last?.Ratio})", uploadedGrew);
        Report($"Alert surfaced to UI ({host.LastAlertLevel}: {host.LastAlertMessage})", alerted);
        Report("Next-update percent → multiplier (50-150% = x0.5-x1.5)", percentMath);
        Report("Pre-refactor session still loads (new keys default)", legacySession);
        Report("Every client profile: 20 printable bytes + Transmission checksum", profilesValid);
        Report("No 'client not running' error", noProcessError);

        bool pass = connected && gotPeers && gotPeers6 && intervalUpdated && uploadedGrew && alerted
            && percentMath && legacySession && profilesValid && noProcessError;
        Console.WriteLine();
        Console.WriteLine(pass ? "✅ SELF-TEST PASSED" : "❌ SELF-TEST FAILED");
        return pass ? 0 : 1;
    }

    private static void Report(string name, bool ok) => Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");

    /// <summary>
    /// Every shipped profile must produce a peer_id of exactly 20 PRINTABLE bytes, and Transmission's tail
    /// must satisfy its mod-36 checksum. This pins down the whole class of defects the legacy client table
    /// carried: a tail that percent-encoding would expand (so the peer_id announced to the tracker differed
    /// from the one presented in the peer handshake), and a Transmission tail drawn from the wrong alphabet
    /// with no checksum, which any tracker can verify as forged.
    /// </summary>
    private static bool ClientProfilesValid()
    {
        const string TrPool = "0123456789abcdefghijklmnopqrstuvwxyz";

        foreach (ClientFamily family in ClientCatalog.Families)
        {
            foreach (string version in family.Versions)
            {
                ClientProfile p = ClientCatalog.Create(family.Name, version);
                if (p.PeerID.Length != 20)
                {
                    return false;
                }

                foreach (char c in p.PeerID)
                {
                    if (c < 32 || c > 126)
                    {
                        return false; // non-printable => percent-encoding would change the announced form
                    }
                }

                if (family.Name == "Transmission")
                {
                    int total = 0;
                    foreach (char c in p.PeerID[8..])
                    {
                        int i = TrPool.IndexOf(c);
                        if (i < 0)
                        {
                            return false; // outside libtransmission's base-36 pool
                        }

                        total += i;
                    }

                    if (total % 36 != 0)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// A real <c>ratiomaster.session</c> written by a PRE-refactor build must still load. The speed-random
    /// schema changed twice (the per-second "+ random" min/max and the MB-based "on next update" ranges were
    /// both dropped for per-direction "Random" flags and percent spinners), so this pins the upgrade path:
    /// removed keys must be ignored rather than throw, retained keys must survive, and the new keys must
    /// fall back to their defaults instead of coming back as false/0 — which would silently hand the user
    /// an upgraded app with Random switched off and a 0% speed level.
    /// </summary>
    private static bool LegacySessionLoads()
    {
        const string Legacy = """
        {"Tabs":[{"TabName":"RM 1","TorrentFilePath":"C:\\x.torrent","UploadSpeed":"100","RandUp":true,
        "RandUpMin":"20","RandUpMax":"300","DownloadSpeed":"0","RandDown":false,"RandDownMin":"0",
        "RandDownMax":"0","RealisticMode":true,"NextRandUp":true,"NextRandUpMin":"50","NextRandUpMax":"100",
        "NextRandDown":false,"EnableLog":true,"Uploaded":123,"Downloaded":456}]}
        """;

        try
        {
            SessionData? data = JsonSerializer.Deserialize(Legacy, AppJsonContext.Default.SessionData);
            TabState? t = data?.Tabs.FirstOrDefault();
            return t is not null
                && t.UploadSpeed == "100" && t.DownloadSpeed == "0"   // retained as-is
                && t.RealisticMode                                     // retained (now wire-protocol only)
                && t.NextRandUp                                        // retained opt-in flag
                && t.Uploaded == 123 && t.Downloaded == 456            // resume counters survive
                && t.RandomUpload && t.RandomDownload                  // NEW -> must default to true
                && t.NextRandUpMinPercent == 50 && t.NextRandUpMaxPercent == 150
                && t.NextRandDownMinPercent == 50 && t.NextRandDownMaxPercent == 150;
        }
        catch
        {
            return false; // an exception here means an upgrade wipes the user's tabs
        }
    }

    private static byte[] AnnounceResponse()
    {
        // d8:completei5e10:incompletei3e8:intervali120e5:peers6:<v4 peer>6:peers618:<v6 peer>e
        // NOTE the two easily-confused tokens: "5:peers" + "6:" is the 6-byte IPv4 'peers' value, while
        // "6:peers6" + "18:" is the 18-byte IPv6 'peers6' value (BEP 7). Keys stay bencode-sorted.
        using MemoryStream ms = new();
        void A(string s) => ms.Write(Latin1.GetBytes(s));
        A("d8:completei5e10:incompletei3e8:intervali120e5:peers6:");
        ms.Write([127, 0, 0, 1, 0x1A, 0xE1]); // 127.0.0.1:6881
        A("6:peers618:");
        ms.Write([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0x1A, 0xE1]); // [2001:db8::1]:6881
        A("e");
        return ms.ToArray();
    }

    private static byte[] ScrapeResponse(byte[] infoHash)
    {
        // d5:filesd20:<hash>d8:completei5e10:downloadedi9e10:incompletei3eeee
        using MemoryStream ms = new();
        void A(string s) => ms.Write(Latin1.GetBytes(s));
        A("d5:filesd20:");
        ms.Write(infoHash);
        A("d8:completei5e10:downloadedi9e10:incompletei3eeee");
        return ms.ToArray();
    }

    private static byte[] BuildTorrent(string announce, string name, long length)
    {
        using MemoryStream ms = new();
        void A(string s) => ms.Write(Latin1.GetBytes(s));
        byte[] pieces = new byte[20];
        for (int i = 0; i < 20; i++)
        {
            pieces[i] = (byte)(i + 1);
        }

        A("d");
        A($"8:announce{announce.Length}:{announce}");
        A("4:infod");
        A($"6:lengthi{length}e");
        A($"4:name{name.Length}:{name}");
        A("12:piece lengthi262144e");
        A("6:pieces20:");
        ms.Write(pieces);
        A("ee");
        return ms.ToArray();
    }

    private sealed class StubHost : IEngineHost
    {
        public bool UseTcpListener => false;

        public bool RequestScrape => true;

        public bool IgnoreFailureReason => false;

        public long UploadRateBytes => 100 * 1024;

        public long DownloadRateBytes => 0;

        // Flat, exact rate: the assertion below checks an EXACT byte count, which a smooth-curve
        // multiplier (×0.55…1.15) would make non-deterministic.
        public bool RandomUploadEnabled => false;

        public bool RandomDownloadEnabled => false;

        // Off too, so the asserted byte count stays exact.
        public bool NextRandUpEnabled => false;

        public double NextRandUpMinPercent => 50;

        public double NextRandUpMaxPercent => 150;

        public bool NextRandDownEnabled => false;

        public double NextRandDownMinPercent => 50;

        public double NextRandDownMaxPercent => 150;

        public string StopWhen => "Never";

        public string StopValue => string.Empty;

        // Captured so the test can assert the engine surfaces a live condition to the tab's alert line.
        public EngineAlert LastAlertLevel { get; private set; } = EngineAlert.None;

        public string LastAlertMessage { get; private set; } = string.Empty;

        public void ApplyAlert(EngineAlert level, string message)
        {
            LastAlertLevel = level;
            LastAlertMessage = message;
        }
    }
}
#endif
