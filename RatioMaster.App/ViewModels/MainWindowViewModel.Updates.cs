namespace RatioMaster.ViewModels;

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RatioMaster.Services;
using RatioMaster.Models;
using System.Collections.Generic;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool aboutOpen;
    [ObservableProperty] private bool changelogOpen;
    public IReadOnlyList<ChangelogRelease> ChangelogReleases => ChangelogOpen ? ChangelogDocument.Releases : Array.Empty<ChangelogRelease>();
    public bool HasInfoOverlay => AboutOpen || ChangelogOpen;
    partial void OnAboutOpenChanged(bool value) => OnPropertyChanged(nameof(HasInfoOverlay));
    partial void OnChangelogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(HasInfoOverlay));
        OnPropertyChanged(nameof(ChangelogReleases));
    }
    [ObservableProperty] private string updateMessage = "Updates are checked only when requested.";
    [ObservableProperty] private bool updateAvailable;
    private CancellationTokenSource? updateCancellation;
    internal Uri? ReleaseUri { get; private set; }
    public string Diagnostics => $"RatioMaster.NET {AppInfo.Version}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}";

    [RelayCommand]
    private void ShowAbout() => AboutOpen = true;

    [RelayCommand]
    private void CloseAbout()
    {
        AboutOpen = false;
        updateCancellation?.Cancel();
    }

    [RelayCommand]
    private void ShowChangelog()
    {
        ChangelogOpen = true;
        AboutOpen = false;
    }

    [RelayCommand]
    private void CloseChangelog()
    {
        AboutOpen = true;
        ChangelogOpen = false;
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        updateCancellation = cancellation;
        UpdateMessage = "Checking GitHub…";
        UpdateAvailable = false;
        ReleaseUri = null;
        try
        {
            UpdateResult result = await UpdateChecker.CheckAsync(AppInfo.Version, cancellation.Token);
            UpdateMessage = result.Message;
            ReleaseUri = result.ReleaseUri;
            UpdateAvailable = ReleaseUri is not null;
        }
        catch (OperationCanceledException)
        {
            UpdateMessage = AboutOpen ? "The update check timed out. Try again later." : "Update check cancelled.";
        }
        catch (Exception ex)
        {
            UpdateMessage = "Unable to check for updates: " + ex.Message;
        }
        finally { updateCancellation = null; }
    }
}
