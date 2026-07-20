using System;
using Avalonia.Controls;
using RatioMaster.ViewModels;

namespace RatioMaster.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Persist the portable session on every close.
        (DataContext as MainWindowViewModel)?.SaveSession();

        // On Windows the X minimises to the tray (tray Exit / ApplicationShutdown really quits).
        // On Linux/macOS a tray host may be absent, so the X just closes normally.
        if (OperatingSystem.IsWindows() && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
