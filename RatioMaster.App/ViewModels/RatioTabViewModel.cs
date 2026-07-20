namespace RatioMaster.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

    private readonly RatioEngine engine;
    private Torrent? loadedTorrent;
    // Real, re-openable path to the loaded .torrent — equals TorrentFilePath on desktop, but on Android
    // (where the picker yields a content:// URI shown only by name) it points at a private materialized copy.
    // Persisted so a restored tab can reload its metadata on either platform.
    private string? torrentSourcePath;

    // Every cache copy currently referenced by SOME tab, so pruning can never evict one that is still in
    // use. Static because pruning is global over the shared cache directory.
    private static readonly HashSet<string> InUseCachePaths = new(StringComparer.OrdinalIgnoreCase);
    private byte[] infoHash = [];
    private long totalLength;
    private int pieceCount;
    private bool suppressVersionReload;
    // Set while SetTorrentContent (Android) assigns the display NAME to TorrentFilePath — that assignment
    // isn't a "clear", so the change hook must not wipe the torrent state we're about to set right after.
    private bool suppressTorrentReset;
    private long resumeUploaded;
    private long resumeDownloaded;
    private long lastUploaded;
    private long lastDownloaded;
    private readonly double[] uploadGraph = new double[GraphPoints];

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

    // ── Speeds (MB/s) ──
    // "Random" replaces both the old per-second "+ random min/max" noise AND the speed half of the old
    // single "realistic mode": it varies the rate along a smooth, slowly drifting curve (×0.55…1.15)
    // around the value below, independently per direction. Unchecked = an exact, flat rate.
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
    private string selectedStopWhen = "When uploaded >";

    [ObservableProperty]
    private string stopValue = "1000";

    [ObservableProperty]
    private bool stopValueVisible = true;

    [ObservableProperty]
    private string stopUnit = "GB";

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
    private bool alwaysNewValues = true;

    [ObservableProperty]
    private bool realisticMode = true;

    [ObservableProperty]
    private string genStatus = string.Empty;

    // ── Other settings ──
    [ObservableProperty]
    private bool useTcpListener = true;

    [ObservableProperty]
    private bool requestScrape = true;

    [ObservableProperty]
    private bool ignoreFailureReason;

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

    // ── On next update: random speed level (PERCENT of the configured MB/s, 100 = as typed) ──
    // decimal because Avalonia's NumericUpDown.Value is decimal? — binding a double would round-trip
    // through a converter and lose the spinner's exact 10-step increments.
    [ObservableProperty]
    private bool nextRandUp;

    [ObservableProperty]
    private decimal? nextRandUpMinPercent = 50;

    [ObservableProperty]
    private decimal? nextRandUpMaxPercent = 150;

    [ObservableProperty]
    private bool nextRandDown;

    [ObservableProperty]
    private decimal? nextRandDownMinPercent = 50;

    [ObservableProperty]
    private decimal? nextRandDownMaxPercent = 150;

    // ── Log ──
    [ObservableProperty]
    private bool enableLog = true;

    [ObservableProperty]
    private string logText = string.Empty;

    // ── Live stats ──
    [ObservableProperty]
    private string uploadedText = "0";

    [ObservableProperty]
    private string downloadedText = "0";

    [ObservableProperty]
    private string ratioText = "0.0";

    [ObservableProperty]
    private string seedersText = "Seeders: -";

    [ObservableProperty]
    private string leechersText = "Leechers: -";

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

        engine = new RatioEngine(this);
        engine.Log += s => Post(() => AppendLog(s));
        engine.Stats += s => Post(() => ApplyStats(s));
        engine.Stopped += r => Post(() => OnEngineStopped(r));
    }

    public ObservableCollection<string> ClientFamilies { get; } = [];

    public ObservableCollection<string> Versions { get; } = [];

    public ObservableCollection<string> StopWhenOptions { get; } =
    [
        "Never", "When ratio >", "When uploaded >", "When downloaded >", "After time:", "When seeders <", "When leechers <",
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

    public bool InputsEnabled => !IsRunning;

    // ══════════════════════ IEngineHost ══════════════════════
    bool IEngineHost.UseTcpListener => UseTcpListener;

    bool IEngineHost.RequestScrape => RequestScrape;

    bool IEngineHost.IgnoreFailureReason => IgnoreFailureReason;

    // Parsed as DOUBLE (ParseDoubleOr also accepts a comma decimal separator): the field is MB/s, and an
    // integer-only parse silently returned 0 for a perfectly reasonable "2.5" or "0,5" — i.e. the tool
    // uploaded nothing at all, with no hint as to why. Sub-1 MB/s rates are now expressible too.
    long IEngineHost.UploadRateBytes => (long)(UploadSpeed.ParseDoubleOr(0) * 1024 * 1024);

    long IEngineHost.DownloadRateBytes => (long)(DownloadSpeed.ParseDoubleOr(0) * 1024 * 1024);

    bool IEngineHost.RandomUploadEnabled => RandomUpload;

    bool IEngineHost.RandomDownloadEnabled => RandomDownload;

    bool IEngineHost.NextRandUpEnabled => NextRandUp;

    double IEngineHost.NextRandUpMinPercent => (double)(NextRandUpMinPercent ?? 50m);

    double IEngineHost.NextRandUpMaxPercent => (double)(NextRandUpMaxPercent ?? 150m);

    bool IEngineHost.NextRandDownEnabled => NextRandDown;

    double IEngineHost.NextRandDownMinPercent => (double)(NextRandDownMinPercent ?? 50m);

    double IEngineHost.NextRandDownMaxPercent => (double)(NextRandDownMaxPercent ?? 150m);

    string IEngineHost.StopWhen => SelectedStopWhen;

    string IEngineHost.StopValue => StopValue;

    void IEngineHost.ApplyAlert(EngineAlert level, string message) => Post(() =>
    {
        AlertLevel = level switch
        {
            EngineAlert.Ok => TabAlertLevel.Ok,
            EngineAlert.Warning => TabAlertLevel.Warning,
            EngineAlert.Error => TabAlertLevel.Error,
            _ => TabAlertLevel.None,
        };
        AlertMessage = message;
    });

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
        _ => IsRunning ? TabDotState.Running : TabDotState.Idle,
    };

    partial void OnAlertLevelChanged(TabAlertLevel value) => OnPropertyChanged(nameof(DotState));

    // ══════════════════════ Commands ══════════════════════
    [RelayCommand(CanExecute = nameof(InputsEnabled))]
    private void Start()
    {
        if (IsRunning)
        {
            return;
        }

        if (loadedTorrent == null || string.IsNullOrEmpty(Tracker) || string.IsNullOrEmpty(HashHex))
        {
            StatusText = "Select a valid torrent file first";
            AppendLog("Please select a valid torrent file!");
            SetAlert(TabAlertLevel.Error, "No torrent loaded - select a .torrent file first.");
            return;
        }

        if (!Uri.TryCreate(Tracker, UriKind.Absolute, out _))
        {
            StatusText = "Invalid tracker URL";
            AppendLog("Invalid tracker URL: " + Tracker);
            SetAlert(TabAlertLevel.Error, "Invalid tracker URL: " + Tracker);
            return;
        }

        // Starting clears whatever the previous run left on the line; the first announce sets the real state.
        SetAlert(TabAlertLevel.None, string.Empty);

        SetPulseStage(PulseStage.None); // user reached Start — onboarding done

        ClientProfile client = ClientCatalog.Create(SelectedFamily, SelectedVersion);

        string key = string.IsNullOrEmpty(CustomKey) ? client.Key : CustomKey;
        string peerId = string.IsNullOrEmpty(CustomPeerId) ? client.PeerID : CustomPeerId;
        string port = string.IsNullOrEmpty(CustomPort) ? Rng.Next(1025, 65535).ToString() : CustomPort;
        string numWant = string.IsNullOrEmpty(CustomPeers) ? client.DefNumWant.ToString() : CustomPeers;

        client.Key = key;
        client.PeerID = peerId;
        CustomKey = key;
        CustomPeerId = peerId;
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
            PeerID = peerId,
            NumWant = numWant,
            PieceCount = pieceCount,
            Realistic = RealisticMode,
            ResumeUploaded = resumeUploaded,
            ResumeDownloaded = resumeDownloaded,
        };

        // Resume is single-shot: consumed on the next Start, cleared afterwards.
        resumeUploaded = 0;
        resumeDownloaded = 0;

        UploadedText = "0";
        DownloadedText = "0";
        RatioText = "0.0";
        SeedersText = "Seeders: -";
        LeechersText = "Leechers: -";
        TotalTimeText = "00:00";
        TimerText = "updating...";
        StatusText = "Running";
        // Baseline the graph at the RESUMED total (not 0): the engine reports a cumulative Uploaded that
        // already includes the resume base, so a 0 baseline would push the entire resumed total into the
        // first bar and flatten every real per-interval delta after a resume.
        lastUploaded = cfg.ResumeUploaded;
        Array.Clear(uploadGraph);
        GraphValues = (double[])uploadGraph.Clone();

        IsRunning = true;
        engine.Start(cfg);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        engine.Stop();
        IsRunning = false;
        StatusText = "Stopped";
        TimerText = "stopped";
        ClearTransientAlert();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void ManualUpdate() => engine.ManualUpdate();

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
        SelectedStopWhen = "When uploaded >";
        StopValue = "1000";
        StopValueVisible = true;
        StopUnit = "GB";

        UseTcpListener = true;
        RequestScrape = true;
        IgnoreFailureReason = false;

        SelectedProxyType = "None";
        ProxyHost = string.Empty;
        ProxyUser = string.Empty;
        ProxyPass = string.Empty;
        ProxyPort = string.Empty;

        NextRandUp = false;
        NextRandUpMinPercent = 50;
        NextRandUpMaxPercent = 150;
        NextRandDown = false;
        NextRandDownMinPercent = 50;
        NextRandDownMaxPercent = 150;

        EnableLog = true;
        AlwaysNewValues = true;
        RealisticMode = true;
        CustomPort = string.Empty;
        CustomPeers = string.Empty;
        RegenerateValues();

        // Wipe the portable session file and any pending resume.
        resumeUploaded = 0;
        resumeDownloaded = 0;
        SessionStore.Delete();
    }

    [RelayCommand]
    private void RegenerateValues()
    {
        ClientProfile client = ClientCatalog.Create(SelectedFamily, SelectedVersion);
        CustomKey = client.Key;
        CustomPeerId = client.PeerID;
        if (string.IsNullOrEmpty(CustomPort))
        {
            CustomPort = Rng.Next(1025, 65535).ToString();
        }

        CustomPeers = client.DefNumWant.ToString();
        GenStatus = "Generated new values for " + SelectedFamily + " " + SelectedVersion;
    }

    [RelayCommand]
    private void ClearLog() => LogText = string.Empty;

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

            // Resume counters belong to the torrent they were captured for. Loading a DIFFERENT torrent
            // must drop them, otherwise the first announce reports the previous torrent's totals — which
            // can exceed the new torrent's own size, an obviously bogus figure to a tracker.
            byte[] newHash = t.InfoHash;
            if (infoHash.Length > 0 && !infoHash.AsSpan().SequenceEqual(newHash))
            {
                resumeUploaded = 0;
                resumeDownloaded = 0;
                lastUploaded = 0;
                lastDownloaded = 0;
            }

            loadedTorrent = t;
            infoHash = newHash;
            totalLength = (long)t.TotalLength;
            pieceCount = t.PieceCount;
            Tracker = t.Announce;
            HashHex = Format.ToHex(infoHash);
            TorrentSize = Format.FileSize(totalLength);
            StatusText = "Loaded: " + t.Name;

            // Torrent defined → move the pulse hint onto the Stop-value box.
            if (pulseStage == PulseStage.TorrentFile)
            {
                SetPulseStage(PulseStage.StopValue);
            }
        }
        catch (Exception ex)
        {
            // Roll the displayed metadata back too. Leaving the previous torrent's tracker/hash/size on
            // screen next to the new file's path reads as "loaded fine", and Start would then refuse with
            // a message that contradicts what the panel shows.
            loadedTorrent = null;
            infoHash = [];
            totalLength = 0;
            pieceCount = 0;
            Tracker = string.Empty;
            HashHex = string.Empty;
            TorrentSize = string.Empty;
            AppendLog("Failed to load torrent: " + ex.Message);
            SetAlert(TabAlertLevel.Error, "Failed to load torrent: " + ex.Message);
        }
    }

    public string? LastDirectory { get; private set; }

    /// <summary>Desktop path (the picker or a typed path gives a real filesystem path). Setting
    /// <see cref="TorrentFilePath"/> triggers the change hook, which loads the metadata + records the source.</summary>
    public void SetTorrentPath(string path)
    {
        try
        {
            LastDirectory = Path.GetDirectoryName(path);
        }
        catch
        {
            // ignore
        }

        TorrentFilePath = path;
    }

    /// <summary>Android entry point: the picker returned content with no re-openable path, so we get the raw
    /// bytes + the display name. We show the clean name in the box, load the metadata from the bytes, and
    /// materialize a private copy so a restored session can reload it.</summary>
    public void SetTorrentContent(byte[] data, string fileName)
    {
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

    private static string? TryCacheTorrent(byte[] data)
    {
        try
        {
            string dir = TorrentCacheDir();
            Directory.CreateDirectory(dir);

            // Key the private copy by CONTENT (SHA-1 of the bytes), NOT the display name: two different
            // torrents that happen to share a name (e.g. "download.torrent") must not overwrite each other's
            // cache — else a restored tab would silently reload the wrong torrent and announce the wrong
            // info-hash. Identical re-picks map to the same file (dedup). Hex is always a safe filename.
            string cache = Path.Combine(dir, Convert.ToHexString(SHA1.HashData(data)) + ".torrent");
            if (!File.Exists(cache))
            {
                File.WriteAllBytes(cache, data);
            }
            else
            {
                // Same content re-picked: refresh the timestamp so pruning treats it as recent. In its OWN
                // try — a failed touch (read-only FS, clock issue) must not fall through to the outer catch
                // and discard a cache path that is perfectly valid and already on disk.
                try
                {
                    File.SetLastWriteTimeUtc(cache, DateTime.UtcNow);
                }
                catch
                {
                    // cosmetic only: the file just sorts as older for pruning purposes
                }
            }

            PruneTorrentCache(dir, cache);
            return cache;
        }
        catch
        {
            return null; // no private copy → the current session still works, just no reload after restart
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
                InUseCachePaths.Remove(torrentSourcePath);
            }

            torrentSourcePath = path;

            if (!string.IsNullOrEmpty(path))
            {
                InUseCachePaths.Add(path);
            }
        }
    }

    /// <summary>
    /// Keep the private torrent cache bounded. Every distinct torrent ever picked leaves a copy behind
    /// (that is the point — a restored tab reloads from it), but nothing deleted them, so the app-private
    /// directory grew forever. Keep the most recently used ones, and never delete the file we just wrote.
    /// </summary>
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
                protectedPaths = new HashSet<string>(InUseCachePaths, StringComparer.OrdinalIgnoreCase);
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

    public void StopIfRunning()
    {
        if (IsRunning)
        {
            engine.Stop();
            IsRunning = false;
        }
    }

    // ══════════════════════ Session persistence ══════════════════════
    internal TabState CaptureState() => new()
    {
        TabName = TabName,
        TorrentFilePath = TorrentFilePath,
        TorrentSourcePath = torrentSourcePath ?? string.Empty,
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
        CustomKey = CustomKey,
        CustomPeerId = CustomPeerId,
        CustomPort = CustomPort,
        CustomPeers = CustomPeers,
        AlwaysNewValues = AlwaysNewValues,
        RealisticMode = RealisticMode,
        UseTcpListener = UseTcpListener,
        RequestScrape = RequestScrape,
        IgnoreFailureReason = IgnoreFailureReason,
        ProxyType = SelectedProxyType,
        ProxyHost = ProxyHost,
        ProxyUser = ProxyUser,
        ProxyPass = ProxyPass,
        ProxyPort = ProxyPort,
        NextRandUp = NextRandUp,
        NextRandUpMinPercent = NextRandUpMinPercent ?? 50m,
        NextRandUpMaxPercent = NextRandUpMaxPercent ?? 150m,
        NextRandDown = NextRandDown,
        NextRandDownMinPercent = NextRandDownMinPercent ?? 50m,
        NextRandDownMaxPercent = NextRandDownMaxPercent ?? 150m,
        EnableLog = EnableLog,

        // Persist whichever counter is authoritative. `last*` only becomes meaningful once the engine has
        // run (Start seeds it from the resume value and it grows from there); `resume*` holds what was
        // restored from the previous session and is zeroed once consumed by Start. Saving `last*` alone
        // meant a tab that was restored but never started wrote 0 back over its own totals on the next
        // save — silently destroying the accumulated ratio.
        Uploaded = Math.Max(lastUploaded, resumeUploaded),
        Downloaded = Math.Max(lastDownloaded, resumeDownloaded),
    };

    internal void ApplyState(TabState st)
    {
        TabName = st.TabName;

        suppressVersionReload = true;
        SelectedFamily = st.Family;
        LoadVersions(st.Family);
        SelectedVersion = st.Version;
        suppressVersionReload = false;

        UploadSpeed = st.UploadSpeed;
        RandomUpload = st.RandomUpload;
        DownloadSpeed = st.DownloadSpeed;
        RandomDownload = st.RandomDownload;
        IntervalText = st.Interval;
        FinishedText = st.Finished;
        SelectedStopWhen = st.StopWhen;
        StopValue = st.StopValue; // override the default the combo change just set
        AlwaysNewValues = st.AlwaysNewValues;
        RealisticMode = st.RealisticMode;
        UseTcpListener = st.UseTcpListener;
        RequestScrape = st.RequestScrape;
        IgnoreFailureReason = st.IgnoreFailureReason;
        SelectedProxyType = st.ProxyType;
        ProxyHost = st.ProxyHost;
        ProxyUser = st.ProxyUser;
        ProxyPass = st.ProxyPass;
        ProxyPort = st.ProxyPort;
        NextRandUp = st.NextRandUp;
        NextRandUpMinPercent = st.NextRandUpMinPercent;
        NextRandUpMaxPercent = st.NextRandUpMaxPercent;
        NextRandDown = st.NextRandDown;
        NextRandDownMinPercent = st.NextRandDownMinPercent;
        NextRandDownMaxPercent = st.NextRandDownMaxPercent;
        EnableLog = st.EnableLog;

        // Custom values last so they win over any regeneration triggered above.
        CustomKey = st.CustomKey;
        CustomPeerId = st.CustomPeerId;
        CustomPort = st.CustomPort;
        CustomPeers = st.CustomPeers;

        resumeUploaded = st.Uploaded;
        resumeDownloaded = st.Downloaded;

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
    }

    // ══════════════════════ Engine callbacks (already on UI thread) ══════════════════════
    private void ApplyStats(EngineStats s)
    {
        // A Stats event queued just before a manual Stop must not run after OnEngineStopped and rewrite the
        // "stopped" UI back to "Seeding…". IsRunning is set true before the engine's first EmitStats, so no
        // legitimate update is dropped.
        if (!IsRunning)
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

        // Status hint while idling between (jittered) announces.
        StatusText = s.IntervalRemaining > 0
            ? $"Seeding — next announce in {Format.Time(s.IntervalRemaining)} (jittered)"
            : "Announcing…";

        // Push instantaneous upload rate (bytes/s) into the live graph.
        long delta = s.Uploaded - lastUploaded;
        lastUploaded = s.Uploaded;
        lastDownloaded = s.Downloaded;
        if (delta < 0)
        {
            delta = 0;
        }

        Array.Copy(uploadGraph, 1, uploadGraph, 0, GraphPoints - 1);
        uploadGraph[GraphPoints - 1] = delta;
        GraphValues = (double[])uploadGraph.Clone();
    }

    private void OnEngineStopped(string reason)
    {
        IsRunning = false;
        StatusText = reason;
        TimerText = "stopped";
        ClearTransientAlert();

        // Anything other than a plain user Stop is worth a toast (stop condition / error).
        if (!reason.Equals("stopped", StringComparison.OrdinalIgnoreCase))
        {
            NotificationHub.Show(TabName, reason, reason.Contains("error", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Cap the log buffer (~100 KB) so a long-running session can't grow LogText without bound.
    private const int LogCap = 100_000;

    private void AppendLog(string line)
    {
        // Surface tracker errors as a toast even if the log is disabled.
        if (line.StartsWith("Tracker Error:", StringComparison.OrdinalIgnoreCase))
        {
            NotificationHub.Show(TabName + " — tracker error", line, error: true);
        }

        if (!EnableLog)
        {
            return;
        }

        string stamp = DateTime.Now.ToString("HH:mm:ss");
        LogText += $"[{stamp}] {line}\n";

        // Bound the buffer so a multi-day session can't grow LogText without limit (O(n^2) reallocation +
        // an ever-larger bound TextBox). Keep roughly the last LogCap chars, trimmed at a line boundary.
        if (LogText.Length > LogCap)
        {
            int cut = LogText.IndexOf('\n', LogText.Length - LogCap);
            LogText = cut >= 0 ? LogText[(cut + 1)..] : LogText[^LogCap..];
        }
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
    partial void OnTorrentFilePathChanged(string value)
    {
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

        // Android's SetTorrentContent assigns the display NAME here and loads from bytes immediately after —
        // that isn't a clear, so leave the state alone (the flag is set only around that assignment).
        if (suppressTorrentReset)
        {
            return;
        }

        // The box no longer names a loadable torrent (emptied, or an invalid/removed path). Drop the loaded
        // torrent + its persisted source so a removed torrent isn't silently resurrected on the next launch.
        SetTorrentSource(null);
        loadedTorrent = null;
        infoHash = [];
        Tracker = string.Empty;
        HashHex = string.Empty;
        TorrentSize = string.Empty;
    }

    partial void OnIsRunningChanged(bool value)
    {
        // Drives the Android foreground service (no-op on desktop): without it a backgrounded session is
        // throttled by Doze and eventually killed with the process.
        if (value)
        {
            SessionActivity.Entered();
        }
        else
        {
            SessionActivity.Exited();
        }

        RaisePulse();
        OnPropertyChanged(nameof(InputsEnabled));
        OnPropertyChanged(nameof(DotState)); // idle ⇄ running changes the dot when there's no alert
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ManualUpdateCommand.NotifyCanExecuteChanged();
        SetDefaultsCommand.NotifyCanExecuteChanged();
    }

    partial void OnStopUnitChanged(string value) => OnPropertyChanged(nameof(StopUnitVisible));

    partial void OnSelectedFamilyChanged(string value)
    {
        suppressVersionReload = true;
        LoadVersions(value);
        SelectedVersion = Versions.Count > 0 ? Versions[0] : string.Empty;
        suppressVersionReload = false;
        if (AlwaysNewValues)
        {
            CustomPort = string.Empty;
            RegenerateValues();
        }
    }

    partial void OnSelectedVersionChanged(string value)
    {
        if (!suppressVersionReload && AlwaysNewValues && !string.IsNullOrEmpty(value))
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
            case "When uploaded >":
            case "When downloaded >":
                StopValue = "1000";
                StopValueVisible = true;
                StopUnit = "GB";
                break;
            default:
                StopValue = string.Empty;
                StopValueVisible = false;
                StopUnit = string.Empty;
                break;
        }

        RaisePulse(); // StopValueVisible affects StopValuePulsing
    }
}

internal enum PulseStage
{
    TorrentFile,
    StopValue,
    StartButton,
    None,
}
