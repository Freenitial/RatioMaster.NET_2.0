namespace RatioMaster.BitTorrent;

using System;
using System.IO;
using System.Security.Cryptography;

/// <summary>A parsed .torrent metainfo file (tracker URL, info-hash, size, name).</summary>
internal sealed class Torrent
{
    private ValueDictionary data = new();
    private ulong totalLengthValue;

    internal Torrent()
    {
    }

    internal Torrent(string localFilename) => OpenTorrent(localFilename);

    /// <summary>Parse from an already-open stream — used on Android, where the file picker returns a
    /// <c>content://</c> URI (no filesystem path) and the bytes are read via the storage stream instead.</summary>
    internal Torrent(Stream stream) => OpenTorrent(stream);

    internal ulong TotalLength => totalLengthValue;

    /// <summary>Number of pieces (SHA-1 hashes / 20) — for the realistic-mode wire bitfield.</summary>
    internal int PieceCount { get; private set; }

    internal ValueDictionary Info => (ValueDictionary)data["info"];

    private bool SingleFile => ((ValueDictionary)data["info"]).Contains("length");

    /// <summary>SHA-1 of the bencoded "info" dictionary — the torrent's info-hash (20 bytes).</summary>
    internal byte[] InfoHash => SHA1.HashData(data["info"].Encode());

    internal string Name => BEncode.String(((ValueDictionary)data["info"])["name"]) ?? string.Empty;

    internal string Announce => BEncode.String(data["announce"]) ?? string.Empty;

    internal bool OpenTorrent(string localFilename)
    {
        try
        {
            using FileStream fs = File.OpenRead(localFilename);
            data = (ValueDictionary)BEncode.Parse(fs);
            LoadTorrent();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Parse the metainfo from an open stream (read in full during this call). Malformed data throws
    /// <see cref="TorrentException"/> just like the file overload; an I/O failure returns false.</summary>
    internal bool OpenTorrent(Stream stream)
    {
        try
        {
            data = (ValueDictionary)BEncode.Parse(stream);
            LoadTorrent();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void LoadTorrent()
    {
        if (!data.Contains("announce"))
        {
            throw new IncompleteTorrentData("No tracker URL");
        }

        if (!data.Contains("info"))
        {
            throw new IncompleteTorrentData("No internal torrent information");
        }

        ValueDictionary info = (ValueDictionary)data["info"];

        if (!info.Contains("pieces"))
        {
            throw new IncompleteTorrentData("No piece hash data");
        }

        ValueString pieces = (ValueString)info["pieces"];
        if ((pieces.Length % 20) != 0)
        {
            throw new IncompleteTorrentData("Missing or damaged piece hash codes");
        }

        PieceCount = pieces.Bytes.Length / 20;

        if (SingleFile)
        {
            totalLengthValue = (ulong)((ValueNumber)info["length"]).Integer;
        }
        else
        {
            totalLengthValue = 0;
            ValueList files = (ValueList)info["files"];
            foreach (object entry in files)
            {
                ValueDictionary file = (ValueDictionary)entry;
                totalLengthValue += (ulong)((ValueNumber)file["length"]).Integer;
            }
        }
    }
}
