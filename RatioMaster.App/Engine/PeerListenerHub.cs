namespace RatioMaster.Engine;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.Models;

internal enum ListenerState
{
    Starting,
    Listening,
    Failed,

    /// <summary>Another tab already serves the same info-hash on this port.</summary>
    Duplicate,
}

/// <summary>
/// One TCP listener per port shared by every session announcing that port, as a real client listens once
/// for all of its torrents. Incoming peers are routed by the info-hash they name (plaintext) or by the
/// obfuscated SKEY (MSE), and each session answers with its own identity. Failed binds are retried.
/// </summary>
internal sealed class PeerListenerHub
{
    private const int MaximumConnections = 64;
    private const int MaximumConnectionsPerTorrent = 8;

    /// <summary>Extension messages read in full: handshakes and metadata requests are small; anything larger is skipped.</summary>
    private const int MaximumExtensionBytes = 1 << 16;
    private static readonly TimeSpan BindRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A peer that sends nothing, or stops reading what is sent, for this long is dropped.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(6);
    private readonly object sync = new();
    private readonly Dictionary<int, PortListener> ports = [];
    private readonly SemaphoreSlim connectionSlots = new(MaximumConnections, MaximumConnections);

    internal static PeerListenerHub Shared { get; } = new();

    /// <summary>
    /// Dual-stack, and exclusive on Windows: there a shared dual-mode bind succeeds even when another application
    /// holds the IPv4 port, which would leave IPv4 peers to that application. Elsewhere .NET's default address
    /// reuse lets a restarted session bind again past the TIME_WAIT of connections it closed.
    /// </summary>
    internal static void ConfigureListenSocket(Socket socket)
    {
        if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
        if (socket.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = true;
    }

    internal Registration Register(int port, PeerTorrent torrent, Action<ListenerState, string> onState)
    {
        Registration registration = new(this, port, torrent, onState);
        PortListener listener;
        (ListenerState state, string detail) initial;
        lock (sync)
        {
            if (!ports.TryGetValue(port, out listener!))
            {
                listener = new PortListener(this, port);
                ports.Add(port, listener);
            }
            initial = listener.Add(registration);
        }

        // Callbacks run outside every lock: they reach the session and, through it, the interface.
        if (initial.state != ListenerState.Starting) registration.Report(initial.state, initial.detail);
        listener.EnsureStarted();
        return registration;
    }

    /// <summary>True when this process already listens on the port (it can be reused for another torrent).</summary>
    internal bool Serves(int port)
    {
        lock (sync) return ports.TryGetValue(port, out PortListener? listener) && listener.IsListening;
    }

    private void Remove(Registration registration)
    {
        IReadOnlyList<(Registration Waiting, ListenerState State, string Detail)> promoted;
        lock (sync)
        {
            if (!ports.TryGetValue(registration.Port, out PortListener? listener)) return;
            promoted = listener.Remove(registration, out int remaining);

            // Closed under the hub lock: a session registering the same port right after binds a free socket.
            if (remaining == 0)
            {
                ports.Remove(registration.Port);
                listener.Stop();
            }
        }
        foreach ((Registration waiting, ListenerState state, string detail) in promoted) waiting.Report(state, detail);
    }

    /// <summary>A session's claim on a port. Disposing it closes that session's peer connections only.</summary>
    internal sealed class Registration : IDisposable
    {
        private readonly PeerListenerHub hub;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Action<ListenerState, string> onState;
        private int connections;
        private int disposed;

        internal Registration(PeerListenerHub hub, int port, PeerTorrent torrent, Action<ListenerState, string> onState)
        {
            this.hub = hub;
            Port = port;
            Torrent = torrent;
            this.onState = onState;
        }

        internal int Port { get; }
        internal PeerTorrent Torrent { get; }
        internal CancellationToken Token => lifetime.Token;

        /// <summary>Written only under the gate of the port's listener, so routing and notifications agree.</summary>
        internal ListenerState State { get; set; } = ListenerState.Starting;

        internal void Report(ListenerState state, string detail)
        {
            if (Volatile.Read(ref disposed) == 0) onState(state, detail);
        }

        internal bool TryEnterConnection() => Interlocked.Increment(ref connections) <= MaximumConnectionsPerTorrent || LeaveAndFail();
        internal void LeaveConnection() => Interlocked.Decrement(ref connections);

        private bool LeaveAndFail()
        {
            Interlocked.Decrement(ref connections);
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            hub.Remove(this);
            lifetime.Dispose();
        }
    }

    private sealed class PortListener(PeerListenerHub hub, int port)
    {
        private readonly object gate = new();
        private readonly List<Registration> registrations = [];
        private CancellationTokenSource? running;
        private TcpListener? socket;
        private Timer? retry;
        private string failure = string.Empty;
        private bool stopped;

        internal bool IsListening { get { lock (gate) return socket is not null; } }

        private ListenerState Current => socket is not null ? ListenerState.Listening
            : failure.Length > 0 ? ListenerState.Failed : ListenerState.Starting;

        internal (ListenerState State, string Detail) Add(Registration registration)
        {
            lock (gate)
            {
                registration.State = IsCovered(registration) ? ListenerState.Duplicate : Current;
                registrations.Add(registration);
                return (registration.State, registration.State == ListenerState.Failed ? failure : string.Empty);
            }
        }

        /// <summary>Removes a session; a tab that waited behind it as a duplicate takes over its info-hashes.</summary>
        internal IReadOnlyList<(Registration, ListenerState, string)> Remove(Registration registration, out int remaining)
        {
            List<(Registration, ListenerState, string)> promoted = [];
            lock (gate)
            {
                registrations.Remove(registration);
                foreach (Registration waiting in registrations)
                {
                    if (waiting.State != ListenerState.Duplicate || IsCovered(waiting)) continue;
                    waiting.State = Current;
                    promoted.Add((waiting, waiting.State, waiting.State == ListenerState.Failed ? failure : string.Empty));
                }
                remaining = registrations.Count;
            }
            return promoted;
        }

        private bool IsCovered(Registration candidate) => registrations.Any(existing => existing != candidate
            && existing.State != ListenerState.Duplicate
            && existing.Torrent.InfoHashes.Any(hash => candidate.Torrent.InfoHashes.Any(other => hash.AsSpan().SequenceEqual(other))));

        private Registration? Find(ReadOnlySpan<byte> infoHash)
        {
            lock (gate)
            {
                foreach (Registration registration in registrations)
                {
                    if (registration.State == ListenerState.Duplicate) continue;
                    foreach (byte[] hash in registration.Torrent.InfoHashes)
                        if (infoHash.SequenceEqual(hash)) return registration;
                }
            }
            return null;
        }

        /// <summary>Resolves the torrent an MSE initiator named through HASH('req2', SKEY).</summary>
        private MessageStreamEncryption.Candidate? Identify(byte[] skeyHash)
        {
            lock (gate)
            {
                foreach (Registration registration in registrations)
                {
                    if (registration.State == ListenerState.Duplicate) continue;
                    PeerTorrent torrent = registration.Torrent;
                    byte[][] hashes = torrent.SkeyHashes;
                    for (int index = 0; index < hashes.Length; index++)
                        if (CryptographicOperations.FixedTimeEquals(hashes[index], skeyHash))
                            return new MessageStreamEncryption.Candidate(torrent.InfoHashes[index], torrent.Wire.PrefersRc4);
                }
            }
            return null;
        }

        /// <summary>Records a listener state on every session that is not a duplicate and returns them for notification.</summary>
        private Registration[] Assign(ListenerState state)
        {
            Registration[] affected = registrations.Where(item => item.State != ListenerState.Duplicate).ToArray();
            foreach (Registration registration in affected) registration.State = state;
            return affected;
        }

        internal void EnsureStarted()
        {
            Registration[] notify;
            ListenerState state;
            string detail = string.Empty;
            lock (gate)
            {
                if (stopped || socket is not null) return;
                try
                {
                    TcpListener candidate = new(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, port);
                    ConfigureListenSocket(candidate.Server);
                    try { candidate.Start(32); }
                    catch { candidate.Stop(); throw; }
                    socket = candidate;
                    failure = string.Empty;
                    retry?.Dispose();
                    retry = null;
                    running = new CancellationTokenSource();
                    CancellationToken token = running.Token;
                    _ = Task.Run(() => AcceptAsync(candidate, token));
                    state = ListenerState.Listening;
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    failure = detail = error.Message;
                    state = ListenerState.Failed;
                    ScheduleRetry();
                }
                notify = Assign(state);
            }
            foreach (Registration registration in notify) registration.Report(state, detail);
        }

        private void ScheduleRetry() => retry ??= new Timer(_ =>
        {
            // A timer callback must never throw: that would end the process.
            try { EnsureStarted(); }
            catch (Exception) { }
        }, null, BindRetry, BindRetry);

        /// <summary>The listening socket failed for good: report it and bind again later.</summary>
        private void Lost(TcpListener server, string message)
        {
            Registration[] notify;
            lock (gate)
            {
                if (stopped || socket != server) return;
                socket = null;
                server.Stop();
                running?.Cancel();
                running?.Dispose();
                running = null;
                failure = message;
                ScheduleRetry();
                notify = Assign(ListenerState.Failed);
            }
            foreach (Registration registration in notify) registration.Report(ListenerState.Failed, message);
        }

        internal void Stop()
        {
            lock (gate)
            {
                stopped = true;
                retry?.Dispose();
                retry = null;
                running?.Cancel();
                running?.Dispose();
                running = null;
                socket?.Stop();
                socket = null;
            }
        }

        private async Task AcceptAsync(TcpListener server, CancellationToken ct)
        {
            while (true)
            {
                Socket client;
                try { client = await server.AcceptSocketAsync(ct).ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { return; }

                // A connection reset while it waited in the backlog only concerns that connection.
                catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { continue; }
                catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    Lost(server, error.Message);
                    return;
                }
                if (!hub.connectionSlots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                // Off the accept loop: the key exchange of one peer must not delay the next.
                _ = Task.Run(() => ServeAsync(client, ct));
            }
        }

        private async Task ServeAsync(Socket client, CancellationToken listenerToken)
        {
            Registration? owner = null;
            try
            {
                client.NoDelay = true;
                using NetworkStream network = new(client, ownsSocket: false);
                PeerTransport transport = new(network);
                using CancellationTokenSource handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(listenerToken);
                handshakeDeadline.CancelAfter(HandshakeTimeout);
                CancellationToken setup = handshakeDeadline.Token;

                // Twenty bytes tell a plaintext handshake from an MSE public key, which may start with 19 too.
                byte[] start = new byte[20];
                await transport.ReadExactlyAsync(start, setup).ConfigureAwait(false);
                byte[]? usedHash = null;
                bool encrypted = false;
                if (PeerWire.IsPlaintextHandshake(start)) transport.Prepend(start);
                else
                {
                    MessageStreamEncryption.Result? negotiated = await MessageStreamEncryption.AcceptAsync(transport, start, Identify, setup).ConfigureAwait(false);
                    if (negotiated is null) return;
                    (usedHash, encrypted) = (negotiated.InfoHash, negotiated.Encrypted);
                }

                byte[] request = new byte[68];
                await transport.ReadExactlyAsync(request, setup).ConfigureAwait(false);
                if (!PeerWire.IsPlaintextHandshake(request)) return;
                if (usedHash is not null && !request.AsSpan(28, 20).SequenceEqual(usedHash)) return;
                owner = Find(request.AsSpan(28, 20));
                if (owner is null || !owner.TryEnterConnection()) { owner = null; return; }

                IPEndPoint? remote = client.RemoteEndPoint as IPEndPoint;
                if (remote is { Address.IsIPv4MappedToIPv6: true }) remote = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
                using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(listenerToken, owner.Token);
                await new PeerConversation(transport, owner.Torrent, request, remote, encrypted).RunAsync(session.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                owner?.LeaveConnection();
                try { client.Shutdown(SocketShutdown.Both); }
                catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
                client.Dispose();
                hub.connectionSlots.Release();
            }
        }
    }

    /// <summary>One accepted peer after a valid handshake: answer like the emulated client, then stay choked.</summary>
    private sealed class PeerConversation(PeerTransport transport, PeerTorrent torrent, byte[] request, IPEndPoint? remote, bool encrypted)
    {
        private readonly SemaphoreSlim writing = new(1, 1);
        private readonly byte[] scratch = new byte[16384];
        private long lastSent = Environment.TickCount64;

        internal async Task RunAsync(CancellationToken ct)
        {
            NegotiatedWire wire = new(torrent, request);
            using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            await SendAsync(PeerWire.Handshake(wire.Reserved, request.AsSpan(28, 20), torrent.PeerId), idle.Token).ConfigureAwait(false);
            string address = remote?.ToString() ?? "?";
            torrent.Log?.Invoke(encrypted ? Localization.Strings.PeerHandshakeAnsweredEncrypted(address) : Localization.Strings.PeerHandshakeAnswered(address));
            if (!torrent.Realistic) return;

            // The order libtorrent and Transmission follow: extension handshake, availability, then the DHT port.
            double progress = Math.Clamp(torrent.Progress(), 0, 1);
            int owned = PeerWire.OwnedPieces(torrent.PieceCount, progress);
            bool seeding = torrent.PieceCount > 0 ? owned == torrent.PieceCount : progress >= 1;
            if (wire.Extensions && remote is not null)
                await SendAsync(PeerWire.ExtensionMessage(0, PeerWire.ExtensionHandshake(torrent, remote.Address, seeding)), idle.Token).ConfigureAwait(false);
            if (torrent.PieceCount > 0 && Availability(wire, owned) is { } availability)
                await SendAsync(availability, idle.Token).ConfigureAwait(false);
            if (wire.Dht) await SendAsync(PeerWire.DhtPort(torrent.AdvertisedPort), idle.Token).ConfigureAwait(false);

            Task keepAlive = KeepAliveAsync(idle);
            try { await ConverseAsync(wire, seeding, idle).ConfigureAwait(false); }
            finally
            {
                idle.Cancel();
                await keepAlive.ConfigureAwait(false);
            }
        }

        /// <summary>libtorrent write_bitfield: HaveAll or HaveNone with the fast extension, nothing for no piece without it.</summary>
        private byte[]? Availability(NegotiatedWire wire, int owned)
        {
            int count = torrent.PieceCount;
            if (owned == 0) return wire.Fast ? PeerWire.Message(PeerWire.HaveNone) : null;
            if (owned == count && wire.Fast) return PeerWire.Message(PeerWire.HaveAll);
            byte[] message = PeerWire.Allocate(PeerWire.Bitfield, PeerWire.BitfieldBytes(count));
            if (owned == count) PeerWire.FillFullBitfield(message.AsSpan(5), count);
            else PeerWire.FillBitfield(message.AsSpan(5), torrent.PieceOrder, owned);
            return message;
        }

        private async Task ConverseAsync(NegotiatedWire wire, bool seeding, CancellationTokenSource idle)
        {
            CancellationToken ct = idle.Token;
            byte ownMetadataId = PeerWire.MetadataExtensionId(wire.Style);
            int peerMetadataId = 0;
            int maximum = Math.Max(1 << 18, 1 + PeerWire.BitfieldBytes(torrent.PieceCount));
            byte[] header = new byte[5];
            while (true)
            {
                await transport.ReadExactlyAsync(header.AsMemory(0, 4), ct).ConfigureAwait(false);
                idle.CancelAfter(IdleTimeout);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length == 0) continue;
                if (length < 0 || length > maximum) return;
                await transport.ReadExactlyAsync(header.AsMemory(4, 1), ct).ConfigureAwait(false);
                int remaining = length - 1;
                switch (header[4])
                {
                    case PeerWire.HaveAll when seeding:
                        return; // two seeds have nothing to exchange; real clients close the connection.
                    case PeerWire.Bitfield when seeding:
                        if (await IsCompleteAsync(remaining, ct).ConfigureAwait(false)) return;
                        break;
                    case PeerWire.Request when wire.Fast && remaining == 12:
                    {
                        byte[] reject = PeerWire.Allocate(PeerWire.RejectRequest, 12);
                        await transport.ReadExactlyAsync(reject.AsMemory(5), ct).ConfigureAwait(false);
                        await SendAsync(reject, ct).ConfigureAwait(false);
                        break;
                    }
                    case PeerWire.Extended when remaining >= 1:
                    {
                        await transport.ReadExactlyAsync(header.AsMemory(4, 1), ct).ConfigureAwait(false);
                        int extension = header[4], size = remaining - 1;
                        bool wanted = size <= MaximumExtensionBytes
                            && (extension == 0 || extension == ownMetadataId && peerMetadataId > 0 && torrent.ServesMetadata);
                        if (!wanted)
                        {
                            await SkipAsync(size, ct).ConfigureAwait(false);
                            break;
                        }
                        byte[] body = new byte[size];
                        await transport.ReadExactlyAsync(body, ct).ConfigureAwait(false);
                        if (extension == 0) peerMetadataId = PeerWire.ReadExtensionId(body, "ut_metadata");
                        else if (PeerWire.ReadMetadataRequest(body) is int piece)
                            await SendAsync(PeerWire.MetadataAnswer(torrent, (byte)peerMetadataId, piece), ct).ConfigureAwait(false);
                        break;
                    }
                    default:
                        await SkipAsync(remaining, ct).ConfigureAwait(false);
                        break;
                }
            }
        }

        /// <summary>Reads a bitfield without storing it and tells whether every piece is set.</summary>
        private async Task<bool> IsCompleteAsync(int length, CancellationToken ct)
        {
            int count = torrent.PieceCount;
            int expected = PeerWire.BitfieldBytes(count);
            byte last = count % 8 == 0 ? (byte)255 : (byte)(255 << (8 - count % 8));
            bool complete = count > 0 && length == expected;
            for (int offset = 0; offset < length;)
            {
                int chunk = Math.Min(scratch.Length, length - offset);
                await transport.ReadExactlyAsync(scratch.AsMemory(0, chunk), ct).ConfigureAwait(false);
                for (int index = 0; complete && index < chunk; index++)
                    complete = scratch[index] == (offset + index == expected - 1 ? last : (byte)255);
                offset += chunk;
            }
            return complete;
        }

        private async Task SkipAsync(int length, CancellationToken ct)
        {
            for (int offset = 0; offset < length;)
            {
                int chunk = Math.Min(scratch.Length, length - offset);
                await transport.ReadExactlyAsync(scratch.AsMemory(0, chunk), ct).ConfigureAwait(false);
                offset += chunk;
            }
        }

        /// <summary>Sends a keep-alive whenever nothing else went out for the client's interval; a failed write ends the conversation.</summary>
        private async Task KeepAliveAsync(CancellationTokenSource idle)
        {
            long interval = torrent.Wire.KeepAliveSeconds * 1000L;
            try
            {
                while (true)
                {
                    long due = interval - (Environment.TickCount64 - Volatile.Read(ref lastSent));
                    if (due > 0) await Task.Delay(TimeSpan.FromMilliseconds(due), idle.Token).ConfigureAwait(false);
                    else await SendAsync(PeerWire.KeepAlive(), idle.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                try { idle.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private async Task SendAsync(byte[] data, CancellationToken ct)
        {
            await writing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await transport.WriteAsync(data, ct).ConfigureAwait(false);
                await transport.FlushAsync(ct).ConfigureAwait(false);
                Volatile.Write(ref lastSent, Environment.TickCount64);
            }
            finally { writing.Release(); }
        }
    }

    /// <summary>The flags both sides agreed on for one connection.</summary>
    private readonly struct NegotiatedWire
    {
        internal NegotiatedWire(PeerTorrent torrent, byte[] request)
        {
            PeerWireDefinition wire = torrent.Wire;
            byte[] reserved = wire.Reserved.ToArray();

            // libtorrent offers the v2 upgrade to v1 connections of hybrid torrents only.
            if (wire.HybridUpgradeBit && torrent.IsHybrid && torrent.InfoHashes.Count > 0
                && request.AsSpan(28, 20).SequenceEqual(torrent.InfoHashes[0])) reserved[7] |= 0x10;
            Reserved = reserved;
            Style = wire.Style;
            Extensions = wire.SupportsExtensions && (request[25] & 0x10) != 0;
            Fast = wire.SupportsFast && (request[27] & 0x04) != 0;
            Dht = wire.SupportsDht && (request[27] & 0x01) != 0;
        }

        internal byte[] Reserved { get; }
        internal ExtensionHandshakeStyle Style { get; }
        internal bool Extensions { get; }
        internal bool Fast { get; }

        /// <summary>Both sides run a DHT: libtorrent and Transmission then send their DHT port (BEP 5).</summary>
        internal bool Dht { get; }
    }
}
