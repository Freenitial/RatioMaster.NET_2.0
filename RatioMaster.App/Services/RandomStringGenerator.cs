namespace RatioMaster.Services;

using System;
using System.Security.Cryptography;
using System.Text;
using RatioMaster.Models;

/// <summary>Generates client identity bytes and percent-encodes their Latin-1 representation.</summary>
internal sealed class RandomStringGenerator
{
    private const string Alphanumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    internal const string LibtorrentAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_.!~*()";

    internal char GetRandomCharacter() => Alphanumeric[RandomNumberGenerator.GetInt32(Alphanumeric.Length)];
    internal string Generate(int length) => Generate(length, randomness: false);

    /// <summary>With randomness enabled, every byte from 0 through 255 is eligible.</summary>
    internal string Generate(int length, bool randomness) => randomness
        ? GenerateBytes(length, 0, 256)
        : Generate(length, Alphanumeric.AsSpan());

    internal string Generate(int length, char[] charArray) => Generate(length, charArray.AsSpan());

    internal string Generate(int length, ReadOnlySpan<char> alphabet)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (alphabet.IsEmpty) throw new ArgumentException("The alphabet cannot be empty.", nameof(alphabet));
        char[] output = new char[length];
        for (int index = 0; index < output.Length; index++)
            output[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(output);
    }

    internal string GenerateBytes(int length, int minimum, int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (minimum < 0 || exclusiveMaximum > 256 || minimum >= exclusiveMaximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        char[] output = new char[length];
        for (int index = 0; index < output.Length; index++)
            output[index] = (char)RandomNumberGenerator.GetInt32(minimum, exclusiveMaximum);
        return new string(output);
    }

    /// <summary>Transmission 4.0.6 maps random bytes modulo 36 and appends a base-36 checksum.</summary>
    internal string GenerateTransmissionTail()
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<byte> random = stackalloc byte[11];
        RandomNumberGenerator.Fill(random);
        Span<char> output = stackalloc char[12];
        int total = 0;
        for (int index = 0; index < random.Length; index++)
        {
            int value = random[index] % alphabet.Length;
            total += value;
            output[index] = alphabet[value];
        }
        output[^1] = alphabet[(alphabet.Length - total % alphabet.Length) % alphabet.Length];
        return new string(output);
    }

    internal string UrlEncode(string input, bool upperCase) => UrlEncode(input, upperCase, ClientUrlEncoding.Libtorrent);

    /// <summary>Each character represents one byte; values outside Latin-1 are rejected.</summary>
    internal string UrlEncode(string input, bool upperCase, ClientUrlEncoding policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        StringBuilder output = new(input.Length * 3);
        string digits = upperCase ? "0123456789ABCDEF" : "0123456789abcdef";
        foreach (char value in input)
        {
            if (value > byte.MaxValue)
                throw new ArgumentException("Binary URL values must contain Latin-1 bytes only.", nameof(input));
            byte current = (byte)value;
            if (IsUnreserved(current, policy)) output.Append(value);
            else output.Append('%').Append(digits[current >> 4]).Append(digits[current & 15]);
        }
        return output.ToString();
    }

    private static bool IsUnreserved(byte value, ClientUrlEncoding policy)
    {
        if (value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')
            return true;
        if (policy == ClientUrlEncoding.Alphanumeric) return false;
        if (value is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~') return true;
        return policy == ClientUrlEncoding.Libtorrent && value is (byte)'!' or (byte)'*' or (byte)'(' or (byte)')';
    }
}
