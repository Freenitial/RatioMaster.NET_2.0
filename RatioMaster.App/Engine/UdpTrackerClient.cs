namespace RatioMaster.Engine;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.BitTorrent;

/// <summary>
/// Direct UDP tracker transport (BEP 15). Callers must exclude proxy sessions before calling.
/// Announce carries the original URL path/query in BEP 41 URLData options; scrape has no URL extension.
/// </summary>
internal sealed class UdpTrackerClient
{
    private const ulong ProtocolId = 0x41727101980;
    private const int MaxPacketBytes = 65507;
    private const int MaxRetryExponent = 3;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(4);
    private readonly TimeProvider timeProvider;

    internal UdpTrackerClient() : this(TimeProvider.System)
    {
    }

    internal UdpTrackerClient(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    internal Task<ValueDictionary> AnnounceAsync(Uri tracker, UdpAnnounceRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateTracker(tracker);
        ArgumentNullException.ThrowIfNull(request);
        ValidateHash(request.InfoHash, nameof(request.InfoHash));
        ValidateHash(request.PeerId, nameof(request.PeerId));
        ArgumentOutOfRangeException.ThrowIfNegative(request.Downloaded);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Left);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Uploaded);
        if (request.Event is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Event));
        }

        if (request.NumWant < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.NumWant));
        }

        if (request.Port == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Port));
        }

        byte[] options = BuildUrlOptions(tracker);
        byte[] packet = CreateRequest(1, 98 + options.Length);
        request.InfoHash.CopyTo(packet, 16);
        request.PeerId.CopyTo(packet, 36);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(56), request.Downloaded);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(64), request.Left);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(72), request.Uploaded);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(80), request.Event);
        // The 32-bit IP override at offset 84 is zero for both address families.
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(88), request.Key);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(92), request.NumWant);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(96), request.Port);
        options.CopyTo(packet, 98);
        return RequestAsync(tracker, packet, ct);
    }

    /// <summary>Returns the single torrent's complete/downloaded/incomplete values, without a files wrapper.</summary>
    internal Task<ValueDictionary> ScrapeAsync(Uri tracker, byte[] infoHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateTracker(tracker);
        ValidateHash(infoHash, nameof(infoHash));
        byte[] packet = CreateRequest(2, 36);
        infoHash.CopyTo(packet, 16);
        return RequestAsync(tracker, packet, ct);
    }

    private async Task<ValueDictionary> RequestAsync(Uri tracker, byte[] packet, CancellationToken ct)
    {
        using CancellationTokenSource deadline = new(OperationTimeout, timeProvider);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        CancellationToken operationCt = operation.Token;
        Socket? socket = null;
        try
        {
            IPAddress[] addresses = await ResolveAsync(tracker, operationCt).ConfigureAwait(false);
            // Covers the largest non-jumbo UDP datagram, so an oversized reply cannot be silently truncated.
            byte[] receiveBuffer = new byte[65536];
            byte[] connect = CreateRequest(0, 16);
            BinaryPrimitives.WriteUInt64BigEndian(connect, ProtocolId);
            ulong connectionId = 0;
            long connectedAt = 0;
            bool hasConnection = false;
            bool connectedOnce = false;
            int addressIndex = 0;
            Exception? lastFailure = null;

            // BEP 15 retransmissions wait 15, 30, 60 and 120 seconds. The operation also has a hard deadline.
            for (int retry = 0; retry <= MaxRetryExponent; retry++)
            {
                operationCt.ThrowIfCancellationRequested();
                try
                {
                    if (socket == null)
                    {
                        IPAddress address = addresses[addressIndex++ % addresses.Length];
                        socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                        if (address.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            socket.DualMode = false;
                        }

                        await socket.ConnectAsync(new IPEndPoint(address, tracker.Port), operationCt).ConfigureAwait(false);
                        RandomNumberGenerator.Fill(connect.AsSpan(12, 4));
                        RandomNumberGenerator.Fill(packet.AsSpan(12, 4));
                    }

                    if (hasConnection && timeProvider.GetElapsedTime(connectedAt) >= TimeSpan.FromMinutes(1))
                    {
                        hasConnection = false;
                        RandomNumberGenerator.Fill(connect.AsSpan(12, 4));
                    }

                    if (!hasConnection)
                    {
                        int length = await ExchangeAsync(socket, connect, receiveBuffer, retry, operationCt).ConfigureAwait(false);
                        RequireLength(length, 16, "connect");
                        connectionId = BinaryPrimitives.ReadUInt64BigEndian(receiveBuffer.AsSpan(8));
                        connectedAt = timeProvider.GetTimestamp();
                        hasConnection = true;
                        if (!connectedOnce)
                        {
                            // Announce/scrape starts its own backoff; connection renewal keeps that backoff.
                            retry = 0;
                            connectedOnce = true;
                        }
                    }

                    BinaryPrimitives.WriteUInt64BigEndian(packet, connectionId);
                    int received = await ExchangeAsync(socket, packet, receiveBuffer, retry, operationCt).ConfigureAwait(false);
                    operationCt.ThrowIfCancellationRequested();
                    return ParseResponse(receiveBuffer.AsSpan(0, received), socket.AddressFamily,
                        BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8)));
                }
                catch (TimeoutException ex)
                {
                    lastFailure = ex;
                    if (!hasConnection && addresses.Length > 1)
                    {
                        socket?.Dispose();
                        socket = null;
                    }
                }
                catch (SocketException ex)
                {
                    operationCt.ThrowIfCancellationRequested();
                    lastFailure = ex;
                    socket?.Dispose();
                    socket = null;
                    hasConnection = false;
                }
            }

            operationCt.ThrowIfCancellationRequested();
            if (lastFailure is TimeoutException)
            {
                throw new TimeoutException("UDP tracker retry limit reached.", lastFailure);
            }

            throw new IOException("UDP tracker retry limit reached.", lastFailure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("UDP tracker operation exceeded four minutes.", ex);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private async Task<int> ExchangeAsync(Socket socket, byte[] packet, byte[] buffer, int retry, CancellationToken ct)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15 * (1 << retry)), timeProvider);
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        CancellationToken attemptCt = attempt.Token;
        uint transaction = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12));
        int expectedAction = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(8));
        try
        {
            int sent = await socket.SendAsync(packet.AsMemory(), SocketFlags.None, attemptCt).ConfigureAwait(false);
            if (sent != packet.Length)
            {
                throw new IOException("UDP tracker request was not sent in full.");
            }

            while (true)
            {
                // Discarding unrelated datagrams must not extend the deadline or starve cancellation.
                attemptCt.ThrowIfCancellationRequested();
                int length = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, attemptCt).ConfigureAwait(false);
                attemptCt.ThrowIfCancellationRequested();
                if (length < 8 || BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4)) != transaction)
                {
                    continue;
                }

                if (length > MaxPacketBytes)
                {
                    throw new InvalidDataException("UDP tracker response exceeds the packet size limit.");
                }

                int action = BinaryPrimitives.ReadInt32BigEndian(buffer);
                if (action == 3)
                {
                    int messageLength = Math.Min(length - 8, 1024);
                    string message = Encoding.UTF8.GetString(buffer, 8, messageLength);
                    string suffix = length - 8 > messageLength ? " (truncated)" : string.Empty;
                    throw new TrackerRejectedException(message + suffix);
                }

                if (action == expectedAction)
                {
                    return length;
                }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"UDP tracker action {expectedAction} timed out.", ex);
        }
    }

    private async Task<IPAddress[]> ResolveAsync(Uri tracker, CancellationToken ct)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15), timeProvider);
        using CancellationTokenSource lookup = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            IPAddress[] resolved = IPAddress.TryParse(tracker.DnsSafeHost, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(tracker.IdnHost, lookup.Token).ConfigureAwait(false);
            List<IPAddress> usable = [];
            foreach (IPAddress candidate in resolved)
            {
                IPAddress address = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
                if (((address.AddressFamily == AddressFamily.InterNetwork && Socket.OSSupportsIPv4)
                    || (address.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6))
                    && !usable.Contains(address))
                {
                    usable.Add(address);
                }
            }

            if (usable.Count == 0)
            {
                throw new IOException("UDP tracker has no address supported by this host.");
            }

            // Put the other address family next so a silent route cannot exhaust every attempt on one family.
            int otherFamily = usable.FindIndex(address => address.AddressFamily != usable[0].AddressFamily);
            if (otherFamily > 1)
            {
                IPAddress fallback = usable[otherFamily];
                usable.RemoveAt(otherFamily);
                usable.Insert(1, fallback);
            }

            return usable.ToArray();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("UDP tracker DNS lookup timed out.", ex);
        }
    }

    private static ValueDictionary ParseResponse(ReadOnlySpan<byte> packet, AddressFamily family, int action)
    {
        RequireLength(packet.Length, 20, action == 1 ? "announce" : "scrape");
        ValueDictionary result = new();
        if (action == 2)
        {
            result["complete"] = new ValueNumber(BinaryPrimitives.ReadUInt32BigEndian(packet[8..]));
            result["downloaded"] = new ValueNumber(BinaryPrimitives.ReadUInt32BigEndian(packet[12..]));
            result["incomplete"] = new ValueNumber(BinaryPrimitives.ReadUInt32BigEndian(packet[16..]));
            return result;
        }

        int stride = family == AddressFamily.InterNetworkV6 ? 18 : 6;
        if ((packet.Length - 20) % stride != 0)
        {
            throw new InvalidDataException("UDP tracker announce contains an incomplete compact peer record.");
        }

        result["interval"] = new ValueNumber(BinaryPrimitives.ReadUInt32BigEndian(packet[8..]));
        result["incomplete"] = new ValueNumber(BinaryPrimitives.ReadUInt32BigEndian(packet[12..]));
        result["complete"] = new ValueNumber(BinaryPrimitives.ReadUInt32BigEndian(packet[16..]));
        result[family == AddressFamily.InterNetworkV6 ? "peers6" : "peers"] =
            new ValueString(Encoding.Latin1.GetString(packet[20..]));
        return result;
    }

    private static byte[] CreateRequest(int action, int length)
    {
        byte[] packet = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), action);
        RandomNumberGenerator.Fill(packet.AsSpan(12, 4));
        return packet;
    }

    private static byte[] BuildUrlOptions(Uri tracker)
    {
        // OriginalString retains percent escapes, private paths, repeated query keys and their order.
        string original = tracker.OriginalString;
        int authority = original.IndexOf("://", StringComparison.Ordinal) + 3;
        int start = original.IndexOfAny(['/', '?', '#'], authority);
        int end = start < 0 ? -1 : original.IndexOf('#', start);
        string pathAndQuery = start < 0 || original[start] == '#'
            ? string.Empty
            : original.Substring(start, (end < 0 ? original.Length : end) - start);
        int byteCount = Encoding.UTF8.GetByteCount(pathAndQuery);
        int chunks = Math.Max(1, (byteCount + 254) / 255);
        if (byteCount > MaxPacketBytes - 98 || 98 + byteCount + 2 * chunks > MaxPacketBytes)
        {
            throw new ArgumentException("UDP tracker URL does not fit in an announce datagram.", nameof(tracker));
        }

        byte[] url = Encoding.UTF8.GetBytes(pathAndQuery);
        byte[] options = new byte[byteCount + 2 * chunks];
        int source = 0;
        int target = 0;
        do
        {
            int length = Math.Min(255, url.Length - source);
            options[target++] = 2;
            options[target++] = (byte)length;
            url.AsSpan(source, length).CopyTo(options.AsSpan(target));
            source += length;
            target += length;
        }
        while (source < url.Length);
        return options;
    }

    private static void ValidateTracker(Uri tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (!tracker.IsAbsoluteUri || !tracker.Scheme.Equals("udp", StringComparison.OrdinalIgnoreCase)
            || !tracker.OriginalString.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)
            || tracker.Host.Length == 0 || tracker.Port is < 1 or > 65535)
        {
            throw new ArgumentException("An absolute udp:// tracker URL with an explicit port is required.", nameof(tracker));
        }

        if (tracker.UserInfo.Length != 0)
        {
            throw new ArgumentException("UDP tracker user-info authentication is unsupported; use a path/query passkey.", nameof(tracker));
        }
    }

    private static void ValidateHash(byte[] value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length != 20)
        {
            throw new ArgumentException("Exactly 20 bytes are required.", name);
        }
    }

    private static void RequireLength(int actual, int minimum, string action)
    {
        if (actual < minimum)
        {
            throw new InvalidDataException($"UDP tracker {action} response is truncated ({actual} bytes, minimum {minimum}).");
        }
    }
}

internal sealed class TrackerRejectedException(string message) : IOException(message);
