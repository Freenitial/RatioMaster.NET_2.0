using System;
using System.Linq;
using Avalonia.VisualTree;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RatioMaster.Services;
using RatioMaster.ViewModels;

namespace RatioMaster.Views;

/// <summary>
/// The whole app UI (tab strip + active tab + status bar). Hosted inside <see cref="MainWindow"/> on
/// desktop and set as the single MainView on Android — so both platforms share one view + one code path.
/// </summary>
public partial class MainView : UserControl
{
    // Android magnifies everything ×1.25 so the desktop-sized UI is comfortable on a phone. Applied as a
    // RenderTransform (NOT LayoutTransform) with a top-left origin, then ScaledContent is given an explicit
    // PRE-scale Width/Height (the safe-area box ÷ scale) so the scaled result lands exactly inside the safe
    // area — no clipping, no overflow. Desktop leaves ScaledContent untouched (plain stretch, no scale).
    private const double MobileScale = 1.25;

    private ScrollViewer? tabScroll;
    private Button? tabLeftBtn;
    private Button? tabRightBtn;
    private WrapPanel? statsPanel;
    private TextBlock? statusText;
    private Grid? scaledContent;
    private WindowNotificationManager? notifications;
    private TaskCompletionSource<CloseDecision?>? closeDialog;
    private IInputElement? focusBeforeCloseDialog;
    private IInputElement? focusBeforeInfoOverlay;
    private bool infoOverlayActive;
    private MainWindowViewModel? observedMain;
    private bool startupWarningShown;

    internal readonly record struct CloseDecision(CloseBehavior Behavior, bool Remember);

    public MainView()
    {
        InitializeComponent();

        scaledContent = this.FindControl<Grid>("ScaledContent");
        tabScroll = this.FindControl<ScrollViewer>("TabScroll");
        tabLeftBtn = this.FindControl<Button>("TabLeftBtn");
        tabRightBtn = this.FindControl<Button>("TabRightBtn");
        statsPanel = this.FindControl<WrapPanel>("StatsPanel");
        statusText = this.FindControl<TextBlock>("StatusTextBlock");
        if (tabScroll is not null)
        {
            tabScroll.ScrollChanged += (_, _) => UpdateTabArrows();
            tabScroll.SizeChanged += (_, _) => UpdateTabArrows();
        }

        SizeChanged += (_, _) =>
        {
            UpdateStatusBarDensity();
            Relayout(); // re-fit the scaled content on any size change (rotation, split-screen); no-op on desktop
        };

        // Android only: pin ScaledContent top-left and magnify it. Both the transform and the MobileInsets
        // subscription are Android-exclusive, so desktop keeps a plain stretched grid with zero overhead.
        if (OperatingSystem.IsAndroid() && scaledContent is not null)
        {
            scaledContent.HorizontalAlignment = HorizontalAlignment.Left;
            scaledContent.VerticalAlignment = VerticalAlignment.Top;
            scaledContent.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            scaledContent.RenderTransform = new ScaleTransform { ScaleX = MobileScale, ScaleY = MobileScale };
            MobileInsets.Changed += Relayout;
        }

        // Subscribe once for the app's lifetime. MainView is the root view — created once per process on
        // both desktop and Android (the activity is set NOT to recreate on rotation) — so there's nothing
        // to unsubscribe on a transient detach; unsubscribing on Unloaded would silently kill all
        // notifications after the first detach/reattach.
        NotificationHub.Requested += OnNotification;
        Loaded += OnLoaded;
        DataContextChanged += (_, _) =>
        {
            if (observedMain is not null) observedMain.PropertyChanged -= OnMainPropertyChanged;
            observedMain = DataContext as MainWindowViewModel;
            if (observedMain is not null) observedMain.PropertyChanged += OnMainPropertyChanged;
        };
        DetachedFromVisualTree += (_, _) => CancelCloseDialog();
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    internal Task<CloseDecision?> ShowCloseDialogAsync(bool canMinimizeToTray)
    {
        if (OperatingSystem.IsAndroid() || DataContext is not MainWindowViewModel { IsShuttingDown: false })
        {
            return Task.FromResult<CloseDecision?>(null);
        }

        if (closeDialog is not null)
        {
            return closeDialog.Task;
        }

        closeDialog = new(TaskCreationOptions.RunContinuationsAsynchronously);
        focusBeforeCloseDialog = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        this.FindControl<CheckBox>("RememberCloseSetting")!.IsChecked = false;
        this.FindControl<Button>("BackgroundCloseButton")!.IsVisible = canMinimizeToTray;
        this.FindControl<TextBlock>("TrayUnavailableText")!.IsVisible = !canMinimizeToTray;
        this.FindControl<Grid>("MainContent")!.IsEnabled = false;
        this.FindControl<Border>("CloseOverlay")!.IsVisible = true;
        this.FindControl<Button>("CancelCloseButton")!.Focus();
        return closeDialog.Task;
    }

    internal bool TryDismissDialog()
    {
        if (closeDialog is not null)
        {
            CancelCloseDialog();
            return true;
        }
        if (DataContext is MainWindowViewModel { ChangelogOpen: true } changelog)
        {
            changelog.CloseChangelogCommand.Execute(null);
            return true;
        }
        if (DataContext is MainWindowViewModel { AboutOpen: true } about)
        {
            about.CloseAboutCommand.Execute(null);
            return true;
        }

        if (DataContext is MainWindowViewModel { SelectedTab: { ManualUpdatePending: true } tab })
        {
            tab.CancelManualUpdateCommand.Execute(null);
            return true;
        }

        return this.GetVisualDescendants().OfType<RatioTabView>().Any(view => view.DismissSizeTip());
    }

    internal void CancelCloseDialog() => CompleteCloseDialog(null);

    private async void OnOpenRelease(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync((DataContext as MainWindowViewModel)?.ReleaseUri);
    private async void OnOpenProject(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync(new Uri("https://github.com/Freenitial/RatioMaster.NET_2.0"));
    private async void OnReportBug(object? sender, RoutedEventArgs e) =>
        await OpenLinkAsync(new Uri("https://github.com/Freenitial/RatioMaster.NET_2.0/issues/new"));

    private async Task OpenLinkAsync(Uri? uri)
    {
        if (uri is null) return;
        try
        {
            if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex) { NotificationHub.Show("Unable to open link", ex.Message, error: true); }
    }

    private void CompleteCloseDialog(CloseBehavior? behavior)
    {
        if (closeDialog is not { } pending)
        {
            return;
        }

        CloseDecision? decision = behavior is { } choice
            ? new CloseDecision(choice, this.FindControl<CheckBox>("RememberCloseSetting")!.IsChecked == true)
            : null;
        closeDialog = null;
        this.FindControl<Border>("CloseOverlay")!.IsVisible = false;
        RefreshInteractionState();
        focusBeforeCloseDialog?.Focus();
        focusBeforeCloseDialog = null;
        pending.TrySetResult(decision);
    }

    private void OnCancelClose(object? sender, RoutedEventArgs e) => CancelCloseDialog();

    private void OnBackgroundClose(object? sender, RoutedEventArgs e) => CompleteCloseDialog(CloseBehavior.Background);

    private void OnQuitClose(object? sender, RoutedEventArgs e) => CompleteCloseDialog(CloseBehavior.Quit);

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.HasInfoOverlay)) return;
        RefreshInteractionState();
        bool open = observedMain?.HasInfoOverlay == true;
        if (open && !infoOverlayActive) focusBeforeInfoOverlay = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        infoOverlayActive = open;
        if (open)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (closeDialog is not null || observedMain?.HasInfoOverlay != true) return;
                this.FindControl<Button>(observedMain.ChangelogOpen ? "ChangelogBackButton" : "AboutCloseButton")?.Focus();
            }, DispatcherPriority.Input);
        }
        else
        {
            focusBeforeInfoOverlay?.Focus();
            focusBeforeInfoOverlay = null;
        }
    }

    private void RefreshInteractionState() => this.FindControl<Grid>("MainContent")!.IsEnabled = closeDialog is null && observedMain?.HasInfoOverlay != true;

    private async void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TryDismissDialog())
        {
            e.Handled = true;
            return;
        }
        if (DataContext is not MainWindowViewModel { IsShuttingDown: false, HasInfoOverlay: false } vm
            || closeDialog is not null || vm.SelectedTab is not { ManualUpdatePending: false } tab) return;
        bool control = e.KeyModifiers == KeyModifiers.Control;
        try
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None && tab.ManualUpdateCommand.CanExecute(null))
            {
                e.Handled = true;
                tab.ManualUpdateCommand.Execute(null);
            }
            else if (control && e.Key == Key.O && tab.InputsEnabled)
            {
                e.Handled = true;
                if (this.GetVisualDescendants().OfType<RatioTabView>().FirstOrDefault() is { } view) await view.BrowseAsync();
            }
            else if (control && e.Key == Key.T)
            {
                e.Handled = true;
                vm.AddTabCommand.Execute(null);
            }
            else if (control && e.Key == Key.W)
            {
                e.Handled = true;
                await vm.CloseTabCommand.ExecuteAsync(tab);
            }
            else if (control && e.Key == Key.Enter && tab.StartCommand.CanExecute(null))
            {
                e.Handled = true;
                await tab.StartCommand.ExecuteAsync(null);
            }
            else if (control && e.Key == Key.OemPeriod && tab.StopCommand.CanExecute(null))
            {
                e.Handled = true;
                await tab.StopCommand.ExecuteAsync(null);
            }
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.C)
            {
                e.Handled = true;
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(tab.HashHex);
            }
        }
        catch (Exception ex) { NotificationHub.Show("Unable to complete action", ex.Message, error: true); }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is not null)
        {
            // Idempotent: guarded so a detach/reattach can't leak a second notification manager.
            notifications ??= new WindowNotificationManager(top)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3,
            };
        }

        if (!startupWarningShown && DataContext is MainWindowViewModel { StartupWarning: { Length: > 0 } warning })
        {
            startupWarningShown = true;
            NotificationHub.Show("Saved settings", warning);
        }
        UpdateTabArrows();
        UpdateStatusBarDensity();
        Relayout(); // RenderScaling is available while the TopLevel is attached.
    }

    /// <summary>
    /// Fit the scaled Android UI inside the safe area supplied by <see cref="MobileInsets"/>.
    /// TopLevel.AutoSafeAreaPadding is disabled so only this layout applies the insets.
    /// Desktop content uses the unscaled layout.
    /// </summary>
    private void Relayout()
    {
        if (!OperatingSystem.IsAndroid() || scaledContent is null)
        {
            return;
        }

        // Insets arrive in PHYSICAL pixels; the layout works in DIPs → divide by RenderScaling.
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scaling <= 0)
        {
            scaling = 1.0;
        }

        Thickness p = MobileInsets.Physical;
        Thickness m = MobileInsets.MandatoryPhysical;
        double left = p.Left / scaling;
        double top = p.Top / scaling;
        double right = p.Right / scaling;
        // The visible home strip (gesture nav) can be taller than the thin nav-bar inset → reserve the max.
        double bottom = Math.Max(p.Bottom, m.Bottom) / scaling;

        // Landscape: a side nav bar is sometimes reported only as a gesture inset, so widen L/R to cover it.
        if (Bounds.Width > Bounds.Height)
        {
            Thickness g = MobileInsets.GesturesPhysical;
            left = Math.Max(left, g.Left / scaling);
            right = Math.Max(right, g.Right / scaling);
        }

        // Offset by the top-left inset; the transform origin is (0,0) so no bottom/right margin is needed —
        // the explicit Width/Height (below) already stops the scaled box short of the bottom/right insets.
        scaledContent.Margin = new Thickness(left, top, 0, 0);

        // The grid is pre-scale, so divide the available safe box by the scale to get its layout size.
        double w = (Bounds.Width - left - right) / MobileScale;
        double h = (Bounds.Height - top - bottom) / MobileScale;
        scaledContent.Width = w > 0 ? w : double.NaN;
        scaledContent.Height = h > 0 ? h : double.NaN;
    }

    // ── Tab overflow: arrows + wheel/swipe, no scrollbar ──
    private void UpdateTabArrows()
    {
        if (tabScroll is null)
        {
            return;
        }

        double max = tabScroll.Extent.Width - tabScroll.Viewport.Width;
        double x = tabScroll.Offset.X;
        bool overflow = max > 1;
        if (tabLeftBtn is not null)
        {
            tabLeftBtn.IsVisible = overflow && x > 1;
        }

        if (tabRightBtn is not null)
        {
            tabRightBtn.IsVisible = overflow && x < max - 1;
        }
    }

    private void ScrollTabs(double delta)
    {
        if (tabScroll is null)
        {
            return;
        }

        double max = Math.Max(0, tabScroll.Extent.Width - tabScroll.Viewport.Width);
        double x = Math.Clamp(tabScroll.Offset.X + delta, 0, max);
        tabScroll.Offset = new Vector(x, tabScroll.Offset.Y);
        UpdateTabArrows();
    }

    private void OnTabScrollLeft(object? sender, RoutedEventArgs e) => ScrollTabs(-150);

    private void OnTabScrollRight(object? sender, RoutedEventArgs e) => ScrollTabs(150);

    private void OnTabWheel(object? sender, PointerWheelEventArgs e)
    {
        // Translate vertical wheel / trackpad swipe into horizontal tab scrolling.
        double delta = (e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) * -80;
        if (delta != 0)
        {
            ScrollTabs(delta);
            e.Handled = true;
        }
    }

    // ── Status bar: shrink the gaps (and the status-text width) progressively as the view narrows ──
    private void UpdateStatusBarDensity()
    {
        double w = Bounds.Width;
        if (w <= 0)
        {
            return;
        }

        // t: 0 at/below 540px (tightest) → 1 at/above 980px (roomy).
        double t = Math.Clamp((w - 540) / (980 - 540), 0, 1);
        if (statsPanel is not null)
        {
            statsPanel.ItemSpacing = Math.Round(6 + t * (24 - 6));
        }

        if (statusText is not null)
        {
            // Below ~500px the 7 stat blocks need the whole bar, so the status text steps aside.
            statusText.IsVisible = w >= 500;
            statusText.MaxWidth = Math.Round(70 + t * (320 - 70));
        }
    }

    private void OnNotification(string title, string message, bool error) =>
        Dispatcher.UIThread.Post(() => notifications?.Show(
            new Notification(title, message, error ? NotificationType.Error : NotificationType.Information)));
}
