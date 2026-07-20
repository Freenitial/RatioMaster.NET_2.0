namespace RatioMaster.Engine;

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.Models;

/// <summary>
/// Sends BitTorrent tracker announce/scrape requests over a raw socket so the
/// pre-percent-encoded info_hash in the query survives verbatim (HttpClient/Uri
/// would re-normalise it). Supports HTTPS and HTTP/SOCKS4/4a/5 proxies.
/// </summary>
internal sealed class TrackerClient(ProxyConfig proxy, Action<string> log)
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    internal async Task<TrackerResponse?> RequestAsync(string url, ClientProfile client, CancellationToken ct)
    {
        for (int redirect = 0; redirect < 5; redirect++)
        {
            if (!TryParseUrl(url, out string scheme, out string host, out int port, out string pathAndQuery))
            {
                log("Invalid tracker URL: " + url);
                return null;
            }

            bool https = scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            Socket? socket = null;
            Stream? stream = null;
            try
            {
                // Bound EVERY network step (connect + TLS handshake + write + read) so a slow-loris,
                // blackhole or MITM tracker can't wedge an announce forever: ioCt trips on the session's
                // ct OR after a hard timeout. Without it, a stalled TLS handshake or a withheld response
                // body hangs until process exit and (because announceBusy is only cleared in the caller's
                // finally) freezes every later announce for the session, not just this one.
                using CancellationTokenSource io = CancellationTokenSource.CreateLinkedTokenSource(ct);
                io.CancelAfter(TimeSpan.FromSeconds(30));
                CancellationToken ioCt = io.Token;

                log($"Connecting to tracker ({host}) on port {port}");
                socket = await ConnectAsync(host, port, ioCt).ConfigureAwait(false);
                log("Connected successfully");

                stream = new NetworkStream(socket, ownsSocket: false);
                if (https)
                {
                    // Certificate validation is left at the .NET default (it used to be a callback that
                    // returned true for ANY certificate). The announce query carries the tracker passkey,
                    // so accepting any certificate meant a machine-in-the-middle could read the passkey AND
                    // hand back a bencode response the engine then trusted. TargetHost drives SNI, which is
                    // what modern trackers behind Cloudflare require.
                    SslStream ssl = new(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = host }, ioCt).ConfigureAwait(false);
                    stream = ssl;
                }

                string headers = client.Headers.Replace("{host}", host);
                if (!headers.ToLowerInvariant().Contains("connection: close"))
                {
                    headers = headers.TrimEnd('\r', '\n') + "\r\nConnection: close\r\n";
                }
                else if (!headers.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    headers += "\r\n";
                }

                string request = $"GET {pathAndQuery} {client.HttpProtocol}\r\n{headers}\r\n";
                request = request.TrimEnd('\r', '\n') + "\r\n\r\n";
                log("======== Sending Command to Tracker ========");
                log(request);

                byte[] reqBytes = Latin1.GetBytes(request);
                await stream.WriteAsync(reqBytes, ioCt).ConfigureAwait(false);
                await stream.FlushAsync(ioCt).ConfigureAwait(false);

                // Bounded read: CopyToAsync would happily buffer a multi-gigabyte reply from a hostile or
                // broken tracker. Stop at the cap and treat it as a failed announce instead.
                using MemoryStream mem = new();
                byte[] chunk = new byte[16 * 1024];
                int got;
                bool tooLarge = false;
                while ((got = await stream.ReadAsync(chunk, ioCt).ConfigureAwait(false)) > 0)
                {
                    if (mem.Length + got > TrackerResponse.MaxBodyBytes)
                    {
                        tooLarge = true;
                        break;
                    }

                    mem.Write(chunk, 0, got);
                }

                stream.Dispose();

                if (tooLarge)
                {
                    log($"Error: tracker response exceeds {TrackerResponse.MaxBodyBytes / (1024 * 1024)} MB - ignored");
                    return null;
                }

                if (mem.Length == 0)
                {
                    log("Error: tracker response is empty");
                    return null;
                }

                TrackerResponse response = new(mem.ToArray());
                if (response.DoRedirect)
                {
                    // Resolve a RELATIVE Location (e.g. "/announce2?..." — legal per RFC 7231) against the
                    // current request's origin; TryParseUrl requires a scheme, so a bare relative target
                    // would otherwise be rejected and the announce would fail instead of following it.
                    string loc = response.RedirectionUrl;
                    if (loc.IndexOf("://", StringComparison.Ordinal) < 0)
                    {
                        string origin = $"{scheme}://{host}:{port}";
                        loc = loc.StartsWith('/') ? origin + loc : origin + "/" + loc;
                    }

                    log("Redirecting to: " + loc);
                    url = loc;
                    continue;
                }

                log("======== Tracker Response ========");
                log(response.Headers);

                if (response.Oversized)
                {
                    // Name the real cause. Without this the announce just looks like a connection failure.
                    log($"*** Tracker response inflates past {TrackerResponse.MaxBodyBytes / (1024 * 1024)} MB - refused");
                    return null;
                }

                if (response.Dict == null)
                {
                    // Truncate: an undecodable body can be megabytes of binary, and the log is built by
                    // string concatenation on the UI thread before it is trimmed.
                    log("*** Failed to decode tracker response:");
                    log(response.Body.Length > 2000 ? response.Body[..2000] + "… (truncated)" : response.Body);
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                log("Exception: " + ex.Message);
                return null;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        log("Too many redirects");
        return null;
    }

    private async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (proxy.Kind == ProxyKind.None)
        {
            // Dual-stack (InterNetworkV6 + DualMode) rather than IPv4-only: a tracker published solely on
            // an AAAA record was previously unreachable, which is inconsistent now that we parse BEP 7
            // IPv6 peer lists. DualMode still reaches IPv4 hosts via v4-mapped addresses.
            Socket direct = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
            {
                DualMode = true,
            };

            try
            {
                await direct.ConnectAsync(host, port, ct).ConfigureAwait(false);
            }
            catch
            {
                direct.Dispose(); // a failed/cancelled connect must not leak the Socket handle
                throw;
            }

            return direct;
        }

        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // The catch below now covers the initial connect too — a proxy that's down/refuses
            // must not leak the Socket, and neither must a failure in the handshake.
            await socket.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            NetworkStream ns = new(socket, ownsSocket: false);
            switch (proxy.Kind)
            {
                case ProxyKind.HttpConnect:
                    await HttpConnectAsync(ns, host, port, ct).ConfigureAwait(false);
                    break;
                case ProxyKind.Socks4:
                    await Socks4Async(ns, host, port, resolveRemotely: false, ct).ConfigureAwait(false);
                    break;
                case ProxyKind.Socks4a:
                    await Socks4Async(ns, host, port, resolveRemotely: true, ct).ConfigureAwait(false);
                    break;
                case ProxyKind.Socks5:
                    await Socks5Async(ns, host, port, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    private async Task HttpConnectAsync(NetworkStream ns, string host, int port, CancellationToken ct)
    {
        StringBuilder sb = new();
        sb.Append($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n");
        if (proxy.User.Length > 0)
        {
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.User + ":" + proxy.Password));
            sb.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }

        sb.Append("\r\n");
        byte[] req = Latin1.GetBytes(sb.ToString());
        await ns.WriteAsync(req, ct).ConfigureAwait(false);

        string response = await ReadLineHeadersAsync(ns, ct).ConfigureAwait(false);
        if (!response.Contains(" 200"))
        {
            throw new IOException("HTTP proxy CONNECT failed: " + response.Split('\n')[0].Trim());
        }
    }

    private async Task Socks4Async(NetworkStream ns, string host, int port, bool resolveRemotely, CancellationToken ct)
    {
        using MemoryStream req = new();
        req.WriteByte(0x04); // version
        req.WriteByte(0x01); // connect
        req.WriteByte((byte)(port >> 8));
        req.WriteByte((byte)(port & 0xFF));

        byte[]? ipBytes = null;
        if (!resolveRemotely)
        {
            System.Net.IPAddress[] addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            foreach (System.Net.IPAddress a in addrs)
            {
                if (a.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipBytes = a.GetAddressBytes();
                    break;
                }
            }
        }

        if (ipBytes == null)
        {
            // SOCKS4a: 0.0.0.x sentinel, hostname appended after the user id.
            req.Write([0, 0, 0, 1], 0, 4);
        }
        else
        {
            req.Write(ipBytes, 0, 4);
        }

        byte[] user = Encoding.ASCII.GetBytes(proxy.User);
        req.Write(user, 0, user.Length);
        req.WriteByte(0x00);
        if (ipBytes == null)
        {
            byte[] domain = Encoding.ASCII.GetBytes(host);
            req.Write(domain, 0, domain.Length);
            req.WriteByte(0x00);
        }

        byte[] reqArr = req.ToArray();
        await ns.WriteAsync(reqArr, ct).ConfigureAwait(false);

        byte[] resp = new byte[8];
        await ReadExactAsync(ns, resp, ct).ConfigureAwait(false);
        if (resp[1] != 0x5A)
        {
            throw new IOException("SOCKS4 proxy refused (code 0x" + resp[1].ToString("X2") + ")");
        }
    }

    private async Task Socks5Async(NetworkStream ns, string host, int port, CancellationToken ct)
    {
        bool auth = proxy.User.Length > 0;
        byte[] greeting = auth ? [0x05, 0x02, 0x00, 0x02] : [0x05, 0x01, 0x00];
        await ns.WriteAsync(greeting, ct).ConfigureAwait(false);

        byte[] methodResp = new byte[2];
        await ReadExactAsync(ns, methodResp, ct).ConfigureAwait(false);
        if (methodResp[1] == 0x02)
        {
            using MemoryStream a = new();
            a.WriteByte(0x01);
            byte[] u = Encoding.UTF8.GetBytes(proxy.User);
            byte[] p = Encoding.UTF8.GetBytes(proxy.Password);
            a.WriteByte((byte)u.Length);
            a.Write(u, 0, u.Length);
            a.WriteByte((byte)p.Length);
            a.Write(p, 0, p.Length);
            byte[] authArr = a.ToArray();
            await ns.WriteAsync(authArr, ct).ConfigureAwait(false);

            byte[] authResp = new byte[2];
            await ReadExactAsync(ns, authResp, ct).ConfigureAwait(false);
            if (authResp[1] != 0x00)
            {
                throw new IOException("SOCKS5 authentication failed");
            }
        }
        else if (methodResp[1] != 0x00)
        {
            throw new IOException("SOCKS5 proxy rejected auth methods");
        }

        using MemoryStream req = new();
        req.WriteByte(0x05);
        req.WriteByte(0x01); // connect
        req.WriteByte(0x00); // reserved
        req.WriteByte(0x03); // domain name
        byte[] domain = Encoding.ASCII.GetBytes(host);
        req.WriteByte((byte)domain.Length);
        req.Write(domain, 0, domain.Length);
        req.WriteByte((byte)(port >> 8));
        req.WriteByte((byte)(port & 0xFF));
        byte[] reqArr = req.ToArray();
        await ns.WriteAsync(reqArr, ct).ConfigureAwait(false);

        byte[] head = new byte[4];
        await ReadExactAsync(ns, head, ct).ConfigureAwait(false);
        if (head[1] != 0x00)
        {
            throw new IOException("SOCKS5 connect failed (code 0x" + head[1].ToString("X2") + ")");
        }

        int skip = head[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => 1, // followed by a length byte then that many
            _ => 0,
        };

        if (head[3] == 0x03)
        {
            byte[] len = new byte[1];
            await ReadExactAsync(ns, len, ct).ConfigureAwait(false);
            skip = len[0];
        }

        byte[] rest = new byte[skip + 2]; // address remainder + port
        await ReadExactAsync(ns, rest, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadLineHeadersAsync(NetworkStream ns, CancellationToken ct)
    {
        using MemoryStream ms = new();
        byte[] one = new byte[1];
        int crlfCount = 0;
        while (crlfCount < 4)
        {
            int n = await ns.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            ms.WriteByte(one[0]);
            crlfCount = one[0] is (byte)'\r' or (byte)'\n' ? crlfCount + 1 : 0;
        }

        return Latin1.GetString(ms.ToArray());
    }

    private static async Task ReadExactAsync(NetworkStream ns, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await ns.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("Proxy closed the connection unexpectedly");
            }

            read += n;
        }
    }

    private static bool TryParseUrl(string url, out string scheme, out string host, out int port, out string pathAndQuery)
    {
        scheme = "http";
        host = string.Empty;
        port = 80;
        pathAndQuery = "/";

        int schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx < 0)
        {
            return false;
        }

        scheme = url.Substring(0, schemeIdx);
        string rest = url.Substring(schemeIdx + 3);
        int slash = rest.IndexOf('/');
        string authority;
        if (slash < 0)
        {
            authority = rest;
            pathAndQuery = "/";
        }
        else
        {
            authority = rest.Substring(0, slash);
            pathAndQuery = rest.Substring(slash);
        }

        port = scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        int colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            host = authority.Substring(0, colon);
            if (int.TryParse(authority.Substring(colon + 1), out int p))
            {
                port = p;
            }
        }
        else
        {
            host = authority;
        }

        return host.Length > 0;
    }
}
