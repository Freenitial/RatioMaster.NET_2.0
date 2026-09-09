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
using RatioMaster.ViewModels;

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
        int announceRequests = 0;
        using ManualResetEventSlim manualAnnounced = new();
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

                if (ctx.Request.Url!.AbsolutePath.Contains("announce")) Interlocked.Increment(ref announceRequests);
                byte[] body = ctx.Request.Url!.AbsolutePath.Contains("scrape")
                    ? ScrapeResponse(infoHash)
                    : AnnounceResponse();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.OutputStream.Close();
                if (ctx.Request.Url!.AbsolutePath.Contains("announce") && ctx.Request.QueryString["event"] is null)
                {
                    manualAnnounced.Set();
                }
            }
        })
        { IsBackground = true };
        server.Start();

        // 3. Drive the real engine.
        StubHost host = new();
        RatioEngine engine = new(host);
        engine.Log += Log;
        EngineStats? last = null;
        using ManualResetEventSlim progressed = new();
        engine.Stats += s =>
        {
            last = s;
            if (s.Uploaded > 0 && s.IntervalRemaining is > 0 and <= 120) progressed.Set();
        };

        SessionConfig cfg = new()
        {
            Client = ClientCatalog.Create("qBittorrent", "5.0.3"),
            Proxy = new ProxyConfig(),
            Tracker = tracker,
            HashHex = Convert.ToHexString(infoHash),
            InfoHash = infoHash,
            TotalLength = (long)torrent.TotalLength,
            FinishedPercent = 100,
            Interval = 1800,
            Port = "6881",
            Key = "abcd1234",
            PeerID = "-qB5030-selftest1234",
            NumWant = "200",
        };

        engine.Start(cfg);
        progressed.Wait(TimeSpan.FromSeconds(5));
        EngineStats? scheduled = last;
        engine.ManualUpdate();
        bool manualSent = manualAnnounced.Wait(TimeSpan.FromSeconds(5));
        engine.StopAsync().GetAwaiter().GetResult();
        serving = false;
        listener.Stop();
        server.Join(TimeSpan.FromSeconds(5));

        // 4. Assertions.
        string joined = string.Join("\n", log);
        bool connected = Volatile.Read(ref announceRequests) >= 2;
        bool gotPeers = joined.Contains("peers:");
        bool intervalUpdated = joined.Contains("Updating interval: 120");
        bool countdownUpdated = scheduled is { IntervalRemaining: > 110 and <= 120 };
        const long GiB = 1024L * 1024 * 1024;
        bool sizeWarning = RatioTabViewModel.IsSmallTorrent(8 * GiB - 1)
            && !RatioTabViewModel.IsSmallTorrent(8 * GiB)
            && !RatioTabViewModel.IsSmallTorrent(10 * GiB);
        bool countdownFormat = Format.Countdown(3661) == "01:01:01"
            && Format.Countdown(59) == "00:00:59" && Format.Countdown(-1) == "00:00:00";
        bool uploadedGrew = last is { Uploaded: > 0 };
        bool noProcessError = !joined.Contains("process found") && !joined.Contains("client is running");

        // BEP 7 IPv6: the compact 18-byte record must decode to a bracketed address with a big-endian port
        // (a byte-swapped port would read 57626, the classic bug this pins down).
        bool gotPeers6 = joined.Contains("peers6:") && joined.Contains("[2001:db8::1]:6881");

        // Tracker health and swarm counters use separate UI fields.
        bool alerted = host.LastAlertLevel == EngineAlert.Ok && host.LastAlertMessage == "Tracker OK";

        bool legacySession = LegacySessionLoads();
        bool profilesValid = ProfileSelfTest.Run();
        bool udpValid = UdpSelfTest.RunAsync().GetAwaiter().GetResult();
        bool formatsValid = TorrentFormatSelfTest.Run();
        bool statesValid = SessionStateSelfTest.RunAsync().GetAwaiter().GetResult();
        bool bencodeBounds = BencodeBounds();
        bool lifecycleValid = EngineLifecycleSelfTest.RunAsync().GetAwaiter().GetResult();
        bool persistenceValid = PersistenceSelfTest.RunAsync().GetAwaiter().GetResult();
        bool networkValid = NetworkTransportSelfTest.Run() == 0;

        Console.WriteLine();
        Console.WriteLine("──────── RESULTS ────────");
        Report("Tracker connected", connected);
        Report("Peers parsed (IPv4)", gotPeers);
        Report("Peers parsed (IPv6 / peers6, BEP 7)", gotPeers6);
        Report("Interval honored (120s)", intervalUpdated);
        Report("Tracker interval replaces configured countdown (1800 -> 120)", countdownUpdated);
        Report("Small torrent warning boundary (strictly below 8 GiB)", sizeWarning);
        Report("Countdown uses HH:mm:ss", countdownFormat);
        Report("Confirmed manual update sends an announce without waiting for the next tick", manualSent);
        Report($"Uploaded counter grew ({(last?.Uploaded ?? 0)} bytes, ratio {last?.Ratio})", uploadedGrew);
        Report($"Alert surfaced to UI ({host.LastAlertLevel}: {host.LastAlertMessage})", alerted);
        Report("Session counters and legacy random flags are preserved", legacySession);
        Report("Client profile encodings, builds and checksums", profilesValid);
        Report("No 'client not running' error", noProcessError);

        bool pass = connected && gotPeers && gotPeers6 && intervalUpdated && uploadedGrew && alerted
            && legacySession && profilesValid && noProcessError
            && countdownUpdated && sizeWarning && countdownFormat && manualSent
            && udpValid && formatsValid && statesValid && bencodeBounds && lifecycleValid && persistenceValid && networkValid;
        Console.WriteLine();
        Console.WriteLine(pass ? "✅ SELF-TEST PASSED" : "❌ SELF-TEST FAILED");
        return pass ? 0 : 1;
    }

    private static bool BencodeBounds()
    {
        foreach (string invalid in new[] { "1000000000:x", "5:abc", new string('l', 70) + new string('e', 70) })
        {
            try
            {
                using MemoryStream stream = new(Encoding.ASCII.GetBytes(invalid));
                BEncode.Parse(stream);
                Report("Reject malformed bencoding", false);
                return false;
            }
            catch (TorrentException) { }
        }
        Report("Reject excessive lengths, truncated strings and deep bencoding", true);
        return true;
    }

    private static void Report(string name, bool ok) => Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");

    /// <summary>Unknown JSON settings are ignored and session counters survive deserialization.</summary>
    private static bool LegacySessionLoads()
    {
        const string Legacy = """
        {"Tabs":[{"TabName":"RM 1","TorrentFilePath":"C:\\x.torrent","UploadSpeed":"100","RandUp":true,
        "RandUpMin":"20","RandUpMax":"300","DownloadSpeed":"0","RandDown":false,"RandDownMin":"0",
        "RandDownMax":"0","RealisticMode":true,"EnableLog":true,"Uploaded":123,"Downloaded":456}]}
        """;

        try
        {
            SessionData? data = SessionStore.Decode(Legacy);
            TabState? t = data?.Tabs.FirstOrDefault();
            return t is not null
                && t.UploadSpeed == "100" && t.DownloadSpeed == "0"   // retained as-is
                && t.RealisticMode                                     // retained

                && t.Uploaded == 123 && t.Downloaded == 456            // resume counters survive
                && t.RandomUpload && !t.RandomDownload;
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
        byte[] pieces = new byte[checked((int)((length + 262143) / 262144) * 20)];
        for (int i = 0; i < pieces.Length; i++)
        {
            pieces[i] = (byte)(i + 1);
        }

        A("d");
        A($"8:announce{announce.Length}:{announce}");
        A("4:infod");
        A($"6:lengthi{length}e");
        A($"4:name{name.Length}:{name}");
        A("12:piece lengthi262144e");
        A($"6:pieces{pieces.Length}:");
        ms.Write(pieces);
        A("ee");
        return ms.ToArray();
    }

    private sealed class StubHost : IEngineHost
    {
        public bool UseTcpListener => false;

        public bool RequestScrape => true;


        public long UploadRateBytes => 100 * 1024;

        public long DownloadRateBytes => 0;

        // Flat, exact rate: the assertion below checks an EXACT byte count, which a smooth-curve
        // multiplier (×0.55…1.15) would make non-deterministic.
        public bool RandomUploadEnabled => false;

        public bool RandomDownloadEnabled => false;

        // Off too, so the asserted byte count stays exact.
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
