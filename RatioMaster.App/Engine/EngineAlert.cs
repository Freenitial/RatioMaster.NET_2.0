namespace RatioMaster.Engine;

/// <summary>
/// Severity of a live engine condition surfaced to the user in the tab's alert line (and, via the tab's
/// dot colour, in the tab strip). Distinct from the free-form log: the alert line always shows the CURRENT
/// state, so each new alert replaces the previous one.
/// </summary>
internal enum EngineAlert
{
    /// <summary>Nothing to report — clears the line.</summary>
    None,

    /// <summary>Healthy state (green): the tracker accepted us and the swarm looks normal.</summary>
    Ok,

    /// <summary>Degraded but running (yellow): e.g. nobody is leeching, so the upload is paused.</summary>
    Warning,

    /// <summary>Broken (red): tracker refused us, no connection, unusable torrent.</summary>
    Error,
}
