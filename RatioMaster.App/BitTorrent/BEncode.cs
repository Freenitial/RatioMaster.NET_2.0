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
    // Latin1 is a lossless 1:1 byte <-> codepoint map for 0..255, so every binary
    // payload (info_hash, pieces, compact peers) round-trips byte-exact and the
    // char length always equals the byte length. The original used Windows-1252,
    // whose undefined slots (0x81, 0x8D, 0x8F, 0x90, 0x9D) corrupted binary data.
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
        string q = ((char)firstByte).ToString();
        if (!char.IsNumber(q[0]))
        {
            throw new TorrentException("\"" + q + "\" is not a string length number.");
        }

        char current = (char)BEncode.ReadByteChecked(s);
        while (current != ':')
        {
            q += current.ToString();
            current = (char)BEncode.ReadByteChecked(s);
        }

        int length = int.Parse(q);
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
            if (n <= 0)
            {
                break;
            }

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
            buffer += current.ToString();
            current = (char)BEncode.ReadByteChecked(s);
        }

        String = long.Parse(buffer).ToString();
    }
}

internal static class BEncode
{
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
}
