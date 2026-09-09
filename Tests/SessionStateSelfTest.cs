#if DEBUG
namespace RatioMaster;

using System.Net;
using System.Net.Sockets;
using System.Text;
using RatioMaster.Engine;
using RatioMaster.Models;

/// <summary>Local tracker checks for manual pause and automatic leecher gating.</summary>
internal static class SessionStateSelfTest
{
    internal static async Task<bool> RunAsync()
    {
        using CancellationTokenSource done = new();
        TcpListener tracker = new(IPAddress.Loopback, 0);
        tracker.Start();
        int port = ((IPEndPoint)tracker.LocalEndpoint).Port;
        int leechers = 0, requests = 0, failStopped = 0, rejectAnnounces = 0;
        Task server = Task.Run(async () =>
        {
            try
            {
                while (!done.IsCancellationRequested)
                {
                    using TcpClient peer = await tracker.AcceptTcpClientAsync(done.Token);
                    using NetworkStream stream = peer.GetStream();
                    using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                    string? line = await reader.ReadLineAsync(done.Token);
                    while (await reader.ReadLineAsync(done.Token) is { Length: > 0 }) { }
                    Interlocked.Increment(ref requests);
                    byte[] body = Encoding.ASCII.GetBytes($"d8:completei4e10:incompletei{Volatile.Read(ref leechers)}e8:intervali120e5:peers0:e");
                    if (Volatile.Read(ref rejectAnnounces) != 0
                        || (Volatile.Read(ref failStopped) != 0 && line?.Contains("event=stopped") == true))
                        body = Encoding.ASCII.GetBytes("d14:failure reason8:rejectede");
                    byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, done.Token);
                    await stream.WriteAsync(body, done.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (done.IsCancellationRequested) { }
        });
        RatioEngine engine = new(new Host());
        bool ok = true;
        void Check(bool condition, string name) { Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}"); ok &= condition; }
        try
        {
            SessionConfig configuration = new()
            {
                Client = ClientCatalog.Create("qBittorrent", "5.1.0"), Proxy = new(),
                Tracker = $"http://127.0.0.1:{port}/announce", HashHex = new string('0', 40), InfoHash = new byte[20],
                TotalLength = 1024 * 1024, FinishedPercent = 100, Interval = 120, Port = "43210",
                Key = "ABC12345", PeerID = "-qB5100-testpause001", NumWant = "20",
            };
            engine.Start(configuration);
            await WaitStats(engine, s => s.UploadPausedNoLeechers);
            Check(engine.GetStats().Uploaded == 0, "zero leechers pauses upload");
            int before = requests;
            engine.Resume();
            Check(engine.GetStats().UploadPausedNoLeechers && requests == before, "manual Resume does not override leecher pause or send a request");
            Volatile.Write(ref leechers, 1);
            await engine.ManualUpdateAsync();
            await WaitStats(engine, s => s.Uploaded > 0);
            Check(!engine.GetStats().UploadPausedNoLeechers, "one leecher automatically resumes upload");
            engine.Pause();
            long paused = engine.GetStats().Uploaded;
            before = requests;
            int time = engine.GetStats().TotalRunSeconds;
            await WaitStats(engine, s => s.TotalRunSeconds > time);
            Check(engine.GetStats().Uploaded == paused && requests == before, "user pause freezes counters without extra polling");
            Volatile.Write(ref leechers, 0);
            await engine.ManualUpdateAsync();
            Volatile.Write(ref leechers, 1);
            await engine.ManualUpdateAsync();
            Check(engine.IsPaused && engine.GetStats().Uploaded == paused, "leecher recovery and manual update preserve user pause");
            engine.Resume();
            await WaitStats(engine, s => s.Uploaded > paused);
            Check(!engine.IsPaused, "Resume restarts counters");
            await engine.StopAsync();
            long stoppedAt = engine.GetStats().Uploaded;
            before = requests;
            engine.Resume(); engine.Pause(); await engine.ManualUpdateAsync(); await engine.StopAsync();
            Check(!engine.IsRunning && !engine.GetStats().IsPaused && engine.GetStats().Uploaded == stoppedAt && requests == before,
                "actions on a stopped session are idempotent");
            Check(!engine.FinishedSuccessfully, "manual Stop is not a successful objective");
            foreach (bool failure in new[] { false, true })
            {
                Volatile.Write(ref failStopped, failure ? 1 : 0);
                RatioEngine goal = new(new Host { StopWhen = "When uploaded >", StopValue = "0" });
                TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
                goal.Stopped += _ => ended.TrySetResult();
                goal.Start(configuration);
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await goal.StopAsync();
                Check(goal.FinishedSuccessfully == !failure, failure ? "rejected final announce cannot turn a tab green" : "reached objective and accepted final announce mark success");
            }
            Volatile.Write(ref rejectAnnounces, 1);
            RatioEngine rejected = new(new Host());
            TaskCompletionSource rejectionStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            rejected.Stopped += _ => rejectionStopped.TrySetResult();
            before = requests;
            try
            {
                rejected.Start(configuration);
                await rejectionStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await rejected.ManualUpdateAsync();
                Check(!rejected.IsRunning && !rejected.FinishedSuccessfully && requests == before + 1,
                    "HTTP failure reason stops the session without retrying or sending another announce");
            }
            finally { await rejected.StopAsync(); }
        }
        catch (Exception ex) { Check(false, ex.ToString()); }
        finally { await engine.StopAsync(); done.Cancel(); tracker.Stop(); await server; }
        return ok;
    }

    private static async Task WaitStats(RatioEngine engine, Func<EngineStats, bool> predicate)
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStats(EngineStats stats) { if (predicate(stats)) signal.TrySetResult(); }
        engine.Stats += OnStats;
        try { OnStats(engine.GetStats()); await signal.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { engine.Stats -= OnStats; }
    }
    private sealed class Host : IEngineHost
    {
        public bool UseTcpListener => false;
        public bool RequestScrape => true;
        public long UploadRateBytes => 4096;
        public long DownloadRateBytes => 0;
        public bool RandomUploadEnabled => false;
        public bool RandomDownloadEnabled => false;
        public string StopWhen { get; init; } = "Never";
        public string StopValue { get; init; } = "";
        public void ApplyAlert(EngineAlert level, string message) { }
    }
}
#endif
