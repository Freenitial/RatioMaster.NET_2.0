#if DEBUG
namespace RatioMaster;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RatioMaster.BitTorrent;
using RatioMaster.Engine;
using RatioMaster.Services;
using RatioMaster.Models;

/// <summary>Deterministic session transitions and loopback-only listener isolation.</summary>
internal static class EngineLifecycleSelfTest
{
    internal static async Task<bool> RunAsync()
    {
        bool passed = true;
        async Task Check(string name, Func<Task> scenario)
        {
            try
            {
                await scenario().ConfigureAwait(false);
                Console.WriteLine("  [PASS] " + name);
            }
            catch (Exception ex)
            {
                passed = false;
                Console.WriteLine("  [FAIL] " + name + ": " + ex);
            }
        }

        await Check("failed initial announce retries started without growing counters", RetryStartedAsync);
        await Check("initial progress preserves the full signed torrent-size range", InitialProgressAsync);
        await Check("peer log summaries bound allocation and preserve counts and byte order", PeerSummariesAsync);
        await Check("interval bounds and accepted minimum survive transport errors", IntervalBoundsAsync);
        await Check("tracker minima beyond one day delay regular announces until their deadline", LongTrackerMinimumAsync);
        await Check("unrepresentable tracker delays stop without an early automatic retry", UnsupportedTrackerDelayAsync);
        await Check("completion precedes an objective stop and duration freezes", CompletionBeforeStopAsync);
        await Check("final completion or stop failure cannot mark an objective successful", FinalFailureAsync);
        await Check("failed completion retries completed before returning to regular announces", RetryCompletedAsync);
        await Check("tracker rejection stops without a retry or final announce", RejectAsync);
        await Check("stop cancels and serializes an in-flight announce", CancellationAsync);
        await Check("pause, counters and stop remain independent between engines", IndependentSessionsAsync);
        await Check("swarm ratio needs known counts and a nonzero denominator", SwarmRatioAsync);
        await Check("key rotation applies profile deadlines to announces and preserves custom values", KeyRotationAsync);
        await Check("listener collision, hash isolation and live cancellation", ListenerIsolationAsync);
        await Check("HTTP fragment removal, scrape, tracker ID, warnings and decode diagnostics", TrackerMetadataAsync);
        await Check("a refused completed is retried while running, and final after repeated refusals", RefusedCompletionAsync);
        await Check("a pending completed is sent before stopped when the user stops", ManualStopCompletionAsync);
        await Check("announces report the transfer of this session, not the totals of earlier ones", SessionCountersAsync);
        await Check("public address parameters follow the profile and are escaped like its other values", AddressParametersAsync);
        await Check("regular and manual announce timing follow each client's own defaults", ClientTimingAsync);
        await Check("stopped is only sent to a tracker that accepted started", StoppedNeedsStartedAsync);
        await Check("a tracker keeping the connection open answers a request without Connection: close", KeepAliveTrackerAsync);
        await Check("a refused scrape ends scraping, not the session", ScrapeRefusalAsync);
        return passed;
    }

    private static async Task InitialProgressAsync()
    {
        foreach ((double percent, long expected) in new[] { (0d, 0L), (50d, long.MaxValue / 2), (100d, long.MaxValue) })
        {
            RatioEngine engine = new(new Host(), new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false);
            try
            {
                engine.Start(Configuration(total: long.MaxValue, finished: percent));
                await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
                EngineStats stats = engine.GetStats();
                Require(stats.Have == expected && stats.Left == long.MaxValue - expected && stats.Downloaded == 0,
                    "Initial progress must not lose bytes or overflow at the engine size limit, and is not downloaded data.");
            }
            finally { await engine.StopAsync(); }
        }

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            RatioEngine engine = new(new Host(), new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false);
            bool rejected = false;
            try { engine.Start(Configuration(finished: invalid)); }
            catch (ArgumentException) { rejected = true; }
            finally { await engine.StopAsync(); }
            Require(rejected && !engine.IsRunning, "A nonfinite percentage must be rejected before the session starts.");
        }
    }

    private static Task PeerSummariesAsync()
    {
        const int count = 100000;
        foreach (int addressBytes in new[] { 4, 16 })
        {
            int stride = addressBytes + 2;
            byte[] bytes = new byte[count * stride];
            for (int offset = 0; offset < bytes.Length; offset += stride)
            {
                bytes[offset + addressBytes - 1] = 1;
                bytes[offset + addressBytes] = 0xC8;
                bytes[offset + addressBytes + 1] = 0xD5;
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            string summary = PeerList.FormatCompact(bytes, addressBytes);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(summary.StartsWith("(100000) ", StringComparison.Ordinal)
                && summary.Count(character => character == ';') == 5
                && summary.Contains(addressBytes == 4 ? "0.0.0.1:51413" : "[::1]:51413", StringComparison.Ordinal),
                "Compact peer logging must keep the total count and five correctly decoded examples.");
            Require(allocated < 65536, "Compact peer summaries must not allocate an object for every returned peer.");
        }
        ValueList dictionaryPeers = new();
        for (int index = 0; index < 7; index++)
        {
            ValueDictionary peer = new();
            peer.SetStringValue("ip", "127.0.0.1");
            peer["port"] = new ValueNumber(51413);
            dictionaryPeers.Add(peer);
        }
        dictionaryPeers.Add(new ValueNumber(0));
        string dictionarySummary = PeerList.FormatDictionary(dictionaryPeers);
        Require(dictionarySummary.StartsWith("(7) ", StringComparison.Ordinal)
            && dictionarySummary.Count(character => character == ';') == 5
            && dictionarySummary.Contains("127.0.0.1:51413", StringComparison.Ordinal),
            "Dictionary peer summaries must count valid entries and retain host-order ports.");
        return Task.CompletedTask;
    }

    private static async Task RetryStartedAsync()
    {
        ManualClock clock = new();
        ConcurrentQueue<string> events = new();
        int attempts = 0;
        RatioEngine engine = new(new Host(), clock, (name, _) =>
        {
            events.Enqueue(name);
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture transport failure");
            return Task.FromResult(Response());
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration());
            await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count == 1);
            Require(engine.GetStats().IntervalRemaining == 60, "Initial failure must schedule a short retry.");
            clock.Advance(59);
            engine.Tick();
            Require(events.Count == 1 && engine.GetStats().Uploaded == 0, "No early retry or upload before acceptance.");
            clock.Advance(1);
            engine.Tick();
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(events.ToArray().SequenceEqual(new[] { "started", "started" }), "The first accepted announce must remain started.");
            Require(engine.GetStats().Uploaded == 0, "The failed interval must not become uploaded bytes.");
            clock.Advance(2);
            engine.Tick();
            Require(engine.GetStats().Uploaded == 200, "Accepted elapsed time must accumulate exactly.");
        }
        finally { await engine.StopAsync(); }
    }

    private static async Task IntervalBoundsAsync()
    {
        ManualClock clock = new();
        int mode = 0;
        RatioEngine engine = new(new Host(), clock, (_, _) =>
        {
            if (Volatile.Read(ref mode) == 2) throw new IOException("fixture retry");
            return Task.FromResult(Volatile.Read(ref mode) == 0 ? Response(interval: 999999, minimum: 172800) : Response(interval: 1, minimum: 400));
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration(interval: int.MinValue));
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(engine.GetStats().IntervalRemaining == 999999, "A supported tracker interval must not be shortened to the configurable one-day limit.");
            Volatile.Write(ref mode, 1);
            Require(!await engine.ManualUpdateAsync() && engine.GetStats().ManualUpdateWait == 172800,
                "A manual update must wait for the tracker's minimum interval.");
            clock.Advance(172800);
            Require(await engine.ManualUpdateAsync(), "Manual update must accept the fixture response.");
            Require(engine.GetStats().IntervalRemaining == 400, "The tracker minimum must dominate a smaller interval and the client's floor.");
            Volatile.Write(ref mode, 2);
            clock.Advance(400);
            Require(!await engine.ManualUpdateAsync(), "A transport failure must not report acceptance.");
            Require(engine.IsRunning && engine.GetStats().IntervalRemaining == 60,
                "Once the tracker minimum has elapsed, a transport failure schedules the short retry without stopping.");
            Volatile.Write(ref mode, 1);
        }
        finally { Volatile.Write(ref mode, 1); await engine.StopAsync(); }
    }

    private static async Task LongTrackerMinimumAsync()
    {
        foreach (int minimum in new[] { 172800, 999999 })
        {
            ManualClock clock = new();
            ConcurrentQueue<string> events = new();
            RatioEngine engine = new(new Host(), clock, (name, _) =>
            {
                events.Enqueue(name);
                return Task.FromResult(Response(minimum: minimum));
            }, automaticTicks: false);
            try
            {
                engine.Start(Configuration());
                await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
                Require(engine.GetStats().IntervalRemaining == minimum, "The full tracker minimum must be retained.");
                clock.Advance(86400);
                engine.Tick();
                Require(events.Count == 1, "The configurable one-day limit must not trigger an early announce.");
                clock.Advance(minimum - 86400 - 1);
                engine.Tick();
                Require(events.Count == 1 && engine.GetStats().IntervalRemaining == 1, "No automatic announce may occur before the tracker deadline.");
                clock.Advance(1);
                engine.Tick();
                await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count == 2);
                Require(events.ToArray().SequenceEqual(new[] { "started", string.Empty }), "A regular announce must be scheduled once at the full deadline.");
            }
            finally { await engine.StopAsync(); }
        }
    }

    private static async Task UnsupportedTrackerDelayAsync()
    {
        foreach ((string field, string value) in new[]
        {
            ("interval", "2147483648"), ("min interval", "999999999999999999999999999"),
        })
        {
            ManualClock clock = new();
            ConcurrentQueue<string> events = new();
            ConcurrentQueue<string> errors = new();
            TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            RatioEngine engine = new(new Host(), clock, (name, _) =>
            {
                events.Enqueue(name);
                ValueDictionary response = Response();
                response.SetStringValue(field, value);
                return Task.FromResult(response);
            }, automaticTicks: false);
            engine.Alert += (level, message) => { if (level == EngineAlert.Error) errors.Enqueue(message); };
            engine.Stopped += _ => stopped.TrySetResult();
            try
            {
                engine.Start(Configuration());
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await engine.StopAsync();
                clock.Advance(999999);
                engine.Tick();
                Require(errors.Any(message => message.Contains("supported integer-second range", StringComparison.Ordinal)), "An unsupported delay must expose a clear scheduling error.");
                Require(!engine.IsRunning && events.ToArray().SequenceEqual(new[] { "started", "stopped" }), "Unsupported delays may send a final stopped event but never an early automatic retry.");
            }
            finally { await engine.StopAsync(); }
        }
    }

    private static async Task CompletionBeforeStopAsync()
    {
        ManualClock clock = new();
        ConcurrentQueue<string> events = new();
        TaskCompletionSource stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource acceptStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Host host = new() { DownloadRateBytes = 10, StopWhen = "When downloaded >", StopValue = "0" };
        RatioEngine engine = new(host, clock, async (name, ct) =>
        {
            events.Enqueue(name);
            if (name == "stopped")
            {
                stopping.TrySetResult();
                await acceptStop.Task.WaitAsync(ct);
            }
            return Response();
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration(total: 100, finished: 0));
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            clock.Advance(10);
            engine.Tick();
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!engine.FinishedSuccessfully, "A pending final announce must not expose success to session persistence.");
            acceptStop.TrySetResult();
            await engine.StopAsync();
            Require(events.ToArray().SequenceEqual(new[] { "started", "completed", "stopped" }), "Completion and stop must be ordered exactly once.");
            Require(engine.FinishedSuccessfully && engine.GetStats().Downloaded == 100, "A complete successful objective must preserve its bytes.");
            EngineStats frozen = engine.GetStats();
            clock.Advance(100);
            engine.Tick();
            Require(engine.GetStats().TotalRunSeconds == frozen.TotalRunSeconds && frozen.TotalRunSeconds == 10, "Duration must freeze at Stop.");
            Require(engine.GetStats().Uploaded == frozen.Uploaded, "Stopped counters must remain fixed.");
        }
        finally { acceptStop.TrySetResult(); await engine.StopAsync(); }
    }

    private static async Task RetryCompletedAsync()
    {
        ManualClock clock = new();
        ConcurrentQueue<string> events = new();
        int completed = 0;
        RatioEngine engine = new(new Host { DownloadRateBytes = 100 }, clock, (name, _) =>
        {
            events.Enqueue(name);
            if (name == "completed" && Interlocked.Increment(ref completed) == 1) throw new IOException("fixture completion timeout");
            return Task.FromResult(Response());
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration(total: 100, finished: 0));
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            clock.Advance(1);
            engine.Tick();
            await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count == 2);
            Require(engine.IsRunning && engine.GetStats().IntervalRemaining == 60, "Failed completion must keep the session recoverable.");
            clock.Advance(60);
            engine.Tick();
            await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count == 3);
            Require(events.ToArray().SequenceEqual(new[] { "started", "completed", "completed" }), "Completed must remain pending until accepted.");
            await engine.ManualUpdateAsync();
            Require(events.Last() == string.Empty, "Accepted completion must return to regular announces.");
        }
        finally { await engine.StopAsync(); }
    }

    private static async Task FinalFailureAsync()
    {
        foreach (string rejectedEvent in new[] { "completed", "stopped" })
        {
            ManualClock clock = new();
            ConcurrentQueue<string> events = new();
            Host host = new() { DownloadRateBytes = 100, StopWhen = "When downloaded >", StopValue = "0" };
            RatioEngine engine = new(host, clock, (name, _) =>
            {
                events.Enqueue(name);
                if (name == rejectedEvent) throw new TrackerRejectedException("fixture final rejection");
                return Task.FromResult(Response());
            }, automaticTicks: false);
            try
            {
                engine.Start(Configuration(total: 100, finished: 0));
                await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
                clock.Advance(1);
                engine.Tick();
                await engine.StopAsync();
                Require(!engine.FinishedSuccessfully && engine.StopError.Contains("fixture final rejection", StringComparison.Ordinal), "A rejected final event must expose failure.");
                // A refused completed still leaves the swarm properly; a refused stopped is the last word.
                Require(events.ToArray().SequenceEqual(new[] { "started", "completed", "stopped" }),
                    "A refused completed must still be followed by stopped, and nothing may follow a refused stopped.");
            }
            finally { await engine.StopAsync(); }
        }
    }

    private static async Task RejectAsync()
    {
        ConcurrentQueue<string> events = new();
        RatioEngine engine = new(new Host(), new ManualClock(), (name, _) =>
        {
            events.Enqueue(name);
            ValueDictionary response = new();
            response.SetStringValue("failure reason", "fixture rejection");
            return Task.FromResult(response);
        }, automaticTicks: false);
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stopped += _ => stopped.TrySetResult();
        try
        {
            engine.Start(Configuration());
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await engine.StopAsync();
            Require(!engine.IsRunning && !engine.FinishedSuccessfully, "Tracker rejection must end the session.");
            Require(events.ToArray().SequenceEqual(new[] { "started" }), "Rejection must not trigger a retry or stopped announce.");
        }
        finally { await engine.StopAsync(); }
    }

    private static async Task CancellationAsync()
    {
        ManualClock clock = new();
        ConcurrentQueue<string> events = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0, maximum = 0;
        RatioEngine engine = new(new Host(), clock, async (name, ct) =>
        {
            events.Enqueue(name);
            int count = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, count);
            try
            {
                if (name.Length == 0)
                {
                    entered.TrySetResult();
                    TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    try { await blocked.Task.WaitAsync(ct); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                }
                return Response();
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration());
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Task<bool> manual = engine.ManualUpdateAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!await engine.ManualUpdateAsync(), "A concurrent manual request must be refused.");
            clock.Advance(1000);
            engine.Tick();
            Task first = engine.StopAsync();
            Require(ReferenceEquals(first, engine.StopAsync()), "Stop callers must share one completion task.");
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Require(cancelled.Task.IsCompletedSuccessfully && !await manual, "Stop must cancel the pending manual announce.");
            Require(maximum == 1 && events.ToArray().SequenceEqual(new[] { "started", string.Empty, "stopped" }), "Final notification must follow cancellation with no overlap.");
        }
        finally { await engine.StopAsync(); }
    }

    private static async Task IndependentSessionsAsync()
    {
        ManualClock clock = new();
        Host firstHost = new(), secondHost = new() { UploadRateBytes = 300 };
        RatioEngine first = new(firstHost, clock, (_, _) => Task.FromResult(Response(leechers: 0)), automaticTicks: false);
        RatioEngine second = new(secondHost, clock, (_, _) => Task.FromResult(Response()), automaticTicks: false);
        try
        {
            first.Start(Configuration());
            second.Start(Configuration(hash: Enumerable.Repeat((byte)2, 20).ToArray()));
            await WaitStats(first, stats => !stats.IsAnnouncing && stats.UploadPausedNoLeechers);
            await WaitStats(second, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            first.Pause();
            Require(first.GetStats() is { IsPaused: true, AlertMessage: "Paused by you." }, "Pause state and displayed alert must share one snapshot.");
            clock.Advance(2);
            first.Tick();
            second.Tick();
            Require(first.GetStats().Uploaded == 0 && second.GetStats().Uploaded == 600, "Leecher and user pauses must not cross session boundaries.");
            first.Resume();
            Require(first.GetStats().UploadPausedNoLeechers, "Resume must preserve the independent leecher pause.");
            Require(first.GetStats().AlertMessage == "Paused: no leechers.", "Resuming user pause must expose the current automatic pause alert.");
            await first.StopAsync();
            clock.Advance(1);
            second.Tick();
            Require(second.IsRunning && second.GetStats().Uploaded == 900, "Stopping a neighbor must preserve the active session.");
        }
        finally { await first.StopAsync(); await second.StopAsync(); }
    }

    private static async Task KeyRotationAsync()
    {
        foreach (bool generated in new[] { true, false })
        {
            ManualClock clock = new();
            SessionConfig config = Configuration(generatedKey: generated);
            config.Client.KeyRefreshInterval = TimeSpan.FromMinutes(10);
            int changes = 0;
            RatioEngine engine = new(new Host(), clock, (_, _) => Task.FromResult(Response()), automaticTicks: false);
            engine.KeyChanged += _ => Interlocked.Increment(ref changes);
            try
            {
                engine.Start(config);
                await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
                clock.Advance(599);
                await engine.ManualUpdateAsync();
                Require(changes == 0, "Keys must remain stable before the profile deadline.");
                clock.Advance(1);
                await engine.ManualUpdateAsync();
                Require(changes == (generated ? 1 : 0), "Only generated keys may follow the profile rotation policy.");
                clock.Advance(600);
                await engine.StopAsync();
                Require(changes == (generated ? 2 : 0), "A final announce must apply the same generated-key deadline as regular announces.");
                Require(generated || engine.CurrentKey == config.Key, "An explicit custom key must remain untouched.");
            }
            finally { await engine.StopAsync(); }
        }
    }

    private static async Task SwarmRatioAsync()
    {
        foreach ((int? seeds, int? peers, bool expectedStop) in new (int?, int?, bool)[]
        {
            (null, null, false), (null, 1, false), (4, null, false), (0, 0, false), (4, 2, false), (4, 1, true),
        })
        {
            ManualClock clock = new();
            ConcurrentQueue<string> events = new();
            Host host = new() { StopWhen = "When leechers / seeders <", StopValue = "0.5" };
            RatioEngine engine = new(host, clock, (name, _) =>
            {
                events.Enqueue(name);
                return Task.FromResult(Response(seeders: seeds, leechers: peers));
            }, automaticTicks: false);
            try
            {
                engine.Start(Configuration());
                await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count == 1);
                engine.Tick();
                Require(engine.IsRunning != expectedStop, "Only known counts with seeders > 0 may reach a strict ratio threshold.");
                if (expectedStop)
                {
                    await engine.StopAsync();
                    Require(engine.FinishedSuccessfully, "A known ratio below the threshold must stop successfully.");
                }
            }
            finally { await engine.StopAsync(); }
        }
    }

    private static async Task ListenerIsolationAsync()
    {
        int port = FreePort();
        PeerListenerHub hub = new();
        Host firstHost = new() { UseTcpListener = true };
        RatioEngine first = new(firstHost, new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false, listenerHub: hub);
        RatioEngine second = new(new Host { UseTcpListener = true }, new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false, listenerHub: hub);
        RatioEngine twin;
        SessionConfig firstConfig = Configuration(port: port);
        SessionConfig secondConfig = Configuration(port: port, hash: Enumerable.Repeat((byte)2, 20).ToArray(), peerId: "-qB5100-testsecond02");
        SessionConfig secondPeer = secondConfig;
        SessionConfig twinConfig = Configuration(port: port, peerId: "-qB5100-testtwin0003");
        Host twinHost = new() { UseTcpListener = true };
        twin = new(twinHost, new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false, listenerHub: hub);
        bool duplicateReported = false, firstListening = false;
        TaskCompletionSource twinListening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string listening = "Listening for peers on port " + port;
        first.Log += message => { if (message.StartsWith(listening, StringComparison.Ordinal)) firstListening = true; };
        twin.Alert += (_, message) => { if (message.StartsWith("Another tab already answers peers", StringComparison.Ordinal)) duplicateReported = true; };
        twin.Log += message => { if (message.StartsWith(listening, StringComparison.Ordinal)) twinListening.TrySetResult(); };
        try
        {
            first.Start(firstConfig);
            await WaitStats(first, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(firstListening, "The first successful bind must be logged.");
            firstConfig.InfoHash[0] = 9;
            byte[] reply = await Handshake(port, new byte[20]);
            Require(reply.Length == 68 && Encoding.Latin1.GetString(reply, 48, 20) == firstConfig.PeerID, "The listener must use its immutable hash and peer ID.");
            second.Start(secondConfig);
            await WaitStats(second, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            reply = await Handshake(port, secondConfig.InfoHash);
            Require(reply.Length == 68 && Encoding.Latin1.GetString(reply, 48, 20) == secondPeer.PeerID,
                "Sessions sharing a port must each answer their own torrent with their own peer ID.");
            Require((await Handshake(port, Enumerable.Repeat((byte)7, 20).ToArray())).Length == 0, "An unknown torrent must be refused.");
            twin.Start(twinConfig);
            await WaitStats(twin, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(duplicateReported, "A second session for the same torrent on the same port must be reported.");
            await second.StopAsync();
            reply = await Handshake(port, new byte[20]);
            Require(reply.Length == 68 && Encoding.Latin1.GetString(reply, 48, 20) == firstConfig.PeerID,
                "Stopping another session must not close the listener, and the duplicate must stay silent.");
            Require((await Handshake(port, secondConfig.InfoHash)).Length == 0, "A stopped session must no longer answer its torrent.");
            await first.StopAsync();
            await twinListening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reply = await Handshake(port, new byte[20]);
            Require(reply.Length == 68 && Encoding.Latin1.GetString(reply, 48, 20) == twinConfig.PeerID,
                "When the answering session stops, the duplicate takes over its torrent.");
            using TcpClient pending = new(AddressFamily.InterNetwork);
            await pending.ConnectAsync(IPAddress.Loopback, port);
            twinHost.UseTcpListener = false;
            twin.RefreshListener();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            byte[] one = new byte[1];
            try { Require(await pending.GetStream().ReadAsync(one, timeout.Token) == 0, "Disabling the listener must close its accepted sockets."); }
            catch (IOException) { }
        }
        finally { await first.StopAsync(); await second.StopAsync(); await twin.StopAsync(); }

        // A port held by another application, even on IPv4 only, is reported, and the session keeps announcing.
        foreach (bool exclusive in new[] { true, false })
        {
            int occupiedPort = FreePort();
            using TcpListener occupied = new(IPAddress.Any, occupiedPort);
            occupied.Server.ExclusiveAddressUse = exclusive;
            occupied.Start();
            bool collisionReported = false;
            RatioEngine blocked = new(new Host { UseTcpListener = true }, new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false, listenerHub: new PeerListenerHub());
            blocked.Alert += (_, message) => { if (message.StartsWith("TCP listener unavailable", StringComparison.Ordinal)) collisionReported = true; };
            try
            {
                blocked.Start(Configuration(port: occupiedPort));
                await WaitStats(blocked, stats => !stats.IsAnnouncing && stats.Seeders == 4);
                Require(collisionReported && blocked.IsRunning, "An occupied port must be reported to the owning session without stopping it.");
            }
            finally { await blocked.StopAsync(); }
        }
    }

    private static int FreePort()
    {
        TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        return port;
    }

    private static async Task RefusedCompletionAsync()
    {
        foreach (int refusals in new[] { 2, 3 })
        {
            ManualClock clock = new();
            ConcurrentQueue<string> events = new();
            int refused = 0;
            TaskCompletionSource<StopNotice> notice = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Host host = new() { DownloadRateBytes = 100 };
            RatioEngine engine = new(host, clock, (name, _) =>
            {
                events.Enqueue(name);
                if (name == "completed" && Interlocked.Increment(ref refused) <= refusals)
                    throw new TrackerRejectedException("min announce time");
                return Task.FromResult(Response(interval: 1800));
            }, automaticTicks: false);
            engine.Stopped += stop => notice.TrySetResult(stop);
            try
            {
                engine.Start(Configuration(total: 100, finished: 0));
                await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
                clock.Advance(1);
                engine.Tick();
                await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count >= 2);
                Require(!await engine.ManualUpdateAsync() && engine.GetStats().ManualUpdateWait == 60,
                    "A manual update must wait for the retry of a refused completed.");
                for (int attempt = 1; attempt < 3 && engine.IsRunning; attempt++)
                {
                    clock.Advance(60);
                    engine.Tick();
                    await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count >= 2 + attempt || !engine.IsRunning);
                }
                if (refusals == 2)
                {
                    Require(engine.IsRunning && !engine.GetStats().CompletionPending && events.Count(name => name == "completed") == 3,
                        "Two refusals must leave the session running and the third completed must be accepted.");
                }
                else
                {
                    StopNotice final = await notice.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(!engine.IsRunning && final.IsError && final.Reason.Contains("min announce time", StringComparison.Ordinal),
                        "A third refusal of the same completed must end the session as a rejection.");
                    Require(!events.Contains("stopped"), "A final rejection is not followed by stopped.");
                }
            }
            finally { await engine.StopAsync(); }
        }
    }

    private static async Task ManualStopCompletionAsync()
    {
        ManualClock clock = new();
        ConcurrentQueue<string> events = new();
        int completedAttempts = 0;
        RatioEngine engine = new(new Host { DownloadRateBytes = 100 }, clock, (name, _) =>
        {
            events.Enqueue(name);
            if (name == "completed" && Interlocked.Increment(ref completedAttempts) == 1) throw new IOException("fixture timeout");
            return Task.FromResult(Response());
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration(total: 100, finished: 0));
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            clock.Advance(1);
            engine.Tick();
            await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count >= 2);
            Require(engine.GetStats().CompletionPending, "A failed completed stays pending.");
            await engine.StopAsync();
            Require(events.ToArray().SequenceEqual(new[] { "started", "completed", "completed", "stopped" }),
                "Stopping by hand must deliver the pending completed before stopped: " + string.Join(",", events));
        }
        finally { await engine.StopAsync(); }
    }

    private static async Task SessionCountersAsync()
    {
        using TcpListener tracker = new(IPAddress.Loopback, 0);
        tracker.Start();
        int port = ((IPEndPoint)tracker.LocalEndpoint).Port;
        ConcurrentQueue<string> requests = new();
        using CancellationTokenSource done = new();
        Task server = Task.Run(async () =>
        {
            try
            {
                while (!done.IsCancellationRequested)
                {
                    using TcpClient peer = await tracker.AcceptTcpClientAsync(done.Token);
                    using NetworkStream stream = peer.GetStream();
                    using StreamReader reader = new(stream, Encoding.Latin1, leaveOpen: true);
                    string line = await reader.ReadLineAsync(done.Token) ?? string.Empty;
                    while (await reader.ReadLineAsync(done.Token) is { Length: > 0 }) { }
                    requests.Enqueue(line);
                    byte[] body = Response().Encode();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n"), done.Token);
                    await stream.WriteAsync(body, done.Token);
                }
            }
            catch (OperationCanceledException) when (done.IsCancellationRequested) { }
            catch (SocketException) when (done.IsCancellationRequested) { }
        });
        ManualClock clock = new();
        RatioEngine engine = new(new Host { UploadRateBytes = 10, DownloadRateBytes = 20 }, clock, automaticTicks: false);
        try
        {
            engine.Start(new SessionConfig
            {
                Client = ClientCatalog.Create("qBittorrent", "5.2.4"), Proxy = new(), Tracker = $"http://127.0.0.1:{port}/announce",
                HashHex = new string('0', 40), InfoHash = new byte[20], TotalLength = 10000, FinishedPercent = 0, InitialHave = 5000,
                Interval = 1800, Port = "6881", Key = "ABC12345", PeerID = "-qB5240-sessioncount", NumWant = "20",
                ResumeUploaded = 7000, ResumeDownloaded = 3000,
            });
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            string started = requests.First();
            Require(started.Contains("&uploaded=0&downloaded=0&left=5000&", StringComparison.Ordinal),
                "The started announce of a resumed torrent reports nothing transferred yet and the exact bytes left: " + started);
            clock.Advance(10);
            engine.Tick();
            Require(await engine.ManualUpdateAsync(), "The regular update must be accepted.");
            string update = requests.Last(line => !line.Contains("event=", StringComparison.Ordinal));
            Require(update.Contains("&uploaded=100&downloaded=200&left=4800&", StringComparison.Ordinal),
                "Regular updates report this session's growth only: " + update);
            EngineStats stats = engine.GetStats();
            Require(stats.Uploaded == 7100 && stats.Downloaded == 3200 && stats.Have == 5200,
                "Displayed totals keep the earlier sessions while announces stay session-relative.");
        }
        finally
        {
            await engine.StopAsync();
            done.Cancel();
            tracker.Stop();
            await server;
        }
    }

    private static Task AddressParametersAsync()
    {
        System.Collections.Generic.IReadOnlyList<IPAddress> addresses = [IPAddress.Parse("203.0.113.9"), IPAddress.Parse("2001:db8::1")];
        string transmission = RatioEngine.BuildAnnounceUrl(ClientCatalog.Create("Transmission", "4.1.3"), "http://t.invalid/announce", new byte[20],
            "-TR4130-abcdefghij0z", "51413", string.Empty, 0, 0, 0, "0A1B2C3D", "80", string.Empty, addresses);
        Require(transmission.EndsWith("&ipv4=203.0.113.9&ipv6=2001%3ADB8%3A%3A1", StringComparison.OrdinalIgnoreCase)
            && transmission.Contains("%3A", StringComparison.Ordinal), "Transmission escapes the addresses in upper-case hex: " + transmission);
        string libtorrent = RatioEngine.BuildAnnounceUrl(ClientCatalog.Create("qBittorrent", "5.2.4"), "http://t.invalid/announce", new byte[20],
            "-qB5240-abcdefghijkl", "6881", string.Empty, 0, 0, 0, "0A1B2C3D", "200", string.Empty, addresses);
        Require(libtorrent.EndsWith("&ipv4=203.0.113.9&ipv6=2001%3adb8%3a%3a1", StringComparison.Ordinal), "libtorrent escapes in lower-case hex: " + libtorrent);
        Require(NetInfo.IsGlobal(IPAddress.Parse("8.8.8.8")) && !NetInfo.IsGlobal(IPAddress.Parse("192.168.1.2")) && !NetInfo.IsGlobal(IPAddress.Parse("100.64.0.1"))
            && NetInfo.IsGlobal(IPAddress.Parse("2a01:e0a::1")) && !NetInfo.IsGlobal(IPAddress.Parse("fd00::1")) && !NetInfo.IsGlobal(IPAddress.Parse("fe80::1")),
            "Only public addresses are announced.");
        return Task.CompletedTask;
    }

    private static async Task ClientTimingAsync()
    {
        foreach ((string family, string version, int regular, int manual) in new[]
        {
            ("qBittorrent", "5.2.4", 300, 30), ("Deluge", "2.2.0", 300, 30), ("Transmission", "4.1.3", 120, 120), ("uTorrent", "3.6.0", 120, 0),
        })
        {
            RatioEngine engine = new(new Host(), new ManualClock(), (_, _) => Task.FromResult(Response(interval: 120, minimum: null)), automaticTicks: false);
            try
            {
                engine.Start(Configuration(client: ClientCatalog.Create(family, version)));
                await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
                EngineStats stats = engine.GetStats();
                Require(stats.IntervalRemaining == regular && stats.ManualUpdateWait == manual,
                    $"{family}: a 120-second interval without min interval gives {stats.IntervalRemaining}/{stats.ManualUpdateWait}, expected {regular}/{manual}.");
            }
            finally { await engine.StopAsync(); }
        }
    }

    private static async Task StoppedNeedsStartedAsync()
    {
        ConcurrentQueue<string> events = new();
        RatioEngine engine = new(new Host(), new ManualClock(), (name, _) =>
        {
            events.Enqueue(name);
            throw new IOException("fixture tracker offline");
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration());
            await WaitStats(engine, stats => !stats.IsAnnouncing && events.Count == 1);
            await engine.StopAsync();
            Require(events.ToArray().SequenceEqual(new[] { "started" }), "A tracker that never accepted started must not receive stopped.");
        }
        finally { await engine.StopAsync(); }
    }

    private static async Task KeepAliveTrackerAsync()
    {
        await using TrackerFixture tracker = new(_ => Response(), keepAlive: true);
        RatioEngine engine = new(new Host(), new ManualClock(), automaticTicks: false);
        try
        {
            engine.Start(Configuration(tracker: $"http://127.0.0.1:{tracker.Port}/announce", client: ClientCatalog.Create("Transmission", "4.1.3")));
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(await engine.ManualUpdateAsync(), "A second request to a keep-alive tracker must complete too.");
            Require(tracker.Requests.Count == 2 && tracker.Requests.All(request => !request.Contains("\nConnection:", StringComparison.OrdinalIgnoreCase)),
                "Transmission sends no Connection header and must not wait for the tracker to close: " + string.Join(" | ", tracker.Requests));
        }
        finally { await engine.StopAsync(); }
    }

    private static async Task ScrapeRefusalAsync()
    {
        await using TrackerFixture tracker = new(line =>
        {
            if (!line.Contains("/scrape?", StringComparison.Ordinal)) return Response(seeders: null, leechers: null);
            ValueDictionary refusal = new();
            refusal.SetStringValue("failure reason", "scrape disabled");
            return refusal;
        });
        RatioEngine engine = new(new Host { RequestScrape = true }, new ManualClock(), automaticTicks: false);
        try
        {
            engine.Start(Configuration(tracker: $"http://127.0.0.1:{tracker.Port}/announce"));
            await WaitStats(engine, stats => !stats.IsAnnouncing && tracker.Requests.Count == 2);
            Require(engine.IsRunning, "A refused scrape must not stop the session.");
            Require(await engine.ManualUpdateAsync() && tracker.Requests.Count == 3, "A refused scrape is not requested again.");
        }
        finally { await engine.StopAsync(); }
    }

    /// <summary>A loopback HTTP tracker answering every request line; with keep-alive it never closes first.</summary>
    private sealed class TrackerFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource done = new();
        private readonly Func<string, ValueDictionary> respond;
        private readonly bool keepAlive;
        private readonly Task server;

        internal TrackerFixture(Func<string, ValueDictionary> respond, bool keepAlive = false)
        {
            this.respond = respond;
            this.keepAlive = keepAlive;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            server = Task.Run(AcceptAsync);
        }

        internal int Port { get; }

        /// <summary>Each request line followed by its header lines.</summary>
        internal ConcurrentQueue<string> Requests { get; } = new();

        private async Task AcceptAsync()
        {
            List<Task> connections = [];
            try
            {
                while (true) connections.Add(ServeAsync(await listener.AcceptTcpClientAsync(done.Token)));
            }
            catch (OperationCanceledException) when (done.IsCancellationRequested) { }
            catch (SocketException) when (done.IsCancellationRequested) { }
            await Task.WhenAll(connections);
        }

        private async Task ServeAsync(TcpClient peer)
        {
            using (peer)
            {
                try
                {
                    NetworkStream stream = peer.GetStream();
                    using StreamReader reader = new(stream, Encoding.Latin1, leaveOpen: true);
                    do
                    {
                        if (await reader.ReadLineAsync(done.Token) is not { } line) return;
                        StringBuilder request = new(line);
                        while (await reader.ReadLineAsync(done.Token) is { Length: > 0 } header) request.Append('\n').Append(header);
                        Requests.Enqueue(request.ToString());
                        byte[] body = respond(line).Encode();
                        string connection = keepAlive ? string.Empty : "Connection: close\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n{connection}\r\n"), done.Token);
                        await stream.WriteAsync(body, done.Token);
                    }
                    while (keepAlive);
                }
                catch (OperationCanceledException) when (done.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            done.Cancel();
            listener.Stop();
            await server;
            done.Dispose();
        }
    }

    private static async Task<byte[]> Handshake(int port, byte[] hash)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using TcpClient peer = new(AddressFamily.InterNetwork);
        await peer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using NetworkStream stream = peer.GetStream();
        byte[] request = new byte[68];
        request[0] = 19;
        "BitTorrent protocol"u8.CopyTo(request.AsSpan(1));
        hash.CopyTo(request, 28);
        await stream.WriteAsync(request, timeout.Token);
        byte[] response = new byte[68];
        int read = 0;
        while (read < response.Length)
        {
            int count = await stream.ReadAsync(response.AsMemory(read), timeout.Token);
            if (count == 0) break;
            read += count;
        }
        return response[..read];
    }

    private static async Task TrackerMetadataAsync()
    {
        using CancellationTokenSource done = new();
        TcpListener tracker = new(IPAddress.Loopback, 0);
        tracker.Start();
        int port = ((IPEndPoint)tracker.LocalEndpoint).Port;
        ConcurrentQueue<string> requests = new();
        ConcurrentQueue<(EngineAlert Level, string Message)> firstAlerts = new();
        ConcurrentQueue<string> brokenErrors = new();
        const string trackerId = "opaque&\0/\u00ff+?";
        const string warning = "Vérification conseillée";
        int firstAnnounces = 0;
        Task server = Task.Run(async () =>
        {
            try
            {
                while (!done.IsCancellationRequested)
                {
                    using TcpClient peer = await tracker.AcceptTcpClientAsync(done.Token);
                    using NetworkStream stream = peer.GetStream();
                    using StreamReader reader = new(stream, Encoding.Latin1, leaveOpen: true);
                    string line = await reader.ReadLineAsync(done.Token) ?? throw new IOException("Missing request line.");
                    while (await reader.ReadLineAsync(done.Token) is { Length: > 0 }) { }
                    requests.Enqueue(line);
                    ValueDictionary response = Response();
                    bool invalidEncoding = line.StartsWith("GET /broken/", StringComparison.Ordinal);
                    if (line.StartsWith("GET /empty-rejection/", StringComparison.Ordinal))
                    {
                        response = new();
                        response.SetStringValue("failure reason", string.Empty);
                    }
                    else if (line.Contains("/scrape.php?", StringComparison.Ordinal))
                    {
                        ValueDictionary files = new();
                        files[Encoding.Latin1.GetString(new byte[20])] = Response();
                        response = new();
                        response["files"] = files;
                    }
                    else if (line.StartsWith("GET /one%2Fraw//", StringComparison.Ordinal))
                    {
                        int count = Interlocked.Increment(ref firstAnnounces);
                        if (count == 1)
                        {
                            response = new();
                            response.SetStringValue("interval", "60");
                            response.SetStringValue("min_interval", "90");
                            response.SetStringValue("tracker id", trackerId);
                            response.SetStringValue("warning message", Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(warning)));
                        }
                        else if (count == 3) response.SetStringValue("tracker id", new string('x', 1025));
                    }
                    byte[] body = response.Encode();
                    string extra = invalidEncoding ? "Content-Encoding: unsupported-fixture\r\n" : string.Empty;
                    byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n{extra}Connection: close\r\n\r\n");
                    await stream.WriteAsync(headers, done.Token);
                    await stream.WriteAsync(body, done.Token);
                }
            }
            catch (OperationCanceledException) when (done.IsCancellationRequested) { }
            catch (SocketException) when (done.IsCancellationRequested) { }
        });
        ManualClock firstClock = new();
        RatioEngine first = new(new Host { RequestScrape = true }, firstClock, automaticTicks: false);
        RatioEngine second = new(new Host(), new ManualClock(), automaticTicks: false);
        RatioEngine broken = new(new Host(), new ManualClock(), automaticTicks: false);
        RatioEngine emptyRejection = new(new Host(), new ManualClock(), automaticTicks: false);
        TaskCompletionSource rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        emptyRejection.Stopped += _ => rejected.TrySetResult();
        first.Alert += (level, message) => firstAlerts.Enqueue((level, message));
        broken.Alert += (level, message) => { if (level == EngineAlert.Error) brokenErrors.Enqueue(message); };
        try
        {
            first.Start(Configuration(tracker: $"http://127.0.0.1:{port}/one%2Fraw//announce.php?pass=x%2Fy&empty=#ignored?bad=1"));
            await WaitStats(first, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(first.IsRunning && first.GetStats().IntervalRemaining == 300 && first.GetStats().ManualUpdateWait == 90,
                "The min_interval alias must control manual updates, and libtorrent raises a 60-second interval to 300 seconds.");
            Require(firstAlerts.Any(alert => alert.Level == EngineAlert.Warning && alert.Message == warning), "A UTF-8 tracker warning must reach the owning tab without stopping it.");
            string[] initial = requests.ToArray();
            Require(initial.Length == 2, "Missing announce counts must trigger only the normal scrape fallback.");
            Require(initial[0].StartsWith("GET /one%2Fraw//announce.php?pass=x%2Fy&empty=&info_hash=", StringComparison.Ordinal)
                && initial[0].Contains("&event=started", StringComparison.Ordinal) && !initial[0].Contains('#'), "Announce parameters must precede the discarded fragment without normalizing the URL.");
            Require(initial[1].StartsWith("GET /one%2Fraw//scrape.php?pass=x%2Fy&empty=&info_hash=", StringComparison.Ordinal)
                && !initial[1].Contains('#'), "Scrape must preserve the escaped path and original query without a fragment.");

            second.Start(Configuration(tracker: $"http://127.0.0.1:{port}/two//announce.php?#discarded"));
            await WaitStats(second, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(!requests.Last().Contains("trackerid=", StringComparison.Ordinal), "A tracker ID must never cross engine boundaries.");
            Require(!await first.ManualUpdateAsync(), "A manual update before the tracker's min_interval must be refused.");
            firstClock.Advance(90);
            Require(await first.ManualUpdateAsync(), "The first tab must accept its regular update.");
            string update = requests.Last();
            Require(update.Contains("&trackerid=opaque%26%00%2f%ff%2b%3f", StringComparison.Ordinal), "The tracker ID must preserve and encode all opaque bytes.");
            Require(firstAlerts.Last().Level == EngineAlert.Ok, "The next accepted announce without a warning must clear it.");

            Require(await first.ManualUpdateAsync(), "An oversized optional tracker ID must not reject an otherwise valid announce.");
            Require(firstAlerts.Last().Message.Contains("Tracker ID ignored", StringComparison.Ordinal), "An oversized tracker ID must be bounded and reported.");
            await first.ManualUpdateAsync();
            Require(!requests.Last().Contains("trackerid=", StringComparison.Ordinal), "An oversized replacement must not emit a truncated or stale tracker ID.");

            broken.Start(Configuration(tracker: $"http://127.0.0.1:{port}/broken/announce"));
            await WaitStats(broken, stats => !stats.IsAnnouncing && !brokenErrors.IsEmpty);
            Require(brokenErrors.Any(message => message.Contains("Unsupported HTTP content encoding", StringComparison.Ordinal)), "Decode errors must retain their actionable protocol detail.");

            emptyRejection.Start(Configuration(tracker: $"http://127.0.0.1:{port}/empty-rejection/announce"));
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await emptyRejection.StopAsync();
            Require(!emptyRejection.IsRunning && !emptyRejection.FinishedSuccessfully, "An HTTP 200 failure reason with an empty value must reject the session.");
            Require(requests.Count(line => line.StartsWith("GET /empty-rejection/", StringComparison.Ordinal)) == 1,
                "An empty rejection must not trigger a retry or stopped announce.");
        }
        finally
        {
            await first.StopAsync();
            await second.StopAsync();
            await broken.StopAsync();
            await emptyRejection.StopAsync();
            done.Cancel();
            tracker.Stop();
            await server;
        }
    }

    private static SessionConfig Configuration(long total = 10000, double finished = 100, int interval = 1800, int port = 43210, byte[]? hash = null, bool generatedKey = false, string? tracker = null, string peerId = "-qB5100-testpause001", ClientProfile? client = null) => new()
    {
        Client = client ?? ClientCatalog.Create("qBittorrent", "5.1.0"),
        Proxy = new(),
        Tracker = tracker ?? "http://127.0.0.1:1/announce",
        HashHex = new string('0', 40),
        InfoHash = hash ?? new byte[20],
        TotalLength = total,
        FinishedPercent = finished,
        Interval = interval,
        Port = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Key = "ABC12345",
        KeyIsGenerated = generatedKey,
        PeerID = peerId,
        NumWant = "20",
    };

    private static ValueDictionary Response(int interval = 1800, int? minimum = 0, int? leechers = 1, int? seeders = 4)
    {
        ValueDictionary response = new();
        response.SetStringValue("interval", interval.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (minimum is { } least) response.SetStringValue("min interval", least.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (seeders is { } seeds) response.SetStringValue("complete", seeds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (leechers is { } peers) response.SetStringValue("incomplete", peers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private static async Task WaitStats(RatioEngine engine, Func<EngineStats, bool> predicate)
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(EngineStats stats) { if (predicate(stats)) signal.TrySetResult(); }
        engine.Stats += Changed;
        try
        {
            Changed(engine.GetStats());
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { engine.Stats -= Changed; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal void Advance(int seconds) => Interlocked.Add(ref timestamp, seconds * TimeSpan.TicksPerSecond);
    }

    private sealed class Host : IEngineHost
    {
        public bool UseTcpListener { get; set; }
        public bool RequestScrape { get; init; }
        public long UploadRateBytes { get; init; } = 100;
        public long DownloadRateBytes { get; init; }
        public bool RandomUploadEnabled => false;
        public bool RandomDownloadEnabled => false;
        public string StopWhen { get; init; } = "Never";
        public string StopValue { get; init; } = string.Empty;
        public StopConditionSettings StopCondition => new(SessionSettings.ParseStopCondition(StopWhen), StopValue);
        public void ApplyAlert(EngineAlert level, string message) { }
    }
}
#endif
