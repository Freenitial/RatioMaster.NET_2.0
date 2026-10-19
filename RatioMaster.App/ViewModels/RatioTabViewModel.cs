namespace RatioMaster.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RatioMaster.BitTorrent;
using RatioMaster.Engine;
using RatioMaster.Localization;
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>A stop rule as listed in the Stop box: stable value, localized label.</summary>
public sealed record StopOption(StopCondition Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A proxy protocol as listed in the Type box.</summary>
public sealed record ProxyOption(ProxyKind Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One RatioMaster tab: holds every bindable field, implements <see cref="IEngineHost"/>
/// (single source of truth for the live speed fields), and drives a <see cref="RatioEngine"/>.
/// </summary>
public partial class RatioTabViewModel : ObservableObject, IEngineHost
{
    private const int GraphPoints = 90;

    private RatioEngine engine;
    private Task pendingStop = Task.CompletedTask;
    private bool activityCounted;
    private Torrent? loadedTorrent;
    // Real, re-openable path to the loaded .torrent — equals TorrentFilePath on desktop, but on Android
    // (where the picker yields a content:// URI shown only by name) it points at a private materialized copy.
    // Persisted so a restored tab can reload its metadata on either platform.
    private string? torrentSourcePath;

    // Every cache copy currently referenced by SOME tab, so pruning can never evict one that is still in
    // use. Static because pruning is global over the shared cache directory.
    private static readonly Dictionary<string, int> InUseCachePaths = new(StringComparer.OrdinalIgnoreCase);
    private byte[] infoHash = [];
    private IReadOnlyList<byte[]> peerInfoHashes = [];
    private string counterInfoHash = string.Empty;
    private long totalLength;
    private int pieceCount;
    private bool isPrivateTorrent;
    private bool isHybridTorrent;
    private ReadOnlyMemory<byte> metadata;
    private bool suppressVersionReload;
    private bool assigningIdentity;
    private bool keyIsGenerated;
    private bool peerIdIsGenerated;
    private readonly TerminalBuffer terminalBuffer = new();
    // Set while SetTorrentContent (Android) assigns the display NAME to TorrentFilePath — that assignment
    // isn't a "clear", so the change hook must not wipe the torrent state we're about to set right after.
    private bool suppressTorrentReset;
    private long resumeUploaded;
    private long resumeDownloaded;

    // Exact bytes present when the last session stopped, and the Finished (%) text written from it. While the
    // field still shows that text, Start resumes from the exact bytes; an edited percentage starts from the edit.
    private long resumeHave = -1;
    private string finishedFromProgress = string.Empty;

    // The Finished (%) the user typed, restored when another torrent replaces one whose progress overwrote it.
    private string finishedChoice = "100";
    private bool assigningProgress;

    // Counters of a session written before format 3 whose torrent was missing: migrated once it is loaded.
    private bool legacyCountersPending;
    private string announcedHash = string.Empty;
    private string trackerIdentity = string.Empty;
    private long lastUploaded;
    private long lastDownloaded;
    private readonly TransferRateHistory uploadHistory = new(GraphPoints);
    private bool released;
    private CancellationTokenSource? startPreparation;
    private StopConditionSettings stopCondition = new(StopCondition.UploadAbove, "1000");

    /// <summary>Set by the window model: names another tab announcing the same torrent to the same tracker.</summary>
    internal Func<RatioTabViewModel, bool, RatioTabViewModel?>? FindTwin { get; set; }

    // ── Header / torrent ──
    [ObservableProperty]
    private string tabName;

    [ObservableProperty]
    private string torrentFilePath = string.Empty;

    [ObservableProperty]
    private string tracker = string.Empty;

    [ObservableProperty]
    private string hashHex = string.Empty;

    [ObservableProperty]
    private string torrentSize = string.Empty;

    [ObservableProperty]
    private bool smallTorrentWarning;

    public string SizeTip => SmallTorrentWarning ? Strings.SizeTipSmall : Strings.SizeTip;

    partial void OnSmallTorrentWarningChanged(bool value) => OnPropertyChanged(nameof(SizeTip));

    internal static bool IsSmallTorrent(long bytes) => bytes >= 0 && bytes < 8L * 1024 * 1024 * 1024;

    [ObservableProperty]
    private bool manualUpdatePending;

    [ObservableProperty]
    private string nextUpdateCountdown = "00:00:00";

    /// <summary>Seconds the tracker's minimum interval still forbids an update.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualUpdateHint))]
    private int manualUpdateWait;

    public string ManualUpdateHint => ManualUpdateWait > 0 ? Strings.ManualUpdateAvailableIn(Format.Countdown(ManualUpdateWait)) : Strings.ManualUpdateTip;

    // Independent upload and download speed variation; rates are expressed in MiB/s.
    [ObservableProperty]
    private string uploadSpeed = "100";

    [ObservableProperty]
    private bool randomUpload = true;

    [ObservableProperty]
    private string downloadSpeed = "0";

    [ObservableProperty]
    private bool randomDownload = true;

    // ── Options ──
    [ObservableProperty]
    private string intervalText = "1800";

    [ObservableProperty]
    private string finishedText = "100";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStopOption))]
    private StopCondition selectedStopWhen = StopCondition.UploadAbove;

    [ObservableProperty]
    private string stopValue = "1000";

    [ObservableProperty]
    private bool stopValueVisible = true;

    [ObservableProperty]
    private string stopUnit = Strings.UnitGiB;

    // ── Client emulation ──
    [ObservableProperty]
    private string selectedFamily = ClientCatalog.DefaultFamily;

    [ObservableProperty]
    private string selectedVersion = ClientCatalog.DefaultVersion;

    [ObservableProperty]
    private string customKey = string.Empty;

    [ObservableProperty]
    private string customPeerId = string.Empty;

    /// <summary>Empty = the port shared by every tab of this client (see <see cref="PortPlaceholder"/>).</summary>
    [ObservableProperty]
    private string customPort = string.Empty;

    /// <summary>Empty = the client's own numwant.</summary>
    [ObservableProperty]
    private string customPeers = string.Empty;

    [ObservableProperty]
    private string portPlaceholder = string.Empty;

    [ObservableProperty]
    private string peersPlaceholder = string.Empty;

    [ObservableProperty]
    private bool realisticMode = true;

    // ── Other settings ──
    [ObservableProperty]
    private bool useTcpListener = true;

    [ObservableProperty]
    private bool requestScrape = true;

    // ── Proxy ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProxyOption))]
    private ProxyKind selectedProxyKind = ProxyKind.None;

    [ObservableProperty]
    private string proxyHost = string.Empty;

    [ObservableProperty]
    private string proxyUser = string.Empty;

    [ObservableProperty]
    private string proxyPass = string.Empty;

    [ObservableProperty]
    private string proxyPort = string.Empty;

    // ── Log ──
    [ObservableProperty]
    private bool enableLog = true;

    [ObservableProperty]
    private string logText = string.Empty;

    [ObservableProperty]
    private string logFilter = string.Empty;

    [ObservableProperty]
    private bool followLog = true;

    public string VisibleLogText => string.IsNullOrEmpty(LogFilter) ? LogText
        : string.Join('\n', LogText.Split('\n').Where(line => line.Contains(LogFilter, StringComparison.OrdinalIgnoreCase)));

    partial void OnLogTextChanged(string value) => OnPropertyChanged(nameof(VisibleLogText));
    partial void OnLogFilterChanged(string value) => OnPropertyChanged(nameof(VisibleLogText));

    [RelayCommand]
    private void ClearLogFilter() => LogFilter = string.Empty;

    // ── Live stats ──
    [ObservableProperty]
    private string uploadedText = "0";

    [ObservableProperty]
    private string downloadedText = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatioText))]
    private double ratio;

    public string RatioText => Format.Number(Ratio);

    /// <summary>Completion of the torrent's data, as the tracker's "left" describes it; negative while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double progressPercent = -1;

    public string ProgressText => ProgressPercent < 0 ? "-" : Format.Percent(ProgressPercent);

    [ObservableProperty]
    private string seedersCountText = "-";

    [ObservableProperty]
    private string leechersCountText = "-";

    [ObservableProperty]
    private string totalTimeText = "00:00";

    private Func<string> timerSource = () => Strings.TimerIdle;
    private Func<string> statusSource = () => Strings.StatusIdle;

    /// <summary>The countdown, or a state such as idle or stopped that follows the interface language.</summary>
    public string TimerText
    {
        get => timerSource();
        set => ShowTimer(() => value);
    }

    /// <summary>The tab's status line; states written by the program follow a change of interface language.</summary>
    public string StatusText
    {
        get => statusSource();
        set => ShowStatus(() => value);
    }

    private void ShowTimer(Func<string> text)
    {
        timerSource = text;
        OnPropertyChanged(nameof(TimerText));
    }

    internal void ShowStatus(Func<string> text)
    {
        statusSource = text;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Incremented whenever counters reach a point worth saving (accepted announce, stop).</summary>
    [ObservableProperty]
    private int countersRevision;

    // The alert line under the terminal: always the CURRENT condition (each message replaces the previous),
    // unlike the append-only log. Its level also drives this tab's dot colour in the tab strip.
    [ObservableProperty]
    private string alertMessage = string.Empty;

    [ObservableProperty]
    private TabAlertLevel alertLevel = TabAlertLevel.None;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isActive;

    // Rolling upload-rate history (bytes/s) for the live graph.
    [ObservableProperty]
    private double[] graphValues = new double[GraphPoints];

    public RatioTabViewModel(string tabName)
    {
        this.tabName = tabName;

        foreach (ClientFamily family in ClientCatalog.Families)
        {
            ClientFamilies.Add(family.Name);
        }

        LoadVersions(SelectedFamily);
        RegenerateValues();
        RefreshLocalizedOptions();

        engine = CreateEngine();
    }

    private RatioEngine CreateEngine()
    {
        RatioEngine created = new(this);
        int statsPending = 0;
        created.Log += message => { if (!released && ReferenceEquals(engine, created)) AppendLog(message); };
        created.KeyChanged += key => Post(() =>
        {
            if (released || !ReferenceEquals(engine, created) || !keyIsGenerated || (!IsRunning && !IsTransitioning)) return;
            assigningIdentity = true;
            CustomKey = key;
            assigningIdentity = false;
        });
        created.Stats += _ =>
        {
            if (Interlocked.Exchange(ref statsPending, 1) != 0) return;
            Post(() =>
            {
                Interlocked.Exchange(ref statsPending, 0);
                if (!released && ReferenceEquals(engine, created)) ApplyStats(created.GetStats());
            });
        };
        created.Announced += () => Post(() => { if (!released && ReferenceEquals(engine, created)) CountersRevision++; });
        created.Stopped += notice => Post(() => { if (!released && ReferenceEquals(engine, created)) _ = OnEngineStoppedAsync(notice); });
        return created;
    }

    public ObservableCollection<string> ClientFamilies { get; } = [];

    public ObservableCollection<string> Versions { get; } = [];

    public ObservableCollection<StopOption> StopWhenOptions { get; } = [];

    public ObservableCollection<ProxyOption> ProxyTypes { get; } = [];

    public StopOption? SelectedStopOption
    {
        get => StopWhenOptions.FirstOrDefault(option => option.Value == SelectedStopWhen);
        set { if (value is not null) SelectedStopWhen = value.Value; }
    }

    public ProxyOption? SelectedProxyOption
    {
        get => ProxyTypes.FirstOrDefault(option => option.Value == SelectedProxyKind);
        set { if (value is not null) SelectedProxyKind = value.Value; }
    }

    /// <summary>Rebuilds every list and text derived from the interface language.</summary>
    internal void RefreshLocalizedOptions()
    {
        StopCondition stop = SelectedStopWhen;
        ProxyKind proxy = SelectedProxyKind;
        StopWhenOptions.Clear();
        foreach (StopCondition condition in new[]
        {
            StopCondition.Never, StopCondition.RatioAbove, StopCondition.UploadAbove, StopCondition.DownloadAbove,
            StopCondition.AfterTime, StopCondition.SeedersBelow, StopCondition.LeechersBelow, StopCondition.SwarmRatioBelow,
        }) StopWhenOptions.Add(new StopOption(condition, Strings.StopLabel(condition)));
        ProxyTypes.Clear();
        foreach (ProxyKind kind in new[] { ProxyKind.None, ProxyKind.HttpConnect, ProxyKind.Socks4, ProxyKind.Socks4a, ProxyKind.Socks5 })
            ProxyTypes.Add(new ProxyOption(kind, Strings.ProxyLabel(kind)));
        SelectedStopWhen = stop;
        SelectedProxyKind = proxy;
        OnPropertyChanged(nameof(SelectedStopOption));
        OnPropertyChanged(nameof(SelectedProxyOption));
        StopUnit = Strings.StopUnit(SelectedStopWhen);
        OnPropertyChanged(nameof(SizeTip));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ManualUpdateHint));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TimerText));
        OnPropertyChanged(nameof(RatioText));
        OnPropertyChanged(nameof(ProgressText));
        RefreshIdentityPlaceholders();
        if (loadedTorrent is not null) TorrentSize = Format.FileSize(totalLength);
        UploadedText = Format.FileSize(Math.Max(lastUploaded, resumeUploaded));
        DownloadedText = Format.FileSize(Math.Max(lastDownloaded, resumeDownloaded));
    }

    // ── Onboarding pulse hints ──
    // torrent-file box  →(torrent loaded)→  stop-value box  →(user touches it/combo)→  START button.
    // Opening a new tab flags a power user and kills all pulsing for the rest of the session.
    private static bool pulsingDisabledGlobally;
    private PulseStage pulseStage = pulsingDisabledGlobally ? PulseStage.None : PulseStage.TorrentFile;

    public bool TorrentInputPulsing => !pulsingDisabledGlobally && !IsRunning && pulseStage == PulseStage.TorrentFile;

    public bool StopValuePulsing => !pulsingDisabledGlobally && !IsRunning && pulseStage == PulseStage.StopValue && StopValueVisible;

    public bool StartButtonPulsing => !pulsingDisabledGlobally && !IsRunning && pulseStage == PulseStage.StartButton;

    public bool StopUnitVisible => !string.IsNullOrEmpty(StopUnit);

    public bool InputsEnabled => !released && !IsRunning && !IsPreparing && !IsTransitioning && !IsClosing;
    public bool CanToggleSession => !released && !IsPreparing && !IsTransitioning && !IsClosing;
    public bool CanManualUpdate => IsRunning && !IsClosing && !IsTransitioning && !IsAnnouncing && !ManualUpdateSending && ManualUpdateWait == 0;
    public string PrimaryActionText => !IsRunning ? Strings.ActionStart : IsPaused ? Strings.ActionResume : Strings.ActionPause;

    public bool TabIsPaused => IsRunning && (IsPaused || UploadPausedNoLeechers);
    public bool TabIsRunning => IsRunning && !TabIsPaused;
    public bool TabIsFinished => !IsRunning && !IsPreparing && !IsTransitioning && HasFinished;
    [ObservableProperty] private bool hasFinished;
    partial void OnHasFinishedChanged(bool value) => NotifySessionState();

    [ObservableProperty] private bool isClosing;
    [ObservableProperty] private bool isPreparing;
    partial void OnIsPreparingChanged(bool value) => NotifySessionState();
    partial void OnIsClosingChanged(bool value) => NotifySessionState();
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool uploadPausedNoLeechers;
    [ObservableProperty] private bool isTransitioning;
    [ObservableProperty] private bool isAnnouncing;
    [ObservableProperty] private bool manualUpdateSending;

    private void NotifySessionState()
    {
        bool shouldCount = IsRunning || IsTransitioning;
        if (activityCounted != shouldCount)
        {
            activityCounted = shouldCount;
            if (shouldCount) SessionActivity.Entered(); else SessionActivity.Exited();
        }
        OnPropertyChanged(nameof(InputsEnabled));
        OnPropertyChanged(nameof(CanToggleSession));
        OnPropertyChanged(nameof(CanManualUpdate));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(DotState));
        OnPropertyChanged(nameof(TabIsPaused));
        OnPropertyChanged(nameof(TabIsRunning));
        OnPropertyChanged(nameof(TabIsFinished));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ManualUpdateCommand.NotifyCanExecuteChanged();
        ConfirmManualUpdateCommand.NotifyCanExecuteChanged();
        SetDefaultsCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsPausedChanged(bool value) => NotifySessionState();
    partial void OnUploadPausedNoLeechersChanged(bool value) => NotifySessionState();
    partial void OnIsTransitioningChanged(bool value) => NotifySessionState();
    partial void OnIsAnnouncingChanged(bool value) => NotifySessionState();
    partial void OnManualUpdateSendingChanged(bool value) => NotifySessionState();
    partial void OnManualUpdateWaitChanged(int value) => NotifySessionState();

    // ══════════════════════ IEngineHost ══════════════════════
    bool IEngineHost.UseTcpListener => UseTcpListener;

    bool IEngineHost.RequestScrape => RequestScrape;

    // Decimal MiB/s values accept either a dot or a comma and are converted to bytes per second.
    long IEngineHost.UploadRateBytes => ValidRate(UploadSpeed) ? (long)(UploadSpeed.ParseDoubleOr(0) * 1024 * 1024) : 0;

    long IEngineHost.DownloadRateBytes => ValidRate(DownloadSpeed) ? (long)(DownloadSpeed.ParseDoubleOr(0) * 1024 * 1024) : 0;

    bool IEngineHost.RandomUploadEnabled => RandomUpload;

    bool IEngineHost.RandomDownloadEnabled => RandomDownload;

    StopConditionSettings IEngineHost.StopCondition => Volatile.Read(ref stopCondition);

    void IEngineHost.ApplyAlert(EngineAlert level, string message)
    {
        // The UI consumes the alert together with counters and pause flags in one engine snapshot.
    }

    private void ApplyEngineAlert(EngineAlert level, string message) => SetAlert(level switch
    {
        EngineAlert.Ok => TabAlertLevel.Ok,
        EngineAlert.Warning => TabAlertLevel.Warning,
        EngineAlert.Error => TabAlertLevel.Error,
        _ => TabAlertLevel.None,
    }, message);

    /// <summary>Set the tab's alert line directly from the UI layer (torrent/config problems the engine
    /// never sees, e.g. "no torrent selected").</summary>
    public void SetAlert(TabAlertLevel level, string message)
    {
        AlertLevel = level;
        AlertMessage = message;
        duplicateWarningShown = false;
    }

    private bool duplicateWarningShown;

    /// <summary>
    /// On stop, drop anything that described the RUN rather than a failure: a healthy "Tracker OK — …" and
    /// the "upload paused" warning both become untrue the moment the session ends (and the warning would
    /// otherwise leave a stopped tab with a yellow dot forever, since it is never a stop reason). Only
    /// <see cref="TabAlertLevel.Error"/> survives — that's what explains why a tab stopped.
    /// </summary>
    private void ClearTransientAlert()
    {
        if (AlertLevel != TabAlertLevel.Error)
        {
            SetAlert(TabAlertLevel.None, string.Empty);
        }
    }

    /// <summary>Colour of the tab's dot: a problem always outranks the run state, so a failing tab stays
    /// visible in the strip even while another tab is selected.</summary>
    public TabDotState DotState => AlertLevel switch
    {
        TabAlertLevel.Error => TabDotState.Error,
        TabAlertLevel.Warning => TabDotState.Warning,
        _ => IsRunning && (IsPaused || UploadPausedNoLeechers) ? TabDotState.Warning
            : IsRunning ? TabDotState.Running : HasFinished && !IsPreparing && !IsTransitioning ? TabDotState.Finished : TabDotState.Idle,
    };

    partial void OnAlertLevelChanged(TabAlertLevel value) => OnPropertyChanged(nameof(DotState));

    // ══════════════════════ Identity of the loaded torrent ══════════════════════

    /// <summary>The hash this tab announces, as lower-case hex (v1 SHA-1, or the truncated v2 hash).</summary>
    internal string AnnouncedHash => announcedHash;

    /// <summary>The tracker compared for duplicates: scheme and host case-folded, default port and fragment removed.</summary>
    internal string TrackerIdentity => trackerIdentity;

    partial void OnTrackerChanged(string value) => trackerIdentity = NormalizeTracker(value);

    internal bool HasTorrent => loadedTorrent is not null;

    internal static string NormalizeTracker(string url)
    {
        string value = url.Trim();
        int fragment = value.IndexOf('#');
        if (fragment >= 0) value = value[..fragment];
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)) return value;
        int authorityEnd = value.IndexOfAny(['/', '?'], value.IndexOf("://", StringComparison.Ordinal) + 3);
        string rest = authorityEnd < 0 ? string.Empty : value[authorityEnd..];
        bool defaultPort = uri.IsDefaultPort || (uri.Scheme == "udp" && uri.Port < 0);
        return uri.Scheme.ToLowerInvariant() + "://" + uri.IdnHost.ToLowerInvariant()
            + (defaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + (rest.Length == 0 ? "/" : rest);
    }

    // ══════════════════════ Commands ══════════════════════
    [RelayCommand(CanExecute = nameof(CanToggleSession))]
    private async Task Start()
    {
        if (!CanToggleSession) return;
        if (IsRunning)
        {
            if (engine.IsPaused) engine.Resume(); else engine.Pause();
            ApplyStats(engine.GetStats());
            return;
        }
        await pendingStop;
        if (!CanToggleSession) return;

        if (loadedTorrent == null || string.IsNullOrEmpty(Tracker) || string.IsNullOrEmpty(HashHex))
        {
            ShowStatus(() => Strings.StatusSelectTorrentFirst);
            AppendLog(Strings.LogSelectTorrent);
            SetAlert(TabAlertLevel.Error, Strings.AlertNoTorrent);
            return;
        }

        if (!Uri.TryCreate(Tracker, UriKind.Absolute, out Uri? trackerUri)
            || trackerUri.Scheme is not ("http" or "https" or "udp"))
        {
            ShowStatus(() => Strings.StatusInvalidTracker);
            AppendLog(Strings.InvalidTracker(Tracker));
            SetAlert(TabAlertLevel.Error, Strings.InvalidTracker(Tracker));
            return;
        }

        // Two sessions announcing one torrent to one tracker look like two peers of the same account.
        if (FindTwin?.Invoke(this, true) is { } twin)
        {
            SetAlert(TabAlertLevel.Error, Strings.DuplicateRunning(twin.TabName));
            return;
        }

        if (!ClientCatalog.TryCreate(SelectedFamily, SelectedVersion, out ClientProfile client))
        {
            SetAlert(TabAlertLevel.Error, Strings.ProfileUnavailable);
            return;
        }
        if (loadedTorrent.IsV2 && !loadedTorrent.IsHybrid && !client.SupportsV2)
        {
            SetAlert(TabAlertLevel.Error, Strings.ProfileNoV2);
            return;
        }
        if (SelectedProxyKind != ProxyKind.None
            && (string.IsNullOrWhiteSpace(ProxyHost) || !ushort.TryParse(ProxyPort, out ushort proxyPort) || proxyPort == 0))
        {
            SetAlert(TabAlertLevel.Error, Strings.ProxyInvalid);
            return;
        }
        if (trackerUri.Scheme == "udp" && SelectedProxyKind != ProxyKind.None)
        {
            SetAlert(TabAlertLevel.Error, Strings.UdpProxyUnsupported);
            return;
        }
        if (!ValidRate(UploadSpeed) || !ValidRate(DownloadSpeed))
        {
            SetAlert(TabAlertLevel.Error, Strings.SpeedsInvalid);
            return;
        }
        if (CustomPort.Length > 0 && (!ushort.TryParse(CustomPort, out ushort typedPort) || typedPort == 0))
        {
            SetAlert(TabAlertLevel.Error, Strings.EnginePortRange);
            return;
        }
        string key = string.IsNullOrEmpty(CustomKey) ? client.Key : CustomKey;
        string peerId = client.PeerID;
        if (!string.IsNullOrEmpty(CustomPeerId) && !PeerIdentityText.TryParse(CustomPeerId, out peerId))
        {
            SetAlert(TabAlertLevel.Error, Strings.PeerIdInvalid);
            return;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(37))
        {
            using CancellationTokenSource preparation = new();
            startPreparation = preparation;
            IsPreparing = true;
            SessionNetworkDecision decision;
            try
            {
                ShowStatus(() => Strings.StatusCheckingNetwork);
                string endpoint = SelectedProxyKind == ProxyKind.None ? trackerUri.DnsSafeHost : ProxyHost;
                decision = await SessionNetworkAccess.PrepareAsync(endpoint,
                    UseTcpListener && SelectedProxyKind == ProxyKind.None, preparation.Token);
            }
            catch (OperationCanceledException) { return; }
            finally { startPreparation = null; IsPreparing = false; }
            if (preparation.IsCancellationRequested || released || IsClosing) return;
            if (decision == SessionNetworkDecision.Cancelled)
            {
                ShowStatus(() => Strings.StatusStartCancelled);
                return;
            }
            if (decision == SessionNetworkDecision.DeniedEndpoint)
            {
                ShowStatus(() => Strings.StatusLocalPermission);
                SetAlert(TabAlertLevel.Error, Strings.LocalNetworkRequired);
                return;
            }
            if (decision == SessionNetworkDecision.DeniedListener)
                ShowLocalPeersUnavailable();
        }
        // A new session starts with its own engine and callbacks.
        SetAlert(TabAlertLevel.None, string.Empty);

        SetPulseStage(PulseStage.None); // user reached Start — onboarding done

        ProxyConfig proxy = BuildProxy();
        string port = CustomPort.Length > 0 ? CustomPort
            : ListeningPortBook.Resolve(client, ListeningPortBook.Route(proxy)).ToString(CultureInfo.InvariantCulture);
        string numWant = string.IsNullOrEmpty(CustomPeers) ? client.DefNumWant.ToString(CultureInfo.InvariantCulture) : CustomPeers;

        client.Key = key;
        client.PeerID = peerId;
        assigningIdentity = true;
        if (string.IsNullOrEmpty(CustomKey)) keyIsGenerated = true;
        if (string.IsNullOrEmpty(CustomPeerId)) peerIdIsGenerated = true;
        CustomKey = key;
        CustomPeerId = PeerIdentityText.Format(peerId);
        assigningIdentity = false;
        RefreshIdentityPlaceholders();

        // A Finished (%) still showing the stopped progress resumes the exact bytes; an edited one starts from the edit.
        double finished = Math.Clamp(FinishedText.ParseDoubleOr(0), 0, 100);
        long? initialHave = resumeHave >= 0 && FinishedText == finishedFromProgress ? Math.Min(resumeHave, totalLength) : null;
        if (initialHave is null)
        {
            FinishedText = finished.ToString(CultureInfo.InvariantCulture);
            resumeHave = -1;
        }

        SessionConfig cfg = new()
        {
            Client = client,
            Proxy = proxy,
            Tracker = Tracker,
            HashHex = HashHex,
            InfoHash = infoHash,
            PeerInfoHashes = peerInfoHashes,
            TotalLength = totalLength,
            FinishedPercent = finished,
            InitialHave = initialHave,
            Interval = IntervalText.ParseIntOr(1800),
            Port = port,
            Key = key,
            KeyIsGenerated = keyIsGenerated,
            PeerID = peerId,
            NumWant = numWant,
            PieceCount = pieceCount,
            Realistic = RealisticMode,
            IsPrivate = isPrivateTorrent,
            IsHybrid = isHybridTorrent,
            Metadata = metadata,
            ResumeUploaded = resumeUploaded,
            ResumeDownloaded = resumeDownloaded,
        };

        SeedersCountText = "-";
        LeechersCountText = "-";
        TotalTimeText = "00:00";
        ShowTimer(() => Strings.TimerUpdating);
        NextUpdateCountdown = Format.Countdown(Math.Clamp(cfg.Interval, 60, 86400));
        ShowStatus(() => Strings.StatusRunning);
        // Baseline the graph at the resumed total: the engine reports cumulative uploaded bytes.
        lastUploaded = cfg.ResumeUploaded;
        uploadHistory.Reset(cfg.ResumeUploaded);
        GraphValues = uploadHistory.Snapshot();

        engine = CreateEngine();
        IsPaused = UploadPausedNoLeechers = false;
        IsRunning = true;
        try
        {
            engine.Start(cfg);
            if (client.DescribeIdentityMismatch(peerId) is not null)
                SetAlert(TabAlertLevel.Warning, Strings.PeerIdMismatch(client.Name));
            string route = ListeningPortBook.Route(proxy);
            if (CustomPort.Length > 0 && ListeningPortBook.Peek(client.Name, route) is int shared
                && shared.ToString(CultureInfo.InvariantCulture) != CustomPort)
                AppendLog(Strings.PortDiffersFromClient(CustomPort, shared));
            if (CustomPort.Length == 0 && ListeningPortBook.SharingProfiles(client.Name, route, int.Parse(port, CultureInfo.InvariantCulture)) is { Count: > 0 } others)
                AppendLog(Strings.PortSharedWithClient(port, string.Join(", ", others)));
        }
        catch (Exception ex)
        {
            IsRunning = false;
            SetAlert(TabAlertLevel.Error, ex.Message);
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task Stop() => StopIfRunningAsync();

    [RelayCommand(CanExecute = nameof(CanManualUpdate))]
    private void ManualUpdate() => ManualUpdatePending = true;

    [RelayCommand]
    private void CancelManualUpdate() => ManualUpdatePending = false;

    [RelayCommand(CanExecute = nameof(CanManualUpdate))]
    private async Task ConfirmManualUpdate()
    {
        if (!ManualUpdatePending || !CanManualUpdate) return;
        ManualUpdatePending = false;
        ManualUpdateSending = true;
        RatioEngine source = engine;
        try
        {
            if (!await source.ManualUpdateAsync() && IsRunning && ReferenceEquals(engine, source))
                SetAlert(TabAlertLevel.Warning, Strings.ManualUpdateRefused);
        }
        finally { ManualUpdateSending = false; }
    }

    private static bool ValidRate(string text) => double.IsFinite(text.ParseDoubleOr(double.NaN))
        && text.ParseDoubleOr(-1) >= 0 && text.ParseDoubleOr(0) <= long.MaxValue / (1024.0 * 1024 * 2);

    private static void ShowLocalPeersUnavailable() => NotificationHub.Show(Strings.LocalPeersUnavailableTitle, Strings.LocalPeersUnavailable);

    partial void OnUseTcpListenerChanged(bool value)
    {
        if (IsRunning) engine.RefreshListener();
        if (value && IsRunning && SelectedProxyKind == ProxyKind.None && OperatingSystem.IsAndroidVersionAtLeast(37))
            _ = PrepareListenerAccessAsync();
    }

    private async Task PrepareListenerAccessAsync()
    {
        RatioEngine source = engine;
        try
        {
            SessionNetworkDecision decision = await SessionNetworkAccess.PrepareAsync("127.0.0.1", true, CancellationToken.None);
            if (!released && IsRunning && UseTcpListener && ReferenceEquals(engine, source)
                && decision == SessionNetworkDecision.DeniedListener) ShowLocalPeersUnavailable();
        }
        catch (Exception exception)
        {
            if (!released && IsRunning && ReferenceEquals(engine, source))
                NotificationHub.Show(Strings.LocalNetworkTitle, exception.Message, error: true);
        }
    }

    [RelayCommand(CanExecute = nameof(InputsEnabled))]
    private void SetDefaults()
    {
        SelectedFamily = ClientCatalog.DefaultFamily;
        LoadVersions(SelectedFamily);
        SelectedVersion = ClientCatalog.DefaultVersion;

        UploadSpeed = "100";
        RandomUpload = true;
        DownloadSpeed = "0";
        RandomDownload = true;

        IntervalText = "1800";
        FinishedText = "100";
        resumeHave = -1;
        SelectedStopWhen = StopCondition.UploadAbove;
        StopValue = "1000";
        StopValueVisible = true;
        StopUnit = Strings.UnitGiB;

        UseTcpListener = true;
        RequestScrape = true;

        SelectedProxyKind = ProxyKind.None;
        ProxyHost = string.Empty;
        ProxyUser = string.Empty;
        ProxyPass = string.Empty;
        ProxyPort = string.Empty;

        EnableLog = true;
        RealisticMode = true;
        CustomPort = string.Empty;
        CustomPeers = string.Empty;
        RegenerateValues();

        // Resetting settings preserves this torrent's counters and every other tab's saved state.
        ClearLog();
    }

    /// <summary>New key and peer ID for the selected client. Port and peer count keep their own setting.</summary>
    [RelayCommand]
    private void RegenerateValues()
    {
        ClientProfile client = ClientCatalog.Create(SelectedFamily, SelectedVersion);
        assigningIdentity = true;
        CustomKey = client.Key;
        CustomPeerId = PeerIdentityText.Format(client.PeerID);
        keyIsGenerated = peerIdIsGenerated = true;
        assigningIdentity = false;
        RefreshIdentityPlaceholders();
    }

    /// <summary>Shows the shared port and default numwant the empty fields stand for.</summary>
    private void RefreshIdentityPlaceholders()
    {
        ClientProfileDefinition definition = ClientCatalog.Definition(SelectedFamily, SelectedVersion);
        int? shared = ListeningPortBook.Peek(definition.Name, ListeningPortBook.Route(BuildProxy()));
        PortPlaceholder = shared is int port ? Strings.AutoValue(port.ToString(CultureInfo.InvariantCulture)) : Strings.AutoPort;
        PeersPlaceholder = Strings.AutoValue(definition.NumWant.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Called on the UI thread by the window model when a client's shared port is assigned.</summary>
    internal void OnPortBookChanged()
    {
        if (!released) RefreshIdentityPlaceholders();
    }

    [RelayCommand]
    private void ClearLog()
    {
        terminalBuffer.Clear();
        LogText = string.Empty;
        EngineStats snapshot = engine.GetStats();
        uploadHistory.Reset(snapshot.IsActive ? snapshot.Uploaded : Math.Max(lastUploaded, resumeUploaded), snapshot.SampleElapsedSeconds);
        GraphValues = uploadHistory.Snapshot();
    }

    // ══════════════════════ Torrent loading ══════════════════════
    public void LoadTorrentMetadata(string path) => LoadTorrentCore(() => new Torrent(path));

    /// <summary>Parse the torrent from bytes (Android: the picker returns a content:// URI, so the file is
    /// read as a stream rather than opened by path). The MemoryStream is fully consumed by the ctor.</summary>
    public void LoadTorrentMetadataFromBytes(byte[] data) => LoadTorrentCore(() =>
    {
        using MemoryStream ms = new(data, writable: false);
        return new Torrent(ms);
    });

    private void LoadTorrentCore(Func<Torrent> open)
    {
        try
        {
            Torrent t = open();

            byte[] newHash = t.InfoHash;
            string identity = Format.ToHex(newHash);
            if (!string.Equals(counterInfoHash, identity, StringComparison.OrdinalIgnoreCase)) ResetCounters();
            counterInfoHash = identity;

            HasFinished = false;
            loadedTorrent = t;
            infoHash = newHash;
            announcedHash = Convert.ToHexStringLower(newHash);
            totalLength = (long)t.TotalLength;
            if (legacyCountersPending && totalLength > 0)
            {
                MigrateLegacyCounters();
                ShowRestoredCounters();
            }
            pieceCount = t.PieceCount;
            isPrivateTorrent = t.IsPrivate;
            isHybridTorrent = t.IsHybrid;
            metadata = t.InfoBytes;
            // Hybrid swarms are reached through the SHA-1 and the truncated SHA-256: answer peers using either.
            peerInfoHashes = t.IsHybrid ? [newHash, t.FullInfoHash.AsSpan(0, 20).ToArray()] : [newHash];
            Tracker = t.Announce;
            HashHex = Format.ToHex(t.IsHybrid ? t.InfoHash : t.FullInfoHash);
            TorrentSize = Format.FileSize(totalLength);
            SmallTorrentWarning = IsSmallTorrent(totalLength);
            string name = t.Name;
            ShowStatus(() => Strings.StatusLoaded(name));
            SetAlert(TabAlertLevel.None, string.Empty);
            if (FindTwin?.Invoke(this, false) is { } twin)
            {
                SetAlert(TabAlertLevel.Warning, Strings.DuplicateOpen(twin.TabName));
                duplicateWarningShown = true;
            }

            // Torrent defined → move the pulse hint onto the Stop-value box.
            if (pulseStage == PulseStage.TorrentFile)
            {
                SetPulseStage(PulseStage.StopValue);
            }
        }
        catch (Exception ex)
        {
            // Clear metadata when parsing fails, so the displayed fields match the selected file.
            ClearTorrentMetadata();
            string reason = ex.Message;
            ShowStatus(() => Strings.TorrentLoadFailed(reason));
            AppendLog(Strings.TorrentLoadFailed(ex.Message));
            SetAlert(TabAlertLevel.Error, Strings.TorrentLoadFailed(ex.Message));
        }
    }

    public string? LastDirectory { get; private set; }

    /// <summary>A file the user picked or dropped: loaded whatever its extension (a typed path needs ".torrent").</summary>
    public void SetTorrentPath(string path) => OpenTorrent(path, null);

    /// <summary>Opens a desktop file; <paramref name="parsed"/> is the torrent already read from it, if any.</summary>
    internal void OpenTorrent(string path, Torrent? parsed)
    {
        if (!InputsEnabled) return;
        try { LastDirectory = Path.GetDirectoryName(path); }
        catch (Exception error) when (error is ArgumentException or PathTooLongException) { }
        suppressTorrentReset = true;
        TorrentFilePath = path;
        suppressTorrentReset = false;
        HasFinished = false;
        SetTorrentSource(path);
        if (parsed is null) LoadTorrentMetadata(path);
        else LoadTorrentCore(() => parsed);
    }

    /// <summary>Android entry point: the picker returned content with no re-openable path, so we get the raw
    /// bytes + the display name. We show the clean name in the box, load the metadata from the bytes, and
    /// materialize a private copy so a restored session can reload it.</summary>
    public void SetTorrentContent(byte[] data, string fileName) => OpenTorrent(data, fileName, null);

    /// <summary>Opens picked content; the private copy is written without pruning when a batch prunes once at its end.</summary>
    internal void OpenTorrent(byte[] data, string fileName, Torrent? parsed, bool pruneCache = true)
    {
        if (!InputsEnabled) return;
        // Show the file NAME (not the private cache path). Flag the assignment so the change hook treats it
        // as a load-in-progress, not a clear — we set the authoritative source + load from the bytes AFTER.
        suppressTorrentReset = true;
        TorrentFilePath = fileName;
        suppressTorrentReset = false;

        // Best-effort private copy for session-restore. The load below works from the bytes regardless of
        // whether this succeeds, so a read-only/full data dir never blocks selecting a torrent.
        SetTorrentSource(TryCacheTorrent(data, prune: pruneCache));
        if (parsed is null) LoadTorrentMetadataFromBytes(data);
        else LoadTorrentCore(() => parsed);
    }

    // Writable app-private dir for the materialized .torrent copies: LocalApplicationData maps to the app's
    // files dir on Android and a per-user dir on desktop; %TEMP% is the fallback if that can't be resolved.
    private static string TorrentCacheDir()
    {
        string baseDir;
        try
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.GetTempPath();
            }
        }
        catch
        {
            baseDir = Path.GetTempPath();
        }

        return Path.Combine(baseDir, "RatioMaster.NET", "torrents");
    }

    internal static string? TryCacheTorrent(byte[] data, string? directory = null, bool prune = true)
    {
        string? temporary = null;
        try
        {
            string dir = directory ?? TorrentCacheDir();
            Directory.CreateDirectory(dir);

            // Key the private copy by content, not the display name: two different
            // torrents that happen to share a name (e.g. "download.torrent") must not overwrite each other's
            // cache — else a restored tab would silently reload the wrong torrent and announce the wrong
            // info-hash. Identical re-picks map to the same file (dedup). Hex is always a safe filename.
            byte[] digest = SHA256.HashData(data);
            string cache = Path.Combine(dir, Convert.ToHexString(digest) + ".torrent");
            bool reusable = false;
            if (File.Exists(cache))
            {
                using FileStream existing = File.OpenRead(cache);
                reusable = existing.Length == data.Length && SHA256.HashData(existing).AsSpan().SequenceEqual(digest);
            }
            if (reusable)
            {
                try { File.SetLastWriteTimeUtc(cache, DateTime.UtcNow); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, cache, overwrite: true);
            }

            if (prune) PruneTorrentCache(dir, cache);
            return cache;
        }
        catch
        {
            return null; // no private copy → the current session still works, just no reload after restart
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Single writer for <see cref="torrentSourcePath"/>, keeping the global in-use registry in
    /// step so <see cref="PruneTorrentCache"/> can never evict a copy this tab still needs.</summary>
    private void SetTorrentSource(string? path)
    {
        lock (InUseCachePaths)
        {
            if (!string.IsNullOrEmpty(torrentSourcePath))
            {
                if (InUseCachePaths.TryGetValue(torrentSourcePath, out int count))
                {
                    if (count <= 1) InUseCachePaths.Remove(torrentSourcePath);
                    else InUseCachePaths[torrentSourcePath] = count - 1;
                }
            }

            torrentSourcePath = path;

            if (!string.IsNullOrEmpty(path))
            {
                InUseCachePaths[path] = InUseCachePaths.GetValueOrDefault(path) + 1;
            }
        }
    }

    /// <summary>Prunes once after a batch of picked files was cached.</summary>
    internal static void PruneTorrentCache() => PruneTorrentCache(TorrentCacheDir(), string.Empty);

    /// <summary>Keep the most recent cache entries while protecting files referenced by open tabs.</summary>
    private static void PruneTorrentCache(string dir, string keep)
    {
        const int MaxCachedTorrents = 32;

        try
        {
            FileInfo[] files = new DirectoryInfo(dir).GetFiles("*.torrent");
            if (files.Length <= MaxCachedTorrents)
            {
                return;
            }

            // Never evict a copy some tab is still pointing at. Eviction is ordered by last-write time and
            // nothing refreshes that when a tab merely LOADS a torrent, so an old-but-live entry would
            // otherwise age out — and on Android the private copy is the only way to reload it, since the
            // picker's content:// URI cannot be reopened. That tab would come back permanently unusable.
            HashSet<string> protectedPaths;
            lock (InUseCachePaths)
            {
                protectedPaths = new HashSet<string>(InUseCachePaths.Keys, StringComparer.OrdinalIgnoreCase);
            }

            protectedPaths.Add(keep);

            foreach (FileInfo f in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxCachedTorrents))
            {
                if (!protectedPaths.Contains(f.FullName))
                {
                    f.Delete();
                }
            }
        }
        catch
        {
            // pruning is housekeeping — never let it break picking a torrent
        }
    }

    public void StopIfRunning() => _ = StopIfRunningAsync();

    public async Task StopIfRunningAsync()
    {
        startPreparation?.Cancel();
        if (IsTransitioning) { await pendingStop; return; }
        if (!IsRunning) { await pendingStop; return; }
        IsTransitioning = true;
        try
        {
            pendingStop = engine.StopAsync();
            CaptureFinalStats();
            IsRunning = false;
            IsPaused = UploadPausedNoLeechers = false;
            ShowStatus(() => Strings.StatusStopped);
            ShowTimer(() => Strings.TimerStopped);
            ClearTransientAlert();
            await pendingStop;
            CaptureFinalStats();
            if (engine.StopError.Length > 0) SetAlert(TabAlertLevel.Error, engine.StopError);
        }
        finally { IsTransitioning = false; }
    }

    private void ResetCounters()
    {
        // Another torrent starts from the percentage the user chose, not from the progress of the previous one.
        if (finishedFromProgress.Length > 0 && FinishedText == finishedFromProgress)
        {
            assigningProgress = true;
            FinishedText = finishedChoice;
            assigningProgress = false;
        }
        resumeUploaded = resumeDownloaded = lastUploaded = lastDownloaded = 0;
        resumeHave = -1;
        finishedFromProgress = string.Empty;
        legacyCountersPending = false;
        UploadedText = DownloadedText = Format.FileSize(0);
        Ratio = 0;
        ProgressPercent = -1;
        SeedersCountText = "-";
        LeechersCountText = "-";
        TotalTimeText = "00:00";
        ShowTimer(() => Strings.TimerIdle);
        NextUpdateCountdown = "00:00:00";
        uploadHistory.Reset(0);
        GraphValues = uploadHistory.Snapshot();
    }

    private void CaptureFinalStats()
    {
        EngineStats stats = engine.GetStats();
        if (keyIsGenerated && engine.CurrentKey.Length > 0)
        {
            assigningIdentity = true;
            CustomKey = engine.CurrentKey;
            assigningIdentity = false;
        }
        HasFinished = engine.FinishedSuccessfully;
        resumeUploaded = lastUploaded = stats.Uploaded;
        resumeDownloaded = lastDownloaded = stats.Downloaded;
        if (stats.TotalSize > 0 || totalLength == 0) RememberProgress(stats.Have);
        UploadedText = Format.FileSize(stats.Uploaded);
        DownloadedText = Format.FileSize(stats.Downloaded);
        Ratio = stats.Ratio;
        ProgressPercent = stats.FinishedPercent;
        if (GraphValues.Length > 0 && GraphValues[^1] != 0)
        {
            // A stopped session uploads nothing: end the curve at zero instead of freezing its last rate.
            double[] values = GraphValues.ToArray();
            Array.Copy(values, 1, values, 0, values.Length - 1);
            values[^1] = 0;
            GraphValues = values;
        }
        CountersRevision++;
    }

    /// <summary>Finished (%) follows the progress a session reached, so the next start resumes it.</summary>
    private void RememberProgress(long have)
    {
        resumeHave = Math.Clamp(have, 0, totalLength);
        finishedFromProgress = ProgressFinished(resumeHave);
        assigningProgress = true;
        FinishedText = finishedFromProgress;
        assigningProgress = false;
    }

    /// <summary>The percentage written for some bytes present, rounded down so it never claims a missing byte.</summary>
    private string ProgressFinished(long have)
    {
        double percent = totalLength == 0 ? 100 : Math.Floor(Math.Clamp(have, 0, totalLength) * 10000.0 / totalLength) / 100;
        return percent.ToString("0.##", CultureInfo.InvariantCulture);
    }

    partial void OnFinishedTextChanged(string value)
    {
        if (!assigningProgress) finishedChoice = value;
    }

    public void ReleaseResources()
    {
        released = true;
        startPreparation?.Cancel();
        terminalBuffer.Clear();
        SetTorrentSource(null);
        NotifySessionState();
    }

    // ══════════════════════ Session persistence ══════════════════════
    internal TabState CaptureState()
    {
        EngineStats? current = engine.IsRunning || IsTransitioning ? engine.GetStats() : null;

        // While running, the bytes present move on: save them with the matching percentage, so a session that is
        // killed (Android may end a paused app) resumes where it was instead of from its starting point.
        string finished = FinishedText, progressFinished = finishedFromProgress;
        if (current is not null && totalLength > 0) finished = progressFinished = ProgressFinished(current.Have);
        return new()
        {
            TabName = TabName,
            TorrentFilePath = TorrentFilePath,
            TorrentSourcePath = torrentSourcePath ?? string.Empty,
            LastDirectory = LastDirectory ?? string.Empty,
            TorrentHash = counterInfoHash,
            Tracker = Tracker,
            UploadSpeed = UploadSpeed,
            RandomUpload = RandomUpload,
            DownloadSpeed = DownloadSpeed,
            RandomDownload = RandomDownload,
            Interval = IntervalText,
            Finished = finished,
            FinishedChoice = finishedChoice,
            StopWhen = SessionSettings.FormatStopCondition(SelectedStopWhen),
            StopValue = StopValue,
            Family = SelectedFamily,
            Version = SelectedVersion,
            CustomKey = keyIsGenerated && (IsRunning || IsTransitioning) && engine.CurrentKey.Length > 0 ? engine.CurrentKey : CustomKey,
            KeyIsGenerated = keyIsGenerated,
            PeerIdIsGenerated = peerIdIsGenerated,
            CustomPeerId = CustomPeerId,
            CustomPort = CustomPort,
            CustomPeers = CustomPeers,
            RealisticMode = RealisticMode,
            UseTcpListener = UseTcpListener,
            RequestScrape = RequestScrape,
            ProxyType = SessionSettings.FormatProxy(SelectedProxyKind),
            ProxyHost = ProxyHost,
            ProxyUser = ProxyUser,
            ProxyPass = ProxyPass,
            ProxyPort = ProxyPort,
            EnableLog = EnableLog,
            FinishedSuccessfully = HasFinished,

            // Active engines provide current counters; inactive tabs retain their saved resume totals.
            Uploaded = current?.Uploaded ?? Math.Max(lastUploaded, resumeUploaded),
            Downloaded = current?.Downloaded ?? Math.Max(lastDownloaded, resumeDownloaded),
            Have = current?.Have ?? resumeHave,
            ProgressFinished = progressFinished,
            LegacyCounters = legacyCountersPending,
        };
    }

    /// <summary>Restores a saved tab. <paramref name="formatVersion"/> is the version the session was written with.</summary>
    internal void ApplyState(TabState st, int formatVersion = SessionData.CurrentFormatVersion)
    {
        TabName = st.TabName;
        LastDirectory = st.LastDirectory;

        (string family, string version, ProfileResolution resolution) = ClientCatalog.Resolve(st.Family, st.Version);
        bool knownProfile = resolution == ProfileResolution.Exact;
        string? identityWarning = null;
        suppressVersionReload = true;
        SelectedFamily = family;
        LoadVersions(SelectedFamily);
        SelectedVersion = version;
        suppressVersionReload = false;

        UploadSpeed = st.UploadSpeed;
        RandomUpload = st.RandomUpload;
        DownloadSpeed = st.DownloadSpeed;
        RandomDownload = st.RandomDownload;
        IntervalText = st.Interval;
        FinishedText = st.Finished;
        finishedChoice = st.FinishedChoice.Length > 0 ? st.FinishedChoice : st.Finished;
        SelectedStopWhen = SessionSettings.ParseStopCondition(st.StopWhen);
        StopValue = st.StopValue; // override the default the combo change just set
        RealisticMode = st.RealisticMode;
        UseTcpListener = st.UseTcpListener;
        RequestScrape = st.RequestScrape;
        SelectedProxyKind = SessionSettings.ParseProxy(st.ProxyType);
        ProxyHost = st.ProxyHost;
        ProxyUser = st.ProxyUser;
        ProxyPass = st.ProxyPass;
        ProxyPort = st.ProxyPort;
        EnableLog = st.EnableLog;

        // Saved identities take precedence over profile defaults; absent provenance remains custom.
        ClientProfile replacement = ClientCatalog.Create(SelectedFamily, SelectedVersion);
        assigningIdentity = true;
        CustomKey = st.CustomKey;
        CustomPeerId = st.CustomPeerId;
        keyIsGenerated = st.KeyIsGenerated == true;
        peerIdIsGenerated = st.PeerIdIsGenerated == true;
        if (!knownProfile)
        {
            if (keyIsGenerated) CustomKey = replacement.Key;
            if (peerIdIsGenerated) CustomPeerId = PeerIdentityText.Format(replacement.PeerID);
            identityWarning = resolution == ProfileResolution.SameFamily
                ? Strings.ProfileRetired(st.Family + " " + st.Version, SelectedFamily + " " + SelectedVersion)
                : Strings.ProfileMissing;

            // The real client keeps its port across an upgrade: the successor inherits the retired version's port.
            string route = ListeningPortBook.Route(BuildProxy());
            if (resolution == ProfileResolution.SameFamily && ListeningPortBook.Peek(st.Family + " " + st.Version, route) is int kept)
                ListeningPortBook.Adopt(ClientCatalog.Definition(SelectedFamily, SelectedVersion), route, kept);
        }
        else if (peerIdIsGenerated && PeerIdentityText.TryParse(st.CustomPeerId, out string savedPeerId)
            && replacement.DescribeIdentityMismatch(savedPeerId) is not null)
        {
            // A generated identity from an earlier build of this profile (another build number or checksum rule).
            CustomPeerId = PeerIdentityText.Format(replacement.PeerID);
            if (keyIsGenerated) CustomKey = replacement.Key;
        }
        else if (st.PeerIdIsGenerated is null && st.Family == "uTorrent"
            && (st.CustomPeerId.StartsWith("-UT3600-", StringComparison.Ordinal) || st.CustomPeerId.StartsWith("-UT3550-", StringComparison.Ordinal)))
            identityWarning = Strings.PeerIdOldFormat;
        assigningIdentity = false;

        // Sessions before format 3 filled Port and Peers automatically on every start: those values were not choices.
        ClientProfileDefinition definition = ClientCatalog.Definition(SelectedFamily, SelectedVersion);
        if (formatVersion < 3)
        {
            if (st.CustomPort.ParseIntOr(0) is int legacyPort and > 0)
                ListeningPortBook.Adopt(definition, ListeningPortBook.Route(BuildProxy()), legacyPort);
            CustomPort = string.Empty;
            CustomPeers = st.CustomPeers == definition.NumWant.ToString(CultureInfo.InvariantCulture) ? string.Empty : st.CustomPeers;
        }
        else
        {
            CustomPort = st.CustomPort;
            CustomPeers = st.CustomPeers;
        }
        RefreshIdentityPlaceholders();

        if (!string.IsNullOrWhiteSpace(st.TorrentFilePath))
        {
            // On desktop this is a real path → the change hook loads the metadata (and sets torrentSourcePath).
            TorrentFilePath = st.TorrentFilePath;
        }

        // Android: TorrentFilePath is just the display name, so the hook above couldn't load anything.
        // Reload the metadata from the materialized private copy recorded at pick time.
        if (loadedTorrent is null
            && !string.IsNullOrWhiteSpace(st.TorrentSourcePath)
            && File.Exists(st.TorrentSourcePath))
        {
            SetTorrentSource(st.TorrentSourcePath);
            LoadTorrentMetadata(st.TorrentSourcePath);
        }
        string identity = loadedTorrent is not null ? Format.ToHex(infoHash) : st.TorrentHash;
        bool matches = st.TorrentHash.Length == 0 || string.Equals(identity, st.TorrentHash, StringComparison.OrdinalIgnoreCase);
        if (matches && identity.Length > 0)
        {
            counterInfoHash = identity;
            resumeUploaded = lastUploaded = Math.Max(0, st.Uploaded);
            resumeDownloaded = lastDownloaded = Math.Max(0, st.Downloaded);
            if (formatVersion < 3 || st.LegacyCounters)
            {
                // Without the torrent its size is unknown: keep the old counters until it can be loaded.
                if (totalLength > 0) MigrateLegacyCounters();
                else legacyCountersPending = true;
            }
            else if (st.Have >= 0 && totalLength > 0 && st.ProgressFinished == st.Finished && st.Finished.Length > 0)
            {
                resumeHave = Math.Min(st.Have, totalLength);
                finishedFromProgress = st.ProgressFinished;
            }
            ShowRestoredCounters();
        }
        else ResetCounters();
        if (matches && !string.IsNullOrWhiteSpace(st.Tracker)) Tracker = st.Tracker;
        HasFinished = matches && loadedTorrent is not null && st.FinishedSuccessfully;
        if (identityWarning is not null) SetAlert(TabAlertLevel.Warning, identityWarning);
        else RefreshDuplicateAlert();
    }

    /// <summary>Earlier versions saved the bytes present as "downloaded", including the data the torrent started with.</summary>
    private void MigrateLegacyCounters()
    {
        long present = Math.Clamp(lastDownloaded, 0, totalLength);
        long initial = (long)(totalLength * (decimal)Math.Clamp(FinishedText.ParseDoubleOr(0), 0, 100) / 100m);
        resumeDownloaded = lastDownloaded = Math.Max(0, present - initial);
        if (present > 0) RememberProgress(present);
        legacyCountersPending = false;
    }

    private void ShowRestoredCounters()
    {
        UploadedText = Format.FileSize(lastUploaded);
        DownloadedText = Format.FileSize(lastDownloaded);
        long denominator = Math.Max(lastDownloaded, resumeHave);
        Ratio = denominator > 0 ? lastUploaded / (double)denominator : 0;
        ProgressPercent = resumeHave >= 0 && totalLength > 0 ? resumeHave * 100.0 / totalLength : -1;
    }

    /// <summary>The duplicate warning reflects the tracker finally restored, not the one the torrent file named.</summary>
    private void RefreshDuplicateAlert()
    {
        if (loadedTorrent is null) return;
        if (FindTwin?.Invoke(this, false) is { } twin) SetAlert(TabAlertLevel.Warning, Strings.DuplicateOpen(twin.TabName));
        else if (AlertLevel == TabAlertLevel.Warning && duplicateWarningShown) SetAlert(TabAlertLevel.None, string.Empty);
    }

    // ══════════════════════ Engine callbacks (already on UI thread) ══════════════════════
    private void ApplyStats(EngineStats s)
    {
        // A Stats event queued just before a manual Stop must not run after OnEngineStopped and rewrite the
        // "stopped" UI back to "Seeding…". IsRunning is set true before the engine's first EmitStats, so no
        // legitimate update is dropped.
        if (!IsRunning || !s.IsActive)
        {
            return;
        }

        UploadedText = Format.FileSize(s.Uploaded);
        DownloadedText = Format.FileSize(s.Downloaded);
        Ratio = s.Ratio;
        ProgressPercent = s.FinishedPercent;
        if (s.Seeders >= 0) SeedersCountText = s.Seeders.ToString(CultureInfo.InvariantCulture);
        if (s.Leechers >= 0) LeechersCountText = s.Leechers.ToString(CultureInfo.InvariantCulture);

        TotalTimeText = Format.Time(s.TotalRunSeconds);
        TimerText = Format.Time(s.IntervalRemaining);
        NextUpdateCountdown = Format.Countdown(s.IntervalRemaining);
        ManualUpdateWait = s.ManualUpdateWait;
        ApplyEngineAlert(s.AlertLevel, s.AlertMessage);

        IsPaused = s.IsPaused;
        UploadPausedNoLeechers = s.UploadPausedNoLeechers;
        IsAnnouncing = s.IsAnnouncing;
        Func<string> status = IsPaused ? () => Strings.StatusPausedByYou : UploadPausedNoLeechers ? () => Strings.StatusPausedNoLeechers
            : IsAnnouncing ? () => Strings.StatusAnnouncing : s.SeedMode ? () => Strings.StatusSeeding : () => Strings.StatusDownloading;
        if (statusSource != status) ShowStatus(status);

        lastUploaded = s.Uploaded;
        lastDownloaded = s.Downloaded;
        if (uploadHistory.Sample(s.Uploaded, s.SampleElapsedSeconds)) GraphValues = uploadHistory.Snapshot();
    }

    private async Task OnEngineStoppedAsync(StopNotice notice)
    {
        bool wasStopping = IsTransitioning;
        IsTransitioning = true;
        CaptureFinalStats();
        IsRunning = false;
        IsPaused = UploadPausedNoLeechers = false;
        StatusText = notice.Reason;
        ShowTimer(() => Strings.TimerStopped);
        ManualUpdateWait = 0;
        if (notice.IsError) SetAlert(TabAlertLevel.Error, notice.Reason);
        else ClearTransientAlert();
        if (wasStopping) return;
        try
        {
            pendingStop = engine.StopAsync(); await pendingStop;
            CaptureFinalStats();
            if (engine.StopError.Length > 0) SetAlert(TabAlertLevel.Error, engine.StopError);
        }
        finally { IsTransitioning = false; }
        if (!notice.IsManual) NotificationHub.Show(TabName, notice.Reason, notice.IsError);
    }

#if DEBUG
    /// <summary>Visual review only: a few representative log lines in the current language.</summary>
    internal void AppendSampleLog()
    {
        LogText = string.Join(Environment.NewLine, new[]
        {
            Strings.LogEmulationInfo, Strings.LogEmulationName("qBittorrent 5.2.4"), Strings.LogConnecting("tracker.example", 443),
            Strings.LogUpdatingInterval(1800), Strings.TrackerWarning("example"), Strings.PeerHandshakeAnsweredEncrypted("203.0.113.7:51413"),
        }.Select(line => "[12:00:00] " + line));
        ShowStatus(() => Strings.StatusSeeding);
        SetAlert(TabAlertLevel.Ok, Strings.AlertTrackerOk);
    }
#endif

    private void AppendLog(string line)
    {
        if (!EnableLog)
        {
            return;
        }

        string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (terminalBuffer.Enqueue($"[{stamp}] {line}\n"))
            Post(() => { if (!released) LogText = terminalBuffer.Drain(LogText); });
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private ProxyConfig BuildProxy() => new()
    {
        Kind = SelectedProxyKind,
        Host = ProxyHost,
        Port = ProxyPort.ParseIntOr(0),
        User = ProxyUser,
        Password = ProxyPass,
    };

    private void LoadVersions(string family)
    {
        Versions.Clear();
        foreach (ClientFamily f in ClientCatalog.Families)
        {
            if (f.Name == family)
            {
                foreach (string v in f.Versions)
                {
                    Versions.Add(v);
                }

                break;
            }
        }
    }

    // ══════════════════════ Pulse hints ══════════════════════

    /// <summary>User clicked the Stop-value box or its combo — move the hint to START.</summary>
    public void NotifyStopInteraction()
    {
        if (!pulsingDisabledGlobally && pulseStage == PulseStage.StopValue)
        {
            SetPulseStage(PulseStage.StartButton);
        }
    }

    /// <summary>A new tab was opened → power user; kill pulsing everywhere for the session.</summary>
    internal static void DisablePulsingGlobally() => pulsingDisabledGlobally = true;

    /// <summary>Re-evaluate the pulse bindings (e.g. after the global disable flips).</summary>
    internal void RefreshPulse() => RaisePulse();

    private void SetPulseStage(PulseStage stage)
    {
        if (pulseStage == stage)
        {
            return;
        }

        pulseStage = stage;
        RaisePulse();
    }

    private void RaisePulse()
    {
        OnPropertyChanged(nameof(TorrentInputPulsing));
        OnPropertyChanged(nameof(StopValuePulsing));
        OnPropertyChanged(nameof(StartButtonPulsing));
    }

    // ══════════════════════ Change hooks ══════════════════════
    partial void OnCustomKeyChanged(string value)
    {
        if (!assigningIdentity) keyIsGenerated = false;
    }

    partial void OnStopValueChanged(string value) => UpdateStopCondition();

    private void UpdateStopCondition() => Volatile.Write(ref stopCondition, new StopConditionSettings(SelectedStopWhen, StopValue));

    partial void OnCustomPeerIdChanged(string value)
    {
        if (!assigningIdentity) peerIdIsGenerated = false;
    }

    partial void OnSelectedProxyKindChanged(ProxyKind value) => RefreshIdentityPlaceholders();
    partial void OnProxyHostChanged(string value) => RefreshIdentityPlaceholders();
    partial void OnProxyPortChanged(string value) => RefreshIdentityPlaceholders();

    partial void OnTorrentFilePathChanged(string value)
    {
        if (suppressTorrentReset) return;
        HasFinished = false;
        if (!string.IsNullOrWhiteSpace(value)
            && value.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            && File.Exists(value))
        {
            SetTorrentSource(value); // a real path typed/dropped/picked = its own reload source
            LoadTorrentMetadata(value);
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            SetPulseStage(PulseStage.TorrentFile); // guide (re)loading a torrent
        }

        // Clearing the path also clears the source and displayed torrent metadata.
        SetTorrentSource(null);
        ClearTorrentMetadata();
        ShowStatus(() => Strings.StatusSelectTorrent);
        SetAlert(TabAlertLevel.None, string.Empty);
    }

    private void ClearTorrentMetadata()
    {
        HasFinished = false;
        loadedTorrent = null;
        counterInfoHash = string.Empty;
        ResetCounters();
        infoHash = [];
        announcedHash = string.Empty;
        peerInfoHashes = [];
        totalLength = 0;
        pieceCount = 0;
        isPrivateTorrent = isHybridTorrent = false;
        metadata = ReadOnlyMemory<byte>.Empty;
        Tracker = string.Empty;
        HashHex = string.Empty;
        TorrentSize = string.Empty;
        SmallTorrentWarning = false;
    }

    partial void OnIsRunningChanged(bool value)
    {
        if (value) HasFinished = false;
        else { ManualUpdatePending = false; IsPaused = UploadPausedNoLeechers = IsAnnouncing = false; }
        RaisePulse();
        NotifySessionState();
    }

    partial void OnStopUnitChanged(string value) => OnPropertyChanged(nameof(StopUnitVisible));

    partial void OnSelectedFamilyChanged(string value)
    {
        if (suppressVersionReload) return;
        suppressVersionReload = true;
        LoadVersions(value);
        SelectedVersion = Versions.Count > 0 ? Versions[0] : string.Empty;
        suppressVersionReload = false;
        CustomPeers = string.Empty;
        RegenerateValues();
    }

    partial void OnSelectedVersionChanged(string value)
    {
        if (!suppressVersionReload && !string.IsNullOrEmpty(value))
        {
            RegenerateValues();
        }
    }

    partial void OnSelectedStopWhenChanged(StopCondition value)
    {
        (StopValue, StopValueVisible) = value switch
        {
            StopCondition.RatioAbove => ("2.0", true),
            StopCondition.SwarmRatioBelow => ("1.0", true),
            StopCondition.AfterTime => ("3600", true),
            StopCondition.SeedersBelow or StopCondition.LeechersBelow => ("10", true),
            StopCondition.UploadAbove or StopCondition.DownloadAbove => ("1000", true),
            _ => (string.Empty, false),
        };
        StopUnit = Strings.StopUnit(value);

        RaisePulse(); // StopValueVisible affects StopValuePulsing
        UpdateStopCondition();
    }
}

internal enum PulseStage
{
    TorrentFile,
    StopValue,
    StartButton,
    None,
}
