using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RatioMaster.Services;
using RatioMaster.ViewModels;
using RatioMaster.Views;

namespace RatioMaster;

public partial class App : Application
{
    private MainWindowViewModel? desktopViewModel;
    private MainWindow? mainWindow;
    private TrayIcon? trayIcon;
    private Task? shutdownTask;
    private bool closeDecisionPending;

    internal bool DesktopShutdownAllowed { get; private set; }

    /// <summary>
    /// Persists the session on a single-view host when MainActivity.OnPause backgrounds the app.
    /// Desktop hosts persist through the centralized shutdown flow.
    /// </summary>
    internal static Action? PersistSession { get; private set; }

    internal static Func<bool>? TryDismissDialog { get; private set; }

    public override void Initialize()
    {
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

            if (window.Content is MainView view)
            {
                TryDismissDialog = view.TryDismissDialog;
            }

            // The single-instance listener dispatches window activation onto the UI thread.
            SingleInstance.StartListener(() => Dispatcher.UIThread.Post(() => ShowWindow(window)));
            try
            {
                SetupTray(window, vm);
            }
            catch
            {
                trayIcon?.Dispose();
                trayIcon = null;
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            MainWindowViewModel vm = new();
            MainView view = new() { DataContext = vm };
            singleView.MainView = view;

            PersistSession = vm.SaveSession;
            TryDismissDialog = view.TryDismissDialog;
        }

        base.OnFrameworkInitializationCompleted();
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
                    NotificationHub.Show("Tray unavailable", "Keep this window open to access your sessions.");
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
            NotificationHub.Show("Unable to close", ex.Message, error: true);
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
            NotificationHub.Show("Unable to quit", ex.Message, error: true);
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

        NativeMenuItem show = new() { Header = "Show" };
        show.Click += (_, _) => ShowWindow(window);
        NativeMenuItem start = new() { Header = "Start / pause / resume current tab" };
        start.Click += (_, _) => _ = ExecuteTrayCommandAsync(vm.SelectedTab?.StartCommand);
        NativeMenuItem stop = new() { Header = "Stop current tab" };
        stop.Click += (_, _) => _ = ExecuteTrayCommandAsync(vm.SelectedTab?.StopCommand);
        NativeMenuItem exit = new() { Header = "Exit" };
        exit.Click += (_, _) => _ = ShutdownDesktopAsync();

        NativeMenu menu = [];
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        trayIcon = new TrayIcon();
        trayIcon.Icon = icon;
        trayIcon.ToolTipText = "RatioMaster.NET";
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.HasActiveSessions) && trayIcon is { } tray)
            {
                int active = vm.Tabs.Count(tab => tab.IsRunning || tab.IsTransitioning);
                tray.ToolTipText = $"RatioMaster.NET — {active} active / {vm.Tabs.Count} tabs";
            }
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
            NotificationHub.Show("Session action failed", ex.Message, error: true);
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
