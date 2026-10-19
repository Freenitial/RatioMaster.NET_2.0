namespace RatioMaster.Engine;

/// <summary>
/// Live settings and an optional alert observer. The ViewModel consumes engine snapshots
/// on the UI thread and publishes an immutable stop-condition pair.
/// </summary>
internal interface IEngineHost
{
    bool UseTcpListener { get; }

    bool RequestScrape { get; }

    long UploadRateBytes { get; }

    long DownloadRateBytes { get; }

    /// <summary>"Random" for the upload direction: vary the rate along a smooth, slowly drifting curve
    /// instead of a flat line. Read LIVE on every tick (like the speed fields) so it can be toggled
    /// mid-session. Independent of the download flag below.</summary>
    bool RandomUploadEnabled { get; }

    /// <summary>"Random" for the download direction. Independent of the upload flag.</summary>
    bool RandomDownloadEnabled { get; }

    /// <summary>The condition and its threshold, published together so the engine never reads a torn pair.</summary>
    StopConditionSettings StopCondition { get; }

    // engine -> UI (must marshal to the UI thread).

    /// <summary>Push the CURRENT engine condition to the tab's alert line (replaces the previous one).</summary>
    void ApplyAlert(EngineAlert level, string message);
}

/// <summary>Automatic stop rules. Display text is localized separately; sessions persist the stable identifiers.</summary>
public enum StopCondition
{
    Never,
    RatioAbove,
    UploadAbove,
    DownloadAbove,
    AfterTime,
    SeedersBelow,
    LeechersBelow,
    SwarmRatioBelow,
}

internal sealed record StopConditionSettings(StopCondition Condition, string Value);
