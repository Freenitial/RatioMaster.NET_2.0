namespace RatioMaster.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using RatioMaster.BitTorrent;
using RatioMaster.Localization;
using RatioMaster.Models;
using RatioMaster.Engine;
using RatioMaster.Services;

/// <summary>A .torrent to open: a local path on desktop, or bytes and a display name from Android's picker.</summary>
internal sealed record TorrentSource(string Name, string? Path = null, byte[]? Data = null);

/// <summary>An entry of the language list; an empty code follows the system language.</summary>
public sealed record LanguageOption(string Code, string Label)
{
    public override string ToString() => Label;
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Adding more torrents than this at once asks for confirmation first.</summary>
    internal const int BatchConfirmationThreshold = 20;

    [ObservableProperty]
    private RatioTabViewModel? selectedTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTabCommand))]
    [NotifyPropertyChangedFor(nameof(CanAddTab))]
    private bool isShuttingDown;

    /// <summary>Number of torrents waiting for the user to confirm a large addition (0 = no question).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchQuestion))]
    [NotifyPropertyChangedFor(nameof(HasPendingBatch))]
    private int pendingBatchCount;

    public bool HasPendingBatch => PendingBatchCount > 0;

    private int tabCounter;
    private readonly bool persistenceEnabled;
    private bool initializing = true;
    private bool saveQueued;
    private bool saveFailureReported;
    private bool changingLanguage;
    private IReadOnlyList<TorrentSource>? pendingBatch;
    private RatioTabViewModel? pendingTarget;
    private readonly string? catalogError;
    private readonly SessionLoadWarning loadWarning;

    internal WindowPlacement? WindowPlacement { get; set; }

    /// <summary>The startup warning is shown once per process, not again by a view rebuilt for another language.</summary>
    internal bool StartupWarningShown { get; set; }

    /// <summary>Problems found while restoring settings, in the interface language.</summary>
    internal string? StartupWarning => string.Join("\n", new[]
    {
        catalogError is null ? null : Strings.CatalogIgnored(catalogError),
        loadWarning switch
        {
            SessionLoadWarning.RestoredBackup => Strings.SessionRestoredBackup,
            SessionLoadWarning.Unreadable => Strings.SessionUnreadable,
            SessionLoadWarning.SharedTemporary => Strings.SessionSharedTemporary,
            _ => null,
        },
    }.Where(value => !string.IsNullOrEmpty(value))) is { Length: > 0 } text ? text : null;

    internal CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;

    public bool HasActiveSessions => Tabs.Any(tab => tab.IsRunning || tab.IsPreparing || tab.IsTransitioning || tab.IsClosing);
    public bool CanAddTab => !IsShuttingDown && Tabs.Count < SessionRepository.MaxTabs;
    public string BatchQuestion => Strings.BatchConfirm(PendingBatchCount);

    public MainWindowViewModel(bool usePersistence = true)
    {
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddTab));
            AddTabCommand.NotifyCanExecuteChanged();
        };
        persistenceEnabled = usePersistence;
        if (usePersistence) catalogError = ClientCatalog.LoadUserProfiles(Path.Combine(SessionStore.SettingsDirectory, "clients.json"));
        SessionData? session = usePersistence ? SessionStore.Load() : null;
        if (usePersistence) loadWarning = SessionStore.LoadWarning;
        if (usePersistence) Language.Apply(session?.Language);
        if (usePersistence) ListeningPortBook.Load(session?.ClientPorts);
        RefreshLanguages();
        WindowPlacement = session?.Window;
        CloseBehavior = session?.CloseBehavior ?? CloseBehavior.Ask;
        int formatVersion = session?.LoadedFormatVersion ?? SessionData.CurrentFormatVersion;
        if (session?.Tabs is { Count: > 0 } saved)
        {
            foreach (TabState st in saved)
            {
                CreateTab(select: true).ApplyState(st, formatVersion);
            }
        }
        else
        {
            CreateTab(select: true);
        }
        ListeningPortBook.Changed += OnPortBookChanged;
        Language.Changed += OnLanguageChanged;
        Language.PreferenceChanged += OnLanguagePreferenceChanged;
        initializing = false;
        if (formatVersion < SessionData.CurrentFormatVersion) RequestSave();
    }

    /// <summary>Leaves the process-wide events; a window model lives as long as the process, tests create several.</summary>
    public void Dispose()
    {
        ListeningPortBook.Changed -= OnPortBookChanged;
        Language.Changed -= OnLanguageChanged;
        Language.PreferenceChanged -= OnLanguagePreferenceChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>Request an immediate stop for every tab.</summary>
    public void StopAll()
    {
        foreach (RatioTabViewModel t in Tabs)
        {
            t.StopIfRunning();
        }
    }

    /// <summary>Wait for every tab to finish stopping and finalize its counters.</summary>
    public async Task StopAllAsync()
    {
        Task[] stops = Tabs.ToArray().Select(StopTabAsync).ToArray();
        await Task.WhenAll(stops);
    }

    private static async Task StopTabAsync(RatioTabViewModel tab) => await tab.StopIfRunningAsync();

    /// <summary>Persist all tabs and the global close preference to the session file.</summary>
    public void SaveSession()
    {
        if (persistenceEnabled) SessionStore.Save(CaptureSession());
    }

    internal SessionData CaptureSession()
    {
        SessionData data = new()
        {
            CloseBehavior = CloseBehavior, Window = WindowPlacement,
            Language = Language.Preference, ClientPorts = ListeningPortBook.Snapshot(),
        };
        foreach (RatioTabViewModel t in Tabs)
        {
            data.Tabs.Add(t.CaptureState());
        }

        return data;
    }

    internal void RequestSave()
    {
        if (!persistenceEnabled || initializing || IsShuttingDown || saveQueued) return;
        saveQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            saveQueued = false;
            if (IsShuttingDown) return;
            try
            {
                await SessionStore.SaveAsync(CaptureSession());
                saveFailureReported = false;
            }
            catch (Exception ex)
            {
                if (!saveFailureReported) NotificationHub.Show(Strings.SaveFailedTitle, ex.Message, error: true);
                saveFailureReported = true;
            }
        }, DispatcherPriority.Background);
    }

    public ObservableCollection<RatioTabViewModel> Tabs { get; } = [];

    public string Title => Strings.WindowTitle(AppInfo.Version);

    public string LocalIp => NetInfo.GetLocalIp();

    [RelayCommand(CanExecute = nameof(CanAddTab))]
    private void AddTab()
    {
        if (!CanAddTab)
        {
            return;
        }

        // Opening another tab disables onboarding pulsing for all tabs.
        RatioTabViewModel.DisablePulsingGlobally();
        CreateTab(select: true);
        foreach (RatioTabViewModel t in Tabs)
        {
            t.RefreshPulse();
        }
    }

    private RatioTabViewModel CreateTab(bool select)
    {
        tabCounter++;
        RatioTabViewModel tab = new($"RM {tabCounter}") { FindTwin = FindTwin };
        tab.PropertyChanged += OnTabPropertyChanged;
        Tabs.Add(tab);
        if (select) SelectedTab = tab;
        OnPropertyChanged(nameof(HasActiveSessions));
        RequestSave();
        return tab;
    }

    /// <summary>
    /// The other tab announcing the same torrent (wire hash) to the same tracker: to the tracker the two would be two
    /// peers of one account. With <paramref name="activeOnly"/>, only a tab that is running or changing state counts.
    /// </summary>
    internal RatioTabViewModel? FindTwin(RatioTabViewModel tab, bool activeOnly)
    {
        if (!tab.HasTorrent) return null;
        string hash = tab.AnnouncedHash, trackerIdentity = tab.TrackerIdentity;
        return Tabs.FirstOrDefault(other => !ReferenceEquals(other, tab) && other.HasTorrent
            && other.AnnouncedHash == hash && other.TrackerIdentity == trackerIdentity
            && (!activeOnly || other.IsRunning || other.IsTransitioning || other.IsPreparing));
    }

    // ══════════════════════ Several torrents at once ══════════════════════

    /// <summary>Opens torrents and folders; above <see cref="BatchConfirmationThreshold"/> the user confirms first.</summary>
    internal void RequestAddTorrents(IReadOnlyList<TorrentSource> sources, RatioTabViewModel? target)
    {
        if (IsShuttingDown || sources.Count == 0) return;

        // Files arriving while a large addition waits join it, so the one answer covers all of them.
        if (pendingBatch is not null)
        {
            pendingBatch = [.. pendingBatch, .. sources];
            PendingBatchCount = pendingBatch.Count;
            return;
        }
        if (sources.Count > BatchConfirmationThreshold)
        {
            pendingBatch = sources;
            pendingTarget = target;
            PendingBatchCount = sources.Count;
            return;
        }
        AddTorrents(sources, target);
    }

    [RelayCommand]
    private void ConfirmBatch()
    {
        IReadOnlyList<TorrentSource>? batch = pendingBatch;
        RatioTabViewModel? target = pendingTarget;
        CancelBatch();
        if (batch is not null) AddTorrents(batch, target);
    }

    [RelayCommand]
    private void CancelBatch()
    {
        pendingBatch = null;
        pendingTarget = null;
        PendingBatchCount = 0;
    }

    /// <summary>
    /// Expands folders to the .torrent files they contain, sorted by name, without descending further. Paths become
    /// absolute: a relative command-line path would not be found again from another working directory.
    /// </summary>
    internal static IReadOnlyList<TorrentSource> ExpandPaths(IEnumerable<string> paths)
    {
        List<TorrentSource> result = [];
        foreach (string given in paths)
        {
            try
            {
                string path = Path.GetFullPath(given);
                if (Directory.Exists(path))
                {
                    result.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                        .Where(file => file.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Select(file => new TorrentSource(Path.GetFileName(file), file)));
                }
                else if (path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new TorrentSource(Path.GetFileName(path), path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return result;
    }

    /// <summary>
    /// The first torrent fills <paramref name="target"/> when it is still empty, each other one opens a new tab.
    /// A torrent already open for the same tracker, or repeated in the batch, is skipped; one summary is shown.
    /// </summary>
    internal BatchResult AddTorrents(IReadOnlyList<TorrentSource> sources, RatioTabViewModel? target)
    {
        HashSet<string> seen = new(Tabs.Where(tab => tab.HasTorrent).Select(tab => tab.AnnouncedHash + " " + tab.TrackerIdentity), StringComparer.Ordinal);
        int added = 0, duplicates = 0, invalid = 0, overLimit = 0;
        RatioTabViewModel? last = null;
        bool reuseTarget = target is { HasTorrent: false, InputsEnabled: true } && Tabs.Contains(target);
        foreach (TorrentSource source in sources)
        {
            Torrent torrent;
            try
            {
                if (source.Data is not null)
                {
                    using MemoryStream stream = new(source.Data, writable: false);
                    torrent = new Torrent(stream);
                }
                else torrent = new Torrent(source.Path!);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or TorrentException
                or InvalidDataException or ArgumentException or OverflowException)
            {
                invalid++;
                continue;
            }
            string identity = Convert.ToHexStringLower(torrent.InfoHash) + " " + RatioTabViewModel.NormalizeTracker(torrent.Announce);
            if (!seen.Add(identity)) { duplicates++; continue; }
            RatioTabViewModel tab;
            if (reuseTarget) { tab = target!; reuseTarget = false; }
            else if (Tabs.Count >= SessionRepository.MaxTabs) { overLimit++; continue; }
            else tab = CreateTab(select: false);
            // The parsed torrent is handed over: no second read, and any file the user chose is accepted.
            if (source.Path is not null) tab.OpenTorrent(source.Path, torrent);
            else tab.OpenTorrent(source.Data!, source.Name, torrent, pruneCache: false);
            added++;
            last = tab;
        }
        if (sources.Any(source => source.Data is not null)) RatioTabViewModel.PruneTorrentCache();
        if (added > 1) RatioTabViewModel.DisablePulsingGlobally();
        foreach (RatioTabViewModel tab in Tabs) tab.RefreshPulse();
        if (last is not null) SelectedTab = last;
        BatchResult result = new(added, duplicates, invalid, overLimit);
        if (sources.Count > 1 || duplicates + invalid + overLimit > 0)
            NotificationHub.Show(Strings.BatchTitle, Strings.BatchSummary(added, duplicates, invalid, overLimit), error: added == 0);
        RequestSave();
        return result;
    }

    internal readonly record struct BatchResult(int Added, int Duplicates, int Invalid, int OverLimit);

    [RelayCommand]
    private void SelectTab(RatioTabViewModel? tab)
    {
        if (!IsShuttingDown && tab != null && Tabs.Contains(tab))
        {
            SelectedTab = tab;
        }
    }

    [RelayCommand]
    private async Task CloseTabAsync(RatioTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (IsShuttingDown || tab == null || Tabs.Count <= 1 || !Tabs.Contains(tab))
        {
            return;
        }

        tab.IsClosing = true;
        try
        {
            await StopTabAsync(tab);
            if (IsShuttingDown || Tabs.Count <= 1 || !Tabs.Contains(tab))
            {
                return;
            }

            int index = Tabs.IndexOf(tab);
            bool closingSelected = ReferenceEquals(tab, SelectedTab);
            tab.PropertyChanged -= OnTabPropertyChanged;
            Tabs.Remove(tab);

            // Preserve the current selection when a different tab finishes closing.
            if (closingSelected)
            {
                SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
            }

            OnPropertyChanged(nameof(HasActiveSessions));
            tab.ReleaseResources();
            RequestSave();
        }
        catch (Exception ex)
        {
            NotificationHub.Show(Strings.CloseTabFailedTitle, ex.Message, error: true);
        }
        finally { if (Tabs.Contains(tab)) tab.IsClosing = false; }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "TabName" or "TorrentFilePath" or "Tracker" or "HashHex"
            or "UploadSpeed" or "DownloadSpeed" or "RandomUpload" or "RandomDownload"
            or "IntervalText" or "FinishedText" or "SelectedStopWhen" or "StopValue"
            or "SelectedFamily" or "SelectedVersion" or "CustomKey" or "CustomPeerId"
            or "CustomPort" or "CustomPeers" or "RealisticMode" or "UseTcpListener" or "RequestScrape"
            or "SelectedProxyKind" or "ProxyHost" or "ProxyUser" or "ProxyPass" or "ProxyPort"
            or "EnableLog" or "IsRunning" or "IsPaused" or "IsTransitioning" or "HasFinished" or "CountersRevision") RequestSave();
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(RatioTabViewModel.IsRunning) or nameof(RatioTabViewModel.IsPreparing)
                or nameof(RatioTabViewModel.IsTransitioning) or nameof(RatioTabViewModel.IsClosing))
        {
            OnPropertyChanged(nameof(HasActiveSessions));
        }
    }

    partial void OnSelectedTabChanged(RatioTabViewModel? oldValue, RatioTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
    }

    private void OnPortBookChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (RatioTabViewModel tab in Tabs) tab.OnPortBookChanged();
            RequestSave();
        });
    }

    // ══════════════════════ Language ══════════════════════

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    [ObservableProperty]
    private LanguageOption? selectedLanguage;

    private void RefreshLanguages()
    {
        changingLanguage = true;
        Languages.Clear();
        Languages.Add(new LanguageOption(Language.SystemPreference, Strings.LanguageSystem(Language.SystemLanguage.NativeName)));
        foreach (LanguageInfo language in Language.All)
            Languages.Add(new LanguageOption(language.Code, language == Language.English ? language.NativeName : language.NativeName + " — " + language.EnglishName));
        SelectedLanguage = Languages.First(option => option.Code == Language.Preference);
        changingLanguage = false;
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (changingLanguage || value is null) return;
        if (!Language.Apply(value.Code)) NotificationHub.Show(Strings.LanguageTitle, Strings.LanguageUnavailable, error: true);
        RequestSave();
    }

    private void OnLanguagePreferenceChanged()
    {
        RefreshLanguages();
        RequestSave();
    }

    private void OnLanguageChanged()
    {
        RefreshLanguages();
        foreach (RatioTabViewModel tab in Tabs) tab.RefreshLocalizedOptions();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(BatchQuestion));
        UpdateMessage = updateText();
    }
}

internal static class AppInfo
{
    internal static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
