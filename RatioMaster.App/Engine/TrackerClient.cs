namespace RatioMaster.Engine;

using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using RatioMaster.Models;

/// <summary>Bounded HTTP tracker transport preserving escaped request targets and explicit proxy routing.</summary>
internal sealed class TrackerClient(ProxyConfig proxy, Action<string> log)
{
    private const int MaxRedirects = 5;
    private const int MaxConnectAttempts = 5;
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal async Task<TrackerResponse?> RequestAsync(string url, ClientProfile client, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            for (int redirect = 0; ; redirect++)
            {
                if (!TryParseUrl(url, out string scheme, out string host, out int port, out string pathAndQuery))
                    throw new IOException("Invalid HTTP tracker URL.");
                CancellationToken ioCt = deadline.Token;
                bool https = scheme == "https";
                log($"Connecting to tracker ({host}) on port {port}");
                using Socket socket = await ConnectAsync(host, port, ioCt).ConfigureAwait(false);
                using NetworkStream network = new(socket, ownsSocket: false);
                using SslStream? tls = https ? new(network, leaveInnerStreamOpen: true) : null;
                Stream stream = tls ?? (Stream)network;
                if (tls is not null)
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ioCt).ConfigureAwait(false);

                string request = BuildRequest(pathAndQuery, Authority(host, port, https ? 443 : 80), client);
                log("======== Sending Command to Tracker ========");
                log(request);
                await stream.WriteAsync(Latin1.GetBytes(request), ioCt).ConfigureAwait(false);
                await stream.FlushAsync(ioCt).ConfigureAwait(false);
                TrackerResponse response = new(await HttpResponseReader.ReadAsync(stream, ioCt).ConfigureAwait(false), ioCt);
                if (response.DoRedirect)
                {
                    if (redirect >= MaxRedirects) throw new IOException("Tracker exceeded the limit of five redirects.");
                    string destination = ResolveRedirect(url, response.RedirectionUrl);
                    if (!TryParseUrl(destination, out string nextScheme, out _, out _, out _))
                        throw new IOException("Invalid tracker redirect destination.");
                    if (https && nextScheme == "http")
                        throw new IOException("Refusing an insecure HTTPS-to-HTTP tracker redirect.");
                    log("Following tracker redirect.");
                    url = destination;
                    continue;
                }
                log("======== Tracker Response ========");
                log(response.Headers);
                if (response.Oversized) log("*** Tracker response exceeds the 8 MiB decoded body limit.");
                else if (response.StatusCode >= 400) log($"*** Tracker returned HTTP {response.StatusCode}.");
                else if (response.Error is { Length: > 0 }) log("*** " + response.Error);
                else if (response.Dict is null) log("*** Tracker response is not a complete bencoded dictionary.");
                return response;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            throw new IOException("Tracker request timed out, including connection and redirect time.", ex);
        }
        catch (AuthenticationException ex)
        {
            throw new IOException("Tracker TLS authentication or certificate validation failed: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
        {
            throw new IOException("Tracker request failed: " + ex.Message, ex);
        }
    }

    private static string BuildRequest(string target, string authority, ClientProfile client)
    {
        if (client.HttpProtocol is not ("HTTP/1.0" or "HTTP/1.1"))
            throw new IOException("Unsupported HTTP version in client profile.");
        string headers = client.Headers.Replace("{host}", authority).TrimEnd('\r', '\n');
        bool connection = false;
        int hostCount = 0;
        foreach (string line in headers.Split("\r\n", StringSplitOptions.None))
        {
            (string name, _) = HttpResponseReader.ParseField(line);
            if (line.Any(c => c > 255)) throw new IOException("Client HTTP headers must contain Latin-1 characters.");
            connection |= name.Equals("Connection", StringComparison.OrdinalIgnoreCase);
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hostCount++;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                throw new IOException("A tracker GET profile cannot declare a request body.");
        }
        if (hostCount != 1) throw new IOException("Client HTTP profile must contain exactly one Host header.");
        if (!connection) headers += "\r\nConnection: close";
        return $"GET {EncodeTarget(target)} {client.HttpProtocol}\r\n{headers}\r\n\r\n";
    }

    private async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
    {
        bool throughProxy = proxy.Kind != ProxyKind.None;
        if (!Enum.IsDefined(proxy.Kind)) throw new IOException("Unknown proxy type.");
        string firstHost = throughProxy ? proxy.Host.Trim().Trim('[', ']') : host;
        int firstPort = throughProxy ? proxy.Port : port;
        if (string.IsNullOrWhiteSpace(firstHost) || firstPort is < 1 or > 65535)
            throw new IOException("Invalid proxy or tracker endpoint.");
        Socket socket = await ConnectFirstHopAsync(firstHost, firstPort, ct).ConfigureAwait(false);
        try
        {
            if (throughProxy)
            {
                using NetworkStream stream = new(socket, ownsSocket: false);
                switch (proxy.Kind)
                {
                    case ProxyKind.HttpConnect: await HttpConnectAsync(stream, host, port, ct).ConfigureAwait(false); break;
                    case ProxyKind.Socks4: await Socks4Async(stream, host, port, false, ct).ConfigureAwait(false); break;
                    case ProxyKind.Socks4a: await Socks4Async(stream, host, port, true, ct).ConfigureAwait(false); break;
                    case ProxyKind.Socks5: await Socks5Async(stream, host, port, ct).ConfigureAwait(false); break;
                }
            }
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private async Task<Socket> ConnectFirstHopAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork
            || (Socket.OSSupportsIPv6 && a.AddressFamily == AddressFamily.InterNetworkV6)).Distinct().ToArray();
        if (addresses.Length == 0) throw new IOException("No supported IP address was found for the connection endpoint.");
        // The shared request deadline bounds DNS and every connection attempt together.
        Exception? last = null;
        for (int attempt = 0; attempt < MaxConnectAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            IPAddress address = addresses[attempt % addresses.Length];
            using CancellationTokenSource connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), connectDeadline.Token).ConfigureAwait(false);
                return socket;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
            catch (SocketException ex) when (IsTransientConnectError(ex.SocketErrorCode))
            {
                socket.Dispose();
                last = ex;
            }
            catch { socket.Dispose(); throw; }
            if (attempt + 1 < MaxConnectAttempts) log($"Connection attempt {attempt + 1} failed; retrying the configured endpoint.");
        }
        throw new IOException($"Could not establish the configured connection after {MaxConnectAttempts} attempts.", last);
    }

    private static bool IsTransientConnectError(SocketError error) => error is
        SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted
        or SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
        or SocketError.NetworkDown or SocketError.TryAgain or SocketError.AddressNotAvailable;

    private async Task HttpConnectAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        string authority = Authority(host, port);
        StringBuilder request = new($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
        if (proxy.User.Length > 0)
        {
            if (proxy.User.Contains(':')) throw new IOException("HTTP proxy username cannot contain a colon.");
            string token = Convert.ToBase64String(Utf8.GetBytes(proxy.User + ":" + proxy.Password));
            request.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }
        request.Append("\r\n");
        if (request.Length > HttpResponseReader.MaxProxyHeaderBytes)
            throw new IOException("HTTP proxy credentials exceed the request header limit.");
        await stream.WriteAsync(Latin1.GetBytes(request.ToString()), ct).ConfigureAwait(false);
        HttpResponseReader.Message response = await HttpResponseReader.ReadConnectHeadersAsync(stream, ct).ConfigureAwait(false);
        if (response.StatusCode is < 200 or >= 300)
            throw new IOException($"HTTP proxy CONNECT refused the tunnel (HTTP {response.StatusCode}).");
    }

    private async Task Socks4Async(Stream stream, string host, int port, bool remotely, CancellationToken ct)
    {
        bool literal = IPAddress.TryParse(host, out IPAddress? address);
        bool domain = remotely && !literal;
        if (!domain)
        {
            if (!literal)
                address = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false)).FirstOrDefault();
            if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
                throw new IOException("SOCKS4 requires an IPv4 destination; use SOCKS5 for IPv6.");
        }
        byte[] user = Utf8.GetBytes(proxy.User);
        if (user.Length > 255 || user.Contains((byte)0)) throw new IOException("Invalid or excessive SOCKS4 user ID.");
        byte[] domainBytes = domain ? DomainBytes(host) : [];
        using MemoryStream request = new();
        request.Write([4, 1, (byte)(port >> 8), (byte)port]);
        request.Write(domain ? [0, 0, 0, 1] : address!.GetAddressBytes());
        request.Write(user);
        request.WriteByte(0);
        if (domain) { request.Write(domainBytes); request.WriteByte(0); }
        await stream.WriteAsync(request.ToArray(), ct).ConfigureAwait(false);
        byte[] response = new byte[8];
        await stream.ReadExactlyAsync(response, ct).ConfigureAwait(false);
        if (response[0] != 0 || response[1] != 0x5A)
            throw new IOException($"Invalid or refused SOCKS4 response (version {response[0]}, status 0x{response[1]:X2}).");
    }

    private async Task Socks5Async(Stream stream, string host, int port, CancellationToken ct)
    {
        bool credentials = proxy.User.Length > 0;
        byte[] user = credentials ? Utf8.GetBytes(proxy.User) : [];
        byte[] password = credentials ? Utf8.GetBytes(proxy.Password) : [];
        if (credentials && (user.Length is < 1 or > 255 || password.Length is < 1 or > 255))
            throw new IOException("SOCKS5 username and password must each contain 1 to 255 UTF-8 bytes.");
        await stream.WriteAsync(credentials ? new byte[] { 5, 2, 0, 2 } : [5, 1, 0], ct).ConfigureAwait(false);
        byte[] method = new byte[2];
        await stream.ReadExactlyAsync(method, ct).ConfigureAwait(false);
        if (method[0] != 5 || (method[1] != 0 && !(method[1] == 2 && credentials)))
            throw new IOException("SOCKS5 proxy selected an invalid or unoffered authentication method.");
        if (method[1] == 2)
        {
            using MemoryStream authentication = new();
            authentication.Write([1, (byte)user.Length]);
            authentication.Write(user);
            authentication.WriteByte((byte)password.Length);
            authentication.Write(password);
            await stream.WriteAsync(authentication.ToArray(), ct).ConfigureAwait(false);
            byte[] result = new byte[2];
            await stream.ReadExactlyAsync(result, ct).ConfigureAwait(false);
            if (result[0] != 1 || result[1] != 0) throw new IOException("SOCKS5 authentication failed or returned an invalid version.");
        }
        using MemoryStream request = new();
        request.Write([5, 1, 0]);
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            request.WriteByte(address.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4);
            request.Write(address.GetAddressBytes());
        }
        else
        {
            byte[] domain = DomainBytes(host);
            request.Write([3, (byte)domain.Length]);
            request.Write(domain);
        }
        request.Write([(byte)(port >> 8), (byte)port]);
        await stream.WriteAsync(request.ToArray(), ct).ConfigureAwait(false);
        byte[] head = new byte[4];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (head[0] != 5 || head[2] != 0) throw new IOException("Invalid SOCKS5 CONNECT response version or reserved byte.");
        if (head[1] != 0) throw new IOException($"SOCKS5 proxy refused the destination (status 0x{head[1]:X2}).");
        int length = head[3] switch
        {
            1 => 4, 4 => 16, 3 => -1,
            _ => throw new IOException("SOCKS5 proxy returned an unknown address type."),
        };
        if (length < 0)
        {
            byte[] count = new byte[1];
            await stream.ReadExactlyAsync(count, ct).ConfigureAwait(false);
            length = count[0];
            if (length == 0) throw new IOException("SOCKS5 proxy returned an empty bound hostname.");
        }
        await stream.ReadExactlyAsync(new byte[length + 2], ct).ConfigureAwait(false);
    }

    private static byte[] DomainBytes(string host)
    {
        string ascii = new IdnMapping().GetAscii(host);
        byte[] bytes = Encoding.ASCII.GetBytes(ascii);
        if (bytes.Length is < 1 or > 255 || ascii.Any(c => c <= 32 || c >= 127))
            throw new IOException("SOCKS destination hostname must contain 1 to 255 ASCII bytes.");
        return bytes;
    }

    private static string Authority(string host, int port, int defaultPort = -1) =>
        (host.Contains(':') ? "[" + host + "]" : host) + (port == defaultPort ? string.Empty : ":" + port.ToString(CultureInfo.InvariantCulture));

    internal static bool TryParseUrl(string url, out string scheme, out string host, out int port, out string pathAndQuery)
    {
        scheme = host = string.Empty;
        port = 0;
        pathAndQuery = "/";
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || url.Contains('\\')
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https")
            || parsed.UserInfo.Length > 0 || parsed.Port is < 1 or > 65535) return false;
        scheme = parsed.Scheme;
        host = parsed.IdnHost.Trim('[', ']');
        port = parsed.Port;
        int start = url.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return false;
        int end = url.IndexOfAny(['/', '?', '#'], start + 3);
        if (end >= 0)
        {
            pathAndQuery = StripFragment(url[end..]);
            if (!pathAndQuery.StartsWith('/')) pathAndQuery = "/" + pathAndQuery;
        }
        return host.Length > 0;
    }

    private static string EncodeTarget(string value)
    {
        StringBuilder encoded = new();
        foreach (byte b in Utf8.GetBytes(value))
        {
            if (b >= 128) encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else encoded.Append((char)b);
        }
        return encoded.ToString();
    }

    /// <summary>Resolves relative references without re-escaping percent sequences or reordering queries.</summary>
    internal static string ResolveRedirect(string current, string location)
    {
        if (location.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || location.Contains('\\'))
            throw new IOException("Invalid characters in tracker redirect.");
        string reference = StripFragment(location);
        if (Uri.TryCreate(reference, UriKind.Absolute, out Uri? absolute)
            && absolute.Scheme is "http" or "https") return reference;
        int schemeEnd = current.IndexOf("://", StringComparison.Ordinal);
        if (reference.StartsWith("//", StringComparison.Ordinal)) return current[..(schemeEnd + 1)] + reference;
        int authorityEnd = current.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
        string origin = authorityEnd < 0 ? current : current[..authorityEnd];
        if (!TryParseUrl(current, out _, out _, out _, out string originalTarget)) throw new IOException("Invalid redirect base URL.");
        if (reference.Length == 0) return origin + originalTarget;
        int query = originalTarget.IndexOf('?');
        string path = query < 0 ? originalTarget : originalTarget[..query];
        if (reference[0] == '?') return origin + path + reference;
        int colon = reference.IndexOf(':');
        int slash = reference.IndexOfAny(['/', '?']);
        if (colon >= 0 && (slash < 0 || colon < slash)) throw new IOException("Unsupported tracker redirect protocol.");
        string target = reference[0] == '/' ? reference : path[..(path.LastIndexOf('/') + 1)] + reference;
        query = target.IndexOf('?');
        string suffix = query < 0 ? string.Empty : target[query..];
        string targetPath = query < 0 ? target : target[..query];
        List<string> segments = [];
        string[] parts = targetPath.Split('/');
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                if (i == parts.Length - 1) segments.Add(string.Empty);
            }
            else if (parts[i] == ".")
            {
                if (i == parts.Length - 1) segments.Add(string.Empty);
            }
            else segments.Add(parts[i]);
        }
        return origin + "/" + string.Join('/', segments) + suffix;
    }

    private static string StripFragment(string value)
    {
        int fragment = value.IndexOf('#');
        return fragment < 0 ? value : value[..fragment];
    }
}
