namespace RatioMaster.Engine;

using System.Globalization;
using System.Text;

/// <summary>Reads one complete HTTP/1.x message with bounded headers, body and chunk framing.</summary>
internal static class HttpResponseReader
{
    internal const int MaxHeaderBytes = 64 * 1024;
    internal const int MaxProxyHeaderBytes = 8 * 1024;
    private const int MaxLineBytes = 8 * 1024;
    private const int MaxInformationalResponses = 16;

    internal sealed record Message(string Headers, int StatusCode, Dictionary<string, string> Fields, byte[] Body);

    internal static async Task<Message> ReadAsync(Stream stream, CancellationToken ct)
    {
        WireReader reader = new(stream, TrackerResponse.MaxBodyBytes);
        Message head = await ReadFinalHeadersAsync(reader, MaxHeaderBytes, ct).ConfigureAwait(false);
        if (head.StatusCode is 204 or 304) return head;

        bool hasLength = head.Fields.TryGetValue("Content-Length", out string? lengthText);
        bool hasTransfer = head.Fields.TryGetValue("Transfer-Encoding", out string? transfer);
        if (hasTransfer && hasLength)
            throw new IOException("Tracker response contains both Transfer-Encoding and Content-Length.");

        byte[] body;
        if (hasTransfer)
        {
            if (!string.Equals(transfer, "chunked", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsupported HTTP transfer coding: " + transfer);
            body = await ReadChunksAsync(reader, ct).ConfigureAwait(false);
        }
        else if (hasLength)
        {
            long? contentLength = null;
            foreach (string part in lengthText!.Split(','))
            {
                if (!long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
                    || value < 0 || (contentLength.HasValue && contentLength.Value != value))
                    throw new IOException("Invalid or conflicting HTTP Content-Length.");
                contentLength = value;
            }
            if (!contentLength.HasValue || contentLength > reader.Remaining)
                throw new IOException("Tracker response exceeds the 8 MiB receive limit.");
            body = new byte[(int)contentLength.Value];
            await reader.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }
        else
        {
            using MemoryStream output = new();
            byte[] buffer = new byte[16 * 1024];
            int count;
            while ((count = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                output.Write(buffer, 0, count);
            body = output.ToArray();
        }
        return head with { Body = body };
    }

    /// <summary>CONNECT stops exactly after the final header block, leaving tunnel bytes unread.</summary>
    internal static Task<Message> ReadConnectHeadersAsync(Stream stream, CancellationToken ct) =>
        ReadFinalHeadersAsync(new WireReader(stream, MaxProxyHeaderBytes, bufferSize: 1), MaxProxyHeaderBytes, ct);

    private static async Task<Message> ReadFinalHeadersAsync(WireReader reader, int limit, CancellationToken ct)
    {
        int initialRemaining = reader.Remaining;
        for (int count = 0; count <= MaxInformationalResponses; count++)
        {
            string statusLine = await ReadHeaderLineAsync(reader, initialRemaining, limit, ct).ConfigureAwait(false);
            int status = ParseStatusLine(statusLine);
            StringBuilder text = new(statusLine);
            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string line = await ReadHeaderLineAsync(reader, initialRemaining, limit, ct).ConfigureAwait(false);
                if (line.Length == 0) break;
                (string name, string value) = ParseField(line);
                if (fields.TryGetValue(name, out string? old))
                {
                    if (name.Equals("Location", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Tracker response contains multiple redirect locations.");
                    fields[name] = old + ", " + value;
                }
                else fields.Add(name, value);
                text.Append("\r\n").Append(line);
            }
            if (status == 101) throw new IOException("HTTP protocol switching is not supported for tracker requests.");
            if (status >= 200) return new(text.ToString(), status, fields, []);
        }
        throw new IOException("Too many informational HTTP responses.");
    }

    private static async Task<string> ReadHeaderLineAsync(WireReader reader, int initialRemaining, int limit, CancellationToken ct)
    {
        int allowed = limit - (initialRemaining - reader.Remaining);
        if (allowed < 2) throw new IOException("HTTP response headers exceed their size limit.");
        return await reader.ReadLineAsync(Math.Min(MaxLineBytes, allowed), ct).ConfigureAwait(false);
    }

    internal static int ParseStatusLine(string line)
    {
        if (line.Length < 12 || !(line.StartsWith("HTTP/1.0 ", StringComparison.Ordinal)
                || line.StartsWith("HTTP/1.1 ", StringComparison.Ordinal))
            || (line.Length > 12 && line[12] != ' ')
            || !int.TryParse(line.AsSpan(9, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int status)
            || status is < 100 or > 599 || line.Any(c => c < 32 && c != '\t'))
            throw new IOException("Invalid HTTP response status line.");
        return status;
    }

    internal static (string Name, string Value) ParseField(string line)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0 || !line.AsSpan(0, colon).ToArray().All(IsToken)
            || line.AsSpan(colon + 1).ToArray().Any(c => (c < 32 && c != '\t') || c == 127))
            throw new IOException("Invalid HTTP response header field.");
        return (line[..colon], line[(colon + 1)..].Trim(' ', '\t'));
    }

    private static bool IsToken(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    private static async Task<byte[]> ReadChunksAsync(WireReader reader, CancellationToken ct)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            string line = await reader.ReadLineAsync(MaxLineBytes, ct).ConfigureAwait(false);
            int extension = line.IndexOf(';');
            string size = extension < 0 ? line : line[..extension];
            if (size.Length == 0 || size.Any(c => !char.IsAsciiHexDigit(c))
                || !ulong.TryParse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong length))
                throw new IOException("Invalid HTTP chunk size.");
            if (extension >= 0) ValidateChunkExtensions(line.AsSpan(extension));
            if (length == 0)
            {
                int before = reader.Remaining;
                while (true)
                {
                    int allowed = 16 * 1024 - (before - reader.Remaining);
                    if (allowed < 2) throw new IOException("HTTP chunk trailers exceed their size limit.");
                    string trailer = await reader.ReadLineAsync(Math.Min(MaxLineBytes, allowed), ct).ConfigureAwait(false);
                    if (trailer.Length == 0) return output.ToArray();
                    (string name, _) = ParseField(trailer);
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("HTTP trailer attempts to change response framing or encoding.");
                }
            }
            if (length > (ulong)reader.Remaining || length > (ulong)(TrackerResponse.MaxBodyBytes - output.Length))
                throw new IOException("Tracker response exceeds the 8 MiB receive limit.");
            int remaining = (int)length;
            while (remaining > 0)
            {
                int take = Math.Min(remaining, buffer.Length);
                await reader.ReadExactlyAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
                output.Write(buffer, 0, take);
                remaining -= take;
            }
            if (await reader.ReadByteAsync(ct).ConfigureAwait(false) != '\r'
                || await reader.ReadByteAsync(ct).ConfigureAwait(false) != '\n')
                throw new IOException("HTTP chunk data is missing its CRLF terminator.");
        }
    }

    private static void ValidateChunkExtensions(ReadOnlySpan<char> value)
    {
        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && value[i] is ' ' or '\t') i++;
            if (i == value.Length || value[i++] != ';') throw new IOException("Invalid HTTP chunk extension.");
            while (i < value.Length && value[i] is ' ' or '\t') i++;
            int start = i;
            while (i < value.Length && IsToken(value[i])) i++;
            if (i == start) throw new IOException("Invalid HTTP chunk extension name.");
            while (i < value.Length && value[i] is ' ' or '\t') i++;
            if (i < value.Length && value[i] == '=')
            {
                i++;
                while (i < value.Length && value[i] is ' ' or '\t') i++;
                if (i < value.Length && value[i] == '"')
                {
                    i++;
                    bool closed = false;
                    while (i < value.Length)
                    {
                        char c = value[i++];
                        if (c == '"') { closed = true; break; }
                        if (c == '\\')
                        {
                            if (i == value.Length) break;
                            c = value[i++];
                        }
                        if ((c < 32 && c != '\t') || c == 127) throw new IOException("Invalid HTTP chunk extension value.");
                    }
                    if (!closed) throw new IOException("Unterminated HTTP chunk extension value.");
                }
                else
                {
                    start = i;
                    while (i < value.Length && IsToken(value[i])) i++;
                    if (i == start) throw new IOException("Invalid HTTP chunk extension value.");
                }
            }
        }
    }

    private sealed class WireReader(Stream stream, int byteLimit, int bufferSize = 16 * 1024)
    {
        private readonly byte[] buffer = new byte[bufferSize];
        private int start;
        private int end;
        internal int Remaining { get; private set; } = byteLimit;

        internal async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (destination.Length == 0) return 0;
            if (start == end)
            {
                end = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, Remaining + 1)), ct).ConfigureAwait(false);
                start = 0;
                if (end == 0) return 0;
            }
            int count = Math.Min(destination.Length, end - start);
            if (count > Remaining) throw new IOException("HTTP response exceeds its receive limit.");
            buffer.AsMemory(start, count).CopyTo(destination);
            start += count;
            Remaining -= count;
            return count;
        }

        internal async ValueTask<int> ReadByteAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (start == end)
            {
                end = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, Remaining + 1)), ct).ConfigureAwait(false);
                start = 0;
                if (end == 0) throw new EndOfStreamException("Truncated HTTP response.");
            }
            if (Remaining == 0) throw new IOException("HTTP response exceeds its receive limit.");
            Remaining--;
            return buffer[start++];
        }

        internal async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken ct)
        {
            int position = 0;
            while (position < destination.Length)
            {
                int count = await ReadAsync(destination[position..], ct).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Truncated HTTP response body.");
                position += count;
            }
        }

        internal async Task<string> ReadLineAsync(int limit, CancellationToken ct)
        {
            using MemoryStream line = new();
            while (line.Length + 2 <= limit)
            {
                int value = await ReadByteAsync(ct).ConfigureAwait(false);
                if (value == '\r')
                {
                    if (await ReadByteAsync(ct).ConfigureAwait(false) != '\n')
                        throw new IOException("Invalid HTTP line terminator.");
                    return Encoding.Latin1.GetString(line.GetBuffer(), 0, (int)line.Length);
                }
                if (value == '\n') throw new IOException("HTTP lines must end with CRLF.");
                line.WriteByte((byte)value);
            }
            throw new IOException("HTTP response line exceeds its size limit.");
        }
    }
}
