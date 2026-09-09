using Avalonia;
using Avalonia.Controls;
using System;
using System.Linq;
using RatioMaster.Services;
using RatioMaster.ViewModels;

namespace RatioMaster.Views;

public partial class MainWindow : Window
{
    private bool placementReady;
    private WindowPlacement normalPlacement = new();
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += (_, _) => RestorePlacement();
        PositionChanged += (_, _) => RememberPlacement();
        SizeChanged += (_, _) => RememberPlacement();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) RememberPlacement(); };
    }

    private void RestorePlacement()
    {
        if (DataContext is MainWindowViewModel { WindowPlacement: { } saved })
        {
            if (double.IsFinite(saved.Width) && double.IsFinite(saved.Height))
            {
                Width = Math.Clamp(saved.Width, MinWidth, 10000);
                Height = Math.Clamp(saved.Height + (saved.LayoutVersion < 1 ? 8 : 0), MinHeight, 10000);
            }
            PixelPoint titlePoint = new(saved.X + 60, saved.Y + 16);
            if (Screens.All.Any(screen => screen.WorkingArea.Contains(titlePoint))) Position = new PixelPoint(saved.X, saved.Y);
            normalPlacement = new() { X = Position.X, Y = Position.Y, Width = Width, Height = Height };
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        placementReady = true;
        RememberPlacement();
    }

    private void RememberPlacement()
    {
        if (!placementReady || DataContext is not MainWindowViewModel vm || WindowState == WindowState.Minimized) return;
        if (WindowState == WindowState.Normal && Bounds.Width > 0 && Bounds.Height > 0)
            normalPlacement = new() { X = Position.X, Y = Position.Y, Width = Bounds.Width, Height = Bounds.Height };
        vm.WindowPlacement = new()
        {
            LayoutVersion = 1,
            X = normalPlacement.X, Y = normalPlacement.Y, Width = normalPlacement.Width,
            Height = normalPlacement.Height, Maximized = WindowState == WindowState.Maximized,
        };
        vm.RequestSave();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Application.Current is not App app || app.DesktopShutdownAllowed)
        {
            return;
        }

        e.Cancel = true;
        _ = e.CloseReason == WindowCloseReason.WindowClosing
            ? app.RequestDesktopCloseAsync()
            : app.ShutdownDesktopAsync();
    }
}
