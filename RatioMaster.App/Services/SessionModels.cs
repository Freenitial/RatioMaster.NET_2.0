namespace RatioMaster.Services;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>Persistable state of the whole app (portable settings + resume).</summary>
internal sealed class SessionData
{
    public List<TabState> Tabs { get; set; } = [];
}

/// <summary>Everything one tab needs to be restored, including resume counters.</summary>
internal sealed class TabState
{
    public string TabName { get; set; } = "RM 1";

    public string TorrentFilePath { get; set; } = string.Empty;

    // Real re-openable path to reload the metadata from (== TorrentFilePath on desktop; a private cache copy
    // on Android, where TorrentFilePath holds only the display name). Empty when no torrent is loaded.
    public string TorrentSourcePath { get; set; } = string.Empty;

    public string UploadSpeed { get; set; } = "100";

    // "Random" = smooth speed curve, per direction. Replaces the old RandUp/RandDown + min/max noise and
    // the speed half of RealisticMode. Sessions written by an older build simply lack these keys and fall
    // back to the defaults here (System.Text.Json ignores the removed keys they do carry).
    public bool RandomUpload { get; set; } = true;

    public string DownloadSpeed { get; set; } = "0";

    public bool RandomDownload { get; set; } = true;

    public string Interval { get; set; } = "1800";

    public string Finished { get; set; } = "100";

    public string StopWhen { get; set; } = "When uploaded >";

    public string StopValue { get; set; } = "1000";

    public string Family { get; set; } = "qBittorrent";

    public string Version { get; set; } = "5.1.0";

    public string CustomKey { get; set; } = string.Empty;

    public string CustomPeerId { get; set; } = string.Empty;

    public string CustomPort { get; set; } = string.Empty;

    public string CustomPeers { get; set; } = string.Empty;

    public bool AlwaysNewValues { get; set; } = true;

    public bool RealisticMode { get; set; } = true;

    public bool UseTcpListener { get; set; } = true;

    public bool RequestScrape { get; set; } = true;

    public bool IgnoreFailureReason { get; set; }

    public string ProxyType { get; set; } = "None";

    public string ProxyHost { get; set; } = string.Empty;

    public string ProxyUser { get; set; } = string.Empty;

    public string ProxyPass { get; set; } = string.Empty;

    public string ProxyPort { get; set; } = string.Empty;

    // "On next update": per-announce speed level, as a PERCENT of the configured MB/s (100 = as typed).
    // Older sessions stored MB min/max under different names; those keys are simply ignored and these
    // percent defaults apply.
    public bool NextRandUp { get; set; }

    public decimal NextRandUpMinPercent { get; set; } = 50;

    public decimal NextRandUpMaxPercent { get; set; } = 150;

    public bool NextRandDown { get; set; }

    public decimal NextRandDownMinPercent { get; set; } = 50;

    public decimal NextRandDownMaxPercent { get; set; } = 150;

    public bool EnableLog { get; set; } = true;

    // Resume counters (cumulative bytes at last save).
    public long Uploaded { get; set; }

    public long Downloaded { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionData))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
