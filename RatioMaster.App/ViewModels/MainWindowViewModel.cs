namespace RatioMaster.ViewModels;

using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RatioMaster.Engine;
using RatioMaster.Services;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private RatioTabViewModel? selectedTab;

    private int tabCounter;

    public MainWindowViewModel()
    {
        SessionData? session = SessionStore.Load();
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
    }

    /// <summary>Persist all tabs (portable settings + resume) to the session file.</summary>
    /// <summary>Stop every running tab. Called before the app exits so each session gets its
    /// <c>&amp;event=stopped</c> announce instead of leaving a phantom peer registered in the swarm.</summary>
    public void StopAll()
    {
        foreach (RatioTabViewModel t in Tabs)
        {
            t.StopIfRunning();
        }
    }

    public void SaveSession()
    {
        SessionData data = new();
        foreach (RatioTabViewModel t in Tabs)
        {
            data.Tabs.Add(t.CaptureState());
        }

        SessionStore.Save(data);
    }

    public ObservableCollection<RatioTabViewModel> Tabs { get; } = [];

    public string Title =>
        $"Ratiomaster.NET {AppInfo.Version}   -   Created by NikolayIT  -  Forked by HdiaSaad  -  Re-forked by Freenitial";

    public string LocalIp => NetInfo.GetLocalIp();

    [RelayCommand]
    private void AddTab()
    {
        // A user-opened tab marks a power user → kill all onboarding pulsing for the session.
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
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    [RelayCommand]
    private void SelectTab(RatioTabViewModel? tab)
    {
        if (tab != null)
        {
            SelectedTab = tab;
        }
    }

    [RelayCommand]
    private void CloseTab(RatioTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab == null || Tabs.Count <= 1)
        {
            return;
        }

        int index = Tabs.IndexOf(tab);
        bool closingSelected = ReferenceEquals(tab, SelectedTab);
        tab.StopIfRunning();
        Tabs.Remove(tab);

        // Only move the selection when the tab being closed WAS the selected one. Closing another tab
        // (its × is reachable without selecting it) used to yank the user away from the tab they were on.
        if (closingSelected)
        {
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
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
    internal const string Version = "2.0";
}
