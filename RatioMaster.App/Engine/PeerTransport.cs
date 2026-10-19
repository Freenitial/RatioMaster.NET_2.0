namespace RatioMaster.Engine;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>RC4 keystream as used by BitTorrent message stream encryption (MSE/PE).</summary>
internal sealed class Rc4
{
    private readonly byte[] state = new byte[256];
    private int i, j;

    internal Rc4(ReadOnlySpan<byte> key, int discard)
    {
        if (key.IsEmpty) throw new ArgumentException("RC4 needs a key.", nameof(key));
        for (int index = 0; index < 256; index++) state[index] = (byte)index;
        for (int index = 0, swap = 0; index < 256; index++)
        {
            swap = (swap + state[index] + key[index % key.Length]) & 255;
            (state[index], state[swap]) = (state[swap], state[index]);
        }
        Span<byte> skipped = stackalloc byte[256];
        for (; discard > 0; discard -= skipped.Length)
        {
            Span<byte> chunk = skipped[..Math.Min(discard, skipped.Length)];
            chunk.Clear();
            Apply(chunk);
        }
    }

    internal void Apply(Span<byte> data)
    {
        for (int index = 0; index < data.Length; index++)
        {
            i = (i + 1) & 255;
            j = (j + state[i]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
            data[index] ^= state[(state[i] + state[j]) & 255];
        }
    }
}

/// <summary>
/// Peer connection byte transport: exact reads with a deadline, an optional pushed-back prefix (the MSE
/// initial payload) and independent RC4 ciphers for each direction once encryption is negotiated.
/// </summary>
internal sealed class PeerTransport(Stream stream)
{
    private byte[] pending = [];
    private int pendingOffset;
    private Rc4? decrypt, encrypt;

    internal void UseCiphers(Rc4? incoming, Rc4? outgoing)
    {
        decrypt = incoming;
        encrypt = outgoing;
    }

    /// <summary>Bytes that were already received (and decrypted) before the transport switched mode.</summary>
    internal void Prepend(ReadOnlySpan<byte> data)
    {
        byte[] combined = new byte[data.Length + pending.Length - pendingOffset];
        data.CopyTo(combined);
        pending.AsSpan(pendingOffset).CopyTo(combined.AsSpan(data.Length));
        pending = combined;
        pendingOffset = 0;
    }

    /// <summary>Replaces the bytes still pushed back, for a prefix that was deciphered before the cipher was known.</summary>
    internal void ReplacePending(ReadOnlySpan<byte> data)
    {
        pending = data.ToArray();
        pendingOffset = 0;
    }

    internal async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int fromPending = Math.Min(buffer.Length, pending.Length - pendingOffset);
        if (fromPending > 0)
        {
            pending.AsMemory(pendingOffset, fromPending).CopyTo(buffer);
            pendingOffset += fromPending;
            if (pendingOffset == pending.Length) { pending = []; pendingOffset = 0; }
        }
        if (fromPending == buffer.Length) return;
        Memory<byte> rest = buffer[fromPending..];
        await stream.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
        decrypt?.Apply(rest.Span);
    }

    /// <summary>Raw read used during the MSE key exchange, before any byte was pushed back or any cipher exists.</summary>
    internal ValueTask<int> ReadRawAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (pendingOffset < pending.Length || decrypt is not null) throw new InvalidOperationException("Raw reads precede buffered or encrypted data.");
        return stream.ReadAsync(buffer, ct);
    }

    internal async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (encrypt is null)
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            return;
        }
        byte[] copy = data.ToArray();
        encrypt.Apply(copy);
        await stream.WriteAsync(copy, ct).ConfigureAwait(false);
    }

    internal Task FlushAsync(CancellationToken ct) => stream.FlushAsync(ct);
}
