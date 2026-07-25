namespace RatioMaster.Services;

using System;
using System.Text;

/// <summary>Generates random client keys / peer-id tails and URL-encodes binary strings.</summary>
internal sealed class RandomStringGenerator
{
    private static readonly char[] Alphanumeric =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    private readonly Random rng = new();

    internal char GetRandomCharacter() => Alphanumeric[rng.Next(Alphanumeric.Length)];

    internal string Generate(int length) => Generate(length, randomness: false);

    /// <summary>Random string. When <paramref name="randomness"/> is true, raw bytes 0..254.</summary>
    internal string Generate(int length, bool randomness)
    {
        StringBuilder sb = new(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(randomness ? (char)rng.Next(255) : GetRandomCharacter());
        }

        return sb.ToString();
    }

    internal string Generate(int length, char[] charArray)
    {
        StringBuilder sb = new(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(charArray[rng.Next(charArray.Length)]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Percent-encode every non-alphanumeric / non-ASCII character (BitTorrent %XX form).
    /// </summary>
    /// <remarks>
    /// Encodes the LATIN-1 BYTES rather than the chars. Going per-char, a value above U+00FF produced
    /// <c>Convert.ToString(c, 16)</c> = three or four hex digits, i.e. a malformed escape like "%20ac"
    /// that no tracker can parse. Working on bytes always yields exactly two digits, and it matches how
    /// the same string is written to the peer-wire handshake (also Latin-1), so the announced value and
    /// the one on the wire cannot drift apart.
    /// </remarks>
    internal string UrlEncode(string input, bool upperCase)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(input);
        StringBuilder sb = new(bytes.Length * 3);
        string digits = upperCase ? "0123456789ABCDEF" : "0123456789abcdef";
        foreach (byte b in bytes)
        {
            if (IsUnreserved(b))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%').Append(digits[b >> 4]).Append(digits[b & 0x0F]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The unreserved set libtorrent's <c>escape_string</c> uses (RFC 2396 §2.3 minus the apostrophe):
    /// alphanumerics plus <c>-_.!~*()</c>.
    /// </summary>
    /// <remarks>
    /// The punctuation matters. Escaping only alphanumerics is still a VALID URL — a tracker decodes it to
    /// the same bytes — but it is not what the emulated clients actually send, and the difference is plainly
    /// visible in the raw query: real qBittorrent announces "peer_id=-qB5100-xxxx" while escaping everything
    /// yields "peer_id=%2dqB5100%2dxxxx". A tracker logging raw queries can pick that out at a glance, which
    /// is exactly the kind of tell an emulation profile exists to avoid. The same set applies to info_hash,
    /// since libtorrent escapes both with the one function.
    /// </remarks>
    private static bool IsUnreserved(byte b) =>
        (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
        || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'!' or (byte)'~' or (byte)'*' or (byte)'(' or (byte)')';
}
