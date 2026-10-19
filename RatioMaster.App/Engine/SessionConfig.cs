namespace RatioMaster.Engine;

using System.Collections.Generic;
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

    /// <summary>Completion used when <see cref="InitialHave"/> is not supplied.</summary>
    internal required double FinishedPercent { get; init; }

    /// <summary>Exact bytes already present when the session starts (resumed progress); null derives it from the percentage.</summary>
    internal long? InitialHave { get; init; }

    internal required int Interval { get; init; }

    internal required string Port { get; init; }

    internal required string Key { get; init; }

    internal bool KeyIsGenerated { get; init; }

    internal required string PeerID { get; init; }

    internal required string NumWant { get; init; }

    /// <summary>Number of pieces (for the realistic-mode wire bitfield). 0 = unknown.</summary>
    internal int PieceCount { get; init; }

    /// <summary>Answer connecting peers like the emulated client: extension handshake, availability and keep-alives.</summary>
    internal bool Realistic { get; init; }

    /// <summary>Private torrents (BEP 27) disable metadata exchange and PEX, and change which addresses libtorrent announces.</summary>
    internal bool IsPrivate { get; init; }

    internal bool IsHybrid { get; init; }

    /// <summary>Hashes a connecting peer may use; defaults to <see cref="InfoHash"/>.</summary>
    internal IReadOnlyList<byte[]>? PeerInfoHashes { get; init; }

    /// <summary>The info dictionary, advertised (metadata_size) and served to peers of public torrents.</summary>
    internal ReadOnlyMemory<byte> Metadata { get; init; }

    /// <summary>Cumulative totals carried over from earlier sessions of this torrent (display and stop conditions only).</summary>
    internal long ResumeUploaded { get; init; }

    internal long ResumeDownloaded { get; init; }
}

internal sealed class EngineStats
{
    internal bool IsActive { get; init; }
    internal double SampleElapsedSeconds { get; init; }
    internal EngineAlert AlertLevel { get; init; }
    internal string AlertMessage { get; init; } = string.Empty;

    /// <summary>Cumulative bytes uploaded across sessions of this torrent.</summary>
    internal long Uploaded { get; init; }

    /// <summary>Cumulative bytes this program downloaded across sessions (data that was already present is not counted).</summary>
    internal long Downloaded { get; init; }

    /// <summary>Bytes of the torrent present now.</summary>
    internal long Have { get; init; }

    internal long Left { get; init; }

    internal long TotalSize { get; init; }

    internal double FinishedPercent { get; init; }

    internal double Ratio { get; init; }

    internal int Seeders { get; init; } = -1;

    internal int Leechers { get; init; } = -1;

    internal int IntervalRemaining { get; init; }

    /// <summary>Seconds until the tracker's minimum interval allows a manual update.</summary>
    internal int ManualUpdateWait { get; init; }

    internal int TotalRunSeconds { get; init; }

    internal string NumPeers { get; init; } = "0";

    internal bool SeedMode { get; init; }
    internal bool IsPaused { get; init; }
    internal bool UploadPausedNoLeechers { get; init; }
    internal bool IsAnnouncing { get; init; }
    internal bool CompletionPending { get; init; }
}

/// <summary>Why a session ended: the text is shown to the user, the flags drive alerts and notifications.</summary>
internal sealed record StopNotice(string Reason, bool IsError, bool IsManual);
