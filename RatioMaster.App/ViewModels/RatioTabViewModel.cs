namespace RatioMaster.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>
/// One RatioMaster tab: holds every bindable field, implements <see cref="IEngineHost"/>
/// (single source of truth for the live speed fields), and drives a <see cref="RatioEngine"/>.
/// </summary>
public partial class RatioTabViewModel : ObservableObject, IEngineHost
{
    private static readonly Random Rng = new();

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
    private string counterInfoHash = string.Empty;
    private long totalLength;
    private int pieceCount;
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
    private long lastUploaded;
    private long lastDownloaded;
    private readonly TransferRateHistory uploadHistory = new(GraphPoints);
    private bool released;
    private CancellationTokenSource? startPreparation;
    private StopConditionSettings stopCondition = new("When upload >", "1000");

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

    public string SizeTip => SmallTorrentWarning
        ? "This torrent is smaller than 8 GiB. Consider a torrent of 10 GiB or more. Size is only a guideline; it does not prevent tracker detection or bans."
        : "Total size of the torrent contents. A torrent of 10 GiB or more is preferable. Size alone does not prevent tracker detection or bans.";

    partial void OnSmallTorrentWarningChanged(bool value) => OnPropertyChanged(nameof(SizeTip));

    internal static bool IsSmallTorrent(long bytes) => bytes >= 0 && bytes < 8L * 1024 * 1024 * 1024;

    [ObservableProperty]
    private bool manualUpdatePending;

    [ObservableProperty]
    private string nextUpdateCountdown = "00:00:00";

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
    private string selectedStopWhen = "When upload >";

    [ObservableProperty]
    private string stopValue = "1000";

    [ObservableProperty]
    private bool stopValueVisible = true;

    [ObservableProperty]
    private string stopUnit = "GiB";

    // ── Client emulation ──
    [ObservableProperty]
    private string selectedFamily = ClientCatalog.DefaultFamily;

    [ObservableProperty]
    private string selectedVersion = ClientCatalog.DefaultVersion;

    [ObservableProperty]
    private string customKey = string.Empty;

    [ObservableProperty]
    private string customPeerId = string.Empty;

    [ObservableProperty]
    private string customPort = string.Empty;

    [ObservableProperty]
    private string customPeers = string.Empty;

    [ObservableProperty]
    private bool realisticMode = true;

    [ObservableProperty]
    private string genStatus = string.Empty;

    // ── Other settings ──
    [ObservableProperty]
    private bool useTcpListener = true;

    [ObservableProperty]
    private bool requestScrape = true;

    // ── Proxy ──
    [ObservableProperty]
    private string selectedProxyType = "None";

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
    private string ratioText = "0.0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeedersCountText))]
    private string seedersText = "Seeders: -";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeechersCountText))]
    private string leechersText = "Leechers: -";

    public string SeedersCountText => SeedersText.StartsWith("Seeders: ", StringComparison.Ordinal) ? SeedersText[9..] : SeedersText;
    public string LeechersCountText => LeechersText.StartsWith("Leechers: ", StringComparison.Ordinal) ? LeechersText[10..] : LeechersText;

    [ObservableProperty]
    private string totalTimeText = "00:00";

    [ObservableProperty]
    private string remainingText = "0";

    [ObservableProperty]
    private string timerText = "idle";

    [ObservableProperty]
    private string statusText = "Idle";

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
        created.Stopped += reason => Post(() => { if (!released && ReferenceEquals(engine, created)) _ = OnEngineStoppedAsync(reason); });
        return created;
    }

    public ObservableCollection<string> ClientFamilies { get; } = [];

    public ObservableCollection<string> Versions { get; } = [];

    public ObservableCollection<string> StopWhenOptions { get; } =
    [
        "Never", "When ratio >", "When upload >", "When download >", "After time:", "When seeders <", "When leechers <", "When leechers / seeders <",
    ];

    public ObservableCollection<string> ProxyTypes { get; } =
    [
        "None", "HTTP", "Socks4", "Socks4a", "Socks5",
    ];

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
    public bool CanManualUpdate => IsRunning && !IsClosing && !IsTransitioning && !IsAnnouncing && !ManualUpdateSending;
    public string PrimaryActionText => !IsRunning ? "START" : IsPaused ? "Resume" : "Pause";

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

    // ══════════════════════ IEngineHost ══════════════════════
    bool IEngineHost.UseTcpListener => UseTcpListener;

    bool IEngineHost.RequestScrape => RequestScrape;


    // Decimal MiB/s values accept either a dot or a comma and are converted to bytes per second.
    long IEngineHost.UploadRateBytes => ValidRate(UploadSpeed) ? (long)(UploadSpeed.ParseDoubleOr(0) * 1024 * 1024) : 0;

    long IEngineHost.DownloadRateBytes => ValidRate(DownloadSpeed) ? (long)(DownloadSpeed.ParseDoubleOr(0) * 1024 * 1024) : 0;

    bool IEngineHost.RandomUploadEnabled => RandomUpload;

    bool IEngineHost.RandomDownloadEnabled => RandomDownload;

    string IEngineHost.StopWhen => SelectedStopWhen;

    string IEngineHost.StopValue => StopValue;
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
    }

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
            StatusText = "Select a valid torrent file first";
            AppendLog("Please select a valid torrent file!");
            SetAlert(TabAlertLevel.Error, "No torrent loaded - select a .torrent file first.");
            return;
        }

        if (!Uri.TryCreate(Tracker, UriKind.Absolute, out Uri? trackerUri)
            || trackerUri.Scheme is not ("http" or "https" or "udp"))
        {
            StatusText = "Invalid tracker URL";
            AppendLog("Invalid tracker URL: " + Tracker);
            SetAlert(TabAlertLevel.Error, "Invalid tracker URL: " + Tracker);
            return;
        }

        if (!ClientCatalog.TryCreate(SelectedFamily, SelectedVersion, out ClientProfile client))
        {
            SetAlert(TabAlertLevel.Error, "The selected client profile is unavailable. Choose a listed family and version.");
            return;
        }
        if (loadedTorrent.IsV2 && !loadedTorrent.IsHybrid && !client.SupportsV2)
        {
            SetAlert(TabAlertLevel.Error, "This client profile does not support pure v2 torrents. Choose a compatible profile such as qBittorrent.");
            return;
        }
        if (!ProxyTypes.Contains(SelectedProxyType) || (SelectedProxyType != "None"
            && (string.IsNullOrWhiteSpace(ProxyHost) || !ushort.TryParse(ProxyPort, out ushort proxyPort) || proxyPort == 0)))
        {
            SetAlert(TabAlertLevel.Error, "Choose a valid proxy type, host and port (1-65535).");
            return;
        }
        if (trackerUri.Scheme == "udp" && SelectedProxyType != "None")
        {
            SetAlert(TabAlertLevel.Error, "UDP trackers cannot use this TCP proxy. Choose an HTTP(S) tracker or disable the proxy.");
            return;
        }
        if (!ValidRate(UploadSpeed) || !ValidRate(DownloadSpeed))
        {
            SetAlert(TabAlertLevel.Error, "Enter finite, non-negative upload and download speeds.");
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
                StatusText = "Checking local network access…";
                string endpoint = SelectedProxyType == "None" ? trackerUri.DnsSafeHost : ProxyHost;
                decision = await SessionNetworkAccess.PrepareAsync(endpoint,
                    UseTcpListener && SelectedProxyType == "None", preparation.Token);
            }
            catch (OperationCanceledException) { return; }
            finally { startPreparation = null; IsPreparing = false; }
            if (preparation.IsCancellationRequested || released || IsClosing) return;
            if (decision == SessionNetworkDecision.Cancelled)
            {
                StatusText = "Session start cancelled";
                return;
            }
            if (decision == SessionNetworkDecision.DeniedEndpoint)
            {
                StatusText = "Local network permission required";
                SetAlert(TabAlertLevel.Error, "Allow Nearby devices in Android Settings > Apps > RatioMaster.NET > Permissions to use this local tracker or proxy.");
                return;
            }
            if (decision == SessionNetworkDecision.DeniedListener)
                ShowLocalPeersUnavailable();
        }
        // A new session starts with its own engine and callbacks.
        SetAlert(TabAlertLevel.None, string.Empty);

        SetPulseStage(PulseStage.None); // user reached Start — onboarding done

        string key = string.IsNullOrEmpty(CustomKey) ? client.Key : CustomKey;
        string peerId = client.PeerID;
        if (!string.IsNullOrEmpty(CustomPeerId) && !PeerIdentityText.TryParse(CustomPeerId, out peerId))
        {
            SetAlert(TabAlertLevel.Error, "Peer ID must contain 20 single-byte characters or hex: followed by 40 hexadecimal digits.");
            return;
        }
        string port = string.IsNullOrEmpty(CustomPort) ? Rng.Next(1025, 65535).ToString() : CustomPort;
        string numWant = string.IsNullOrEmpty(CustomPeers) ? client.DefNumWant.ToString() : CustomPeers;

        client.Key = key;
        client.PeerID = peerId;
        assigningIdentity = true;
        if (string.IsNullOrEmpty(CustomKey)) keyIsGenerated = true;
        if (string.IsNullOrEmpty(CustomPeerId)) peerIdIsGenerated = true;
        CustomKey = key;
        CustomPeerId = PeerIdentityText.Format(peerId);
        assigningIdentity = false;
        CustomPort = port;
        CustomPeers = numWant;

        double finished = Math.Clamp(FinishedText.ParseDoubleOr(0), 0, 100);
        FinishedText = finished.ToString(System.Globalization.CultureInfo.InvariantCulture);

        SessionConfig cfg = new()
        {
            Client = client,
            Proxy = BuildProxy(),
            Tracker = Tracker,
            HashHex = HashHex,
            InfoHash = infoHash,
            TotalLength = totalLength,
            FinishedPercent = finished,
            Interval = IntervalText.ParseIntOr(1800),
            Port = port,
            Key = key,
            KeyIsGenerated = keyIsGenerated,
            PeerID = peerId,
            NumWant = numWant,
            PieceCount = pieceCount,
            Realistic = RealisticMode,
            ResumeUploaded = resumeUploaded,
            ResumeDownloaded = resumeDownloaded,
        };


        UploadedText = "0";
        DownloadedText = "0";
        RatioText = "0.0";
        SeedersText = "Seeders: -";
        LeechersText = "Leechers: -";
        TotalTimeText = "00:00";
        TimerText = "updating...";
        NextUpdateCountdown = Format.Countdown(Math.Clamp(cfg.Interval, 60, 86400));
        StatusText = "Running";
        // Baseline the graph at the RESUMED total (not 0): the engine reports a cumulative Uploaded that
        // already includes the resume base, so a 0 baseline would push the entire resumed total into the
        // first bar and flatten every real per-interval delta after a resume.
        lastUploaded = cfg.ResumeUploaded;
        uploadHistory.Reset(cfg.ResumeUploaded);
        GraphValues = uploadHistory.Snapshot();

        engine = CreateEngine();
        IsPaused = UploadPausedNoLeechers = false;
        IsRunning = true;
        try
        {
            engine.Start(cfg);
            resumeUploaded = resumeDownloaded = 0;
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
                SetAlert(TabAlertLevel.Warning, "An update is already in progress, or the tracker did not respond.");
        }
        finally { ManualUpdateSending = false; }
    }

    private static bool ValidRate(string text) => double.IsFinite(text.ParseDoubleOr(double.NaN))
        && text.ParseDoubleOr(-1) >= 0 && text.ParseDoubleOr(0) <= long.MaxValue / (1024.0 * 1024 * 2);

    private static void ShowLocalPeersUnavailable() => NotificationHub.Show("Local peers unavailable",
        "Public trackers remain available. To allow local peers, enable Nearby devices in Android Settings > Apps > RatioMaster.NET > Permissions.");

    partial void OnUseTcpListenerChanged(bool value)
    {
        if (value && IsRunning && SelectedProxyType == "None" && OperatingSystem.IsAndroidVersionAtLeast(37))
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
                NotificationHub.Show("Local network access", exception.Message, error: true);
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
        SelectedStopWhen = "When upload >";
        StopValue = "1000";
        StopValueVisible = true;
        StopUnit = "GiB";

        UseTcpListener = true;
        RequestScrape = true;

        SelectedProxyType = "None";
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

    [RelayCommand]
    private void RegenerateValues()
    {
        ClientProfile client = ClientCatalog.Create(SelectedFamily, SelectedVersion);
        assigningIdentity = true;
        CustomKey = client.Key;
        CustomPeerId = PeerIdentityText.Format(client.PeerID);
        keyIsGenerated = peerIdIsGenerated = true;
        assigningIdentity = false;
        if (string.IsNullOrEmpty(CustomPort))
        {
            CustomPort = Rng.Next(1025, 65535).ToString();
        }

        CustomPeers = client.DefNumWant.ToString();
        GenStatus = "Generated new values for " + SelectedFamily + " " + SelectedVersion;
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
            totalLength = (long)t.TotalLength;
            pieceCount = t.PieceCount;
            Tracker = t.Announce;
            HashHex = Format.ToHex(t.IsHybrid ? t.InfoHash : t.FullInfoHash);
            TorrentSize = Format.FileSize(totalLength);
            SmallTorrentWarning = IsSmallTorrent(totalLength);
            StatusText = "Loaded: " + t.Name;
            SetAlert(TabAlertLevel.None, string.Empty);

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
            StatusText = "Failed to load torrent: " + ex.Message;
            AppendLog("Failed to load torrent: " + ex.Message);
            SetAlert(TabAlertLevel.Error, "Failed to load torrent: " + ex.Message);
        }
    }

    public string? LastDirectory { get; private set; }

    /// <summary>Desktop path (the picker or a typed path gives a real filesystem path). Setting
    /// <see cref="TorrentFilePath"/> triggers the change hook, which loads the metadata + records the source.</summary>
    public void SetTorrentPath(string path)
    {
        if (!InputsEnabled) return;
        try
        {
            LastDirectory = Path.GetDirectoryName(path);
        }
        catch
        {
            // ignore
        }

        if (TorrentFilePath == path) OnTorrentFilePathChanged(path);
        else TorrentFilePath = path;
    }

    /// <summary>Android entry point: the picker returned content with no re-openable path, so we get the raw
    /// bytes + the display name. We show the clean name in the box, load the metadata from the bytes, and
    /// materialize a private copy so a restored session can reload it.</summary>
    public void SetTorrentContent(byte[] data, string fileName)
    {
        if (!InputsEnabled) return;
        // Show the file NAME (not the private cache path). Flag the assignment so the change hook treats it
        // as a load-in-progress, not a clear — we set the authoritative source + load from the bytes AFTER.
        suppressTorrentReset = true;
        TorrentFilePath = fileName;
        suppressTorrentReset = false;

        // Best-effort private copy for session-restore. The load below works from the bytes regardless of
        // whether this succeeds, so a read-only/full data dir never blocks selecting a torrent.
        SetTorrentSource(TryCacheTorrent(data));
        LoadTorrentMetadataFromBytes(data);
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

    internal static string? TryCacheTorrent(byte[] data, string? directory = null)
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

            PruneTorrentCache(dir, cache);
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
            StatusText = "Stopped";
            TimerText = "stopped";
            ClearTransientAlert();
            await pendingStop;
            CaptureFinalStats();
            if (engine.StopError.Length > 0) SetAlert(TabAlertLevel.Error, engine.StopError);
        }
        finally { IsTransitioning = false; }
    }

    private void ResetCounters()
    {
        resumeUploaded = resumeDownloaded = lastUploaded = lastDownloaded = 0;
        UploadedText = DownloadedText = "0 bytes";
        RatioText = "0.00";
        SeedersText = "Seeders: -";
        LeechersText = "Leechers: -";
        TotalTimeText = "00:00";
        TimerText = "idle";
        NextUpdateCountdown = "00:00:00";
        RemainingText = "0";
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
        UploadedText = Format.FileSize(stats.Uploaded);
        DownloadedText = Format.FileSize(stats.Downloaded);
        RatioText = stats.Ratio;
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
            Finished = FinishedText,
            StopWhen = SelectedStopWhen,
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
            ProxyType = SelectedProxyType,
            ProxyHost = ProxyHost,
            ProxyUser = ProxyUser,
            ProxyPass = ProxyPass,
            ProxyPort = ProxyPort,
            EnableLog = EnableLog,
            FinishedSuccessfully = HasFinished,

            // Active engines provide current counters; inactive tabs retain their saved resume totals.
            Uploaded = current?.Uploaded ?? Math.Max(lastUploaded, resumeUploaded),
            Downloaded = current?.Downloaded ?? Math.Max(lastDownloaded, resumeDownloaded),
        };
    }

    internal void ApplyState(TabState st)
    {
        TabName = st.TabName;
        LastDirectory = st.LastDirectory;

        bool knownProfile = ClientCatalog.TryCreate(st.Family, st.Version, out _);
        string? identityWarning = null;
        suppressVersionReload = true;
        SelectedFamily = knownProfile ? st.Family : ClientCatalog.DefaultFamily;
        LoadVersions(SelectedFamily);
        SelectedVersion = knownProfile ? st.Version : ClientCatalog.DefaultVersion;
        suppressVersionReload = false;

        UploadSpeed = st.UploadSpeed;
        RandomUpload = st.RandomUpload;
        DownloadSpeed = st.DownloadSpeed;
        RandomDownload = st.RandomDownload;
        IntervalText = st.Interval;
        FinishedText = st.Finished;
        SelectedStopWhen = SessionSettings.NormalizeStopCondition(st.StopWhen);
        StopValue = st.StopValue; // override the default the combo change just set
        RealisticMode = st.RealisticMode;
        UseTcpListener = st.UseTcpListener;
        RequestScrape = st.RequestScrape;
        SelectedProxyType = st.ProxyType;
        ProxyHost = st.ProxyHost;
        ProxyUser = st.ProxyUser;
        ProxyPass = st.ProxyPass;
        ProxyPort = st.ProxyPort;
        EnableLog = st.EnableLog;

        // Saved identities take precedence over profile defaults; absent provenance remains custom.
        assigningIdentity = true;
        CustomKey = st.CustomKey;
        CustomPeerId = st.CustomPeerId;
        keyIsGenerated = st.KeyIsGenerated == true;
        peerIdIsGenerated = st.PeerIdIsGenerated == true;
        if (!knownProfile)
        {
            ClientProfile replacement = ClientCatalog.Create(SelectedFamily, SelectedVersion);
            if (keyIsGenerated) CustomKey = replacement.Key;
            if (peerIdIsGenerated) CustomPeerId = PeerIdentityText.Format(replacement.PeerID);
            identityWarning = "The saved client profile is unavailable. The default profile was selected; custom identity values were retained.";
        }
        else if (st.PeerIdIsGenerated is null && st.Family == "uTorrent"
            && (st.CustomPeerId.StartsWith("-UT3600-", StringComparison.Ordinal) || st.CustomPeerId.StartsWith("-UT3550-", StringComparison.Ordinal)))
            identityWarning = "The saved peer ID differs from this profile's build format. It was retained as custom. Use Regenerate to generate this profile's identity.";
        assigningIdentity = false;
        CustomPort = st.CustomPort;
        CustomPeers = st.CustomPeers;


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
            UploadedText = Format.FileSize(lastUploaded);
            DownloadedText = Format.FileSize(lastDownloaded);
        }
        else ResetCounters();
        if (matches && !string.IsNullOrWhiteSpace(st.Tracker)) Tracker = st.Tracker;
        RatioText = lastDownloaded > 0 ? (lastUploaded / (double)lastDownloaded).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "0.00";
        HasFinished = matches && loadedTorrent is not null && st.FinishedSuccessfully;
        if (identityWarning is not null) SetAlert(TabAlertLevel.Warning, identityWarning);
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
        RatioText = s.Ratio;
        if (s.Seeders >= 0)
        {
            SeedersText = "Seeders: " + s.Seeders;
        }

        if (s.Leechers >= 0)
        {
            LeechersText = "Leechers: " + s.Leechers;
        }

        TotalTimeText = Format.Time(s.TotalRunSeconds);
        TimerText = Format.Time(s.IntervalRemaining);
        NextUpdateCountdown = Format.Countdown(s.IntervalRemaining);
        ApplyEngineAlert(s.AlertLevel, s.AlertMessage);

        IsPaused = s.IsPaused;
        UploadPausedNoLeechers = s.UploadPausedNoLeechers;
        IsAnnouncing = s.IsAnnouncing;
        StatusText = IsPaused ? "Paused by you" : UploadPausedNoLeechers ? "Upload paused: no leechers"
            : IsAnnouncing ? "Announcing…" : s.SeedMode ? "Seeding" : "Downloading";

        lastUploaded = s.Uploaded;
        lastDownloaded = s.Downloaded;
        if (uploadHistory.Sample(s.Uploaded, s.SampleElapsedSeconds)) GraphValues = uploadHistory.Snapshot();
    }

    private async Task OnEngineStoppedAsync(string reason)
    {
        bool wasStopping = IsTransitioning;
        IsTransitioning = true;
        CaptureFinalStats();
        IsRunning = false;
        IsPaused = UploadPausedNoLeechers = false;
        StatusText = reason;
        TimerText = "stopped";
        if (reason.Contains("error", StringComparison.OrdinalIgnoreCase) || reason.Contains("rejection", StringComparison.OrdinalIgnoreCase))
            SetAlert(TabAlertLevel.Error, reason);
        else ClearTransientAlert();
        if (wasStopping) return;
        try
        {
            pendingStop = engine.StopAsync(); await pendingStop;
            CaptureFinalStats();
            if (engine.StopError.Length > 0) SetAlert(TabAlertLevel.Error, engine.StopError);
        }
        finally { IsTransitioning = false; }
        if (!reason.Equals("stopped", StringComparison.OrdinalIgnoreCase))
            NotificationHub.Show(TabName, reason, reason.Contains("error", StringComparison.OrdinalIgnoreCase));
    }

    private void AppendLog(string line)
    {
        // Surface tracker errors as a toast even if the log is disabled.
        if (line.StartsWith("Tracker Error:", StringComparison.OrdinalIgnoreCase))
        {
            Post(() => NotificationHub.Show(TabName + " — tracker error", line, error: true));
        }

        if (!EnableLog)
        {
            return;
        }

        string stamp = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (terminalBuffer.Enqueue($"[{stamp}] {line}\n"))
            Post(() => { if (!released) LogText = terminalBuffer.Drain(LogText); });
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private ProxyConfig BuildProxy() => new()
    {
        Kind = SelectedProxyType switch
        {
            "HTTP" => ProxyKind.HttpConnect,
            "Socks4" => ProxyKind.Socks4,
            "Socks4a" => ProxyKind.Socks4a,
            "Socks5" => ProxyKind.Socks5,
            _ => ProxyKind.None,
        },
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
        StatusText = "Select a valid torrent file";
        SetAlert(TabAlertLevel.None, string.Empty);
    }

    private void ClearTorrentMetadata()
    {
        HasFinished = false;
        loadedTorrent = null;
        counterInfoHash = string.Empty;
        ResetCounters();
        infoHash = [];
        totalLength = 0;
        pieceCount = 0;
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
        CustomPort = string.Empty;
        RegenerateValues();
    }

    partial void OnSelectedVersionChanged(string value)
    {
        if (!suppressVersionReload && !string.IsNullOrEmpty(value))
        {
            RegenerateValues();
        }
    }

    partial void OnSelectedStopWhenChanged(string value)
    {
        switch (value)
        {
            case "When ratio >":
                StopValue = "2.0";
                StopValueVisible = true;
                StopUnit = "ratio";
                break;
            case "When leechers / seeders <":
                StopValue = "1.0";
                StopValueVisible = true;
                StopUnit = "ratio";
                break;
            case "After time:":
                StopValue = "3600";
                StopValueVisible = true;
                StopUnit = "s";
                break;
            case "When seeders <":
            case "When leechers <":
                StopValue = "10";
                StopValueVisible = true;
                StopUnit = string.Empty;
                break;
            case "When upload >":
            case "When download >":
                StopValue = "1000";
                StopValueVisible = true;
                StopUnit = "GiB";
                break;
            default:
                StopValue = string.Empty;
                StopValueVisible = false;
                StopUnit = string.Empty;
                break;
        }

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
