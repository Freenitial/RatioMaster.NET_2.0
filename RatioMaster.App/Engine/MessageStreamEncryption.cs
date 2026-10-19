namespace RatioMaster.Engine;

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Receiving side of BitTorrent Message Stream Encryption: Diffie-Hellman over the 768-bit MSE prime,
/// torrent selection through the obfuscated SKEY hash, then RC4 or plaintext as negotiated.
/// Real clients accept both by default, so an emulated listener that drops encrypted peers stands out.
/// </summary>
internal static class MessageStreamEncryption
{
    internal const int KeyBytes = 96;
    private const int MaximumPad = 512;
    private const uint CryptoPlaintext = 0x01;
    private const uint CryptoRc4 = 0x02;
    private static readonly BigInteger Prime = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563",
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static readonly BigInteger Generator = 2;

    internal sealed record Result(byte[] InfoHash, bool Encrypted);

    /// <summary>The torrent an initiator named, and whether its client picks RC4 when plaintext is also offered.</summary>
    internal readonly record struct Candidate(byte[] InfoHash, bool PrefersRc4);

    internal sealed class KeyPair
    {
        internal KeyPair()
        {
            Private = new BigInteger(RandomNumberGenerator.GetBytes(20), isUnsigned: true, isBigEndian: true);
            Public = Pad(BigInteger.ModPow(Generator, Private, Prime));
        }

        internal BigInteger Private { get; }
        internal byte[] Public { get; }

        internal byte[] Secret(ReadOnlySpan<byte> peerPublic) =>
            Pad(BigInteger.ModPow(new BigInteger(peerPublic, isUnsigned: true, isBigEndian: true), Private, Prime));
    }

    /// <summary>Completes the responder side. <paramref name="received"/> holds bytes already read from the peer.</summary>
    /// <param name="identify">Maps HASH('req2', SKEY) to the torrent it names, or null when this port does not serve it.</param>
    internal static async Task<Result?> AcceptAsync(PeerTransport transport, byte[] received,
        Func<byte[], Candidate?> identify, CancellationToken ct)
    {
        // 1. Ya, already partially read while probing for a plaintext handshake.
        byte[] peerPublic = new byte[KeyBytes];
        received.AsSpan(0, Math.Min(received.Length, KeyBytes)).CopyTo(peerPublic);
        if (received.Length < KeyBytes)
            await transport.ReadExactlyAsync(peerPublic.AsMemory(received.Length), ct).ConfigureAwait(false);
        BigInteger peerValue = new(peerPublic, isUnsigned: true, isBigEndian: true);
        if (peerValue <= BigInteger.One || peerValue >= Prime - BigInteger.One) return null;

        // 2. Yb + PadB.
        KeyPair keys = new();
        byte[] secret = keys.Secret(peerPublic);
        await transport.WriteAsync((byte[])[.. keys.Public, .. RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(MaximumPad + 1))], ct).ConfigureAwait(false);
        await transport.FlushAsync(ct).ConfigureAwait(false);

        // 3. Synchronize on HASH('req1', S), which follows at most 512 bytes of PadA.
        byte[] req1 = Hash("req1", secret);
        int extra = Math.Max(0, received.Length - KeyBytes);
        byte[] buffer = new byte[extra + 2048];
        received.AsSpan(received.Length - extra).CopyTo(buffer);
        int filled = extra, scanned = 0, marker;
        while ((marker = buffer.AsSpan(scanned, filled - scanned).IndexOf(req1)) < 0)
        {
            if (filled >= MaximumPad + req1.Length) return null;
            scanned = Math.Max(0, filled - req1.Length + 1);
            int read = await transport.ReadRawAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) return null;
            filled += read;
        }
        marker += scanned;
        if (marker > MaximumPad) return null;
        int named = marker + req1.Length;
        while (filled < named + 20)
        {
            int read = await transport.ReadRawAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) return null;
            filled += read;
        }

        // HASH('req2', SKEY) xor HASH('req3', S) names the torrent without revealing it.
        byte[] obfuscated = buffer.AsSpan(named, 20).ToArray();
        byte[] req3 = Hash("req3", secret);
        for (int index = 0; index < 20; index++) obfuscated[index] ^= req3[index];
        if (identify(obfuscated) is not { } chosen) return null;

        Rc4 incoming = new(Hash("keyA", secret, chosen.InfoHash), 1024);
        Rc4 outgoing = new(Hash("keyB", secret, chosen.InfoHash), 1024);
        byte[] early = buffer.AsSpan(named + 20, filled - named - 20).ToArray();
        byte[] decrypted = (byte[])early.Clone();
        incoming.Apply(decrypted);
        transport.Prepend(decrypted);
        transport.UseCiphers(incoming, null);

        // ENCRYPT(VC, crypto_provide, len(PadC), PadC, len(IA)), ENCRYPT(IA); IA may hold up to 65535 bytes.
        byte[] header = new byte[14];
        await transport.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (header.AsSpan(0, 8).IndexOfAnyExcept((byte)0) >= 0) return null;
        uint provide = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        int padLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(12));
        if (padLength > MaximumPad) return null;
        byte[] padAndLength = new byte[padLength + 2];
        await transport.ReadExactlyAsync(padAndLength, ct).ConfigureAwait(false);
        byte[] initialPayload = new byte[BinaryPrimitives.ReadUInt16BigEndian(padAndLength.AsSpan(padLength))];
        await transport.ReadExactlyAsync(initialPayload, ct).ConfigureAwait(false);

        uint select = (provide & CryptoRc4) != 0 && (chosen.PrefersRc4 || (provide & CryptoPlaintext) == 0) ? CryptoRc4
            : (provide & CryptoPlaintext) != 0 ? CryptoPlaintext : 0;
        if (select == 0) return null;

        // 4. ENCRYPT(VC, crypto_select, len(PadD), PadD); afterwards the payload uses the selected method.
        int padD = RandomNumberGenerator.GetInt32(MaximumPad + 1);
        byte[] answer = new byte[14 + padD];
        BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(8), select);
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(12), (ushort)padD);
        RandomNumberGenerator.Fill(answer.AsSpan(14));
        outgoing.Apply(answer);
        await transport.WriteAsync(answer, ct).ConfigureAwait(false);

        bool encrypted = select == CryptoRc4;
        transport.UseCiphers(encrypted ? incoming : null, encrypted ? outgoing : null);

        // Bytes that arrived after IA were deciphered in advance; once plaintext is selected they are plaintext.
        int consumed = header.Length + padAndLength.Length + initialPayload.Length;
        if (!encrypted && early.Length > consumed) transport.ReplacePending(early.AsSpan(consumed));
        transport.Prepend(initialPayload);
        return new Result(chosen.InfoHash, encrypted);
    }

    internal static byte[] Hash(string label, params byte[][] parts)
    {
        using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(Encoding.ASCII.GetBytes(label));
        foreach (byte[] part in parts) sha1.AppendData(part);
        return sha1.GetHashAndReset();
    }

    internal static byte[] Pad(BigInteger value)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == KeyBytes) return bytes;
        byte[] padded = new byte[KeyBytes];
        bytes.CopyTo(padded, KeyBytes - bytes.Length);
        return padded;
    }
}
