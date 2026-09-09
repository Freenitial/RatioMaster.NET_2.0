namespace RatioMaster.BitTorrent;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// BEP 3 and BEP 52 metainfo validation. Sizes fit signed engine counters and piece indexes fit Int32.
/// References: https://www.bittorrent.org/beps/bep_0003.html and https://www.bittorrent.org/beps/bep_0052.html.
/// </summary>
internal sealed class TorrentMetainfo
{
    private const int MaxPathBytes = 4096;
    private const int MaxTotalPathBytes = 16 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal ValueDictionary Info { get; private init; } = new();
    internal ulong TotalLength { get; private init; }
    internal int PieceCount { get; private init; }
    internal byte[] InfoHash { get; private init; } = [];
    internal byte[] FullInfoHash { get; private init; } = [];
    internal bool IsV2 { get; private init; }
    internal bool IsHybrid { get; private init; }
    internal string Name { get; private init; } = string.Empty;
    internal string Announce { get; private init; } = string.Empty;

    private sealed record FileEntry(string Path, ulong Length, bool Padding, byte[]? Root = null);

    internal static TorrentMetainfo Read(Stream stream)
    {
        ValueDictionary root = TorrentBencodeReader.Read(stream, out byte[] sha1, out byte[] sha256);
        ValueDictionary info = Required<ValueDictionary>(root, "info");
        ValueString rawName = Required<ValueString>(info, "name");
        string name = PathComponent(rawName.Bytes);
        ulong pieceLength = Nonnegative(info, "piece length");
        if (pieceLength == 0)
        {
            throw Error("Piece length must be positive.");
        }

        bool isV2 = info.Contains("meta version");
        if (isV2 && Nonnegative(info, "meta version") != 2)
        {
            throw Error("Unsupported metainfo version.");
        }

        if (!isV2 && (info.Contains("file tree") || root.Contains("piece layers")))
        {
            throw Error("V2 structures require meta version 2.");
        }

        if (isV2 && (pieceLength < 16384 || (pieceLength & (pieceLength - 1)) != 0))
        {
            throw Error("V2 piece length must be a power of two of at least 16 KiB.");
        }

        bool hasV1 = !isV2 || info.Contains("pieces") || info.Contains("length") || info.Contains("files");
        List<FileEntry> v1Files = [];
        List<FileEntry> v2Files = [];
        ulong v1Length = 0;
        ulong v2Length = 0;
        int v1Pieces = 0;
        int v2Pieces = 0;
        if (hasV1)
        {
            v1Files = ReadV1Files(info, rawName.String, out v1Length);
            ValueString pieces = Required<ValueString>(info, "pieces");
            ulong expected = PiecesFor(v1Length, pieceLength);
            if (pieces.Length % 20 != 0 || (ulong)(pieces.Length / 20) != expected)
            {
                throw Error("V1 pieces must contain exactly one 20-byte hash per piece of the file layout.");
            }

            v1Pieces = PieceIndexCount(expected);
        }

        if (isV2)
        {
            ValueDictionary tree = Required<ValueDictionary>(info, "file tree");
            int pathBytes = 0;
            ReadV2Tree(tree, string.Empty, 0, v2Files, ref pathBytes);
            if (v2Files.Count == 0)
            {
                throw Error("The file tree must contain at least one file.");
            }

            ulong count = 0;
            Dictionary<string, int> expectedLayers = new(StringComparer.Ordinal);
            foreach (FileEntry file in v2Files)
            {
                v2Length = AddLength(v2Length, file.Length);
                count += PiecesFor(file.Length, pieceLength);
                PieceIndexCount(count);
                if (file.Length > pieceLength)
                {
                    string key = Encoding.Latin1.GetString(file.Root!);
                    int pieces = PieceIndexCount(PiecesFor(file.Length, pieceLength));
                    if (expectedLayers.TryGetValue(key, out int prior) && prior != pieces)
                    {
                        throw Error("Files sharing a pieces root require incompatible piece layer sizes.");
                    }

                    expectedLayers[key] = pieces;
                }
            }

            v2Pieces = PieceIndexCount(count);
            ValidateLayers(root, expectedLayers, pieceLength);
            if (hasV1)
            {
                ValidateHybrid(v1Files, v2Files, pieceLength);
                if (v1Pieces != v2Pieces)
                {
                    throw Error("Hybrid piece index spaces do not agree.");
                }
            }
        }

        return new TorrentMetainfo
        {
            Info = info,
            TotalLength = hasV1 ? v1Length : v2Length,
            PieceCount = hasV1 ? v1Pieces : v2Pieces,
            InfoHash = hasV1 ? sha1 : sha256.AsSpan(0, 20).ToArray(),
            FullInfoHash = isV2 ? sha256 : sha1,
            IsV2 = isV2,
            IsHybrid = isV2 && hasV1,
            Name = name,
            Announce = ReadAnnounce(root),
        };
    }

    private static List<FileEntry> ReadV1Files(ValueDictionary info, string name, out ulong total)
    {
        if (info.Contains("length") == info.Contains("files"))
        {
            throw Error("V1 info must contain exactly one of length and files.");
        }

        total = 0;
        List<FileEntry> result = [];
        if (info.Contains("length"))
        {
            bool padding = ReadAttributes(info);
            if (padding)
            {
                throw Error("A single-file torrent cannot consist of a padding file.");
            }

            total = Nonnegative(info, "length");
            result.Add(new FileEntry(name, total, false));
            return result;
        }

        ValueList files = Required<ValueList>(info, "files");
        int pathBytes = 0;
        int payloadFiles = 0;
        foreach (IBEncodeValue value in files)
        {
            if (value is not ValueDictionary file)
            {
                throw Error("Each v1 files entry must be a dictionary.");
            }

            ulong length = Nonnegative(file, "length");
            bool padding = ReadAttributes(file);
            string path = ReadPath(Required<ValueList>(file, "path"));
            AccountPath(path, ref pathBytes);
            if (padding)
            {
                if (length == 0)
                {
                    throw Error("Padding files must have a positive length.");
                }

            }
            else
            {
                payloadFiles++;
            }

            total = AddLength(total, length);
            result.Add(new FileEntry(path, length, padding));
        }

        if (payloadFiles == 0)
        {
            throw Error("The v1 files list must contain a payload file.");
        }

        // A terminal separator sorts every ancestor immediately before its descendants.
        List<(string Key, FileEntry File)> sorted = new(result.Count);
        foreach (FileEntry file in result)
        {
            sorted.Add((file.Path + "/", file));
        }

        sorted.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        for (int index = 1; index < sorted.Count; index++)
        {
            FileEntry previous = sorted[index - 1].File;
            FileEntry current = sorted[index].File;
            if ((current.Path == previous.Path && (!current.Padding || !previous.Padding))
                || (sorted[index].Key.Length > sorted[index - 1].Key.Length
                    && sorted[index].Key.StartsWith(sorted[index - 1].Key, StringComparison.Ordinal)))
            {
                throw Error("Duplicate or conflicting v1 file paths.");
            }
        }

        return result;
    }

    private static void ReadV2Tree(ValueDictionary tree, string path, int depth,
        List<FileEntry> files, ref int pathBytes)
    {
        if (depth > TorrentBencodeReader.MaxDepth || tree.Keys.Count == 0)
        {
            throw Error("Invalid or excessively nested file tree.");
        }

        if (tree.Contains(string.Empty))
        {
            if (path.Length == 0 || tree.Keys.Count != 1)
            {
                throw Error("A file tree leaf needs a filename and cannot also contain children.");
            }

            ValueDictionary properties = Required<ValueDictionary>(tree, string.Empty);
            if (ReadAttributes(properties))
            {
                throw Error("V2 file trees cannot contain padding files.");
            }

            ulong length = Nonnegative(properties, "length");
            byte[]? piecesRoot = null;
            if (length == 0)
            {
                if (properties.Contains("pieces root"))
                {
                    throw Error("Empty v2 files must omit pieces root.");
                }
            }
            else
            {
                piecesRoot = Required<ValueString>(properties, "pieces root").Bytes;
                if (piecesRoot.Length != 32)
                {
                    throw Error("V2 pieces root must contain 32 bytes.");
                }
            }

            AccountPath(path, ref pathBytes);
            files.Add(new FileEntry(path, length, false, piecesRoot));
            return;
        }

        List<string> names = [];
        foreach (string key in tree.Keys)
        {
            names.Add(key);
        }

        names.Sort(StringComparer.Ordinal);
        foreach (string key in names)
        {
            PathComponent(Encoding.Latin1.GetBytes(key));
            string childPath = path.Length == 0 ? key : path + "/" + key;
            if (childPath.Length > MaxPathBytes)
            {
                throw Error("File path exceeds the 4096-byte limit.");
            }

            ReadV2Tree(Required<ValueDictionary>(tree, key), childPath, depth + 1, files, ref pathBytes);
        }
    }

    private static void ValidateHybrid(List<FileEntry> v1, List<FileEntry> v2, ulong pieceLength)
    {
        ulong offset = 0;
        int index = 0;
        foreach (FileEntry file in v1)
        {
            if (file.Padding)
            {
                ulong remainder = offset % pieceLength;
                if (remainder == 0 || file.Length != pieceLength - remainder)
                {
                    throw Error("Hybrid padding must fill exactly the remainder of a piece.");
                }
            }
            else
            {
                if (index == v2.Count || file.Path != v2[index].Path || file.Length != v2[index].Length)
                {
                    throw Error("Hybrid file paths, lengths and order must agree between v1 and v2.");
                }

                if (file.Length != 0 && offset % pieceLength != 0)
                {
                    throw Error("Each nonempty hybrid file must begin at a piece boundary.");
                }

                index++;
            }

            offset = AddLength(offset, file.Length);
        }

        if (index != v2.Count)
        {
            throw Error("Hybrid v1 description is missing v2 files.");
        }
    }

    private static void ValidateLayers(ValueDictionary root, Dictionary<string, int> expected, ulong pieceLength)
    {
        if (!root.Contains("piece layers"))
        {
            if (expected.Count != 0)
            {
                throw Error("Required v2 piece layers are missing.");
            }

            return;
        }

        ValueDictionary layers = Required<ValueDictionary>(root, "piece layers");
        if (layers.Keys.Count != expected.Count)
        {
            throw Error("Piece layers must match exactly the files larger than one piece.");
        }

        foreach (string key in layers.Keys)
        {
            if (key.Length != 32 || !expected.TryGetValue(key, out int pieces))
            {
                throw Error("Piece layer has an unreferenced or invalid root key.");
            }

            byte[] hashes = Required<ValueString>(layers, key).Bytes;
            if (hashes.Length % 32 != 0 || hashes.Length / 32 != pieces)
            {
                throw Error("Piece layer must contain exactly one 32-byte hash per file piece.");
            }

            if (!TorrentMerkle.ComputeRoot(hashes, pieceLength).AsSpan()
                .SequenceEqual(Encoding.Latin1.GetBytes(key)))
            {
                throw Error("Piece layer Merkle root does not match the file's pieces root.");
            }
        }
    }

    private static bool ReadAttributes(ValueDictionary file)
    {
        string attributes = file.Contains("attr") ? Required<ValueString>(file, "attr").String : string.Empty;
        if (attributes.Contains('l') || file.Contains("symlink path"))
        {
            throw Error("Symbolic-link file entries are not supported.");
        }

        foreach (char attribute in attributes)
        {
            if (attribute < 'a' || attribute > 'z')
            {
                throw Error("File attributes must be ASCII letters.");
            }
        }

        return attributes.Contains('p');
    }

    private static string ReadPath(ValueList components)
    {
        if (components.values.Count == 0)
        {
            throw Error("File paths must contain at least one component.");
        }

        StringBuilder path = new();
        foreach (IBEncodeValue component in components)
        {
            if (component is not ValueString text)
            {
                throw Error("File path components must be strings.");
            }

            PathComponent(text.Bytes);
            if (path.Length != 0)
            {
                path.Append('/');
            }

            if (text.Length > MaxPathBytes - path.Length)
            {
                throw Error("File path exceeds the 4096-byte limit.");
            }

            path.Append(text.String);
        }

        return path.ToString();
    }

    private static string PathComponent(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxPathBytes)
        {
            throw Error("File names must contain between 1 and 4096 UTF-8 bytes.");
        }

        string name = DecodeUtf8(bytes);
        if (name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw Error("File names must be individual path components without traversal or NUL bytes.");
        }

        return name;
    }

    private static void AccountPath(string path, ref int used)
    {
        if (path.Length > MaxPathBytes || path.Length > MaxTotalPathBytes - used)
        {
            throw Error("Torrent paths exceed the path memory limit.");
        }

        used += path.Length;
    }

    private static string ReadAnnounce(ValueDictionary root)
    {
        string announce = root.Contains("announce")
            ? DecodeUtf8(Required<ValueString>(root, "announce").Bytes) : string.Empty;
        if (root.Contains("announce-list"))
        {
            foreach (IBEncodeValue value in Required<ValueList>(root, "announce-list"))
            {
                if (value is not ValueList tier || tier.values.Count == 0)
                {
                    throw Error("Announce-list tiers must be nonempty lists.");
                }

                foreach (IBEncodeValue entry in tier)
                {
                    if (entry is not ValueString url)
                    {
                        throw Error("Announce-list URLs must be strings.");
                    }

                    string decoded = DecodeUtf8(url.Bytes);
                    if (decoded.Length == 0)
                    {
                        throw Error("Announce-list URLs must not be empty.");
                    }

                    if (announce.Length == 0)
                    {
                        announce = decoded;
                    }
                }
            }
        }

        return announce;
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Error("Torrent text must be valid UTF-8.");
        }
    }

    private static T Required<T>(ValueDictionary dictionary, string key) where T : class, IBEncodeValue
    {
        if (!dictionary.Contains(key) || dictionary[key] is not T value)
        {
            throw Error("Missing or incorrectly typed metainfo field: " + key);
        }

        return value;
    }

    private static ulong Nonnegative(ValueDictionary dictionary, string key)
    {
        string text = Required<ValueNumber>(dictionary, key).String;
        long value = long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (value < 0)
        {
            throw Error("Metainfo sizes and versions must not be negative: " + key);
        }

        return (ulong)value;
    }

    private static ulong AddLength(ulong total, ulong length)
    {
        if (length > (ulong)long.MaxValue - total)
        {
            throw Error("Total torrent length exceeds the engine's signed 64-bit size limit.");
        }

        return total + length;
    }

    private static ulong PiecesFor(ulong length, ulong pieceLength) =>
        length / pieceLength + (length % pieceLength == 0 ? 0UL : 1UL);

    private static int PieceIndexCount(ulong count)
    {
        if (count > int.MaxValue - 7)
        {
            throw Error("Piece count exceeds the supported wire bitfield index range.");
        }

        return (int)count;
    }

    private static TorrentException Error(string message) => new IncompleteTorrentData(message);
}
