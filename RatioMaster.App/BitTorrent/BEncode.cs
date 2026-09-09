namespace RatioMaster.BitTorrent;

using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;

/// <summary>A bencoded value (dictionary, list, string or integer).</summary>
internal interface IBEncodeValue
{
    byte[] Encode();

    void Parse(Stream p);
}

internal class TorrentException(string message) : Exception(message);

internal sealed class IncompleteTorrentData(string message) : TorrentException(message);

internal sealed class ValueList : IBEncodeValue, IEnumerable
{
    internal readonly Collection<IBEncodeValue> values = [];

    public IEnumerator GetEnumerator() => values.GetEnumerator();

    public void Parse(Stream s)
    {
        byte current = BEncode.ReadByteChecked(s);
        while ((char)current != 'e')
        {
            values.Add(BEncode.Parse(s, current));
            current = BEncode.ReadByteChecked(s);
        }
    }

    internal void Add(IBEncodeValue value) => values.Add(value);

    internal IBEncodeValue this[int index]
    {
        get => values[index];
        set => values[index] = value;
    }

    public byte[] Encode()
    {
        Collection<byte> bytes = [(byte)'l'];
        foreach (IBEncodeValue member in values)
        {
            foreach (byte b in member.Encode())
            {
                bytes.Add(b);
            }
        }

        bytes.Add((byte)'e');
        byte[] result = new byte[bytes.Count];
        bytes.CopyTo(result, 0);
        return result;
    }
}

internal sealed class ValueString : IBEncodeValue
{
    // Latin-1 maps each byte to one character, preserving binary bencode payloads.
    private static readonly Encoding Enc = Encoding.Latin1;

    private string v = string.Empty;
    private byte[] data = [];

    internal int Length => v.Length;

    internal byte[] Bytes => data;

    internal string String
    {
        get => v;
        set
        {
            v = value;
            data = Enc.GetBytes(v);
        }
    }

    public byte[] Encode()
    {
        byte[] prefix = Enc.GetBytes(v.Length.ToString() + ":");
        byte[] result = new byte[prefix.Length + data.Length];
        Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
        Buffer.BlockCopy(data, 0, result, prefix.Length, data.Length);
        return result;
    }

    internal ValueString(string stringValue) => String = stringValue;

    internal ValueString()
    {
    }

    public void Parse(Stream s) => throw new TorrentException(
        "Parse method not supported; the first byte must be passed into the string parse routine.");

    public void Parse(Stream s, byte firstByte)
    {
        if (firstByte < (byte)'0' || firstByte > (byte)'9') throw new TorrentException("Invalid string length.");
        int length = firstByte - '0';
        byte current;
        while ((current = BEncode.ReadByteChecked(s)) != (byte)':')
        {
            if (current < (byte)'0' || current > (byte)'9' || length > BEncode.MaxStringBytes / 10)
                throw new TorrentException("Invalid or excessive string length.");
            length = checked(length * 10 + current - '0');
        }
        if (length > BEncode.MaxStringBytes || (s.CanSeek && length > s.Length - s.Position))
            throw new TorrentException("Truncated or excessive bencoded string.");
        data = new byte[length];
        ReadExact(s, data, length);
        v = Enc.GetString(data);
    }

    private static void ReadExact(Stream s, byte[] buffer, int length)
    {
        int read = 0;
        while (read < length)
        {
            int n = s.Read(buffer, read, length - read);
            if (n <= 0) throw new IncompleteTorrentData("Truncated bencoded string.");

            read += n;
        }
    }
}

internal sealed class ValueNumber : IBEncodeValue
{
    private static readonly Encoding Enc = Encoding.Latin1;

    private string v = "0";
    private byte[] data = Enc.GetBytes("0");

    internal string String
    {
        get => v;
        set
        {
            v = value;
            data = Enc.GetBytes(v);
        }
    }

    internal long Integer
    {
        get => long.Parse(v);
        set => String = value.ToString();
    }

    public byte[] Encode()
    {
        byte[] result = new byte[data.Length + 2];
        result[0] = (byte)'i';
        Buffer.BlockCopy(data, 0, result, 1, data.Length);
        result[data.Length + 1] = (byte)'e';
        return result;
    }

    internal ValueNumber(long number) => String = number.ToString();

    internal ValueNumber()
    {
    }

    public void Parse(Stream s)
    {
        string buffer = string.Empty;
        char current = (char)BEncode.ReadByteChecked(s);
        while (current != 'e')
        {
            if (buffer.Length >= 20 || (current != '-' && (current < '0' || current > '9')))
                throw new TorrentException("Invalid bencoded integer.");
            buffer += current.ToString();
            current = (char)BEncode.ReadByteChecked(s);
        }

        String = long.Parse(buffer).ToString();
    }
}

internal static class BEncode
{
    internal const int MaxStringBytes = 8 * 1024 * 1024;
    [ThreadStatic] private static int nesting;
    // Read one byte or throw on end-of-stream. Stream.ReadByte() returns -1 at EOF; casting that
    // straight to char yields U+FFFF (and to byte yields 255), neither of which matches a bencode
    // terminator ('e' / ':') — so a truncated payload would otherwise spin the parse loops forever,
    // appending the sentinel and growing memory without bound. This turns truncation into a clean throw.
    internal static byte ReadByteChecked(Stream s)
    {
        int b = s.ReadByte();
        if (b < 0)
        {
            throw new IncompleteTorrentData("Unexpected end of bencoded data (truncated).");
        }

        return (byte)b;
    }

    internal static IBEncodeValue Parse(Stream d) => Parse(d, ReadByteChecked(d));

    internal static string? String(IBEncodeValue v) => v switch
    {
        ValueString s => s.String,
        ValueNumber n => n.String,
        _ => null,
    };

    internal static IBEncodeValue Parse(Stream d, byte firstByte)
    {
        if (++nesting > 64) { nesting--; throw new TorrentException("Bencoded data is nested too deeply."); }
        try
        {
            char first = (char)firstByte;
            IBEncodeValue v = first switch
            {
                'd' => new ValueDictionary(),
                'l' => new ValueList(),
                'i' => new ValueNumber(),
                _ => new ValueString(),
            };

            if (v is ValueString vs)
            {
                vs.Parse(d, (byte)first);
            }
            else
            {
                v.Parse(d);
            }

            return v;
        }
        finally { nesting--; }
    }
}
