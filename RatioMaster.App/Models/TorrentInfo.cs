namespace RatioMaster.Models;

using System;

/// <summary>Live announce state for one running session (mirrors the tracker's view).</summary>
internal sealed class TorrentInfo
{
    internal long Uploaded { get; set; }

    internal long Downloaded { get; set; }

    internal string Tracker { get; set; } = string.Empty;

    internal Uri? TrackerUri { get; set; }

    internal string Hash { get; set; } = string.Empty;

    internal long Left { get; set; }

    internal long TotalSize { get; set; }

    internal string Filename { get; set; } = string.Empty;

    internal long UploadRate { get; set; }

    internal long DownloadRate { get; set; }

    internal int Interval { get; set; } = 1800;

    internal string Key { get; set; } = string.Empty;

    internal string Port { get; set; } = "0";

    internal string NumberOfPeers { get; set; } = "200";

    internal string PeerID { get; set; } = string.Empty;
}
