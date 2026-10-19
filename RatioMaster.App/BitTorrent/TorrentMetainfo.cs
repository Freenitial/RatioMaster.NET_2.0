namespace RatioMaster.BitTorrent;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RatioMaster.Localization;

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

    /// <summary>BEP 27: peers learnt outside the tracker (DHT, PEX, metadata exchange) are not used.</summary>
    internal bool IsPrivate { get; private init; }

    /// <summary>The exact bencoded info dictionary: the metadata a client serves to peers (BEP 9).</summary>
    internal ReadOnlyMemory<byte> InfoBytes { get; private init; }

    private sealed record FileEntry(string Path, ulong Length, bool Padding, byte[]? Root = null);

    internal static TorrentMetainfo Read(Stream stream)
    {
        ValueDictionary root = TorrentBencodeReader.Read(stream, out byte[] sha1, out byte[] sha256, out byte[] infoBytes);
        ValueDictionary info = Required<ValueDictionary>(root, "info");
        ValueString rawName = Required<ValueString>(info, "name");
        string name = PathComponent(rawName.Bytes);
        ulong pieceLength = Nonnegative(info, "piece length");
        if (pieceLength == 0)
        {
            throw Error(Strings.TorrentPieceLengthPositive);
        }

        bool isV2 = info.Contains("meta version");
        if (isV2 && Nonnegative(info, "meta version") != 2)
        {
            throw Error(Strings.TorrentUnsupportedMetainfoVersion);
        }

        if (!isV2 && (info.Contains("file tree") || root.Contains("piece layers")))
        {
            throw Error(Strings.TorrentV2StructuresRequireMetaVersion);
        }

        if (isV2 && (pieceLength < 16384 || (pieceLength & (pieceLength - 1)) != 0))
        {
            throw Error(Strings.TorrentV2PieceLengthPowerTwo);
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
                throw Error(Strings.TorrentV1PiecesLength);
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
                throw Error(Strings.TorrentFileTreeContainLeastFile);
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
                        throw Error(Strings.TorrentFilesSharingPiecesRootRequire);
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
                    throw Error(Strings.TorrentHybridPieceCountMismatch);
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
            IsPrivate = info.Contains("private") && info["private"] is ValueNumber flag && flag.Integer == 1,
            InfoBytes = infoBytes,
        };
    }

    private static List<FileEntry> ReadV1Files(ValueDictionary info, string name, out ulong total)
    {
        if (info.Contains("length") == info.Contains("files"))
        {
            throw Error(Strings.TorrentV1InfoContainExactlyLength);
        }

        total = 0;
        List<FileEntry> result = [];
        if (info.Contains("length"))
        {
            bool padding = ReadAttributes(info);
            if (padding)
            {
                throw Error(Strings.TorrentSingleFileIsPadding);
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
                throw Error(Strings.TorrentV1FilesEntryDictionary);
            }

            ulong length = Nonnegative(file, "length");
            bool padding = ReadAttributes(file);
            string path = ReadPath(Required<ValueList>(file, "path"));
            AccountPath(path, ref pathBytes);
            if (padding)
            {
                if (length == 0)
                {
                    throw Error(Strings.TorrentPaddingFilesHavePositiveLength);
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
            throw Error(Strings.TorrentV1FilesListContainPayload);
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
                throw Error(Strings.TorrentDuplicateConflictingV1FilePaths);
            }
        }

        return result;
    }

    private static void ReadV2Tree(ValueDictionary tree, string path, int depth,
        List<FileEntry> files, ref int pathBytes)
    {
        if (depth > TorrentBencodeReader.MaxDepth || tree.Keys.Count == 0)
        {
            throw Error(Strings.TorrentInvalidExcessivelyNestedFileTree);
        }

        if (tree.Contains(string.Empty))
        {
            if (path.Length == 0 || tree.Keys.Count != 1)
            {
                throw Error(Strings.TorrentFileTreeLeafNeedsFilename);
            }

            ValueDictionary properties = Required<ValueDictionary>(tree, string.Empty);
            if (ReadAttributes(properties))
            {
                throw Error(Strings.TorrentV2PaddingFile);
            }

            ulong length = Nonnegative(properties, "length");
            byte[]? piecesRoot = null;
            if (length == 0)
            {
                if (properties.Contains("pieces root"))
                {
                    throw Error(Strings.TorrentEmptyV2FilesOmitPieces);
                }
            }
            else
            {
                piecesRoot = Required<ValueString>(properties, "pieces root").Bytes;
                if (piecesRoot.Length != 32)
                {
                    throw Error(Strings.TorrentV2PiecesRootLength);
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
                throw Error(Strings.TorrentFilePathTooLong);
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
                    throw Error(Strings.TorrentHybridPaddingFillExactlyRemainder);
                }
            }
            else
            {
                if (index == v2.Count || file.Path != v2[index].Path || file.Length != v2[index].Length)
                {
                    throw Error(Strings.TorrentHybridFilePathsLengthsOrder);
                }

                if (file.Length != 0 && offset % pieceLength != 0)
                {
                    throw Error(Strings.TorrentNonemptyHybridFileBeginPiece);
                }

                index++;
            }

            offset = AddLength(offset, file.Length);
        }

        if (index != v2.Count)
        {
            throw Error(Strings.TorrentHybridV1DescriptionMissingV2);
        }
    }

    private static void ValidateLayers(ValueDictionary root, Dictionary<string, int> expected, ulong pieceLength)
    {
        if (!root.Contains("piece layers"))
        {
            if (expected.Count != 0)
            {
                throw Error(Strings.TorrentRequiredV2PieceLayersMissing);
            }

            return;
        }

        ValueDictionary layers = Required<ValueDictionary>(root, "piece layers");
        if (layers.Keys.Count != expected.Count)
        {
            throw Error(Strings.TorrentPieceLayersMatchExactlyFiles);
        }

        foreach (string key in layers.Keys)
        {
            if (key.Length != 32 || !expected.TryGetValue(key, out int pieces))
            {
                throw Error(Strings.TorrentPieceLayerHasUnreferencedInvalid);
            }

            byte[] hashes = Required<ValueString>(layers, key).Bytes;
            if (hashes.Length % 32 != 0 || hashes.Length / 32 != pieces)
            {
                throw Error(Strings.TorrentPieceLayerLength);
            }

            if (!TorrentMerkle.ComputeRoot(hashes, pieceLength).AsSpan()
                .SequenceEqual(Encoding.Latin1.GetBytes(key)))
            {
                throw Error(Strings.TorrentPieceLayerRootMismatch);
            }
        }
    }

    private static bool ReadAttributes(ValueDictionary file)
    {
        string attributes = file.Contains("attr") ? Required<ValueString>(file, "attr").String : string.Empty;
        if (attributes.Contains('l') || file.Contains("symlink path"))
        {
            throw Error(Strings.TorrentSymlinksUnsupported);
        }

        foreach (char attribute in attributes)
        {
            if (attribute < 'a' || attribute > 'z')
            {
                throw Error(Strings.TorrentFileAttributesNotLetters);
            }
        }

        return attributes.Contains('p');
    }

    private static string ReadPath(ValueList components)
    {
        if (components.values.Count == 0)
        {
            throw Error(Strings.TorrentFilePathsContainLeastComponent);
        }

        StringBuilder path = new();
        foreach (IBEncodeValue component in components)
        {
            if (component is not ValueString text)
            {
                throw Error(Strings.TorrentFilePathComponentsStrings);
            }

            PathComponent(text.Bytes);
            if (path.Length != 0)
            {
                path.Append('/');
            }

            if (text.Length > MaxPathBytes - path.Length)
            {
                throw Error(Strings.TorrentFilePathTooLong);
            }

            path.Append(text.String);
        }

        return path.ToString();
    }

    private static string PathComponent(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxPathBytes)
        {
            throw Error(Strings.TorrentFileNameLength);
        }

        string name = DecodeUtf8(bytes);
        if (name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw Error(Strings.TorrentFileNamesIndividualPathComponents);
        }

        return name;
    }

    private static void AccountPath(string path, ref int used)
    {
        if (path.Length > MaxPathBytes || path.Length > MaxTotalPathBytes - used)
        {
            throw Error(Strings.TorrentPathsTooLarge);
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
                    throw Error(Strings.TorrentAnnounceListTiersNonemptyLists);
                }

                foreach (IBEncodeValue entry in tier)
                {
                    if (entry is not ValueString url)
                    {
                        throw Error(Strings.TorrentAnnounceListUrlsStrings);
                    }

                    string decoded = DecodeUtf8(url.Bytes);
                    if (decoded.Length == 0)
                    {
                        throw Error(Strings.TorrentAnnounceListUrlsEmpty);
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
            throw Error(Strings.TorrentTextNotUtf8);
        }
    }

    private static T Required<T>(ValueDictionary dictionary, string key) where T : class, IBEncodeValue
    {
        if (!dictionary.Contains(key) || dictionary[key] is not T value)
        {
            throw Error(Strings.TorrentMissingField(key));
        }

        return value;
    }

    private static ulong Nonnegative(ValueDictionary dictionary, string key)
    {
        string text = Required<ValueNumber>(dictionary, key).String;
        long value = long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (value < 0)
        {
            throw Error(Strings.TorrentNegativeField(key));
        }

        return (ulong)value;
    }

    private static ulong AddLength(ulong total, ulong length)
    {
        if (length > (ulong)long.MaxValue - total)
        {
            throw Error(Strings.TorrentLengthTooLarge);
        }

        return total + length;
    }

    private static ulong PiecesFor(ulong length, ulong pieceLength) =>
        length / pieceLength + (length % pieceLength == 0 ? 0UL : 1UL);

    private static int PieceIndexCount(ulong count)
    {
        if (count > int.MaxValue - 7)
        {
            throw Error(Strings.TorrentPieceCountExceedsSupportedWire);
        }

        return (int)count;
    }

    private static TorrentException Error(string message) => new IncompleteTorrentData(message);
}
