namespace RatioMaster.Engine;

using RatioMaster.Models;

/// <summary>Immutable snapshot of everything the engine needs to start a session.</summary>
internal sealed class SessionConfig
{
    internal required ClientProfile Client { get; init; }

    internal required ProxyConfig Proxy { get; init; }

    internal required string Tracker { get; init; }

    internal required string HashHex { get; init; }

    internal required byte[] InfoHash { get; init; }

    internal required long TotalLength { get; init; }

    internal required double FinishedPercent { get; init; }

    internal required int Interval { get; init; }

    internal required string Port { get; init; }

    internal required string Key { get; init; }

    internal required string PeerID { get; init; }

    internal required string NumWant { get; init; }

    /// <summary>Number of pieces (for the realistic-mode wire bitfield). 0 = unknown.</summary>
    internal int PieceCount { get; init; }

    /// <summary>Realistic mode — WIRE PROTOCOL ONLY: answer connecting peers with a real BitTorrent
    /// handshake + full bitfield so we look like a genuine seeder. The speed-curve half of the old
    /// "realistic mode" is now the per-direction "Random" checkboxes, read live off the host.</summary>
    internal bool Realistic { get; init; }

    /// <summary>Resume: cumulative uploaded/downloaded to start from (0 = fresh).</summary>
    internal long ResumeUploaded { get; init; }

    internal long ResumeDownloaded { get; init; }
}

internal sealed class EngineStats
{
    internal long Uploaded { get; init; }

    internal long Downloaded { get; init; }

    internal long Left { get; init; }

    internal long TotalSize { get; init; }

    internal double FinishedPercent { get; init; }

    internal string Ratio { get; init; } = "0.0";

    internal int Seeders { get; init; } = -1;

    internal int Leechers { get; init; } = -1;

    internal int IntervalRemaining { get; init; }

    internal int TotalRunSeconds { get; init; }

    internal string NumPeers { get; init; } = "0";

    internal bool SeedMode { get; init; }
}
