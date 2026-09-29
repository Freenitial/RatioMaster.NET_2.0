#if DEBUG
namespace RatioMaster;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RatioMaster.BitTorrent;
using RatioMaster.Engine;
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
                Require(stats.Downloaded == expected && stats.Left == long.MaxValue - expected,
                    "Initial progress must not lose bytes or overflow at the engine size limit.");
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
            return Task.FromResult(Volatile.Read(ref mode) == 0 ? Response(interval: 999999, minimum: 172800) : Response(interval: 1, minimum: 120));
        }, automaticTicks: false);
        try
        {
            engine.Start(Configuration(interval: int.MinValue));
            await WaitStats(engine, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(engine.GetStats().IntervalRemaining == 999999, "A supported tracker interval must not be shortened to the configurable one-day limit.");
            Volatile.Write(ref mode, 1);
            Require(await engine.ManualUpdateAsync(), "Manual update must accept the fixture response.");
            Require(engine.GetStats().IntervalRemaining == 120, "The tracker minimum must dominate a smaller interval.");
            Volatile.Write(ref mode, 2);
            Require(!await engine.ManualUpdateAsync(), "A transport failure must not report acceptance.");
            Require(engine.IsRunning && engine.GetStats().IntervalRemaining == 120, "A retry must retain the accepted minimum deadline.");
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
                string[] expected = rejectedEvent == "completed" ? new[] { "started", "completed" } : new[] { "started", "completed", "stopped" };
                Require(events.ToArray().SequenceEqual(expected), "No further announce may follow an explicit rejection.");
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
        TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Host firstHost = new() { UseTcpListener = true };
        RatioEngine first = new(firstHost, new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false);
        RatioEngine second = new(new Host { UseTcpListener = true }, new ManualClock(), (_, _) => Task.FromResult(Response()), automaticTicks: false);
        SessionConfig firstConfig = Configuration(port: port);
        SessionConfig secondConfig = Configuration(port: port, hash: Enumerable.Repeat((byte)2, 20).ToArray());
        bool collisionReported = false;
        second.Alert += (_, message) => { if (message.StartsWith("TCP listener unavailable", StringComparison.Ordinal)) collisionReported = true; };
        try
        {
            first.Start(firstConfig);
            await WaitStats(first, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            firstConfig.InfoHash[0] = 9;
            byte[] reply = await Handshake(port, new byte[20]);
            Require(reply.Length == 68 && Encoding.Latin1.GetString(reply, 48, 20) == firstConfig.PeerID, "The listener must use its immutable hash and peer ID.");
            second.Start(secondConfig);
            await WaitStats(second, stats => !stats.IsAnnouncing && stats.Seeders == 4);
            Require(collisionReported, "An occupied port must be reported to the owning session.");
            Require((await Handshake(port, secondConfig.InfoHash)).Length == 0, "The first session must reject the other torrent's hash.");
            await second.StopAsync();
            Require((await Handshake(port, new byte[20])).Length == 68, "Stopping a failed listener must not close the other session's listener.");
            using TcpClient pending = new(AddressFamily.InterNetwork);
            await pending.ConnectAsync(IPAddress.Loopback, port);
            firstHost.UseTcpListener = false;
            first.Tick();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            byte[] one = new byte[1];
            try { Require(await pending.GetStream().ReadAsync(one, timeout.Token) == 0, "Disabling the listener must close its accepted sockets."); }
            catch (IOException) { }
        }
        finally { await first.StopAsync(); await second.StopAsync(); }
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
        RatioEngine first = new(new Host { RequestScrape = true }, new ManualClock(), automaticTicks: false);
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
            Require(first.IsRunning && first.GetStats().IntervalRemaining == 90, "The min_interval alias must control the announce deadline.");
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

    private static SessionConfig Configuration(long total = 10000, double finished = 100, int interval = 1800, int port = 43210, byte[]? hash = null, bool generatedKey = false, string? tracker = null) => new()
    {
        Client = ClientCatalog.Create("qBittorrent", "5.1.0"),
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
        PeerID = "-qB5100-testpause001",
        NumWant = "20",
    };

    private static ValueDictionary Response(int interval = 1800, int minimum = 0, int? leechers = 1, int? seeders = 4)
    {
        ValueDictionary response = new();
        response.SetStringValue("interval", interval.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.SetStringValue("min interval", minimum.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        public void ApplyAlert(EngineAlert level, string message) { }
    }
}
#endif
