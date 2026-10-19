namespace RatioMaster.Engine;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using RatioMaster.Models;

/// <summary>What the listener knows about one emulated torrent while answering its peers.</summary>
internal sealed class PeerTorrent
{
    private int[]? pieceOrder;
    private byte[][]? skeyHashes;

    /// <summary>Wire hashes a peer may name: SHA-1 for v1 and hybrid, truncated SHA-256 for v2 and hybrid.</summary>
    internal required IReadOnlyList<byte[]> InfoHashes { get; init; }
    internal required byte[] PeerId { get; init; }
    internal required PeerWireDefinition Wire { get; init; }
    internal required int AdvertisedPort { get; init; }
    internal bool IsPrivate { get; init; }
    internal bool IsHybrid { get; init; }

    /// <summary>The bencoded info dictionary; public torrents advertise its size and serve it (BEP 9).</summary>
    internal ReadOnlyMemory<byte> Metadata { get; init; }
    internal int PieceCount { get; init; }

    /// <summary>Full peer-wire behaviour (extension handshake, availability, keep-alives); otherwise handshake only.</summary>
    internal bool Realistic { get; init; }

    /// <summary>Current completion as a fraction of the torrent, read for each connection.</summary>
    internal required Func<double> Progress { get; init; }
    internal Action<string>? Log { get; init; }

    /// <summary>Metadata exchange runs on public torrents only, as in every client (BEP 27).</summary>
    internal bool ServesMetadata => !IsPrivate && !Metadata.IsEmpty;

    /// <summary>
    /// The order in which this torrent's pieces are owned: every peer sees the same pieces, and the set only grows
    /// with progress. Seeded by the first info-hash, so v1 and v2 connections to a hybrid torrent agree.
    /// </summary>
    internal int[] PieceOrder => LazyInitializer.EnsureInitialized(ref pieceOrder,
        () => PeerWire.PieceOrder(PieceCount, [.. InfoHashes[0], .. PeerId]));

    /// <summary>HASH('req2', SKEY) for each info-hash: how an MSE initiator names the torrent (computed once).</summary>
    internal byte[][] SkeyHashes => LazyInitializer.EnsureInitialized(ref skeyHashes,
        () => InfoHashes.Select(hash => MessageStreamEncryption.Hash("req2", hash)).ToArray());
}

/// <summary>Peer-wire message construction for the listener. Message layouts follow BEP 3, 5, 6, 9 and 10.</summary>
internal static class PeerWire
{
    internal const byte Bitfield = 5, Request = 6, Port = 9, HaveAll = 14, HaveNone = 15, RejectRequest = 16, Extended = 20;

    /// <summary>BEP 9 metadata pieces are 16 KiB.</summary>
    internal const int MetadataPieceBytes = 16384;

    internal static readonly byte[] Protocol = "BitTorrent protocol"u8.ToArray();

    internal static byte[] Handshake(ReadOnlySpan<byte> reserved, ReadOnlySpan<byte> infoHash, ReadOnlySpan<byte> peerId)
    {
        byte[] message = new byte[68];
        message[0] = 19;
        Protocol.CopyTo(message, 1);
        reserved.CopyTo(message.AsSpan(20));
        infoHash.CopyTo(message.AsSpan(28));
        peerId.CopyTo(message.AsSpan(48));
        return message;
    }

    internal static bool IsPlaintextHandshake(ReadOnlySpan<byte> start) =>
        start.Length >= 20 && start[0] == 19 && start.Slice(1, 19).SequenceEqual(Protocol);

    internal static byte[] Message(byte id, ReadOnlySpan<byte> payload = default)
    {
        byte[] message = Allocate(id, payload.Length);
        payload.CopyTo(message.AsSpan(5));
        return message;
    }

    /// <summary>A framed message whose payload (from offset 5) the caller fills in place.</summary>
    internal static byte[] Allocate(byte id, int payloadLength)
    {
        byte[] message = new byte[5 + payloadLength];
        BinaryPrimitives.WriteInt32BigEndian(message, payloadLength + 1);
        message[4] = id;
        return message;
    }

    internal static byte[] KeepAlive() => new byte[4];

    /// <summary>BEP 5 PORT: the DHT port, which libtorrent and Transmission keep equal to the listening port.</summary>
    internal static byte[] DhtPort(int port)
    {
        byte[] message = Allocate(Port, 2);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(5), (ushort)port);
        return message;
    }

    internal static byte[] ExtensionMessage(byte extensionId, ReadOnlySpan<byte> bencoded, ReadOnlySpan<byte> trailer = default)
    {
        byte[] message = Allocate(Extended, 1 + bencoded.Length + trailer.Length);
        message[5] = extensionId;
        bencoded.CopyTo(message.AsSpan(6));
        trailer.CopyTo(message.AsSpan(6 + bencoded.Length));
        return message;
    }

    /// <summary>The ut_metadata number each family assigns itself in its extension handshake.</summary>
    internal static byte MetadataExtensionId(ExtensionHandshakeStyle style) => style == ExtensionHandshakeStyle.Transmission ? (byte)3 : (byte)2;

    /// <summary>
    /// Builds the BEP 10 handshake each family actually sends on an incoming connection, from the
    /// libtorrent (bt_peer_connection::write_extensions) and Transmission (send_ltep_handshake) sources.
    /// </summary>
    internal static byte[] ExtensionHandshake(PeerTorrent torrent, IPAddress remote, bool seeding)
    {
        PeerWireDefinition wire = torrent.Wire;
        BencodeWriter.Dictionary handshake = new();
        BencodeWriter.Dictionary extensions = new();
        long metadataId = MetadataExtensionId(wire.Style);
        switch (wire.Style)
        {
            case ExtensionHandshakeStyle.Libtorrent:
                extensions.Add("upload_only", 3).Add("ut_holepunch", 4).Add("lt_donthave", 7).Add("share_mode", 8);
                if (!torrent.IsPrivate) extensions.Add("ut_metadata", metadataId).Add("ut_pex", 1);
                handshake.Add("m", extensions).Add("reqq", wire.RequestQueue).Add("complete_ago", seeding ? 0 : -1);
                if (seeding) handshake.Add("upload_only", 1);
                break;
            case ExtensionHandshakeStyle.Transmission:
                if (!torrent.IsPrivate) extensions.Add("ut_metadata", metadataId).Add("ut_pex", 1);
                handshake.Add("e", 1).Add("p", torrent.AdvertisedPort).Add("reqq", wire.RequestQueue).Add("upload_only", seeding ? 1 : 0);
                if (!torrent.IsPrivate) handshake.Add("m", extensions);
                break;
            case ExtensionHandshakeStyle.UTorrent:
                extensions.Add("upload_only", 3).Add("ut_holepunch", 4).Add("lt_donthave", 7);
                if (!torrent.IsPrivate) extensions.Add("ut_metadata", metadataId).Add("ut_pex", 1);
                handshake.Add("e", 0).Add("m", extensions).Add("p", torrent.AdvertisedPort).Add("reqq", wire.RequestQueue)
                    .Add("complete_ago", seeding ? 0 : -1);
                break;
            default:
                return [];
        }
        if (torrent.ServesMetadata) handshake.Add("metadata_size", torrent.Metadata.Length);
        if (wire.ClientName.Length > 0) handshake.Add("v", Encoding.UTF8.GetBytes(wire.ClientName));
        byte[] address = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).GetAddressBytes();
        handshake.Add("yourip", address);
        return handshake.Encode();
    }

    /// <summary>Pieces owned at this progress; rounding down, so a torrent is only complete when it is.</summary>
    internal static int OwnedPieces(int pieceCount, double progress) =>
        (int)Math.Clamp(Math.Floor(pieceCount * Math.Clamp(progress, 0, 1)), 0, pieceCount);

    /// <summary>A reproducible shuffle of the piece indexes for one seed.</summary>
    internal static int[] PieceOrder(int pieceCount, ReadOnlySpan<byte> seed)
    {
        int[] order = Enumerable.Range(0, pieceCount).ToArray();
        new Random(BinaryPrimitives.ReadInt32LittleEndian(System.Security.Cryptography.SHA256.HashData(seed))).Shuffle(order);
        return order;
    }

    /// <summary>The bitfield of the first <paramref name="owned"/> pieces of <paramref name="order"/>.</summary>
    internal static void FillBitfield(Span<byte> bits, ReadOnlySpan<int> order, int owned)
    {
        bits.Clear();
        foreach (int piece in order[..owned]) bits[piece >> 3] |= (byte)(0x80 >> (piece & 7));
    }

    internal static void FillFullBitfield(Span<byte> bits, int pieceCount)
    {
        bits.Fill(255);
        int remainder = pieceCount % 8;
        if (remainder != 0) bits[^1] = (byte)(255 << (8 - remainder));
    }

    internal static int BitfieldBytes(int pieceCount) => (pieceCount + 7) / 8;

    /// <summary>Reads the extension number a peer assigned to <paramref name="name"/> in its own extension handshake.</summary>
    internal static int ReadExtensionId(ReadOnlySpan<byte> bencoded, string name)
    {
        if (Decode(bencoded) is not BitTorrent.ValueDictionary root || !root.Contains("m")
            || root["m"] is not BitTorrent.ValueDictionary map || !map.Contains(name)
            || map[name] is not BitTorrent.ValueNumber number) return 0;
        return number.Integer is > 0 and < 256 ? (int)number.Integer : 0;
    }

    /// <summary>The metadata index a ut_metadata request asks for, when the message is a request (msg_type 0).</summary>
    internal static int? ReadMetadataRequest(ReadOnlySpan<byte> bencoded)
    {
        if (Decode(bencoded) is not BitTorrent.ValueDictionary root
            || !root.Contains("msg_type") || root["msg_type"] is not BitTorrent.ValueNumber type || type.Integer != 0
            || !root.Contains("piece") || root["piece"] is not BitTorrent.ValueNumber piece || piece.Integer is < 0 or > int.MaxValue) return null;
        return (int)piece.Integer;
    }

    /// <summary>
    /// The answer to a ut_metadata request: the 16 KiB piece (msg_type 1) when it exists, else a reject (msg_type 2).
    /// libtorrent also states total_size in its rejects (ut_metadata.cpp write_metadata_packet); Transmission does not.
    /// </summary>
    internal static byte[] MetadataAnswer(PeerTorrent torrent, byte peerExtensionId, int piece)
    {
        int size = torrent.Metadata.Length;
        int pieces = (size + MetadataPieceBytes - 1) / MetadataPieceBytes;
        BencodeWriter.Dictionary header = new BencodeWriter.Dictionary().Add("piece", piece);
        if (piece < pieces)
        {
            int offset = piece * MetadataPieceBytes;
            header.Add("msg_type", 1).Add("total_size", size);
            return ExtensionMessage(peerExtensionId, header.Encode(),
                torrent.Metadata.Span.Slice(offset, Math.Min(MetadataPieceBytes, size - offset)));
        }
        header.Add("msg_type", 2);
        if (torrent.Wire.Style == ExtensionHandshakeStyle.Libtorrent) header.Add("total_size", size);
        return ExtensionMessage(peerExtensionId, header.Encode());
    }

    private static BitTorrent.IBEncodeValue? Decode(ReadOnlySpan<byte> bencoded)
    {
        try
        {
            using MemoryStream stream = new(bencoded.ToArray(), writable: false);
            return BitTorrent.BEncode.Parse(stream);
        }
        catch (Exception error) when (error is BitTorrent.TorrentException or FormatException or OverflowException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Minimal canonical bencode writer: dictionary keys are emitted in raw byte order as BEP 3 requires.</summary>
internal static class BencodeWriter
{
    internal sealed class Dictionary
    {
        private readonly SortedDictionary<string, object> entries = new(StringComparer.Ordinal);

        internal Dictionary Add(string key, long value) { entries[key] = value; return this; }
        internal Dictionary Add(string key, byte[] value) { entries[key] = value; return this; }
        internal Dictionary Add(string key, Dictionary value) { entries[key] = value; return this; }

        internal byte[] Encode()
        {
            using MemoryStream output = new();
            Write(output);
            return output.ToArray();
        }

        private void Write(MemoryStream output)
        {
            output.WriteByte((byte)'d');
            foreach ((string key, object value) in entries)
            {
                WriteBytes(output, Encoding.ASCII.GetBytes(key));
                switch (value)
                {
                    case long number:
                        output.Write(Encoding.ASCII.GetBytes("i" + number.ToString(CultureInfo.InvariantCulture) + "e"));
                        break;
                    case byte[] bytes:
                        WriteBytes(output, bytes);
                        break;
                    case Dictionary nested:
                        nested.Write(output);
                        break;
                }
            }
            output.WriteByte((byte)'e');
        }

        private static void WriteBytes(MemoryStream output, byte[] bytes)
        {
            output.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            output.Write(bytes);
        }
    }
}
