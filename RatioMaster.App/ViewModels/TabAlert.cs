namespace RatioMaster.ViewModels;

/// <summary>Severity of the message shown in a tab's alert line, under the terminal.</summary>
public enum TabAlertLevel
{
    /// <summary>No message — the (reserved) line renders empty.</summary>
    None,

    /// <summary>Green: healthy.</summary>
    Ok,

    /// <summary>Yellow: running but degraded (e.g. upload paused, empty swarm).</summary>
    Warning,

    /// <summary>Red: broken (tracker refused, no connection, unusable torrent).</summary>
    Error,
}

/// <summary>
/// Colour state of the little round dot left of a tab's title. Problems win over run state so a failing
/// tab is visible in the strip even when another tab is selected.
/// </summary>
public enum TabDotState
{
    /// <summary>Grey: idle.</summary>
    Idle,

    /// <summary>Green: running normally.</summary>
    Running,

    /// <summary>Yellow: running with a warning.</summary>
    Warning,

    /// <summary>Red: in error.</summary>
    Error,
}
