using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RatioMaster.Services;

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
    private StackPanel? statsPanel;
    private TextBlock? statusText;
    private Grid? scaledContent;
    private WindowNotificationManager? notifications;

    public MainView()
    {
        InitializeComponent();

        scaledContent = this.FindControl<Grid>("ScaledContent");
        tabScroll = this.FindControl<ScrollViewer>("TabScroll");
        tabLeftBtn = this.FindControl<Button>("TabLeftBtn");
        tabRightBtn = this.FindControl<Button>("TabRightBtn");
        statsPanel = this.FindControl<StackPanel>("StatsPanel");
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

        UpdateTabArrows();
        UpdateStatusBarDensity();
        Relayout(); // TopLevel is attached now → RenderScaling is valid for the DIP conversion
    }

    /// <summary>
    /// Android: fit the ×1.25-scaled UI exactly inside the REAL safe area. We own the insets ourselves
    /// (<c>TopLevel.AutoSafeAreaPadding=False</c> in XAML + the AndroidX listener feeding
    /// <see cref="MobileInsets"/>) — Avalonia's built-in auto safe-area was double-stacking with ours and
    /// leaving the big empty strip at the top. No-op on desktop (gated + insets are zero there anyway).
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
            statsPanel.Spacing = Math.Round(3 + t * (24 - 3));
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
