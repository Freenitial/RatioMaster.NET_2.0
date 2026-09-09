namespace RatioMaster.Engine;

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RatioMaster.BitTorrent;

/// <summary>Decodes a framed HTTP response and accepts only one complete bencoded dictionary.</summary>
internal sealed class TrackerResponse
{
    internal const int MaxBodyBytes = 8 * 1024 * 1024;
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal string Headers { get; private set; } = string.Empty;
    internal string Body { get; private set; } = string.Empty;
    internal ValueDictionary? Dict { get; private set; }
    internal bool DoRedirect { get; private set; }
    internal int StatusCode { get; private set; }
    internal bool Oversized { get; private set; }
    internal string RedirectionUrl { get; private set; } = string.Empty;
    internal string Error { get; private set; } = string.Empty;

    internal TrackerResponse(byte[] raw)
        : this(ReadMemoryResponse(raw), CancellationToken.None) { }

    internal TrackerResponse(HttpResponseReader.Message message, CancellationToken ct = default)
    {
        Headers = message.Headers;
        StatusCode = message.StatusCode;
        if (message.Fields.TryGetValue("Location", out string? location)) RedirectionUrl = location;
        DoRedirect = StatusCode is 301 or 302 or 303 or 307 or 308 && RedirectionUrl.Length > 0;
        if (DoRedirect) return;
        try
        {
            byte[] body = message.Body;
            if (body.Length > MaxBodyBytes) throw new BodyLimitException();
            if (message.Fields.TryGetValue("Content-Encoding", out string? contentEncoding) && body.Length > 0)
            {
                string[] encodings = contentEncoding.Split(',');
                if (encodings.Length > 4) throw new InvalidDataException("Too many HTTP content encoding layers.");
                for (int i = encodings.Length - 1; i >= 0; i--)
                {
                    ct.ThrowIfCancellationRequested();
                    body = DecodeContent(body, encodings[i].Trim().ToLowerInvariant(), ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            Body = Encoding.Latin1.GetString(body);
            using MemoryStream input = new(body, writable: false);
            Dict = BEncode.Parse(input) as ValueDictionary;
            if (input.Position != input.Length) Dict = null;
            if (StatusCode is < 200 or >= 300 && Dict?.Contains("failure reason") != true) Dict = null;
        }
        catch (BodyLimitException)
        {
            Oversized = true;
            Error = "Tracker response exceeds the 8 MiB decoded body limit.";
            Dict = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or TorrentException or OverflowException or FormatException)
        {
            Error = "Invalid tracker response: " + ex.Message;
            Dict = null;
        }
    }

    private static HttpResponseReader.Message ReadMemoryResponse(byte[] raw)
    {
        using MemoryStream input = new(raw, writable: false);
        return HttpResponseReader.ReadAsync(input, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static byte[] DecodeContent(byte[] data, string encoding, CancellationToken ct)
    {
        if (encoding == "identity") return data;
        if (encoding is "gzip" or "x-gzip") return DecodeGzip(data, ct);
        if (encoding == "br") return DecodeBrotli(data, ct);
        if (encoding is not ("deflate" or "x-deflate"))
            throw new InvalidDataException("Unsupported HTTP content encoding: " + encoding);
        bool zlib = data.Length >= 2 && (data[0] & 15) == 8 && ((data[0] << 8) + data[1]) % 31 == 0;
        using StrictInput input = new(data);
        using Stream decoder = zlib
            ? new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true)
            : new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
        using MemoryStream output = new();
        CopyDecoded(decoder, output, ct);
        if (input.Position != input.Length) throw new InvalidDataException("Trailing bytes after the deflate stream.");
        return output.ToArray();
    }

    private static byte[] DecodeGzip(byte[] data, CancellationToken ct)
    {
        using StrictInput input = new(data);
        using MemoryStream output = new();
        int members = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (++members > 32) throw new InvalidDataException("Too many gzip members.");
            int start = (int)input.Position;
            int position = start;
            RequireBytes(data, position, 10);
            if (data[position] != 0x1F || data[position + 1] != 0x8B || data[position + 2] != 8
                || (data[position + 3] & 0xE0) != 0)
                throw new InvalidDataException("Invalid gzip header.");
            byte flags = data[position + 3];
            position += 10;
            if ((flags & 4) != 0)
            {
                RequireBytes(data, position, 2);
                int extra = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2));
                position += 2;
                RequireBytes(data, position, extra);
                position += extra;
            }
            if ((flags & 8) != 0) SkipGzipString(data, ref position);
            if ((flags & 16) != 0) SkipGzipString(data, ref position);
            if ((flags & 2) != 0)
            {
                RequireBytes(data, position, 2);
                uint crc = Crc32(data.AsSpan(start, position - start));
                if ((ushort)crc != BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2)))
                    throw new InvalidDataException("Invalid gzip header checksum.");
                position += 2;
            }
            input.Position = position;
            long memberStart = output.Length;
            using (DeflateStream decoder = new(input, CompressionMode.Decompress, leaveOpen: true))
                CopyDecoded(decoder, output, ct);
            position = (int)input.Position;
            RequireBytes(data, position, 8);
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
            uint expectedLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4, 4));
            int memberLength = (int)(output.Length - memberStart);
            if (expectedLength != memberLength
                || expectedCrc != Crc32(output.GetBuffer().AsSpan((int)memberStart, memberLength)))
                throw new InvalidDataException("Invalid gzip body checksum or length.");
            input.Position = position + 8;
        }
        while (input.Position < input.Length);
        return output.ToArray();
    }

    private static byte[] DecodeBrotli(byte[] data, CancellationToken ct)
    {
        BrotliDecoder decoder = new();
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        int position = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                OperationStatus status = decoder.Decompress(data.AsSpan(position), buffer, out int consumed, out int written);
                position += consumed;
                WriteBounded(output, buffer.AsSpan(0, written));
                if (status == OperationStatus.Done)
                {
                    if (position != data.Length) throw new InvalidDataException("Trailing bytes after the Brotli stream.");
                    return output.ToArray();
                }
                if (status == OperationStatus.InvalidData || status == OperationStatus.NeedMoreData
                    || (consumed == 0 && written == 0))
                    throw new InvalidDataException("Invalid or truncated Brotli stream.");
            }
        }
        finally { decoder.Dispose(); }
    }

    private static void CopyDecoded(Stream decoder, MemoryStream output, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int count = decoder.Read(buffer, 0, buffer.Length);
            if (count == 0) return;
            WriteBounded(output, buffer.AsSpan(0, count));
        }
    }

    private static void WriteBounded(MemoryStream output, ReadOnlySpan<byte> bytes)
    {
        if (output.Length + bytes.Length > MaxBodyBytes) throw new BodyLimitException();
        output.Write(bytes);
    }

    private static void RequireBytes(byte[] data, int offset, int count)
    {
        if (offset < 0 || count > data.Length - offset) throw new InvalidDataException("Truncated gzip stream.");
    }

    private static void SkipGzipString(byte[] data, ref int position)
    {
        int end = Array.IndexOf(data, (byte)0, position);
        if (end < 0) throw new InvalidDataException("Unterminated gzip header field.");
        position = end + 1;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320U);
            table[i] = value;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }

    private sealed class BodyLimitException : IOException;

    /// <summary>A decoder requesting more input beyond the framed body reports truncation.</summary>
    private sealed class StrictInput(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count != 0 && Position == Length) throw new InvalidDataException("Truncated compressed response.");
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length != 0 && Position == Length) throw new InvalidDataException("Truncated compressed response.");
            return base.Read(buffer);
        }
    }
}
