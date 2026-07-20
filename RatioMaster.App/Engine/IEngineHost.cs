namespace RatioMaster.Engine;

/// <summary>
/// Live values the engine reads from / pushes back to the UI while a session runs.
/// The ViewModel implements this over its bindable properties (single source of
/// truth for the editable speed fields), marshalling the Apply* callbacks to the UI thread.
/// </summary>
internal interface IEngineHost
{
    bool UseTcpListener { get; }

    bool RequestScrape { get; }

    bool IgnoreFailureReason { get; }

    long UploadRateBytes { get; }

    long DownloadRateBytes { get; }

    /// <summary>"Random" for the upload direction: vary the rate along a smooth, slowly drifting curve
    /// instead of a flat line. Read LIVE on every tick (like the speed fields) so it can be toggled
    /// mid-session. Independent of the download flag below.</summary>
    bool RandomUploadEnabled { get; }

    /// <summary>"Random" for the download direction. Independent of the upload flag.</summary>
    bool RandomDownloadEnabled { get; }

    // "On next update — get random speeds": at EVERY announce the rate jumps to a new level, drawn as a
    // PERCENTAGE of the configured MB/s (100 = the value as typed). Coarse, per-interval level changes, as
    // opposed to the per-second wobble of "Random" above; the two multiply when both are on. The percentage
    // is applied INTERNALLY — the engine must never write back into the user's speed field (doing that is
    // what used to corrupt the saved session).
    bool NextRandUpEnabled { get; }

    double NextRandUpMinPercent { get; }

    double NextRandUpMaxPercent { get; }

    bool NextRandDownEnabled { get; }

    double NextRandDownMinPercent { get; }

    double NextRandDownMaxPercent { get; }

    string StopWhen { get; }

    string StopValue { get; }

    // engine -> UI (must marshal to the UI thread).

    /// <summary>Push the CURRENT engine condition to the tab's alert line (replaces the previous one).</summary>
    void ApplyAlert(EngineAlert level, string message);
}
