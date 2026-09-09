namespace RatioMaster.BitTorrent;

using System.IO;

/// <summary>A validated BEP 3 / BEP 52 metainfo snapshot.</summary>
internal sealed class Torrent
{
    private TorrentMetainfo? metadata;

    internal Torrent()
    {
    }

    internal Torrent(string localFilename)
    {
        using FileStream stream = File.OpenRead(localFilename);
        metadata = TorrentMetainfo.Read(stream);
    }

    /// <summary>Reads from the current position, leaving the caller's stream open.</summary>
    internal Torrent(Stream stream) => metadata = TorrentMetainfo.Read(stream);

    private TorrentMetainfo Metadata => metadata
        ?? throw new IncompleteTorrentData("No torrent has been loaded.");

    /// <summary>Swarm byte length; hybrids include their v1 padding files.</summary>
    internal ulong TotalLength => Metadata.TotalLength;

    /// <summary>Piece indexes in the selected swarm; v2 files each start on a piece boundary.</summary>
    internal int PieceCount => Metadata.PieceCount;

    internal ValueDictionary Info => Metadata.Info;

    /// <summary>20-byte wire hash: SHA-1 for v1/hybrid, first 20 SHA-256 bytes for pure v2.</summary>
    internal byte[] InfoHash => (byte[])Metadata.InfoHash.Clone();

    /// <summary>SHA-256 (32 bytes) for v2/hybrid, SHA-1 (20 bytes) for v1, over original info bytes.</summary>
    internal byte[] FullInfoHash => (byte[])Metadata.FullInfoHash.Clone();

    /// <summary>True when a validated v2 description is present, including hybrids.</summary>
    internal bool IsV2 => Metadata.IsV2;

    internal bool IsHybrid => Metadata.IsHybrid;

    internal string Name => Metadata.Name;

    /// <summary>Announce URL, first announce-list URL, or empty for trackerless metainfo.</summary>
    internal string Announce => Metadata.Announce;

    internal bool OpenTorrent(string localFilename)
    {
        try
        {
            using FileStream stream = File.OpenRead(localFilename);
            return OpenTorrent(stream);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Commits only a fully validated snapshot. I/O failures return false; format errors throw.</summary>
    internal bool OpenTorrent(Stream stream)
    {
        try
        {
            metadata = TorrentMetainfo.Read(stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
