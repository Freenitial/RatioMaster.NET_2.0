#if DEBUG
namespace RatioMaster;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.Engine;
using RatioMaster.Models;

/// <summary>Incoming peers against the shared listener: plaintext and MSE handshakes, extension and availability messages.</summary>
internal static class PeerWireSelfTest
{
    private static readonly byte[] Hash = Enumerable.Range(1, 20).Select(value => (byte)value).ToArray();
    private static readonly byte[] Metadata = Enumerable.Range(0, 40000).Select(value => (byte)(value * 7)).ToArray();
    private static readonly BigInteger Prime = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563",
        System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

    internal static async Task<bool> RunAsync()
    {
        bool passed = true;
        async Task Check(string name, Func<Task> scenario)
        {
            try
            {
                await scenario().WaitAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine("  [PASS] Peer wire: " + name);
            }
            catch (Exception error)
            {
                passed = false;
                Console.WriteLine("  [FAIL] Peer wire: " + name + " — " + error.Message);
            }
        }
        await Check("plaintext handshake, extension handshake, availability, DHT port and rejected requests", PlaintextAsync);
        await Check("two seeds part after the availability exchange", BothSeedsAsync);
        await Check("no piece yet: HaveNone with the fast extension", NothingYetAsync);
        await Check("public metadata is served in 16 KiB pieces and unknown pieces are rejected", MetadataAsync);
        await Check("MSE with RC4 answers with the torrent's identity through the cipher", () => EncryptedAsync(new Exchange(0x02, ExpectRc4: true)));
        await Check("MSE negotiated down to plaintext when the peer offers both and the client prefers it",
            () => EncryptedAsync(new Exchange(0x03, ExpectRc4: false, Profile: "qBittorrent")));
        await Check("MSE whose public key starts with 19 is not mistaken for a plaintext handshake",
            () => EncryptedAsync(new Exchange(0x02, ExpectRc4: true, PublicKeyStartsWith19: true)));
        await Check("MSE accepts a long initial payload", () => EncryptedAsync(new Exchange(0x02, ExpectRc4: true, InitialPadding: 400)));
        await Check("MSE accepts a handshake sent after the crypto selection", () => EncryptedAsync(new Exchange(0x02, ExpectRc4: true, HandshakeAfterSelection: true)));
        await Check("MSE plaintext-only: bytes sent right after the crypto header stay plaintext",
            () => EncryptedAsync(new Exchange(0x01, ExpectRc4: false, HandshakePipelinedInPlaintext: true)));
        await Check("MSE for an unknown torrent is closed without a reply", UnknownEncryptedAsync);
        await Check("connections reset before being accepted do not stop the listener", ResetBeforeAcceptAsync);
        await Check("partial availability is stable, grows with progress and fills spare bits correctly", PartialBitfield);
        return passed;
    }

    private sealed record Exchange(uint Provide, bool ExpectRc4, string Profile = "Transmission", bool PublicKeyStartsWith19 = false,
        int InitialPadding = 0, bool HandshakeAfterSelection = false, bool HandshakePipelinedInPlaintext = false);

    private static (PeerListenerHub Hub, PeerListenerHub.Registration Registration, int Port, PeerTorrent Torrent) Serve(string family, double progress)
    {
        int port = FreePort();
        PeerListenerHub hub = new();
        ClientProfile profile = family == "qBittorrent" ? ClientCatalog.Create("qBittorrent", "5.2.4") : ClientCatalog.Create("Transmission", "4.1.3");
        PeerTorrent torrent = new()
        {
            InfoHashes = [Hash], PeerId = Encoding.Latin1.GetBytes(profile.PeerID), Wire = profile.PeerWire, AdvertisedPort = port,
            PieceCount = 21, Metadata = Metadata, Realistic = true, Progress = () => progress,
        };
        PeerListenerHub.Registration registration = hub.Register(port, torrent, (_, _) => { });
        return (hub, registration, port, torrent);
    }

    private static async Task PlaintextAsync()
    {
        var (_, registration, port, torrent) = Serve("Transmission", 1);
        using (registration)
        {
            using TcpClient peer = await Connect(port);
            NetworkStream stream = peer.GetStream();
            await stream.WriteAsync(PeerWire.Handshake(Reserved(), Hash, new byte[20]));
            byte[] reply = await ReadExactly(stream, 68);
            Require(reply.AsSpan(28, 20).SequenceEqual(Hash) && reply.AsSpan(48, 20).SequenceEqual(torrent.PeerId), "The reply must name the torrent and its peer ID.");
            Require(reply.AsSpan(20, 8).SequenceEqual(torrent.Wire.Reserved.AsSpan()), "The reserved bits must be the client's.");
            byte[] extended = await ReadMessage(stream);
            Require(extended[0] == PeerWire.Extended && extended[1] == 0 && Encoding.Latin1.GetString(extended, 2, extended.Length - 2).Contains("1:v18:Transmission 4.1.3", StringComparison.Ordinal),
                "An extension handshake must follow when both sides support BEP 10.");
            byte[] availability = await ReadMessage(stream);
            Require(availability.Length == 1 && availability[0] == PeerWire.HaveAll, "A seed with the fast extension announces HaveAll.");
            byte[] dht = await ReadMessage(stream);
            Require(dht.Length == 3 && dht[0] == PeerWire.Port && BinaryPrimitives.ReadUInt16BigEndian(dht.AsSpan(1)) == port,
                "Both sides run a DHT: the listening port follows as the DHT port.");
            byte[] request = new byte[13];
            request[0] = PeerWire.Request;
            BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(1), 3);
            BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(9), 16384);
            await stream.WriteAsync(Frame(request));
            byte[] rejected = await ReadMessage(stream);
            Require(rejected[0] == PeerWire.RejectRequest && rejected.AsSpan(1).SequenceEqual(request.AsSpan(1)), "A block request must be rejected, never served.");
        }
    }

    private static async Task BothSeedsAsync()
    {
        var (_, registration, port, _) = Serve("qBittorrent", 1);
        using (registration)
        {
            using TcpClient peer = await Connect(port);
            NetworkStream stream = peer.GetStream();
            await stream.WriteAsync(PeerWire.Handshake(Reserved(), Hash, new byte[20]));
            await ReadExactly(stream, 68);
            for (int message = 0; message < 3; message++) await ReadMessage(stream);
            await stream.WriteAsync(Frame([PeerWire.HaveAll]));
            Require(await ClosedAsync(stream), "Two seeds have nothing to exchange; the connection must close.");
        }
    }

    private static async Task NothingYetAsync()
    {
        var (_, registration, port, _) = Serve("Transmission", 0.01);
        using (registration)
        {
            using TcpClient peer = await Connect(port);
            NetworkStream stream = peer.GetStream();
            await stream.WriteAsync(PeerWire.Handshake(Convert.FromHexString("0000000000000004"), Hash, new byte[20]));
            await ReadExactly(stream, 68);
            byte[] availability = await ReadMessage(stream);
            Require(availability.Length == 1 && availability[0] == PeerWire.HaveNone,
                "Less than one piece is no piece: HaveNone, never an empty bitfield.");
        }
    }

    private static async Task MetadataAsync()
    {
        var (_, registration, port, _) = Serve("qBittorrent", 1);
        using (registration)
        {
            using TcpClient peer = await Connect(port);
            NetworkStream stream = peer.GetStream();
            await stream.WriteAsync(PeerWire.Handshake(Convert.FromHexString("0000000000100004"), Hash, new byte[20]));
            await ReadExactly(stream, 68);
            byte[] handshake = await ReadMessage(stream);
            Require(Encoding.Latin1.GetString(handshake).Contains("13:metadata_sizei40000e", StringComparison.Ordinal)
                && PeerWire.ReadExtensionId(handshake.AsSpan(2), "ut_metadata") == 2, "A public torrent advertises its metadata.");
            await ReadMessage(stream);
            await stream.WriteAsync(Frame(PeerWire.ExtensionMessage(0, "d1:md11:ut_metadatai5eee"u8)[4..]));
            await stream.WriteAsync(Frame(PeerWire.ExtensionMessage(2, "d8:msg_typei0e5:piecei2ee"u8)[4..]));
            byte[] piece = await ReadMessage(stream);
            byte[] header = "d8:msg_typei1e5:piecei2e10:total_sizei40000ee"u8.ToArray();
            Require(piece[0] == PeerWire.Extended && piece[1] == 5 && piece.AsSpan(2, header.Length).SequenceEqual(header)
                && piece.AsSpan(2 + header.Length).SequenceEqual(Metadata.AsSpan(32768)), "The last metadata piece carries the remaining bytes.");
            await stream.WriteAsync(Frame(PeerWire.ExtensionMessage(2, "d8:msg_typei0e5:piecei3ee"u8)[4..]));
            byte[] reject = await ReadMessage(stream);
            Require(Encoding.Latin1.GetString(reject, 2, reject.Length - 2) == "d8:msg_typei2e5:piecei3e10:total_sizei40000ee",
                "A piece beyond the metadata is rejected, with total_size as libtorrent writes it.");
        }
    }

    private static async Task EncryptedAsync(Exchange exchange)
    {
        var (_, registration, port, torrent) = Serve(exchange.Profile, 1);
        using (registration)
        {
            using TcpClient peer = await Connect(port);
            NetworkStream stream = peer.GetStream();
            BigInteger privateKey;
            byte[] publicKey;
            do
            {
                privateKey = new(RandomNumberGenerator.GetBytes(20), isUnsigned: true, isBigEndian: true);
                publicKey = MessageStreamEncryption.Pad(BigInteger.ModPow(2, privateKey, Prime));
            }
            while (exchange.PublicKeyStartsWith19 && publicKey[0] != 19);
            await stream.WriteAsync((byte[])[.. publicKey, .. RandomNumberGenerator.GetBytes(17)]);
            byte[] answer = await ReadExactly(stream, 96);
            byte[] secret = MessageStreamEncryption.Pad(BigInteger.ModPow(new BigInteger(answer, isUnsigned: true, isBigEndian: true), privateKey, Prime));
            Rc4 outgoing = new(MessageStreamEncryption.Hash("keyA", secret, Hash), 1024);
            Rc4 incoming = new(MessageStreamEncryption.Hash("keyB", secret, Hash), 1024);
            byte[] obfuscated = MessageStreamEncryption.Hash("req2", Hash);
            byte[] req3 = MessageStreamEncryption.Hash("req3", secret);
            for (int index = 0; index < 20; index++) obfuscated[index] ^= req3[index];

            // IA: the handshake, optionally followed by keep-alives, or nothing when the handshake comes later.
            byte[] handshake = PeerWire.Handshake(Reserved(), Hash, Enumerable.Repeat((byte)'p', 20).ToArray());
            bool later = exchange.HandshakeAfterSelection || exchange.HandshakePipelinedInPlaintext;
            byte[] initial = later ? [] : [.. handshake, .. new byte[exchange.InitialPadding]];
            byte[] encrypted = new byte[8 + 4 + 2 + 3 + 2 + initial.Length];
            BinaryPrimitives.WriteUInt32BigEndian(encrypted.AsSpan(8), exchange.Provide);
            BinaryPrimitives.WriteUInt16BigEndian(encrypted.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt16BigEndian(encrypted.AsSpan(17), (ushort)initial.Length);
            initial.CopyTo(encrypted, 19);
            outgoing.Apply(encrypted);
            byte[] plaintextTail = exchange.HandshakePipelinedInPlaintext ? handshake : [];
            await stream.WriteAsync((byte[])[.. MessageStreamEncryption.Hash("req1", secret), .. obfuscated, .. encrypted, .. plaintextTail]);

            // Synchronize on ENCRYPT(VC) after the responder's random PadB.
            byte[] verification = new byte[8];
            incoming.Apply(verification);
            byte[] window = await ReadExactly(stream, 8);
            for (int skipped = 0; !window.AsSpan().SequenceEqual(verification); skipped++)
            {
                Require(skipped <= 512, "The verification constant must follow at most 512 bytes of padding.");
                window = [.. window[1..], .. await ReadExactly(stream, 1)];
            }
            byte[] selection = await ReadExactly(stream, 6);
            incoming.Apply(selection);
            uint selected = BinaryPrimitives.ReadUInt32BigEndian(selection);
            Require(selected == (exchange.ExpectRc4 ? 2u : 1u), "Unexpected crypto selection " + selected + ".");
            byte[] pad = await ReadExactly(stream, BinaryPrimitives.ReadUInt16BigEndian(selection.AsSpan(4)));
            incoming.Apply(pad);
            if (exchange.HandshakeAfterSelection)
            {
                byte[] sent = (byte[])handshake.Clone();
                if (exchange.ExpectRc4) outgoing.Apply(sent);
                await stream.WriteAsync(sent);
            }
            byte[] reply = await ReadExactly(stream, 68);
            if (exchange.ExpectRc4) incoming.Apply(reply);
            Require(reply[0] == 19 && reply.AsSpan(28, 20).SequenceEqual(Hash) && reply.AsSpan(48, 20).SequenceEqual(torrent.PeerId),
                "The encrypted session must carry the torrent's own handshake.");
        }
    }

    private static async Task UnknownEncryptedAsync()
    {
        var (_, registration, port, _) = Serve("Transmission", 1);
        using (registration)
        {
            using TcpClient peer = await Connect(port);
            NetworkStream stream = peer.GetStream();
            BigInteger privateKey = new(RandomNumberGenerator.GetBytes(20), isUnsigned: true, isBigEndian: true);
            await stream.WriteAsync(MessageStreamEncryption.Pad(BigInteger.ModPow(2, privateKey, Prime)));
            byte[] answer = await ReadExactly(stream, 96);
            byte[] secret = MessageStreamEncryption.Pad(BigInteger.ModPow(new BigInteger(answer, isUnsigned: true, isBigEndian: true), privateKey, Prime));
            byte[] obfuscated = MessageStreamEncryption.Hash("req2", new byte[20]);
            byte[] req3 = MessageStreamEncryption.Hash("req3", secret);
            for (int index = 0; index < 20; index++) obfuscated[index] ^= req3[index];
            await stream.WriteAsync((byte[])[.. MessageStreamEncryption.Hash("req1", secret), .. obfuscated, .. new byte[40]]);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            byte[] rest = new byte[1024];
            int total = 0, read;
            try { while ((read = await stream.ReadAsync(rest, timeout.Token)) > 0) total += read; }
            catch (IOException) { }
            Require(total <= 512, "Only PadB may follow for a torrent this port does not serve.");
        }
    }

    private static async Task ResetBeforeAcceptAsync()
    {
        var (_, registration, port, torrent) = Serve("Transmission", 1);
        using (registration)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                using Socket reset = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await reset.ConnectAsync(IPAddress.Loopback, port);
                reset.LingerState = new LingerOption(true, 0);
                reset.Close();
            }
            using TcpClient peer = await Connect(port);
            NetworkStream stream = peer.GetStream();
            await stream.WriteAsync(PeerWire.Handshake(new byte[8], Hash, new byte[20]));
            byte[] reply = await ReadExactly(stream, 68);
            Require(reply.AsSpan(48, 20).SequenceEqual(torrent.PeerId), "The listener must keep answering after reset connections.");
        }
    }

    private static Task PartialBitfield()
    {
        int[] order = PeerWire.PieceOrder(1000, [1, 2, 3]);
        Require(order.Order().SequenceEqual(Enumerable.Range(0, 1000)) && order.SequenceEqual(PeerWire.PieceOrder(1000, [1, 2, 3])),
            "The piece order is a reproducible permutation.");
        byte[] quarter = new byte[PeerWire.BitfieldBytes(1000)], half = new byte[PeerWire.BitfieldBytes(1000)];
        PeerWire.FillBitfield(quarter, order, PeerWire.OwnedPieces(1000, 0.25));
        PeerWire.FillBitfield(half, order, PeerWire.OwnedPieces(1000, 0.5));
        Require(quarter.Sum(value => BitOperations.PopCount(value)) == 250, "The number of pieces must follow progress.");
        Require(quarter.Zip(half).All(pair => (pair.First & ~pair.Second) == 0), "Pieces once announced stay announced as progress grows.");
        Require(PeerWire.OwnedPieces(21, 0.04) == 0 && PeerWire.OwnedPieces(21, 0.999) == 20, "Progress rounds down to whole pieces.");
        byte[] full = new byte[3];
        PeerWire.FillFullBitfield(full, 21);
        Require(full.SequenceEqual(new byte[] { 255, 255, 0xF8 }), "Spare bits of a full bitfield stay clear.");
        return Task.CompletedTask;
    }

    private static byte[] Reserved() => Convert.FromHexString("0000000000100005");

    private static byte[] Frame(ReadOnlySpan<byte> body)
    {
        byte[] message = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(message, body.Length);
        body.CopyTo(message.AsSpan(4));
        return message;
    }

    private static async Task<bool> ClosedAsync(NetworkStream stream)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try { return await stream.ReadAsync(new byte[1], timeout.Token) == 0; }
        catch (IOException) { return true; }
    }

    private static async Task<byte[]> ReadMessage(NetworkStream stream)
    {
        while (true)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(await ReadExactly(stream, 4));
            if (length > 0) return await ReadExactly(stream, length);
        }
    }

    private static async Task<byte[]> ReadExactly(NetworkStream stream, int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    private static async Task<TcpClient> Connect(int port)
    {
        TcpClient client = new(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static int FreePort()
    {
        TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        return port;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
#endif
