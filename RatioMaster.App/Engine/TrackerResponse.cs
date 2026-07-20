namespace RatioMaster.Engine;

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using RatioMaster.BitTorrent;

/// <summary>
/// Parses a raw HTTP tracker response: splits headers/body, follows 302 redirects,
/// de-chunks Transfer-Encoding: chunked, gunzips gzip bodies, then bencode-decodes.
/// </summary>
internal sealed class TrackerResponse
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>Ceiling on a decoded tracker body. A real announce/scrape reply is a few KB even with a
    /// large peer list; anything past this is either broken or hostile, so it is refused rather than
    /// buffered.</summary>
    internal const int MaxBodyBytes = 8 * 1024 * 1024;

    internal string Headers { get; private set; } = string.Empty;

    internal string Body { get; private set; } = string.Empty;

    internal ValueDictionary? Dict { get; private set; }

    internal bool DoRedirect { get; private set; }

    /// <summary>The gzip body exceeded <see cref="MaxBodyBytes"/> and was refused, so this response carries
    /// no usable content. Distinguishes "hostile/oversized reply" from "could not reach the tracker".</summary>
    internal bool Oversized { get; private set; }

    internal string RedirectionUrl { get; private set; } = string.Empty;

    internal TrackerResponse(byte[] raw)
    {
        // Header/body split: prefer CRLFCRLF, fall back to LFLF. Latin1 keeps the
        // char index equal to the byte index so the byte split is exact.
        string all = Latin1.GetString(raw);
        int sep = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int sepLen = 4;
        if (sep < 0)
        {
            sep = all.IndexOf("\n\n", StringComparison.Ordinal);
            sepLen = 2;
        }

        int bodyStart;
        string headerText;
        if (sep < 0)
        {
            headerText = all;
            bodyStart = raw.Length;
        }
        else
        {
            headerText = all.Substring(0, sep);
            bodyStart = sep + sepLen;
        }

        Headers = headerText;

        bool chunked = false;
        string contentEncoding = string.Empty;
        int statusCode = 0;
        foreach (string line in headerText.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (statusCode == 0 && l.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = l.Split(' ');
                if (parts.Length > 1)
                {
                    int.TryParse(parts[1], out statusCode);
                }
            }
            else if (l.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
            {
                RedirectionUrl = l.Substring("Location:".Length).Trim();
            }
            else if (l.StartsWith("Content-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                contentEncoding = l.Substring("Content-Encoding:".Length).Trim().ToLowerInvariant();
            }
            else if (l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                     && l.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }

        if (statusCode is >= 300 and < 400 && RedirectionUrl.Length > 0)
        {
            DoRedirect = true;
            return;
        }

        byte[] body = new byte[raw.Length - bodyStart];
        Buffer.BlockCopy(raw, bodyStart, body, 0, body.Length);

        if (chunked)
        {
            body = DeChunk(body);
        }

        if (contentEncoding is "gzip" or "x-gzip")
        {
            byte[]? inflated = TryGunzip(body);
            if (inflated is null)
            {
                Oversized = true;
                body = [];
            }
            else
            {
                body = inflated;
            }
        }

        Body = Latin1.GetString(body);
        Dict = ParseBEncodeDict(body);
    }

    private static byte[] DeChunk(byte[] body)
    {
        using MemoryStream outStream = new();
        int pos = 0;
        while (pos < body.Length)
        {
            int lineEnd = IndexOfCrlf(body, pos);
            if (lineEnd < 0)
            {
                break;
            }

            string sizeLine = Latin1.GetString(body, pos, lineEnd - pos).Split(';')[0].Trim();
            pos = lineEnd + 2;
            if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out int chunkSize) || chunkSize <= 0)
            {
                break;
            }

            if (pos + chunkSize > body.Length)
            {
                chunkSize = body.Length - pos;
            }

            outStream.Write(body, pos, chunkSize);
            pos += chunkSize + 2; // skip chunk data + trailing CRLF
        }

        return outStream.ToArray();
    }

    private static int IndexOfCrlf(byte[] data, int start)
    {
        for (int i = start; i < data.Length - 1; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Inflate a gzip body, or null if it exceeds <see cref="MaxBodyBytes"/>. A body the tracker
    /// merely mislabelled as gzip is returned unchanged (the catch), since those bytes still parse.</summary>
    private static byte[]? TryGunzip(byte[] body)
    {
        try
        {
            using MemoryStream input = new(body);
            using GZipStream gz = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();

            // Bounded inflate. A tracker answer is a few KB; a compromised or hostile one could otherwise
            // send a few hundred bytes that expand to gigabytes (a gzip bomb) and take the process down.
            // Copy in chunks and stop at the cap rather than trusting the stream to end.
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxBodyBytes)
                {
                    // null = "refused, too big". Deliberately NOT the raw body: these bytes are known-gzip,
                    // so handing them back guarantees a bencode parse failure that surfaces to the user as
                    // "No connection to tracker" — a false diagnosis — while dumping megabytes of binary
                    // into the log. (Returning the raw body IS right for the catch below, where the tracker
                    // merely mislabelled a plain body as gzip and the bytes do parse.)
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch
        {
            return body;
        }
    }

    private static ValueDictionary? ParseBEncodeDict(byte[] body)
    {
        try
        {
            using MemoryStream ms = new(body);
            return BEncode.Parse(ms) as ValueDictionary;
        }
        catch
        {
            return null;
        }
    }
}
