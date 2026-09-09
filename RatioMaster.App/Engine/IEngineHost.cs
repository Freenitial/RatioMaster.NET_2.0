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

    string StopWhen { get; }

    string StopValue { get; }

    StopConditionSettings StopCondition => new(StopWhen, StopValue);

    // engine -> UI (must marshal to the UI thread).

    /// <summary>Push the CURRENT engine condition to the tab's alert line (replaces the previous one).</summary>
    void ApplyAlert(EngineAlert level, string message);
}

internal sealed record StopConditionSettings(string Condition, string Value);
