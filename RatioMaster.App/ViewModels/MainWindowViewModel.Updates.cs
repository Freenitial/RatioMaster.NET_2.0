namespace RatioMaster.ViewModels;

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RatioMaster.Localization;
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
    [ObservableProperty] private string updateMessage = Strings.UpdatesOnRequest;
    [ObservableProperty] private bool updateAvailable;

    // Rendered again when the language changes, so a found update stays announced in the new language.
    private Func<string> updateText = () => Strings.UpdatesOnRequest;
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
        ShowUpdateText(() => Strings.UpdateChecking);
        UpdateAvailable = false;
        ReleaseUri = null;
        try
        {
            UpdateResult result = await UpdateChecker.CheckAsync(AppInfo.Version, cancellation.Token);
            ShowUpdateText(result.Describe);
            ReleaseUri = result.ReleaseUri;
            UpdateAvailable = ReleaseUri is not null;
        }
        catch (OperationCanceledException)
        {
            ShowUpdateText(AboutOpen ? () => Strings.UpdateTimedOut : () => Strings.UpdateCancelled);
        }
        catch (Exception ex)
        {
            string detail = ex.Message;
            ShowUpdateText(() => Strings.UpdateFailed(detail));
        }
        finally { updateCancellation = null; }
    }

    private void ShowUpdateText(Func<string> text)
    {
        updateText = text;
        UpdateMessage = text();
    }
}
