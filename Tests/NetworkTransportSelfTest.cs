#if DEBUG
namespace RatioMaster;

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RatioMaster.BitTorrent;
using RatioMaster.Engine;
using RatioMaster.Models;

/// <summary>Deterministic local HTTP and proxy fixtures exercising the application transport.</summary>
internal static class NetworkTransportSelfTest
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("d8:intervali120e5:peers0:e");
    private static readonly ClientProfile Profile = new()
    {
        HttpProtocol = "HTTP/1.1",
        Headers = "Host: {host}\r\nUser-Agent: TransportFixture/1\r\nConnection: Keep-Alive\r\n",
    };
    private static int checks;
    private static int failures;

    internal static int Run() => RunAsync().GetAwaiter().GetResult();

    internal static async Task<int> RunAsync()
    {
        checks = failures = 0;
        Console.WriteLine("--- Local HTTP and proxy transport checks ---");
        try
        {
            CheckBencoding();
            await CheckHttp("Content-Length completes before keep-alive closure", Response(Body), r => r?.Dict != null, holdOpen: true,
                inspect: request => request.Contains("Connection: Keep-Alive\r\n") && !request.Contains("Connection: close\r\n"));
            await CheckHttp("initial request preserves escapes, duplicate keys and fragment boundary", Response(Body), r => r?.Dict != null,
                target: "/a/%2f%2F?x=%aa&x=%AA&flag=#unused",
                inspect: request => request.StartsWith("GET /a/%2f%2F?x=%aa&x=%AA&flag= HTTP/1.1\r\n", StringComparison.Ordinal));
            await CheckHttp("informational response is followed by final response",
                Join(Ascii("HTTP/1.1 103 Early Hints\r\nLink: </a>\r\n\r\n"), Response(Body)), r => r?.Dict != null, true);
            await CheckHttp("204 completes without connection closure", Ascii("HTTP/1.1 204 No Content\r\n\r\n"), r => r?.StatusCode == 204 && r.Dict == null, true);
            await CheckHttp("304 ignores representation Content-Length", Ascii("HTTP/1.1 304 Not Modified\r\nContent-Length: 99999999\r\n\r\n"),
                r => r?.StatusCode == 304 && r.Dict == null, true);
            await CheckHttp("HTTP/1.0 EOF-delimited response", Join(Ascii("HTTP/1.0 200 OK\r\n\r\n"), Body), r => r?.Dict != null);
            await CheckHttp("identical Content-Length values are accepted",
                Response(Body, extra: "Content-Length: " + Body.Length + "\r\n"), r => r?.Dict != null, true);
            await CheckHttp("chunk extensions and trailers complete while connection stays open",
                Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"
                    + "1;kind=\"a\\\"b\"\r\nd\r\n" + (Body.Length - 1).ToString("X") + "\r\n"
                    + Encoding.ASCII.GetString(Body[1..]) + "\r\n0\r\nX-Trace: fixture\r\n\r\n"), r => r?.Dict != null, true);
            foreach ((string name, string raw) in new[]
            {
                ("truncated Content-Length", "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nde"),
                ("conflicting Content-Length", "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 3\r\n\r\nde"),
                ("ambiguous transfer coding and length", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 2\r\n\r\n"),
                ("invalid chunk size", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nZ\r\nde\r\n0\r\n\r\n"),
                ("truncated chunk", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nde"),
                ("missing chunk terminator", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\ndeXX0\r\n\r\n"),
                ("missing final trailers terminator", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nde\r\n0\r\n"),
                ("forbidden framing trailer", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nContent-Length: 2\r\n\r\n"),
                ("unterminated chunk extension", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1;x=\"no\r\nd\r\n0\r\n\r\n"),
                ("protocol switching", "HTTP/1.1 101 Switching Protocols\r\n\r\n"),
                ("invalid status line", "BAD 200 OK\r\n\r\n"),
                ("bare LF header termination", "HTTP/1.1 200 OK\n\n"),
                ("oversized Content-Length before allocation", "HTTP/1.1 200 OK\r\nContent-Length: 8388608\r\n\r\n"),
                ("oversized header line", "HTTP/1.1 200 OK\r\nX: " + new string('a', 8192) + "\r\n\r\n"),
                ("unsupported transfer coding", "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n"),
            })
                await CheckHttp(name + " rejected", Ascii(raw), _ => false, expectError: true);

            await CheckHttp("HTTP failure cannot validate bencoded success", Response(Body, status: 503), r => r?.StatusCode == 503 && r.Dict == null);
            await CheckHttp("HTTP error preserves an explicit empty tracker refusal", Response(Ascii("d14:failure reason0:e"), status: 503),
                r => r?.StatusCode == 503 && r.Dict?.Contains("failure reason") == true);
            await CheckHttp("trailing bencode bytes invalidate a response", Response(Join(Body, Ascii("junk"))), r => r?.Dict == null);
            foreach (string format in new[] { "gzip", "x-gzip", "deflate", "raw-deflate", "x-deflate", "br" })
            {
                byte[] compressed = Compress(Body, format);
                string coding = format == "raw-deflate" ? "deflate" : format;
                await CheckHttp(format + " decoded", Response(compressed, extra: "Content-Encoding: " + coding + "\r\n"), r => r?.Dict != null, true);
                await CheckHttp("truncated " + format + " refused", Response(compressed[..^1], extra: "Content-Encoding: " + coding + "\r\n"),
                    r => r is { Dict: null, Oversized: false, Error.Length: > 0 });
            }
            byte[] damaged = Compress(Body, "gzip");
            damaged[^8] ^= 0x80;
            await CheckHttp("gzip checksum mismatch refused", Response(damaged, extra: "Content-Encoding: gzip\r\n"),
                r => r is { Dict: null, Error.Length: > 0 });
            await CheckHttp("mislabeled gzip is not accepted as plain bencode", Response(Body, extra: "Content-Encoding: gzip\r\n"), r => r?.Dict == null);
            await CheckHttp("concatenated gzip members decoded", Response(Join(Compress(Body[..4], "gzip"), Compress(Body[4..], "gzip")),
                extra: "Content-Encoding: gzip\r\n"), r => r?.Dict != null);
            await CheckHttp("content encoding layers decoded in reverse order",
                Response(Compress(Compress(Body, "gzip"), "br"), extra: "Content-Encoding: gzip, br\r\n"), r => r?.Dict != null);
            await CheckHttp("unknown content encoding refused", Response(Body, extra: "Content-Encoding: mystery\r\n"), r => r is { Dict: null, Error.Length: > 0 });
            await CheckHttp("gzip expansion bounded", Response(Compress(new byte[TrackerResponse.MaxBodyBytes + 1], "gzip"),
                extra: "Content-Encoding: gzip\r\n"), r => r?.Oversized == true && r.Dict == null);
            await CheckHttp("Brotli expansion bounded", Response(Compress(new byte[TrackerResponse.MaxBodyBytes + 1], "br"),
                extra: "Content-Encoding: br\r\n"), r => r?.Oversized == true && r.Dict == null);
            await CheckFragmentation();
            await CheckRedirects();
            await CheckCancellation(false);
            await CheckCancellation(true);
            await CheckRetry();
            await CheckConnect("CONNECT IPv6 authority and UTF-8 credentials", "HTTP/1.1 200 Connection Established\r\n\r\n", success: true);
            await CheckConnect("CONNECT any 2xx establishes tunnel", "HTTP/1.1 201 Created\r\n\r\n", success: true);
            await CheckConnect("CONNECT rejects misleading 200 header", "HTTP/1.1 407 Proxy Authentication Required\r\nX-Note: 200\r\n\r\n", false);
            await CheckConnect("CONNECT rejects incomplete headers", "HTTP/1.1 200 Connection Established\r\n", false);
            await CheckConnect("CONNECT headers bounded", "HTTP/1.1 200 OK\r\nX: " + new string('x', 8192) + "\r\n\r\n", false);
            foreach (ProxyKind kind in new[] { ProxyKind.Socks4, ProxyKind.Socks4a })
            {
                await CheckSocks4(kind, "127.0.0.1", valid: true);
                await CheckSocks4(kind, "127.0.0.1", valid: false);
            }
            await CheckSocks4(ProxyKind.Socks4a, "fixture.invalid", true);
            foreach (string host in new[] { "127.0.0.1", "[2001:db8::1]", "fixture.invalid" })
                await CheckSocks5(host, "valid");
            await CheckSocks5("fixture.invalid", "credentials");
            foreach (string invalid in new[] { "version", "unoffered", "auth-version", "reply-version", "reserved", "address-type", "truncated", "long-password" })
                await CheckSocks5("fixture.invalid", invalid);
            if (Socket.OSSupportsIPv6)
            {
                await CheckHttp("IPv6 first hop to tracker", Response(Body), r => r?.Dict != null, true, address: IPAddress.IPv6Loopback);
                await CheckConnect("IPv6 first hop to proxy", "HTTP/1.1 200 OK\r\n\r\n", true, IPAddress.IPv6Loopback);
            }
            else Console.WriteLine("  [SKIP] IPv6 fixtures: IPv6 unavailable on this host.");
        }
        catch (Exception ex)
        {
            Check(false, "fixture failure: " + ex);
        }
        Console.WriteLine($"Network transport: {checks - failures}/{checks} checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string name)
    {
        checks++;
        if (!condition) failures++;
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
    }

    private static void CheckBencoding()
    {
        foreach (string invalid in new[]
        {
            "d1:xi-0ee", "d1:xi01ee", "d1:xi-01ee", "d1:xiee", "d1:xi--1ee",
            "d1:xi9223372036854775808ee", "d1:x01:ae", "d1:x00:e", "d1:xi1e1:xi2ee",
        })
        {
            TrackerResponse response = new(Response(Ascii(invalid)));
            Check(response.Dict is null && response.Error.Length > 0, "malformed tracker bencoding refused: " + invalid);
        }

        string manyNodes = "d1:xl" + string.Concat(Enumerable.Repeat("0:", BEncode.MaxNodes)) + "ee";
        TrackerResponse excessive = new(Response(Ascii(manyNodes)));
        Check(excessive.Dict is null && excessive.Error.Contains("node limit", StringComparison.Ordinal),
            "tracker bencoding bounds allocation count within the receive limit");
        Check(new TrackerResponse(Response(Body)).Dict is not null,
            "bencoding budget resets after a rejected document");

        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            custom.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = custom;
            ValueNumber encoded = new(long.MinValue);
            Check(Encoding.ASCII.GetString(encoded.Encode()) == "i-9223372036854775808e",
                "bencoded integer serialization uses invariant decimal notation");
            using MemoryStream input = new(Ascii("i-9223372036854775808e"));
            Check(BEncode.Parse(input) is ValueNumber number && number.Integer == long.MinValue,
                "bencoded integer decoding is independent of the process culture");
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static async Task CheckHttp(string name, byte[] response, Func<TrackerResponse?, bool> check, bool holdOpen = false,
        bool expectError = false, string target = "/announce", Func<string, bool>? inspect = null, IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        TcpListener listener = new(address, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(6));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string request = string.Empty;
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using NetworkStream stream = peer.GetStream();
            request = await ReadHeaders(stream, deadline.Token);
            try { await stream.WriteAsync(response, deadline.Token); }
            catch (IOException) when (expectError) { }
            if (holdOpen) await release.Task.WaitAsync(deadline.Token);
        });
        try
        {
            string host = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
            TrackerClient client = new(new ProxyConfig(), _ => { }) { RequestTimeout = TimeSpan.FromSeconds(3) };
            try
            {
                TrackerResponse? result = await client.RequestAsync($"http://{host}:{port}{target}", Profile, deadline.Token);
                Check(!expectError && check(result) && (inspect?.Invoke(request) ?? true), name);
            }
            catch (IOException) { Check(expectError, name); }
        }
        finally
        {
            release.TrySetResult();
            try { await server; }
            finally { listener.Stop(); }
        }
    }

    private static async Task CheckFragmentation()
    {
        byte[] raw = Join(Ascii("HTTP/1.1 100 Continue\r\n\r\n"), Response(Body));
        using FragmentedInput input = new(raw);
        HttpResponseReader.Message message = await HttpResponseReader.ReadAsync(input, CancellationToken.None);
        Check(new TrackerResponse(message).Dict != null, "headers and body split at every byte boundary");
    }

    private static async Task CheckRedirects()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(6));
        string last = string.Empty;
        Task server = Task.Run(async () =>
        {
            for (int index = 0; index < 2; index++)
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
                using NetworkStream stream = peer.GetStream();
                string request = await ReadHeaders(stream, deadline.Token);
                if (index == 0)
                    await stream.WriteAsync(Response([], "Location: ../a/%2f?x=%aa&x=%AA#drop\r\n", 302), deadline.Token);
                else
                {
                    last = request;
                    await stream.WriteAsync(Response(Body), deadline.Token);
                }
            }
        });
        try
        {
            TrackerResponse? response = await new TrackerClient(new(), _ => { }).RequestAsync(
                $"http://127.0.0.1:{port}/base/announce?secret=%2F", Profile, deadline.Token);
            await server;
            Check(response?.Dict != null && last.StartsWith("GET /a/%2f?x=%aa&x=%AA HTTP/1.1\r\n", StringComparison.Ordinal),
                "relative redirect preserves encoded request target through transport");
            Check(TrackerClient.ResolveRedirect("http://a.test/x/%2e%2e/z?x=1", "?x=%2f&x=%2F") ==
                "http://a.test/x/%2e%2e/z?x=%2f&x=%2F", "query-only redirect preserves encoded path");
        }
        finally { listener.Stop(); }
    }

    private static async Task CheckCancellation(bool user)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(5));
        using CancellationTokenSource canceled = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(guard.Token);
            using NetworkStream stream = peer.GetStream();
            await ReadHeaders(stream, guard.Token);
            if (user) canceled.Cancel();
            await release.Task.WaitAsync(guard.Token);
        });
        try
        {
            TrackerClient client = new(new(), _ => { }) { RequestTimeout = TimeSpan.FromMilliseconds(user ? 3000 : 250) };
            try
            {
                await client.RequestAsync($"http://127.0.0.1:{port}/announce", Profile, canceled.Token);
                Check(false, user ? "user cancellation preserved" : "request deadline surfaced as timeout");
            }
            catch (OperationCanceledException) { Check(user, "user cancellation preserved"); }
            catch (IOException ex) { Check(!user && ex.Message.Contains("timed out"), "request deadline surfaced as timeout"); }
        }
        finally { release.TrySetResult(); await server; listener.Stop(); }
    }

    private static async Task CheckRetry()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        listener = new(IPAddress.Loopback, port);
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(5));
        TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        Task server = Task.Run(async () =>
        {
            await listening.Task.WaitAsync(guard.Token);
            using TcpClient peer = await listener.AcceptTcpClientAsync(guard.Token);
            using NetworkStream stream = peer.GetStream();
            await ReadHeaders(stream, guard.Token);
            await stream.WriteAsync(Response(Body), guard.Token);
        });
        try
        {
            TrackerClient client = new(new(), entry =>
            {
                if (entry.StartsWith("Connection attempt ", StringComparison.Ordinal) && Interlocked.Increment(ref attempts) == 1)
                {
                    listener.Start();
                    listening.TrySetResult();
                }
            });
            TrackerResponse? response = await client.RequestAsync($"http://127.0.0.1:{port}/announce", Profile, guard.Token);
            await server;
            Check(attempts == 1 && response?.Dict != null, "connection retry reacts to failure without replaying an announce");
        }
        finally { listener.Stop(); }
    }

    private static async Task CheckConnect(string name, string reply, bool success, IPAddress? address = null)
    {
        await WithProxy(ProxyKind.HttpConnect, "u", "p", address, async (stream, ct) =>
        {
            string connect = await ReadHeaders(stream, ct);
            bool authority = connect.StartsWith("CONNECT [2001:db8::1]:81 HTTP/1.1\r\n", StringComparison.Ordinal)
                && connect.Contains("Host: [2001:db8::1]:81\r\n") && connect.Contains("Proxy-Authorization: Basic dTpw\r\n");
            await stream.WriteAsync(Ascii(reply), ct);
            if (!success) return true;
            string request = await ReadHeaders(stream, ct);
            await stream.WriteAsync(Response(Body), ct);
            return authority && request.Contains("Host: [2001:db8::1]:81\r\n");
        }, "http://[2001:db8::1]:81/announce", success, name);
    }

    private static async Task CheckSocks4(ProxyKind kind, string host, bool valid)
    {
        await WithProxy(kind, "u", "", null, async (stream, ct) =>
        {
            byte[] head = await ReadExact(stream, 8, ct);
            string user = await ReadNullTerminated(stream, ct);
            bool domain = host == "fixture.invalid";
            bool expected = head[0] == 4 && head[1] == 1 && head[2] == 0 && head[3] == 81 && user == "u";
            if (domain)
                expected &= head.AsSpan(4).SequenceEqual(new byte[] { 0, 0, 0, 1 }) && await ReadNullTerminated(stream, ct) == host;
            else expected &= head.AsSpan(4).SequenceEqual(new byte[] { 127, 0, 0, 1 });
            await stream.WriteAsync(new byte[] { valid ? (byte)0 : (byte)4, 0x5A, 0, 0, 0, 0, 0, 0 }, ct);
            if (!valid) return true;
            await ReadHeaders(stream, ct);
            await stream.WriteAsync(Response(Body), ct);
            return expected;
        }, $"http://{host}:81/announce", valid, $"{kind} {(valid ? host + " destination encoding" : "invalid response version rejected")}");
    }

    private static async Task CheckSocks5(string host, string mode)
    {
        bool auth = mode is "credentials" or "auth-version" or "long-password";
        bool success = mode is "valid" or "credentials";
        await WithProxy(ProxyKind.Socks5, auth ? "é" : "", mode == "long-password" ? new string('p', 256) : auth ? "pass" : "", null,
            async (stream, ct) =>
            {
                if (mode == "long-password")
                {
                    Check(await stream.ReadAsync(new byte[1], ct) == 0, "SOCKS5 oversized credentials rejected before handshake");
                    return true;
                }
                byte[] greeting = await ReadExact(stream, 2, ct);
                byte[] methods = await ReadExact(stream, greeting[1], ct);
                bool valid = greeting[0] == 5 && methods.Contains((byte)0) && (!auth || methods.Contains((byte)2));
                await stream.WriteAsync(new byte[] { mode == "version" ? (byte)4 : (byte)5, mode == "unoffered" || auth ? (byte)2 : (byte)0 }, ct);
                if (mode is "version" or "unoffered") return true;
                if (auth)
                {
                    byte[] authHead = await ReadExact(stream, 2, ct);
                    byte[] username = await ReadExact(stream, authHead[1], ct);
                    byte[] size = await ReadExact(stream, 1, ct);
                    byte[] password = await ReadExact(stream, size[0], ct);
                    valid &= authHead[0] == 1 && Encoding.UTF8.GetString(username) == "é" && Encoding.UTF8.GetString(password) == "pass";
                    await stream.WriteAsync(mode == "auth-version" ? new byte[] { 2, 0 } : [1, 0], ct);
                    if (mode == "auth-version") return true;
                }
                byte[] request = await ReadExact(stream, 4, ct);
                byte[] destination;
                if (request[3] == 3) destination = await ReadExact(stream, (await ReadExact(stream, 1, ct))[0], ct);
                else destination = await ReadExact(stream, request[3] == 1 ? 4 : 16, ct);
                byte[] port = await ReadExact(stream, 2, ct);
                valid &= request[0] == 5 && request[1] == 1 && request[2] == 0 && port[0] == 0 && port[1] == 81;
                if (host == "fixture.invalid") valid &= request[3] == 3 && Encoding.ASCII.GetString(destination) == host;
                else valid &= request[3] == (host.StartsWith('[') ? 4 : 1)
                    && destination.SequenceEqual(IPAddress.Parse(host.Trim('[', ']')).GetAddressBytes());
                byte[] reply = [mode == "reply-version" ? (byte)4 : (byte)5, 0, mode == "reserved" ? (byte)1 : (byte)0,
                    mode == "address-type" ? (byte)9 : (byte)1, 127, 0, 0, 1, 0, 0];
                await stream.WriteAsync(mode == "truncated" ? reply[..^1] : reply, ct);
                if (!success) return true;
                await ReadHeaders(stream, ct);
                await stream.WriteAsync(Response(Body), ct);
                return valid;
            }, $"http://{host}:81/announce", success, "SOCKS5 " + mode + " " + host);
    }

    private static async Task WithProxy(ProxyKind kind, string user, string password, IPAddress? address,
        Func<NetworkStream, CancellationToken, Task<bool>> handler, string url, bool success, string name)
    {
        address ??= IPAddress.Loopback;
        TcpListener listener = new(address, 0);
        listener.Start();
        using CancellationTokenSource guard = new(TimeSpan.FromSeconds(6));
        Task<bool> server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(guard.Token);
            using NetworkStream stream = peer.GetStream();
            return await handler(stream, guard.Token);
        });
        try
        {
            ProxyConfig proxy = new() { Kind = kind, Host = address.ToString(), Port = ((IPEndPoint)listener.LocalEndpoint).Port, User = user, Password = password };
            try
            {
                TrackerResponse? response = await new TrackerClient(proxy, _ => { }).RequestAsync(url, Profile, guard.Token);
                bool correctHandshake = await server;
                Check(success && correctHandshake && response?.Dict != null, name);
            }
            catch (IOException)
            {
                bool correctHandshake = await server;
                Check(!success && correctHandshake, name);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
    {
        StringBuilder text = new();
        byte[] one = new byte[1];
        while (text.Length <= 65536)
        {
            await stream.ReadExactlyAsync(one, ct);
            text.Append((char)one[0]);
            int n = text.Length;
            if (n >= 4 && text[n - 4] == '\r' && text[n - 3] == '\n' && text[n - 2] == '\r' && text[n - 1] == '\n')
                return text.ToString();
        }
        throw new IOException("Fixture request headers exceeded the bound.");
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length, CancellationToken ct)
    {
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task<string> ReadNullTerminated(Stream stream, CancellationToken ct)
    {
        StringBuilder value = new();
        while (value.Length < 256)
        {
            byte b = (await ReadExact(stream, 1, ct))[0];
            if (b == 0) return value.ToString();
            value.Append((char)b);
        }
        throw new IOException("Fixture string exceeded the bound.");
    }

    private static byte[] Response(byte[] body, string extra = "", int status = 200) =>
        Join(Ascii($"HTTP/1.1 {status} Fixture\r\nContent-Length: {body.Length}\r\n{extra}\r\n"), body);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Join(byte[] first, byte[] second)
    {
        byte[] bytes = new byte[first.Length + second.Length];
        first.CopyTo(bytes, 0);
        second.CopyTo(bytes, first.Length);
        return bytes;
    }

    private static byte[] Compress(byte[] body, string format)
    {
        using MemoryStream output = new();
        using (Stream encoder = format switch
        {
            "gzip" or "x-gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "raw-deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" or "x-deflate" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentException("Unknown fixture compression."),
        })
            encoder.Write(body);
        return output.ToArray();
    }

    private sealed class FragmentedInput(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }
}
#endif
