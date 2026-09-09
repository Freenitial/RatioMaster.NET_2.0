#if DEBUG
namespace RatioMaster;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.BitTorrent;
using RatioMaster.Engine;
using RatioMaster.Models;

/// <summary>Loopback-only UDP transport checks. Run with await UdpSelfTest.RunAsync().</summary>
internal static class UdpSelfTest
{
    private const ulong ConnectionId = 0x8123456789ABCDEF;

    internal static async Task<bool> RunAsync()
    {
        try
        {
            await AnnounceAsync(AddressFamily.InterNetwork).ConfigureAwait(false);
            if (Socket.OSSupportsIPv6)
            {
                await AnnounceAsync(AddressFamily.InterNetworkV6).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("[SKIP] UDP IPv6: unavailable on this host.");
            }

            await ScrapeAsync().ConfigureAwait(false);
            await ErrorsAndMalformedPacketsAsync().ConfigureAwait(false);
            await CancellationAsync().ConfigureAwait(false);
            await ConcurrentOperationsAsync().ConfigureAwait(false);
            await RetransmissionAsync().ConfigureAwait(false);
            await RetryLimitAsync(false).ConfigureAwait(false);
            await RetryLimitAsync(true).ConfigureAwait(false);
            await ValidationAsync().ConfigureAwait(false);
            await EngineV2Async(false).ConfigureAwait(false);
            await EngineV2Async(true).ConfigureAwait(false);
            Console.WriteLine("[PASS] UDP transport self-test.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FAIL] UDP transport self-test: " + ex);
            return false;
        }
    }

    private static Task EngineV2Async(bool reject) => WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
    {
        Torrent metadata = TorrentFormatSelfTest.V2Fixture();
        EngineHost host = new();
        RatioEngine engine = new(host);
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stats += stats => { if (stats.Leechers == 7) accepted.TrySetResult(); };
        engine.Stopped += _ => stopped.TrySetResult();
        async Task ServeAsync()
        {
            for (int i = 0; i < (reject ? 1 : 2); i++)
            {
                UdpReceiveResult connect = await ReadConnectAsync(server, ct).ConfigureAwait(false);
                await SendAsync(server, connect, ConnectReply(connect), ct).ConfigureAwait(false);
                UdpReceiveResult request = await server.ReceiveAsync(ct).ConfigureAwait(false);
                Require(request.Buffer.AsSpan(16, 20).SequenceEqual(metadata.FullInfoHash.AsSpan(0, 20)), "Engine must announce the truncated v2 hash.");
                Require(Read32(request.Buffer, 80) == (i == 0 ? 2u : 3u), "Engine event mapping.");
                byte[] reply = AnnounceReply(request, []);
                if (reject)
                {
                    byte[] message = Encoding.UTF8.GetBytes("passkey rejected");
                    reply = Reply(request, 3, 8 + message.Length);
                    message.CopyTo(reply, 8);
                }
                await SendAsync(server, request, reply, ct).ConfigureAwait(false);
            }
        }
        Task serving = ServeAsync();
        try
        {
            engine.Start(new SessionConfig
            {
                Client = ClientCatalog.Create("qBittorrent", "5.1.0"), Proxy = new(),
                Tracker = uri.OriginalString, HashHex = Convert.ToHexString(metadata.FullInfoHash), InfoHash = metadata.InfoHash,
                TotalLength = (long)metadata.TotalLength, PieceCount = metadata.PieceCount,
                FinishedPercent = 100, Interval = 120, Port = "43210", Key = "ABC12345",
                PeerID = "-qB5100-integration0", NumWant = "20",
            });
            if (reject)
            {
                await stopped.Task.WaitAsync(ct).ConfigureAwait(false);
                Require(!engine.IsRunning && host.Error.Contains("passkey rejected"), "UDP rejection must stop the session.");
            }
            else
            {
                await accepted.Task.WaitAsync(ct).ConfigureAwait(false);
                Require(engine.GetStats().Seeders == int.MaxValue && engine.IsRunning, "UDP counts and active state.");
            }
            await engine.StopAsync().ConfigureAwait(false);
            await serving.ConfigureAwait(false);
        }
        finally { await engine.StopAsync().ConfigureAwait(false); }
    });

    private sealed class EngineHost : IEngineHost
    {
        public bool UseTcpListener => false;
        public bool RequestScrape => false;
        public long UploadRateBytes => 0;
        public long DownloadRateBytes => 0;
        public bool RandomUploadEnabled => false;
        public bool RandomDownloadEnabled => false;
        public string StopWhen => "Never";
        public string StopValue => "";
        internal string Error = "";
        public void ApplyAlert(EngineAlert level, string message) { if (level == EngineAlert.Error) Error = message; }
    }

    private static Task AnnounceAsync(AddressFamily family) => WithTrackerAsync(family, async (server, uri, ct) =>
    {
        string path = family == AddressFamily.InterNetwork
            ? "/private/%2f%41/../announce?passkey=a%2Fb%3D&x=1&x=2&long=" + new string('x', 600)
            : string.Empty;
        Uri tracker = new(uri.OriginalString + path + "#fragment");
        UdpAnnounceRequest request = Request();
        UdpAnnounceRequest expected = Request();
        byte[] peers = family == AddressFamily.InterNetwork
            ? [129, 141, 143, 255, 200, 213]
            : [32, 1, 13, 184, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 129, 200, 213];
        async Task ServeAsync()
        {
            UdpReceiveResult connect = await ReadConnectAsync(server, ct).ConfigureAwait(false);
            // Mutating the caller's DTO after dispatch must not alter the wire request or retransmissions.
            Array.Fill(request.InfoHash, (byte)0);
            Array.Fill(request.PeerId, (byte)0);
            request.Uploaded = 0;
            request.Event = 3;

            using UdpClient stranger = new(family);
            byte[] forged = ConnectReply(connect, ConnectionId + 1);
            await stranger.SendAsync(forged.AsMemory(), connect.RemoteEndPoint, ct).ConfigureAwait(false);
            await SendAsync(server, connect, new byte[7], ct).ConfigureAwait(false);
            byte[] wrongTransaction = Reply(connect, 3, 8);
            wrongTransaction[4] ^= 0x80;
            await SendAsync(server, connect, wrongTransaction, ct).ConfigureAwait(false);
            await SendAsync(server, connect, Reply(connect, 1, 8), ct).ConfigureAwait(false);
            byte[] connection = ConnectReply(connect);
            Array.Resize(ref connection, 19);
            await SendAsync(server, connect, connection, ct).ConfigureAwait(false);

            UdpReceiveResult announce = await server.ReceiveAsync(ct).ConfigureAwait(false);
            Require(announce.RemoteEndPoint.Equals(connect.RemoteEndPoint), "Socket changed during announce.");
            CheckAnnounce(announce.Buffer, expected, path);
            await SendAsync(server, announce, AnnounceReply(announce, peers), ct).ConfigureAwait(false);
        }

        Task<ValueDictionary> client = new UdpTrackerClient().AnnounceAsync(tracker, request, ct);
        await Task.WhenAll(ServeAsync(), client).ConfigureAwait(false);
        ValueDictionary result = await client.ConfigureAwait(false);
        Require(Number(result, "interval") == 120 && Number(result, "complete") == uint.MaxValue
            && Number(result, "incomplete") == 7, "Announce counters differ.");
        string key = family == AddressFamily.InterNetwork ? "peers" : "peers6";
        Require(result.Keys.Count == 4 && result.Contains(key)
            && ((ValueString)result[key]).Bytes.SequenceEqual(peers), "Compact peers differ.");
    });

    private static Task ScrapeAsync() => WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
    {
        byte[] hash = Request().InfoHash;
        async Task ServeAsync()
        {
            UdpReceiveResult connect = await ReadConnectAsync(server, ct).ConfigureAwait(false);
            await SendAsync(server, connect, ConnectReply(connect), ct).ConfigureAwait(false);
            UdpReceiveResult scrape = await server.ReceiveAsync(ct).ConfigureAwait(false);
            Require(scrape.Buffer.Length == 36 && Read32(scrape.Buffer, 8) == 2
                && BinaryPrimitives.ReadUInt64BigEndian(scrape.Buffer) == ConnectionId
                && scrape.Buffer.AsSpan(16).SequenceEqual(hash), "Scrape request differs.");
            byte[] response = ScrapeReply(scrape);
            Array.Resize(ref response, 24);
            await SendAsync(server, scrape, response, ct).ConfigureAwait(false);
        }

        Task<ValueDictionary> client = new UdpTrackerClient().ScrapeAsync(new Uri(uri + "/private?key=abc"), hash, ct);
        await Task.WhenAll(ServeAsync(), client).ConfigureAwait(false);
        ValueDictionary result = await client.ConfigureAwait(false);
        Require(result.Keys.Count == 3 && !result.Contains("files") && Number(result, "complete") == 11
            && Number(result, "downloaded") == uint.MaxValue && Number(result, "incomplete") == 13,
            "Scrape dictionary differs.");
    });

    private static async Task ErrorsAndMalformedPacketsAsync()
    {
        foreach (int action in new[] { 0, 1, 2 })
        {
            foreach (bool error in new[] { false, true })
            {
                await WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
                {
                    async Task ServeAsync()
                    {
                        UdpReceiveResult incoming = await ReadConnectAsync(server, ct).ConfigureAwait(false);
                        if (action != 0)
                        {
                            await SendAsync(server, incoming, ConnectReply(incoming), ct).ConfigureAwait(false);
                            incoming = await server.ReceiveAsync(ct).ConfigureAwait(false);
                        }

                        byte[] response = Reply(incoming, error ? 3 : action, error ? 1508 : action == 0 ? 15 : 19);
                        if (error)
                        {
                            Encoding.UTF8.GetBytes("passkey rejected: " + new string('x', 1400)).CopyTo(response, 8);
                        }

                        await SendAsync(server, incoming, response, ct).ConfigureAwait(false);
                    }

                    Task operation = action == 2
                        ? new UdpTrackerClient().ScrapeAsync(uri, Request().InfoHash, ct)
                        : new UdpTrackerClient().AnnounceAsync(uri, Request(), ct);
                    Task assertion = error
                        ? ExpectAsync<IOException>(() => operation, ex => ex.Message.Contains("passkey rejected:")
                            && ex.Message.Contains("truncated") && ex.Message.Length < 1100)
                        : ExpectAsync<InvalidDataException>(() => operation, ex => ex.Message.Contains("truncated"));
                    await Task.WhenAll(ServeAsync(), assertion).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }

        await WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
        {
            async Task ServeAsync()
            {
                UdpReceiveResult connect = await ReadConnectAsync(server, ct).ConfigureAwait(false);
                await SendAsync(server, connect, ConnectReply(connect), ct).ConfigureAwait(false);
                UdpReceiveResult announce = await server.ReceiveAsync(ct).ConfigureAwait(false);
                await SendAsync(server, announce, AnnounceReply(announce, [127]), ct).ConfigureAwait(false);
            }

            await Task.WhenAll(ServeAsync(), ExpectAsync<InvalidDataException>(
                () => new UdpTrackerClient().AnnounceAsync(uri, Request(), ct),
                ex => ex.Message.Contains("peer record"))).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task CancellationAsync()
    {
        foreach (bool afterConnect in new[] { false, true })
        {
            await WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
            {
                using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                async Task ServeAsync()
                {
                    UdpReceiveResult connect = await ReadConnectAsync(server, ct).ConfigureAwait(false);
                    if (afterConnect)
                    {
                        await SendAsync(server, connect, ConnectReply(connect), ct).ConfigureAwait(false);
                        await server.ReceiveAsync(ct).ConfigureAwait(false);
                    }

                    cancellation.Cancel();
                }

                Task<ValueDictionary> client = new UdpTrackerClient().AnnounceAsync(uri, Request(), cancellation.Token);
                await Task.WhenAll(ServeAsync(), ExpectAsync<OperationCanceledException>(() => client,
                    ex => ex.CancellationToken == cancellation.Token)).ConfigureAwait(false);
                Require(client.IsCanceled, "Cancellation did not cancel the task.");
            }).ConfigureAwait(false);
        }
    }

    private static Task ConcurrentOperationsAsync() => WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
    {
        HashSet<IPEndPoint> endpoints = [];
        async Task ServeAsync()
        {
            for (int i = 0; i < 4; i++)
            {
                UdpReceiveResult incoming = await server.ReceiveAsync(ct).ConfigureAwait(false);
                int action = (int)Read32(incoming.Buffer, 8);
                byte[] response;
                if (action == 0)
                {
                    endpoints.Add(incoming.RemoteEndPoint);
                    response = ConnectReply(incoming);
                }
                else
                {
                    response = action == 1 ? AnnounceReply(incoming, []) : ScrapeReply(incoming);
                }

                await SendAsync(server, incoming, response, ct).ConfigureAwait(false);
            }
        }

        UdpTrackerClient client = new();
        await Task.WhenAll(ServeAsync(), client.AnnounceAsync(uri, Request(), ct),
            client.ScrapeAsync(uri, Request().InfoHash, ct)).ConfigureAwait(false);
        Require(endpoints.Count == 2, "Concurrent operations shared a socket.");
    });

    private static Task RetransmissionAsync() => WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
    {
        ManualTimeProvider clock = new();
        async Task ServeAsync()
        {
            UdpReceiveResult connect = await ReadConnectAsync(server, ct).ConfigureAwait(false);
            await SendAsync(server, connect, ConnectReply(connect), ct).ConfigureAwait(false);
            UdpReceiveResult first = await server.ReceiveAsync(ct).ConfigureAwait(false);
            foreach (int seconds in new[] { 15, 30, 60 })
            {
                clock.Advance(TimeSpan.FromSeconds(seconds - 1));
                Require(clock.FiredTimers == 0, "A retry deadline fired early.");
                clock.Advance(TimeSpan.FromSeconds(1));
                Require(clock.FiredTimers == 1, "Retry deadline differs from BEP 15.");
                clock.FiredTimers = 0;
                UdpReceiveResult incoming = await server.ReceiveAsync(ct).ConfigureAwait(false);
                if (seconds != 60)
                {
                    Require(incoming.Buffer.SequenceEqual(first.Buffer), "Retransmission changed the request.");
                    continue;
                }

                Require(Read32(incoming.Buffer, 8) == 0 && incoming.Buffer.Length == 16
                    && Read32(incoming.Buffer, 12) != Read32(connect.Buffer, 12), "Expired connection was not renewed.");
                Require(incoming.RemoteEndPoint.Equals(connect.RemoteEndPoint), "Renewal changed the socket.");
                await SendAsync(server, incoming, ConnectReply(incoming, ConnectionId + 1), ct).ConfigureAwait(false);
                UdpReceiveResult renewed = await server.ReceiveAsync(ct).ConfigureAwait(false);
                Require(BinaryPrimitives.ReadUInt64BigEndian(renewed.Buffer) == ConnectionId + 1
                    && renewed.Buffer.AsSpan(8).SequenceEqual(first.Buffer.AsSpan(8)), "Renewed announce differs.");
                await SendAsync(server, renewed, AnnounceReply(renewed, []), ct).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(ServeAsync(), new UdpTrackerClient(clock).AnnounceAsync(uri, Request(), ct)).ConfigureAwait(false);
    });

    private static Task RetryLimitAsync(bool operationDeadline) => WithTrackerAsync(AddressFamily.InterNetwork, async (server, uri, ct) =>
    {
        ManualTimeProvider clock = new();
        async Task ServeAsync()
        {
            byte[]? first = null;
            foreach (int seconds in new[] { 15, 30, 60, 120 })
            {
                UdpReceiveResult connect = await ReadConnectAsync(server, ct).ConfigureAwait(false);
                first ??= connect.Buffer;
                Require(connect.Buffer.SequenceEqual(first), "Connect retransmission changed transaction.");
                if (operationDeadline && seconds == 120)
                {
                    await SendAsync(server, connect, ConnectReply(connect), ct).ConfigureAwait(false);
                    foreach (int announceWait in new[] { 15, 30, 60, 30 })
                    {
                        await server.ReceiveAsync(ct).ConfigureAwait(false);
                        clock.Advance(TimeSpan.FromSeconds(announceWait));
                    }

                    return;
                }

                clock.Advance(TimeSpan.FromSeconds(seconds));
            }
        }

        await Task.WhenAll(ServeAsync(), ExpectAsync<TimeoutException>(
            () => new UdpTrackerClient(clock).ScrapeAsync(uri, Request().InfoHash, ct),
            ex => ex.Message.Contains(operationDeadline ? "four minutes" : "retry limit"))).ConfigureAwait(false);
    });

    private static async Task ValidationAsync()
    {
        UdpTrackerClient client = new();
        Uri uri = new("udp://127.0.0.1:1");
        foreach (Uri invalid in new[] { new Uri("/announce", UriKind.Relative), new Uri("http://127.0.0.1:1"),
            new Uri("udp://127.0.0.1"), new Uri("udp://user:password@127.0.0.1:1") })
        {
            await ExpectAsync<ArgumentException>(() => client.ScrapeAsync(invalid, Request().InfoHash, default)).ConfigureAwait(false);
        }

        foreach (Action<UdpAnnounceRequest> invalidate in new Action<UdpAnnounceRequest>[]
        {
            request => request.InfoHash = new byte[19], request => request.PeerId = new byte[21],
            request => request.Downloaded = -1, request => request.Left = -1, request => request.Uploaded = -1,
            request => request.Event = 4, request => request.NumWant = -2, request => request.Port = 0,
        })
        {
            UdpAnnounceRequest request = Request();
            invalidate(request);
            await ExpectAsync<ArgumentException>(() => client.AnnounceAsync(uri, request, default)).ConfigureAwait(false);
        }

        await ExpectAsync<ArgumentNullException>(() => client.ScrapeAsync(uri, null!, default)).ConfigureAwait(false);
        await ExpectAsync<ArgumentException>(() => client.AnnounceAsync(
            new Uri("udp://127.0.0.1:1/" + new string('x', 65350)), Request(), default)).ConfigureAwait(false);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => client.AnnounceAsync(uri, Request(), canceled.Token)).ConfigureAwait(false);
    }

    private static async Task WithTrackerAsync(AddressFamily family, Func<UdpClient, Uri, CancellationToken, Task> test)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using UdpClient server = new(family);
        if (family == AddressFamily.InterNetworkV6)
        {
            server.Client.DualMode = false;
        }

        server.Client.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback, 0));
        Uri uri = new("udp://" + server.Client.LocalEndPoint);
        await test(server, uri, deadline.Token).ConfigureAwait(false);
    }

    private static UdpAnnounceRequest Request() => new()
    {
        InfoHash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray(),
        PeerId = Enumerable.Range(0, 20).Select(i => (byte)(255 - i)).ToArray(),
        Downloaded = 0x0102030405060708,
        Left = 0x1112131415161718,
        Uploaded = 0x2122232425262728,
        Event = 2,
        Key = 0xFFEEDDCC,
        NumWant = -1,
        Port = 51413,
    };

    private static void CheckAnnounce(byte[] packet, UdpAnnounceRequest request, string path)
    {
        Require(packet.Length >= 100 && BinaryPrimitives.ReadUInt64BigEndian(packet) == ConnectionId
            && Read32(packet, 8) == 1 && packet.AsSpan(16, 20).SequenceEqual(request.InfoHash)
            && packet.AsSpan(36, 20).SequenceEqual(request.PeerId)
            && BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(56)) == request.Downloaded
            && BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(64)) == request.Left
            && BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(72)) == request.Uploaded
            && Read32(packet, 80) == (uint)request.Event && Read32(packet, 84) == 0
            && Read32(packet, 88) == request.Key && BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(92)) == -1
            && BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(96)) == request.Port, "Announce layout differs.");
        List<byte> url = [];
        for (int offset = 98; offset < packet.Length;)
        {
            Require(offset + 2 <= packet.Length && packet[offset] == 2, "URLData header differs.");
            int count = packet[offset + 1];
            offset += 2;
            Require(count <= packet.Length - offset, "URLData exceeds packet.");
            url.AddRange(packet.AsSpan(offset, count).ToArray());
            offset += count;
        }

        Require(url.SequenceEqual(Encoding.UTF8.GetBytes(path)), "URLData did not preserve the original path/query.");
    }

    private static async Task<UdpReceiveResult> ReadConnectAsync(UdpClient server, CancellationToken ct)
    {
        UdpReceiveResult request = await server.ReceiveAsync(ct).ConfigureAwait(false);
        Require(request.Buffer.Length == 16 && Read32(request.Buffer, 8) == 0
            && BinaryPrimitives.ReadUInt64BigEndian(request.Buffer) == 0x41727101980, "Connect layout differs.");
        return request;
    }

    private static byte[] ConnectReply(UdpReceiveResult request, ulong id = ConnectionId)
    {
        byte[] packet = Reply(request, 0, 16);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), id);
        return packet;
    }

    private static byte[] AnnounceReply(UdpReceiveResult request, byte[] peers)
    {
        byte[] packet = Reply(request, 1, 20 + peers.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 120);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), 7);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), uint.MaxValue);
        peers.CopyTo(packet, 20);
        return packet;
    }

    private static byte[] ScrapeReply(UdpReceiveResult request)
    {
        byte[] packet = Reply(request, 2, 20);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 11);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), 13);
        return packet;
    }

    private static byte[] Reply(UdpReceiveResult request, int action, int length)
    {
        byte[] packet = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(packet, action);
        request.Buffer.AsSpan(12, 4).CopyTo(packet.AsSpan(4));
        return packet;
    }

    private static async Task SendAsync(UdpClient server, UdpReceiveResult request, byte[] packet, CancellationToken ct) =>
        await server.SendAsync(packet.AsMemory(), request.RemoteEndPoint, ct).ConfigureAwait(false);

    private static uint Read32(byte[] packet, int offset) => BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(offset));

    private static long Number(ValueDictionary dictionary, string key) => ((ValueNumber)dictionary[key]).Integer;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static async Task ExpectAsync<T>(Func<Task> operation, Func<T, bool>? check = null) where T : Exception
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (T ex)
        {
            Require(check == null || check(ex), "Unexpected exception diagnostic: " + ex.Message);
            return;
        }

        throw new InvalidDataException("Expected " + typeof(T).Name + ".");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private long timestamp;

        internal int FiredTimers { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (sync)
            {
                return timestamp;
            }
        }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (sync)
            {
                ManualTimer timer = new(this, callback, state);
                timer.Change(dueTime, period);
                timers.Add(timer);
                return timer;
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due = [];
            lock (sync)
            {
                timestamp += elapsed.Ticks;
                foreach (ManualTimer timer in timers)
                {
                    if (timer.DueAt.HasValue && timer.DueAt <= timestamp)
                    {
                        timer.DueAt = null;
                        due.Add(timer);
                    }
                }
            }

            foreach (ManualTimer timer in due)
            {
                FiredTimers++;
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal long? DueAt { get; set; }

            internal void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Require(period == Timeout.InfiniteTimeSpan, "Test clock requires one-shot deadlines.");
                lock (owner.sync)
                {
                    if (disposed)
                    {
                        return false;
                    }

                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.timestamp + dueTime.Ticks;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner.sync)
                {
                    disposed = true;
                    DueAt = null;
                    owner.timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
#endif
