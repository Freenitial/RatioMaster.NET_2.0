namespace RatioMaster.Services;

using System;
using System.Security.Cryptography;
using System.Text;
using RatioMaster.Models;

/// <summary>Generates client identity bytes and percent-encodes their Latin-1 representation.</summary>
internal sealed class RandomStringGenerator
{
    internal const string LibtorrentAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_.!~*()";

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

    /// <summary>Transmission maps random bytes modulo 36 and appends a base-36 checksum.</summary>
    internal string GenerateTransmissionTail()
    {
        Span<byte> random = stackalloc byte[11];
        RandomNumberGenerator.Fill(random);
        Span<char> output = stackalloc char[12];
        int total = 0;
        for (int index = 0; index < random.Length; index++)
        {
            int value = random[index] % ClientIdentity.Base36.Length;
            total += value;
            output[index] = ClientIdentity.Base36[value];
        }
        output[^1] = ClientIdentity.Base36[(ClientIdentity.Base36.Length - total % ClientIdentity.Base36.Length) % ClientIdentity.Base36.Length];
        return new string(output);
    }

    /// <summary>Random bytes followed by the two µTorrent check bytes computed over the complete identity.</summary>
    internal string GenerateUTorrentTail(string prefix, int minimum, int exclusiveMaximum)
    {
        if (prefix.Length > 18) throw new ArgumentException("The µTorrent check bytes need two free positions.", nameof(prefix));
        string body = prefix + GenerateBytes(18 - prefix.Length, minimum, exclusiveMaximum);
        return body[prefix.Length..] + ClientIdentity.UTorrentCheckBytes(body);
    }

    /// <summary>Each character represents one byte; values outside Latin-1 are rejected. Letters and digits are
    /// never escaped; <paramref name="unreserved"/> lists the punctuation the client also leaves as is.</summary>
    internal string UrlEncode(string input, bool upperCase, string unreserved)
    {
        ArgumentNullException.ThrowIfNull(input);
        StringBuilder output = new(input.Length * 3);
        string digits = upperCase ? "0123456789ABCDEF" : "0123456789abcdef";
        foreach (char value in input)
        {
            if (value > byte.MaxValue)
                throw new ArgumentException("Binary URL values must contain Latin-1 bytes only.", nameof(input));
            byte current = (byte)value;
            if (char.IsAsciiLetterOrDigit(value) || unreserved.Contains(value)) output.Append(value);
            else output.Append('%').Append(digits[current >> 4]).Append(digits[current & 15]);
        }
        return output.ToString();
    }
}

/// <summary>Checksum rules embedded in the peer IDs of clients that validate their own identities.</summary>
internal static class ClientIdentity
{
    internal const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>µTorrent ends its peer ID with the first two bytes of the SHA-1 of the eighteen bytes before them.</summary>
    internal static string UTorrentCheckBytes(string firstEighteen)
    {
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(Encoding.Latin1.GetBytes(firstEighteen), digest);
        return new string([(char)digest[0], (char)digest[1]]);
    }

    internal static bool HasUTorrentChecksum(string peerId) =>
        peerId.Length == 20 && peerId.All(value => value <= 255) && UTorrentCheckBytes(peerId[..18]) == peerId[18..];

    internal static bool HasTransmissionChecksum(string peerId)
    {
        if (peerId.Length != 20) return false;
        int total = 0;
        foreach (char value in peerId.AsSpan(8))
        {
            int digit = Base36.IndexOf(value);
            if (digit < 0) return false;
            total += digit;
        }
        return total % 36 == 0;
    }
}
