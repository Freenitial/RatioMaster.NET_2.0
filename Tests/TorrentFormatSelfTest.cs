#if DEBUG
namespace RatioMaster;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RatioMaster.BitTorrent;

/// <summary>Offline metainfo fixtures and format checks; invoke TorrentFormatSelfTest.Run() in DEBUG.</summary>
internal static class TorrentFormatSelfTest
{
    private const string V1Hash = "5717562f647c27a4be5d2f83aed3c22802659137";
    private const string V2Hash = "1d77eccff89e6a68d69fbc7009d7262912a5a7600437ba670547f69e64eb7b26";
    private const string HybridHash = "da391a4b8b697dd0cdde2b2e839060c2935cfa65";
    private const string HybridFullHash = "b041ecb03d18c4a5fa1288d32875ed828b7291b90a3357ad80f08dcbf2632abe";
    private const string AbcSha1 = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    internal static bool Run()
    {
        bool passed = true;
        void Check(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("  [PASS] Torrent format: " + name);
            }
            catch (Exception exception)
            {
                passed = false;
                Console.WriteLine("  [FAIL] Torrent format: " + name + " — " + exception.Message);
            }
        }

        Check("known v1/v2/hybrid info hashes", KnownHashes);
        Check("exact info slice and immutable hash snapshots", ExactInfoBytes);
        Check("UTF-8 names and byte-ordered file trees", UnicodeNames);
        Check("v1 multi-file layout and empty files", V1Layouts);
        Check("v2 multi-file piece alignment and empty files", V2Layouts);
        Check("hybrid alignment, padding and matching file order", HybridLayouts);
        Check("BEP 52 Merkle padding at three piece sizes", MerkleLayers);
        Check("piece layer shape, missing roots and root mismatches", MalformedLayers);
        Check("strict canonical bencoding and typed fields", MalformedBencoding);
        Check("size ranges and exact v1 pieces count", MalformedSizes);
        Check("file trees and path conflicts", MalformedPaths);
        Check("malformed v1/v2 hybrid descriptions cannot fall back", MalformedHybrids);
        Check("every prefix of valid metainfo is rejected", TruncatedInput);
        Check("stream ownership, short reads and atomic reload", StreamsAndReload);
        Check("trackerless metainfo and announce-list fallback", AnnounceMetadata);
        Check("bounded nesting, nodes, paths and input bytes", ResourceLimits);
        return passed;
    }

    internal static byte[] V2FixtureBytes() => Wrap(V2(TestTree()));

    internal static Torrent V2Fixture() => Parse(Wrap(V2(TestTree())));

    private static void KnownHashes()
    {
        // Hash constants use independently encoded metainfo for the three-byte payload "abc".
        Torrent v1 = Parse(Wrap(V1(3, Hex(AbcSha1))));
        EqualHash(v1.InfoHash, V1Hash);
        EqualHash(v1.FullInfoHash, V1Hash);
        Require(!v1.IsV2 && !v1.IsHybrid && v1.TotalLength == 3 && v1.PieceCount == 1);

        Torrent v2 = Parse(Wrap(V2(TestTree())));
        EqualHash(v2.InfoHash, V2Hash[..40]);
        EqualHash(v2.FullInfoHash, V2Hash);
        Require(v2.IsV2 && !v2.IsHybrid && v2.TotalLength == 3 && v2.PieceCount == 1);

        Torrent hybrid = Parse(Wrap(V2(TestTree(), 16384,
            ("length", Number(3)), ("pieces", Bytes(Hex(AbcSha1))))));
        EqualHash(hybrid.InfoHash, HybridHash);
        EqualHash(hybrid.FullInfoHash, HybridFullHash);
        Require(hybrid.IsV2 && hybrid.IsHybrid && hybrid.InfoHash.Length == 20);
        Require(v1.Name == "test" && v1.Announce == "https://tracker.invalid/announce");
    }

    private static void ExactInfoBytes()
    {
        byte[] info = Dict(("length", Number(3)), ("name", Text("test")),
            ("piece length", Number(16384)), ("pieces", Bytes(Hex(AbcSha1))),
            ("unknown", Raw("i-9223372036854775808e")), ("\u0080", Bytes([0, 128, 255])));
        byte[] metainfo = Dict(("comment", Bytes(Raw("4:infod6:lengthi999ee"))), ("info", info));
        Torrent torrent = Parse(metainfo);
        byte[] expected = SHA1.HashData(info);
        Require(torrent.InfoHash.AsSpan().SequenceEqual(expected));
        torrent.Info["name"] = new ValueString("mutated");
        torrent.Info["pieces"] = new ValueString("mutated");
        byte[] returned = torrent.InfoHash;
        returned[0] ^= 255;
        Require(torrent.InfoHash.AsSpan().SequenceEqual(expected));
        Require(torrent.FullInfoHash.AsSpan().SequenceEqual(expected) && torrent.Name == "test");

        Torrent v2 = Parse(Wrap(V2(TestTree())));
        v2.FullInfoHash[0] ^= 255;
        EqualHash(v2.FullInfoHash, V2Hash);
        Require(v2.InfoHash.Length == 20);
    }

    private static void UnicodeNames()
    {
        const string name = "été-日本-😀.bin";
        Torrent v1 = Parse(Wrap(V1(3, Hex(AbcSha1), name: name)));
        Require(v1.Name == name);
        byte[] tree = Dict((Key("é"), Leaf(3)), ("z", Leaf(0)));
        Torrent hybrid = Parse(Wrap(V2(tree, 16384,
            ("files", List(FileEntry(0, "z"), FileEntry(3, "é"))), ("pieces", Bytes(new byte[20])))));
        Require(hybrid.IsHybrid && hybrid.TotalLength == 3);
        Reject(Wrap(Dict(("length", Number(0)), ("name", Bytes([0xC0, 0xAF])),
            ("piece length", Number(16384)), ("pieces", Bytes([])))));
        Reject(Wrap(V2(Dict(("\u00FF", Leaf(0))))));
    }

    private static void V1Layouts()
    {
        byte[] files = List(FileEntry(1, "a"), FileEntry(0, "empty"), FileEntry(2, "dir/b"));
        Torrent torrent = Parse(Wrap(V1Files(files, new byte[40], 2)));
        // Three bytes at two bytes per piece require two SHA-1 hashes.
        Require(torrent.PieceCount == 2 && torrent.TotalLength == 3);
    }

    private static void V2Layouts()
    {
        byte[] tree = Dict(("a", Leaf(1)), ("dir", Dict(("b", Leaf(2)))), ("empty", Leaf(0)));
        Torrent torrent = Parse(Wrap(V2(tree)));
        Require(torrent.TotalLength == 3 && torrent.PieceCount == 2);
        Torrent empty = Parse(Wrap(V2(Dict(("empty", Leaf(0))))));
        Require(empty.TotalLength == 0 && empty.PieceCount == 0 && empty.InfoHash.Length == 20);
        Torrent v1Empty = Parse(Wrap(V1(0, [])));
        Require(v1Empty.PieceCount == 0 && v1Empty.TotalLength == 0);
        Torrent multiEmpty = Parse(Wrap(V1Files(List(FileEntry(0, "empty")), [])));
        Require(multiEmpty.TotalLength == 0 && multiEmpty.PieceCount == 0);
    }

    private static void HybridLayouts()
    {
        byte[] tree = Dict(("a", Leaf(1)), ("b", Leaf(2)), ("empty", Leaf(0)));
        byte[] files = List(FileEntry(1, "a"), FileEntry(16383, ".pad/16383", true),
            FileEntry(2, "b"), FileEntry(0, "empty"));
        Torrent torrent = Parse(Wrap(V2(tree, 16384, ("files", files), ("pieces", Bytes(new byte[40])))));
        Require(torrent.IsHybrid && torrent.TotalLength == 16386 && torrent.PieceCount == 2);
        byte[] tailPadded = List(FileEntry(1, "a"), FileEntry(16383, ".pad/16383", true),
            FileEntry(2, "b"), FileEntry(16382, ".pad/16382", true), FileEntry(0, "empty"));
        Torrent padded = Parse(Wrap(V2(tree, 16384, ("files", tailPadded), ("pieces", Bytes(new byte[40])))));
        Require(padded.TotalLength == 32768 && padded.PieceCount == 2);
        Torrent empty = Parse(Wrap(V2(Dict(("test", Leaf(0))), 16384,
            ("length", Number(0)), ("pieces", Bytes([])))));
        Require(empty.IsHybrid && empty.TotalLength == 0 && empty.PieceCount == 0);

        byte[] repeatedPads = List(FileEntry(1, "a"), FileEntry(16383, ".pad/16383", true),
            FileEntry(1, "b"), FileEntry(16383, ".pad/16383", true), FileEntry(1, "c"));
        Torrent repeated = Parse(Wrap(V2(Dict(("a", Leaf(1)), ("b", Leaf(1)), ("c", Leaf(1))), 16384,
            ("files", repeatedPads), ("pieces", Bytes(new byte[60])))));
        Require(repeated.PieceCount == 3 && repeated.TotalLength == 32769);
    }

    private static readonly (int PieceLength, int Blocks, string Root, string Layer)[] MerkleVectors =
    [
        (16384, 3, "3a399668ed9c3bb00bde9e1d7cde44b39b39867155793bbc0a879cbc4ca9dd1e",
            "4fe7b59af6de3b665b67788cc2f99892ab827efae3a467342b3bb4e3bc8e5bfe111ce3c2a38d83a2e4706bde4abddd509d7f8248116c6832b06745bdc349e09f746664dba900c81ef311c8456e15b02a5efeee3736a4f3827ce1eb1e0c24d8da"),
        (32768, 5, "02d0b015ee763c012ad0246da46d035d5aeaef792982375394e32a28c7d02712",
            "bb3d7ed87517034230008512a51a4bdc428c7cc128ef619425e01d124030c7b6ca93ba2c3a43211ef471fc52d1e6bac03c02b31ece8e8ba560cb487253d5e0e9e05004071bb3577eec8e7097e4f20d2cc18485b5fca1c5619ff7e3f0760ee57e"),
        (65536, 9, "71da307761460db26fcde06214eebfc4f5e1bcf9645425b53ec8d79bc09f5b79",
            "c8370a13184041b3299b5822a2f92ccac9d87427d736b0e7dfe433d28387e7abf60f4a05e9a61c2215b05561bd01004836b30690f0088db4715f424e75a0cde4224b1a992b7304bbaa113ff7f2491c93db87b1ab4eeead35ff4f7157528f0a63"),
    ];

    private static void MerkleLayers()
    {
        // Each vector hashes consecutive 16 KiB blocks filled with their zero-based block index.
        foreach (var vector in MerkleVectors)
        {
            byte[] root = Hex(vector.Root);
            byte[] layer = Hex(vector.Layer);
            byte[] tree = Dict(("test", Leaf(vector.Blocks * 16384, root)));
            Torrent torrent = Parse(Wrap(V2(tree, vector.PieceLength), Layers(root, layer)));
            Require(torrent.TotalLength == (ulong)vector.Blocks * 16384 && torrent.PieceCount == 3);
            byte[] hybrid = V2(tree, vector.PieceLength,
                ("length", Number(vector.Blocks * 16384)), ("pieces", Bytes(new byte[60])));
            Require(Parse(Wrap(hybrid, Layers(root, layer))).IsHybrid);

            byte[] shared = Dict(("a", Leaf(vector.Blocks * 16384, root)),
                ("b", Leaf(vector.Blocks * 16384, root)));
            Require(Parse(Wrap(V2(shared, vector.PieceLength), Layers(root, layer))).PieceCount == 6);
        }

        byte[] twoHashes = Hex(MerkleVectors[0].Layer)[..64];
        byte[] twoRoot = SHA256.HashData(twoHashes);
        Require(Parse(Wrap(V2(Dict(("test", Leaf(32768, twoRoot)))), Layers(twoRoot, twoHashes))).PieceCount == 2);
    }

    private static void MalformedLayers()
    {
        var vector = MerkleVectors[1];
        byte[] root = Hex(vector.Root);
        byte[] layer = Hex(vector.Layer);
        byte[] info = V2(Dict(("test", Leaf(vector.Blocks * 16384, root))), vector.PieceLength);
        Reject(Wrap(info));
        Reject(Wrap(info, Dict()));
        Reject(Wrap(info, List()));
        Reject(Wrap(info, Layers(root, layer[..^1])));
        Reject(Wrap(info, Layers(root, layer[..^32])));
        Reject(Wrap(info, Layers(root, Join(layer, new byte[32]))));
        byte[] damaged = (byte[])layer.Clone();
        damaged[7] ^= 1;
        Reject(Wrap(info, Layers(root, damaged)));
        byte[] wrongRoot = (byte[])root.Clone();
        wrongRoot[0] ^= 1;
        Reject(Wrap(info, Layers(wrongRoot, layer)));
        Reject(Wrap(info, Dict((Encoding.Latin1.GetString(root), List()))));
        Reject(Wrap(info, Layers(root[..31], layer)));
        Reject(Wrap(V2(TestTree()), Layers(root, layer)));
        Reject(Wrap(V2(TestTree()), Dict((new string('x', 32), Bytes([])))));
        byte[] conflicting = Dict(("a", Leaf(65536, root)), ("b", Leaf(81920, root)));
        Reject(Wrap(V2(conflicting, 32768), Layers(root, layer)));
        byte[] hybrid = V2(Dict(("test", Leaf(81920, root))), 32768,
            ("length", Number(81920)), ("pieces", Bytes(new byte[60])));
        Reject(Wrap(hybrid, Layers(root, damaged)));
    }

    private static void MalformedBencoding()
    {
        foreach (string malformed in new[]
        {
            "", "de", "le", "i0e", "d4:infoi1ee", "d4:infolee", "d1:ai0e1:ai1ee",
            "d1:bi0e1:ai0ee", "diei0ee", "d1:a4:abc", "d1:a99999999999999999999:",
        })
        {
            Reject(Raw(malformed));
        }

        foreach (string malformedValue in new[]
        {
            "i01e", "i-0e", "i+1e", "ie", "i-e", "i--1e", "i 1e", "i1.0e",
            "i9223372036854775808e", "i-9223372036854775809e", "i11111111111111111111111111111e",
            "01:x", "-1:", ":", "1x:x", "2:x", "x", "d1:bi0e1:ai0ee", "d1:ai0e1:ai1ee",
        })
        {
            Reject(Dict(("extra", Raw(malformedValue)), ("info", V1(3, Hex(AbcSha1)))));
        }

        Reject(Join(Wrap(V1(3, Hex(AbcSha1))), Raw("e")));
        Reject(Dict(("info", V1(3, Hex(AbcSha1))), ("info", V1(3, Hex(AbcSha1)))));
        Reject(Wrap(Dict(("length", Number(0)), ("name", Number(1)),
            ("piece length", Number(16384)), ("pieces", Bytes([])))));
    }

    private static void MalformedSizes()
    {
        Reject(Wrap(V1(-1, [])));
        Reject(Wrap(V1(1, new byte[19])));
        Reject(Wrap(V1(1, new byte[21])));
        Reject(Wrap(V1(32768, new byte[20])));
        Reject(Wrap(V1(1, new byte[40])));
        Reject(Wrap(V1(0, new byte[20])));
        Reject(Wrap(V1(0, [], 0)));
        Reject(Wrap(V1(0, [], -1)));
        Reject(Wrap(V1(long.MaxValue, [], 1)));
        Reject(Wrap(V1Files(List(FileEntry(long.MaxValue, "a"), FileEntry(1, "b")), [])));
        Reject(Wrap(V1Files(List(FileEntry(-1, "a")), [])));
        Reject(Wrap(V1Files(List(), [])));
        foreach (long pieceLength in new long[] { -1, 0, 8192, 24576 })
        {
            Reject(Wrap(V2(TestTree(), pieceLength)));
        }

        Reject(Wrap(V2(Dict(("a", Leaf(long.MaxValue)), ("b", Leaf(1))), 1L << 62)));
        Reject(Wrap(V2(Dict(("a", Leaf(-1))))));
        Require(Parse(Wrap(V1(long.MaxValue, new byte[20], long.MaxValue))).TotalLength == (ulong)long.MaxValue);
        Require(Parse(Wrap(V1(2, new byte[40], 1))).PieceCount == 2);
    }

    private static void MalformedPaths()
    {
        Reject(Wrap(V2(Dict())));
        Reject(Wrap(V2(Leaf(3))));
        Reject(Wrap(V2(Dict(("test", Dict())))));
        Reject(Wrap(V2(Dict(("test", Number(3))))));
        Reject(Wrap(V2(Dict(("test", Dict(("", List())))))));
        Reject(Wrap(V2(Dict(("test", Dict(("", Dict(("length", Number(1))))))))));
        Reject(Wrap(V2(Dict(("test", Leaf(0, Hex(AbcSha256)))))));
        Reject(Wrap(V2(Dict(("test", Leaf(1, new byte[31]))))));
        byte[] both = Dict(("", Dict(("length", Number(0)))), ("child", Leaf(0)));
        Reject(Wrap(V2(Dict(("test", both)))));
        foreach (string path in new[] { "", ".", "..", "a/b", "a\\b", "a\0b" })
        {
            Reject(Wrap(V2(Dict((path, Leaf(0))))));
            Reject(Wrap(V1(0, [], name: path)));
        }

        Reject(Wrap(V1Files(List(FileEntry(0, "same"), FileEntry(0, "same")), [])));
        Reject(Wrap(V1Files(List(FileEntry(0, "a"), FileEntry(0, "a-"), FileEntry(0, "a/b")), [])));
        Reject(Wrap(V1Files(List(Dict(("length", Number(0)), ("path", List()))), [])));
        Reject(Wrap(V1Files(List(FileEntry(0, "../a")), [])));
        Reject(Wrap(V1Files(List(Number(0)), [])));
        Reject(Wrap(V2(Dict(("test", Dict(("", Dict(("attr", Text("l")),
            ("length", Number(0)), ("symlink path", List(Text("target")))))))))));
    }

    private static void MalformedHybrids()
    {
        Reject(Wrap(V2(TestTree(), 16384, ("length", Number(3)))));
        Reject(Wrap(V2(TestTree(), 16384, ("pieces", Bytes(new byte[20])))));
        Reject(Wrap(V2(TestTree(), 16384, ("length", Number(3)), ("pieces", List()))));
        Reject(Wrap(V2(TestTree(), 16384, ("files", List()), ("pieces", Bytes([])))));
        Reject(Wrap(V2(TestTree(), 16384, ("length", Number(4)), ("pieces", Bytes(new byte[20])))));
        Reject(Wrap(V2(TestTree(), 16384, ("length", Number(3)), ("files", List()), ("pieces", Bytes(new byte[20])))));
        Reject(Wrap(Dict(("file tree", TestTree()), ("length", Number(3)), ("name", Text("test")),
            ("piece length", Number(16384)), ("pieces", Bytes(new byte[20])))));
        Reject(Wrap(Dict(("length", Number(3)), ("meta version", Number(3)), ("name", Text("test")),
            ("piece length", Number(16384)), ("pieces", Bytes(new byte[20])))));
        Reject(Wrap(Dict(("length", Number(3)), ("meta version", Number(2)), ("name", Text("test")),
            ("piece length", Number(16384)), ("pieces", Bytes(new byte[20])))));
        Reject(Wrap(V1(3, new byte[20]), Dict()));
        byte[] tree = Dict(("a", Leaf(1)), ("b", Leaf(2)));
        foreach (byte[] files in new[]
        {
            List(FileEntry(1, "a"), FileEntry(2, "b")),
            List(FileEntry(2, "b"), FileEntry(16382, ".pad/x", true), FileEntry(1, "a")),
            List(FileEntry(1, "a"), FileEntry(16382, ".pad/x", true), FileEntry(2, "b")),
            List(FileEntry(1, "a"), FileEntry(16383, ".pad/x", true), FileEntry(2, "c")),
            List(FileEntry(1, "a"), FileEntry(16383, ".pad/x", true)),
            List(FileEntry(16384, ".pad/x", true), FileEntry(1, "a"), FileEntry(2, "b")),
        })
        {
            Reject(Wrap(V2(tree, 16384, ("files", files), ("pieces", Bytes(new byte[40])))));
        }
    }

    private static void TruncatedInput()
    {
        var vector = MerkleVectors[0];
        foreach (byte[] complete in new[]
        {
            Wrap(V1(3, Hex(AbcSha1))), Wrap(V2(TestTree())),
            Wrap(V2(TestTree(), 16384, ("length", Number(3)), ("pieces", Bytes(Hex(AbcSha1))))),
            Wrap(V2(Dict(("test", Leaf(49152, Hex(vector.Root))))), Layers(Hex(vector.Root), Hex(vector.Layer))),
        })
        {
            Parse(complete);
            for (int length = 0; length < complete.Length; length++)
            {
                Reject(complete[..length]);
            }
        }
    }

    private static void StreamsAndReload()
    {
        byte[] valid = Wrap(V1(3, Hex(AbcSha1)));
        using TestStream chunked = new(valid, 1);
        Torrent torrent = new(chunked);
        Require(!chunked.Disposed && chunked.BytesRead == valid.Length);
        EqualHash(torrent.InfoHash, V1Hash);
        using MemoryStream positioned = new(Join([9, 8, 7], valid));
        positioned.Position = 3;
        EqualHash(new Torrent(positioned).InfoHash, V1Hash);
        Require(positioned.CanRead);
        using TestStream broken = new([], fail: true);
        Require(!torrent.OpenTorrent(broken) && !broken.Disposed);
        EqualHash(torrent.InfoHash, V1Hash);
        bool threw = false;
        try
        {
            _ = new Torrent(broken);
        }
        catch (IOException)
        {
            threw = true;
        }

        Require(threw);
        using TestStream malformed = new(valid[..^1], 2);
        try
        {
            torrent.OpenTorrent(malformed);
            throw new InvalidOperationException("Malformed reload was accepted.");
        }
        catch (TorrentException)
        {
            Require(!malformed.Disposed);
        }

        EqualHash(torrent.InfoHash, V1Hash);
        using MemoryStream v2 = new(Wrap(V2(TestTree())));
        Require(torrent.OpenTorrent(v2) && torrent.IsV2 && !torrent.IsHybrid);
        EqualHash(torrent.FullInfoHash, V2Hash);
    }

    private static void AnnounceMetadata()
    {
        byte[] info = V1(0, []);
        Require(Parse(Dict(("info", info))).Announce == string.Empty);
        byte[] tiers = List(List(Text("https://first.invalid/a")), List(Text("udp://second.invalid:80")));
        Require(Parse(Dict(("announce-list", tiers), ("info", info))).Announce == "https://first.invalid/a");
        Reject(Dict(("announce", Number(1)), ("info", info)));
        Reject(Dict(("announce-list", List(Text("https://invalid.invalid"))), ("info", info)));
        Reject(Dict(("announce-list", List(List(Number(1)))), ("info", info)));
    }

    private static void ResourceLimits()
    {
        Reject(Raw(new string('l', TorrentBencodeReader.MaxDepth + 2)
            + new string('e', TorrentBencodeReader.MaxDepth + 2)));
        Reject(Raw("l" + string.Concat(Enumerable.Repeat("0:", TorrentBencodeReader.MaxNodes)) + "e"));
        Reject(Raw("d1:a67108865:"));
        Reject(Wrap(V1(0, [], name: new string('a', 4097))));
        Reject(Dict((new string('a', 4097), Number(0)), ("info", V1(0, []))));
        using TestStream oversized = new([], syntheticLength: (long)TorrentBencodeReader.MaxMetainfoBytes + 1);
        try
        {
            _ = new Torrent(oversized);
            throw new InvalidOperationException("Oversized stream was accepted.");
        }
        catch (TorrentException)
        {
            Require(oversized.BytesRead == (long)TorrentBencodeReader.MaxMetainfoBytes + 1 && !oversized.Disposed);
        }
    }

    private static byte[] V1(long length, byte[] pieces, long pieceLength = 16384, string name = "test") =>
        Dict(("length", Number(length)), ("name", Text(name)),
            ("piece length", Number(pieceLength)), ("pieces", Bytes(pieces)));

    private static byte[] V1Files(byte[] files, byte[] pieces, long pieceLength = 16384) =>
        Dict(("files", files), ("name", Text("test")), ("piece length", Number(pieceLength)), ("pieces", Bytes(pieces)));

    private static byte[] V2(byte[] tree, long pieceLength = 16384, params (string Key, byte[] Value)[] extra)
    {
        List<(string Key, byte[] Value)> fields =
        [
            ("file tree", tree), ("meta version", Number(2)), ("name", Text("test")),
            ("piece length", Number(pieceLength)),
        ];
        fields.AddRange(extra);
        return Dict(fields.ToArray());
    }

    private static byte[] TestTree() => Dict(("test", Leaf(3)));

    private static byte[] Leaf(long length, byte[]? root = null)
    {
        byte[] properties = length == 0 && root is null
            ? Dict(("length", Number(0)))
            : Dict(("length", Number(length)), ("pieces root", Bytes(root ?? Hex(AbcSha256))));
        return Dict(("", properties));
    }

    private static byte[] FileEntry(long length, string path, bool padding = false)
    {
        byte[] parts = List(path.Split('/').Select(Text).ToArray());
        return padding
            ? Dict(("attr", Text("p")), ("length", Number(length)), ("path", parts))
            : Dict(("length", Number(length)), ("path", parts));
    }

    private static byte[] Layers(byte[] root, byte[] hashes) => Dict((Encoding.Latin1.GetString(root), Bytes(hashes)));

    private static byte[] Wrap(byte[] info, byte[]? layers = null) => layers is null
        ? Dict(("announce", Text("https://tracker.invalid/announce")), ("info", info))
        : Dict(("announce", Text("https://tracker.invalid/announce")), ("info", info), ("piece layers", layers));

    private static byte[] Dict(params (string Key, byte[] Value)[] fields)
    {
        using MemoryStream stream = new();
        stream.WriteByte((byte)'d');
        foreach (var field in fields.OrderBy(field => field.Key, StringComparer.Ordinal))
        {
            stream.Write(Bytes(Encoding.Latin1.GetBytes(field.Key)));
            stream.Write(field.Value);
        }

        stream.WriteByte((byte)'e');
        return stream.ToArray();
    }

    private static byte[] List(params byte[][] items) => Join(Raw("l"), Join(items), Raw("e"));
    private static byte[] Text(string text) => Bytes(Encoding.UTF8.GetBytes(text));
    private static string Key(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));
    private static byte[] Number(long number) => Raw("i" + number.ToString(CultureInfo.InvariantCulture) + "e");
    private static byte[] Bytes(byte[] bytes) => Join(Raw(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"), bytes);
    private static byte[] Raw(string text) => Encoding.Latin1.GetBytes(text);
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static byte[] Join(params byte[][] parts)
    {
        using MemoryStream stream = new();
        foreach (byte[] part in parts)
        {
            stream.Write(part);
        }

        return stream.ToArray();
    }

    private static Torrent Parse(byte[] bytes)
    {
        using MemoryStream stream = new(bytes);
        return new Torrent(stream);
    }

    private static void Reject(byte[] bytes)
    {
        try
        {
            Parse(bytes);
        }
        catch (TorrentException)
        {
            return;
        }

        throw new InvalidOperationException("Malformed metainfo was accepted.");
    }

    private static void EqualHash(byte[] actual, string expected) => Require(actual.AsSpan().SequenceEqual(Hex(expected)));

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Torrent format assertion failed.");
        }
    }

    private sealed class TestStream(byte[] data, int maxChunk = int.MaxValue, bool fail = false,
        long syntheticLength = 0) : Stream
    {
        internal long BytesRead { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (fail)
            {
                throw new IOException("Test I/O failure.");
            }

            long available = (syntheticLength == 0 ? data.LongLength : syntheticLength) - BytesRead;
            int length = (int)Math.Min(available, Math.Min(count, maxChunk));
            if (syntheticLength == 0)
            {
                Array.Copy(data, (int)BytesRead, buffer, offset, length);
            }
            else
            {
                Array.Clear(buffer, offset, length);
            }

            BytesRead += length;
            return length;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif
