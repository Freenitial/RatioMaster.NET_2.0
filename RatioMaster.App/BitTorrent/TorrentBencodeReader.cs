namespace RatioMaster.BitTorrent;

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RatioMaster.Localization;

/// <summary>Strict, bounded metainfo decoding with exact source offsets for the info hashes.</summary>
internal sealed class TorrentBencodeReader
{
    internal const int MaxMetainfoBytes = 64 * 1024 * 1024;
    internal const int MaxDepth = 64;
    internal const int MaxNodes = 262144;
    private const int MaxKeyBytes = 4096;

    private readonly byte[] source;
    private int position;
    private int nodes;
    private int infoStart = -1;
    private int infoLength;

    private TorrentBencodeReader(byte[] source) => this.source = source;

    /// <summary>Parses metainfo and returns the exact info dictionary bytes with their v1 and v2 hashes.</summary>
    internal static ValueDictionary Read(Stream stream, out byte[] sha1, out byte[] sha256, out byte[] infoBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (buffer.Length < MaxMetainfoBytes)
        {
            int count = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxMetainfoBytes - buffer.Length));
            if (count == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, count);
        }

        if (buffer.Length == MaxMetainfoBytes && stream.ReadByte() != -1)
        {
            throw Error(Strings.TorrentMetainfoTooLarge);
        }

        TorrentBencodeReader reader = new(buffer.ToArray());
        IBEncodeValue value = reader.ReadValue(0);
        if (value is not ValueDictionary root || reader.position != reader.source.Length)
        {
            throw Error(Strings.TorrentMetainfoDictionaryWithoutTrailingBytes);
        }

        if (reader.infoStart < 0 || root["info"] is not ValueDictionary)
        {
            throw Error(Strings.TorrentInfoDictionaryMissingInvalid);
        }

        ReadOnlySpan<byte> info = reader.source.AsSpan(reader.infoStart, reader.infoLength);
        infoBytes = info.ToArray();
        sha1 = SHA1.HashData(info);
        sha256 = SHA256.HashData(info);
        return root;
    }

    private IBEncodeValue ReadValue(int depth)
    {
        CountNode();
        if (depth > MaxDepth)
        {
            throw Error(Strings.TorrentBencodingNestingExceedsDepthLimit);
        }

        byte token = Peek();
        switch (token)
        {
            case (byte)'d':
                return ReadDictionary(depth);
            case (byte)'l':
                position++;
                ValueList list = new();
                while (Peek() != (byte)'e')
                {
                    list.Add(ReadValue(depth + 1));
                }

                position++;
                return list;
            case (byte)'i':
                return ReadInteger();
            default:
                (int start, int length) = ReadStringRange();
                return new ValueString(Encoding.Latin1.GetString(source, start, length));
        }
    }

    private ValueDictionary ReadDictionary(int depth)
    {
        position++;
        ValueDictionary dictionary = new();
        int previousStart = 0;
        int previousLength = 0;
        bool hasPrevious = false;
        while (Peek() != (byte)'e')
        {
            CountNode();
            (int start, int length) = ReadStringRange();
            if (length > MaxKeyBytes)
            {
                throw Error(Strings.TorrentDictionaryKeyTooLong);
            }

            if (hasPrevious && source.AsSpan(previousStart, previousLength)
                    .SequenceCompareTo(source.AsSpan(start, length)) >= 0)
            {
                throw Error(Strings.TorrentDictionaryKeysUniqueSortedRaw);
            }

            string key = Encoding.Latin1.GetString(source, start, length);
            int valueStart = position;
            IBEncodeValue value = ReadValue(depth + 1);
            dictionary.Add(key, value);
            if (depth == 0 && key == "info")
            {
                infoStart = valueStart;
                infoLength = position - valueStart;
            }

            previousStart = start;
            previousLength = length;
            hasPrevious = true;
        }

        position++;
        return dictionary;
    }

    private ValueNumber ReadInteger()
    {
        position++;
        int start = position;
        bool negative = Peek() == (byte)'-';
        if (negative)
        {
            position++;
        }

        int digitsStart = position;
        while (Peek() != (byte)'e')
        {
            byte digit = source[position];
            if (digit < (byte)'0' || digit > (byte)'9' || position - digitsStart >= 19)
            {
                throw Error(Strings.TorrentInvalidOversizedBencodedInteger);
            }

            position++;
        }

        int digits = position - digitsStart;
        if (digits == 0 || (source[digitsStart] == (byte)'0' && (digits != 1 || negative)))
        {
            throw Error(Strings.TorrentBencodedIntegersUseCanonicalDecimal);
        }

        string text = Encoding.ASCII.GetString(source, start, position - start);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            throw Error(Strings.TorrentIntegerOutOfRange);
        }

        position++;
        return new ValueNumber { String = text };
    }

    private (int Start, int Length) ReadStringRange()
    {
        byte first = Peek();
        int digits = 0;
        int length = 0;
        while (Peek() != (byte)':')
        {
            byte digit = source[position++];
            if (digit < (byte)'0' || digit > (byte)'9' || ++digits > 8)
            {
                throw Error(Strings.TorrentInvalidBencodedStringLength);
            }

            length = length * 10 + digit - (byte)'0';
            if (length > MaxMetainfoBytes)
            {
                throw Error(Strings.TorrentBencodedStringExceedsMetainfoSize);
            }
        }

        if (digits == 0 || (digits > 1 && first == (byte)'0'))
        {
            throw Error(Strings.TorrentBencodedStringsUseCanonicalDecimal);
        }

        position++;
        if (length > source.Length - position)
        {
            throw Error(Strings.TorrentTruncatedBencodedString);
        }

        int start = position;
        position += length;
        return (start, length);
    }

    private byte Peek()
    {
        if (position == source.Length)
        {
            throw Error(Strings.TorrentUnexpectedEndBencodedData);
        }

        return source[position];
    }

    private void CountNode()
    {
        if (++nodes > MaxNodes)
        {
            throw Error(Strings.TorrentBencodingExceedsNodeLimit);
        }
    }

    private static TorrentException Error(string message) => new IncompleteTorrentData(message);
}
