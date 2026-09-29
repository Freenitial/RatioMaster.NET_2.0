namespace RatioMaster.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using RatioMaster.Models;
using RatioMaster.Engine;
using RatioMaster.Services;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private RatioTabViewModel? selectedTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTabCommand))]
    [NotifyPropertyChangedFor(nameof(CanAddTab))]
    private bool isShuttingDown;

    private int tabCounter;
    private readonly bool persistenceEnabled;
    private bool initializing = true;
    private bool saveQueued;
    private bool saveFailureReported;

    internal WindowPlacement? WindowPlacement { get; set; }
    internal string? StartupWarning { get; private set; }

    internal CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;

    public bool HasActiveSessions => Tabs.Any(tab => tab.IsRunning || tab.IsPreparing || tab.IsTransitioning || tab.IsClosing);
    public bool CanAddTab => !IsShuttingDown && Tabs.Count < SessionRepository.MaxTabs;

    public MainWindowViewModel(bool usePersistence = true)
    {
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddTab));
            AddTabCommand.NotifyCanExecuteChanged();
        };
        persistenceEnabled = usePersistence;
        if (usePersistence) StartupWarning = ClientCatalog.LoadUserProfiles(System.IO.Path.Combine(SessionStore.SettingsDirectory, "clients.json"));
        SessionData? session = usePersistence ? SessionStore.Load() : null;
        if (SessionStore.LoadWarning is { } warning && usePersistence)
            StartupWarning = string.Join("\n", new[] { StartupWarning, warning }.Where(value => !string.IsNullOrEmpty(value)));
        WindowPlacement = session?.Window;
        CloseBehavior = session?.CloseBehavior ?? CloseBehavior.Ask;
        if (session?.Tabs is { Count: > 0 } saved)
        {
            foreach (TabState st in saved)
            {
                CreateTab().ApplyState(st);
            }
        }
        else
        {
            CreateTab();
        }
        initializing = false;
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
        SessionData data = new() { CloseBehavior = CloseBehavior, Window = WindowPlacement };
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
                if (!saveFailureReported) NotificationHub.Show("Unable to save", ex.Message, error: true);
                saveFailureReported = true;
            }
        }, DispatcherPriority.Background);
    }

    public ObservableCollection<RatioTabViewModel> Tabs { get; } = [];

    public string Title =>
        $"Ratiomaster.NET {AppInfo.Version}   -   Created by NikolayIT  -  Forked by HdiaSaad  -  Re-forked by Freenitial";

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
        CreateTab();
        foreach (RatioTabViewModel t in Tabs)
        {
            t.RefreshPulse();
        }
    }

    private RatioTabViewModel CreateTab()
    {
        tabCounter++;
        RatioTabViewModel tab = new($"RM {tabCounter}");
        tab.PropertyChanged += OnTabPropertyChanged;
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(HasActiveSessions));
        RequestSave();
        return tab;
    }

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
            NotificationHub.Show("Unable to close tab", ex.Message, error: true);
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
            or "SelectedProxyType" or "ProxyHost" or "ProxyUser" or "ProxyPass" or "ProxyPort"
            or "EnableLog" or "IsRunning" or "IsPaused" or "IsTransitioning" or "HasFinished") RequestSave();
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(RatioTabViewModel.IsRunning) or nameof(RatioTabViewModel.IsPreparing)
                or nameof(RatioTabViewModel.IsTransitioning) or nameof(RatioTabViewModel.IsClosing))
        {
            OnPropertyChanged(nameof(HasActiveSessions));
        }
    }

    partial void OnSelectedTabChanged(RatioTabViewModel? value)
    {
        foreach (RatioTabViewModel tab in Tabs)
        {
            tab.IsActive = ReferenceEquals(tab, value);
        }
    }
}

internal static class AppInfo
{
    internal static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
