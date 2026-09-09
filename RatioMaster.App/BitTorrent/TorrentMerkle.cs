namespace RatioMaster.BitTorrent;

using System;
using System.Security.Cryptography;

/// <summary>BEP 52 Merkle reduction of piece-layer hashes, including zero-hash subtree padding.</summary>
internal static class TorrentMerkle
{
    internal static byte[] ComputeRoot(byte[] pieceHashes, ulong pieceLength)
    {
        // Missing 16 KiB blocks are represented by 32 zero bytes, not SHA-256 of zero-filled data.
        Span<byte> padding = stackalloc byte[32];
        padding.Clear();
        Span<byte> pair = stackalloc byte[64];
        for (ulong blocks = pieceLength / 16384; blocks > 1; blocks /= 2)
        {
            HashPair(padding, padding, pair, padding);
        }

        byte[] hashes = (byte[])pieceHashes.Clone();
        int count = hashes.Length / 32;
        while (count > 1)
        {
            for (int index = 0; index < count; index += 2)
            {
                ReadOnlySpan<byte> left = hashes.AsSpan(index * 32, 32);
                ReadOnlySpan<byte> right = index + 1 < count ? hashes.AsSpan((index + 1) * 32, 32) : padding;
                HashPair(left, right, pair, hashes.AsSpan(index / 2 * 32, 32));
            }

            count = (count + 1) / 2;
            HashPair(padding, padding, pair, padding);
        }

        return hashes.AsSpan(0, 32).ToArray();
    }

    private static void HashPair(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right,
        Span<byte> pair, Span<byte> destination)
    {
        left.CopyTo(pair);
        right.CopyTo(pair[32..]);
        SHA256.HashData(pair, destination);
    }
}
