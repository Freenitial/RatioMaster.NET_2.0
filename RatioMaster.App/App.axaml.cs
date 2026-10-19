using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RatioMaster.Localization;
using RatioMaster.Services;
using RatioMaster.ViewModels;
using RatioMaster.Views;

namespace RatioMaster;

public partial class App : Application
{
    private MainWindowViewModel? desktopViewModel;
    private MainWindow? mainWindow;
    private ISingleViewApplicationLifetime? singleViewLifetime;
    private TrayIcon? trayIcon;
    private NativeMenuItem? trayShow, trayStart, trayStop, trayExit;
    private Task? shutdownTask;
    private bool closeDecisionPending;

    internal bool DesktopShutdownAllowed { get; private set; }

    /// <summary>.torrent files and folders the desktop process was launched with.</summary>
    internal static IReadOnlyList<string> StartupFiles { get; set; } = [];

    /// <summary>
    /// Persists the session on a single-view host when MainActivity.OnPause backgrounds the app.
    /// Desktop hosts persist through the centralized shutdown flow.
    /// </summary>
    internal static Action? PersistSession { get; private set; }

    internal static Func<bool>? TryDismissDialog { get; private set; }

    public override void Initialize()
    {
        Language.CaptureSystemLanguage();
        AppLanguage.UseEnglishResources();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel vm = new();
            MainWindow window = new() { DataContext = vm };
            desktopViewModel = vm;
            mainWindow = window;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnDesktopShutdownRequested;
            ApplyFlowDirection(window);

            if (window.Content is MainView view)
            {
                TryDismissDialog = view.TryDismissDialog;
            }

            // Later launches (and those made while this window was being built) surface it and hand over their files.
            SingleInstance.Attach(files => Dispatcher.UIThread.Post(() =>
            {
                ShowWindow(window);
                OpenFiles(vm, files);
            }));
            try
            {
                SetupTray(window, vm);
            }
            catch
            {
                trayIcon?.Dispose();
                trayIcon = null;
            }
            if (StartupFiles.Count > 0) Dispatcher.UIThread.Post(() => OpenFiles(vm, StartupFiles), DispatcherPriority.Background);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            MainWindowViewModel vm = new();
            MainView view = new() { DataContext = vm };
            singleView.MainView = view;
            singleViewLifetime = singleView;
            desktopViewModel = vm;

            PersistSession = vm.SaveSession;
            TryDismissDialog = view.TryDismissDialog;
        }

        ApplyThemeStrings();
        Language.Changed += () => Dispatcher.UIThread.Post(RebuildForLanguage);
        base.OnFrameworkInitializationCompleted();
    }

    private static void OpenFiles(MainWindowViewModel vm, IReadOnlyList<string> files)
    {
        if (files.Count == 0 || vm.IsShuttingDown) return;
        vm.RequestAddTorrents(MainWindowViewModel.ExpandPaths(files), vm.SelectedTab);
    }

    private static void ApplyFlowDirection(Control control) =>
        control.FlowDirection = Language.Current.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Texts the Fluent theme reads from resources: the text box context menu and the managed file dialogs.</summary>
    private void ApplyThemeStrings()
    {
        Resources["StringTextFlyoutCutText"] = Strings.ContextCut;
        Resources["StringTextFlyoutCopyText"] = Strings.ContextCopy;
        Resources["StringTextFlyoutPasteText"] = Strings.ContextPaste;
        Resources["StringManagedFileChooserOkText"] = Strings.DialogOk;
        Resources["StringManagedFileChooserCancelText"] = Strings.Cancel;
        Resources["StringManagedFileChooserOverwritePromptCancelText"] = Strings.Cancel;
        Resources["StringManagedFileChooserOverwritePromptConfirmText"] = Strings.DialogReplace;
        Resources["StringManagedFileChooserShowHiddenFilesText"] = Strings.ShowHiddenFiles;
    }

    /// <summary>
    /// Applies a new interface language without restarting: the view tree is created again (its texts are read once,
    /// at creation), while the view models, sessions and engines keep running untouched.
    /// </summary>
    private void RebuildForLanguage()
    {
        ApplyThemeStrings();
        if (desktopViewModel is not { } vm) return;
        MainView replacement = new() { DataContext = vm };
        if (mainWindow is { } window)
        {
            (window.Content as MainView)?.Detach();
            window.Content = replacement;
            ApplyFlowDirection(window);
            LocalizeTrayMenu();
            UpdateTrayTooltip(vm);
        }
        else if (singleViewLifetime is { } single)
        {
            (single.MainView as MainView)?.Detach();
            single.MainView = replacement;
        }
        TryDismissDialog = replacement.TryDismissDialog;
    }

    private bool CanMinimizeToTray =>
        trayIcon is { IsVisible: true, NativeMenuExporter: not null };

    internal async Task RequestDesktopCloseAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (closeDecisionPending || shutdownTask is not null
            || desktopViewModel is not { } vm || mainWindow is not { } window)
        {
            return;
        }

        closeDecisionPending = true;
        try
        {
            if (!OperatingSystem.IsWindows() || !vm.HasActiveSessions)
            {
                await ShutdownDesktopAsync();
                return;
            }

            CloseBehavior behavior = vm.CloseBehavior;
            bool remember = false;
            if (behavior == CloseBehavior.Ask || (behavior == CloseBehavior.Background && !CanMinimizeToTray))
            {
                ShowWindow(window);
                if (window.Content is not MainView view)
                {
                    return;
                }

                MainView.CloseDecision? decision = await view.ShowCloseDialogAsync(CanMinimizeToTray);
                if (decision is null || shutdownTask is not null)
                {
                    return;
                }

                behavior = decision.Value.Behavior;
                remember = decision.Value.Remember;
            }

            if (behavior == CloseBehavior.Background)
            {
                // Check again when applying a decision so a missing tray cannot strand a hidden window.
                if (!CanMinimizeToTray)
                {
                    ShowWindow(window);
                    NotificationHub.Show(Strings.TrayUnavailableTitle, Strings.TrayUnavailableToast);
                    return;
                }

                if (remember)
                {
                    vm.CloseBehavior = behavior;
                }

                vm.SaveSession();
                window.Hide();
            }
            else if (behavior == CloseBehavior.Quit)
            {
                if (remember)
                {
                    vm.CloseBehavior = behavior;
                }

                await ShutdownDesktopAsync();
            }
        }
        catch (Exception ex)
        {
            ShowWindow(window);
            NotificationHub.Show(Strings.CloseFailedTitle, ex.Message, error: true);
        }
        finally
        {
            closeDecisionPending = false;
        }
    }

    private void OnDesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (DesktopShutdownAllowed)
        {
            return;
        }

        e.Cancel = true;
        _ = ShutdownDesktopAsync();
    }

    internal Task ShutdownDesktopAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktopViewModel is not { } vm || mainWindow is not { } window)
        {
            return Task.CompletedTask;
        }

        return shutdownTask ??= ShutdownDesktopCoreAsync(desktop, window, vm);
    }

    private async Task ShutdownDesktopCoreAsync(
        IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel vm)
    {
        // Leave the Closing/ShutdownRequested event stack before issuing the final Shutdown.
        // This also installs the shared task before any property notifications can request another exit.
        await Task.Yield();
        try
        {
            // Save first: if the system ends the process while the stop announces are still in flight,
            // the counters reached so far are already on disk.
            try { vm.SaveSession(); }
            catch (Exception) { }
            vm.IsShuttingDown = true;
            (window.Content as MainView)?.CancelCloseDialog();
            await vm.StopAllAsync();
            vm.SaveSession();
            DesktopShutdownAllowed = true;
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            DesktopShutdownAllowed = false;
            vm.IsShuttingDown = false;
            shutdownTask = null;
            ShowWindow(window);
            NotificationHub.Show(Strings.QuitFailedTitle, ex.Message, error: true);
        }
    }

    private void SetupTray(MainWindow window, MainWindowViewModel vm)
    {
        WindowIcon icon;
        try
        {
            icon = new WindowIcon(AssetLoader.Open(new Uri("avares://RatioMaster.NET/Assets/icon.ico")));
        }
        catch
        {
            return; // no tray without an icon
        }

        trayShow = new NativeMenuItem();
        trayShow.Click += (_, _) => ShowWindow(window);
        trayStart = new NativeMenuItem();
        trayStart.Click += (_, _) => _ = ExecuteTrayCommandAsync(vm.SelectedTab?.StartCommand);
        trayStop = new NativeMenuItem();
        trayStop.Click += (_, _) => _ = ExecuteTrayCommandAsync(vm.SelectedTab?.StopCommand);
        trayExit = new NativeMenuItem();
        trayExit.Click += (_, _) => _ = ShutdownDesktopAsync();

        NativeMenu menu = [];
        menu.Items.Add(trayShow);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(trayStart);
        menu.Items.Add(trayStop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(trayExit);

        trayIcon = new TrayIcon();
        trayIcon.Icon = icon;
        LocalizeTrayMenu();
        UpdateTrayTooltip(vm);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.HasActiveSessions)) UpdateTrayTooltip(vm);
        };
        trayIcon.Menu = menu;
        trayIcon.IsVisible = true;
        if (trayIcon.NativeMenuExporter is null)
        {
            trayIcon.Dispose();
            trayIcon = null;
            return;
        }

        trayIcon.Clicked += (_, _) => ShowWindow(window);
        TrayIcon.SetIcons(this, [trayIcon]);
    }

    /// <summary>Menu labels change with the language only; the tooltip follows the sessions.</summary>
    private void LocalizeTrayMenu()
    {
        if (trayShow is null || trayStart is null || trayStop is null || trayExit is null) return;
        trayShow.Header = Strings.TrayShow;
        trayStart.Header = Strings.TrayStartPause;
        trayStop.Header = Strings.TrayStop;
        trayExit.Header = Strings.TrayExit;
    }

    private void UpdateTrayTooltip(MainWindowViewModel vm)
    {
        if (trayIcon is not { } tray) return;
        int active = vm.Tabs.Count(tab => tab.IsRunning || tab.IsTransitioning);
        string text = vm.HasActiveSessions ? Strings.TrayTooltip(active, vm.Tabs.Count) : "RatioMaster.NET";
        if (tray.ToolTipText != text) tray.ToolTipText = text;
    }

    private async Task ExecuteTrayCommandAsync(IAsyncRelayCommand? command)
    {
        if (shutdownTask is not null || command?.CanExecute(null) != true)
        {
            return;
        }

        try
        {
            await command.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            NotificationHub.Show(Strings.SessionActionFailedTitle, ex.Message, error: true);
        }
    }

    private void ShowWindow(Window window)
    {
        if (DesktopShutdownAllowed)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
