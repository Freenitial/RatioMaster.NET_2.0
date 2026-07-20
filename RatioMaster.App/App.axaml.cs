using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using RatioMaster.Services;
using RatioMaster.ViewModels;
using RatioMaster.Views;

namespace RatioMaster;

public partial class App : Application
{
    /// <summary>
    /// Persists the active session on a single-view (Android) host, which fires no ShutdownRequested /
    /// window-Closing event. <c>MainActivity.OnPause</c> invokes this when the app is backgrounded.
    /// Null on desktop (which saves via ShutdownRequested + MainWindow.Closing instead).
    /// </summary>
    internal static Action? PersistSession { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel vm = new();
            MainWindow window = new() { DataContext = vm };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                // Stop BEFORE saving: each engine sends its &event=stopped announce and finalises its
                // counters, so the tracker does not keep a phantom peer and the saved totals are final.
                vm.StopAll();
                vm.SaveSession();
            };

            // A second launch signals us instead of starting its own process (see SingleInstance): surface
            // the window, which is normally hidden in the tray. The listener runs on a background thread.
            SingleInstance.StartListener(() => Dispatcher.UIThread.Post(() => ShowWindow(window)));
            try
            {
                SetupTray(desktop, window, vm); // tray backend may be absent on some Linux DEs
            }
            catch
            {
                // no tray → the app still runs normally
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Android (and any single-view host): no Window/tray — the shared MainView IS the app.
            MainWindowViewModel vm = new();
            singleView.MainView = new MainView { DataContext = vm };

            // Single-view hosts raise no ShutdownRequested/Closing, so wire a background-save hook that
            // MainActivity.OnPause calls — otherwise the session would never be persisted on Android.
            PersistSession = vm.SaveSession;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel vm)
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
        NativeMenuItem start = new() { Header = "Start current tab" };
        start.Click += (_, _) => vm.SelectedTab?.StartCommand.Execute(null);
        NativeMenuItem stop = new() { Header = "Stop current tab" };
        stop.Click += (_, _) => vm.SelectedTab?.StopCommand.Execute(null);
        NativeMenuItem exit = new() { Header = "Exit" };
        exit.Click += (_, _) =>
        {
            vm.StopAll();
            vm.SaveSession();
            desktop.Shutdown();
        };

        NativeMenu menu = [];
        menu.Items.Add(show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        TrayIcon tray = new()
        {
            Icon = icon,
            ToolTipText = "RatioMaster.NET",
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) => ShowWindow(window);

        TrayIcon.SetIcons(this, [tray]);
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
