using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RatioMaster.ViewModels;

namespace RatioMaster.Views;

public partial class RatioTabView : UserControl
{
    private TextBox? logBox;
    private RatioTabViewModel? observedVm;
    private bool scrollPending;

    public RatioTabView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);

        logBox = this.FindControl<TextBox>("LogBox");
        if (logBox != null)
        {
            logBox.TextChanged += (_, _) => ScrollLogToEnd();
        }

        DataContextChanged += (_, _) => ObserveViewModel();
        AttachedToVisualTree += (_, _) => ObserveViewModel();
        DetachedFromVisualTree += (_, _) =>
        {
            if (observedVm is not null)
            {
                observedVm.CancelManualUpdateCommand.Execute(null);
                observedVm.PropertyChanged -= OnViewModelChanged;
                observedVm = null;
            }
        };
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    private void ObserveViewModel()
    {
        if (observedVm is not null)
        {
            observedVm.PropertyChanged -= OnViewModelChanged;
        }

        observedVm = Vm;
        if (observedVm is not null)
        {
            observedVm.PropertyChanged += OnViewModelChanged;
        }

        UpdateSizeHint();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RatioTabViewModel.TorrentSize) or nameof(RatioTabViewModel.SmallTorrentWarning))
        {
            UpdateSizeHint();
        }

        if (e.PropertyName == nameof(RatioTabViewModel.ManualUpdatePending) && Vm?.ManualUpdatePending == true)
        {
            this.FindControl<Button>("CancelUpdateButton")?.Focus();
        }
        if (e.PropertyName == nameof(RatioTabViewModel.FollowLog) && Vm?.FollowLog == true) ScrollLogToEnd();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm?.ManualUpdatePending == true)
        {
            Vm.CancelManualUpdateCommand.Execute(null);
            e.Handled = true;
        }
    }

    internal bool DismissSizeTip()
    {
        bool dismissed = false;
        foreach (string name in new[] { "SizeLabel", "SizeBox" })
        {
            if (this.FindControl<Control>(name) is { } control && ToolTip.GetIsOpen(control))
            {
                ToolTip.SetIsOpen(control, false);
                dismissed = true;
            }
        }
        return dismissed;
    }

    private void OnSizeTipTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control)
        {
            ToolTip.SetIsOpen(control, !ToolTip.GetIsOpen(control));
        }
    }

    private void OnSizeBoxSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateSizeHint();

    private void UpdateSizeHint()
    {
        TextBox? box = this.FindControl<TextBox>("SizeBox");
        TextBlock? hint = this.FindControl<TextBlock>("SizeHint");
        if (box is null || hint is null)
        {
            return;
        }

        double Width(string text, FontFamily family, double fontSize) => new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family), fontSize, Brushes.White).Width;

        double needed = Width(Vm?.TorrentSize ?? string.Empty, box.FontFamily, box.FontSize)
            + Width(hint.Text ?? string.Empty, hint.FontFamily, hint.FontSize)
            + box.Padding.Left + box.Padding.Right + box.BorderThickness.Left + box.BorderThickness.Right + 22;
        hint.IsVisible = Vm?.SmallTorrentWarning == true && box.Bounds.Width >= needed;
    }

    /// <summary>Coalesce log scroll requests until layout has measured the text, then scroll the terminal viewport.</summary>
    private void ScrollLogToEnd()
    {
        if (logBox is null || scrollPending || Vm?.FollowLog == false)
        {
            return;
        }

        scrollPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                scrollPending = false;
                if (logBox is null || !this.IsAttachedToVisualTree() || Vm?.FollowLog == false)
                {
                    return;
                }
                ScrollViewer? sv = logBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (sv is not null)
                {
                    // Preserve the horizontal offset (the log is NoWrap and scrolls sideways too).
                    sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                }
            },
            DispatcherPriority.Background);
    }

    private RatioTabViewModel? Vm => DataContext as RatioTabViewModel;

    // Responsive reflow: below this width the two columns stack into one scrollable column
    // (narrow desktop windows and phone-sized screens).
    private const double NarrowThreshold = 640;
    private bool isNarrow; // false = wide (matches the XAML default: panels start in WideRoot)

    // User clicked/focused the Stop combo or its value box → advance the pulse hint to START.
    private void OnStopInteraction(object? sender, RoutedEventArgs e) => Vm?.NotifyStopInteraction();

    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsive(e.NewSize.Width);
        if (isNarrow && this.FindControl<Border>("LogBorder") is { } terminal)
        {
            // The portrait viewport determines terminal height independently of the log contents.
            terminal.Height = Math.Clamp(e.NewSize.Height * 0.25 - 40, 118, 272);
        }
    }

    private void ApplyResponsive(double width)
    {
        bool narrow = width > 0 && width < NarrowThreshold;
        if (isNarrow == narrow)
        {
            return;
        }

        WideColumnsPanel? wide = this.FindControl<WideColumnsPanel>("WideRoot");
        ScrollViewer? wideScroll = this.FindControl<ScrollViewer>("WideScroll");
        ScrollViewer? narrowRoot = this.FindControl<ScrollViewer>("NarrowRoot");
        StackPanel? stack = this.FindControl<StackPanel>("NarrowStack");
        Grid? left = this.FindControl<Grid>("LeftPanel");
        Grid? right = this.FindControl<Grid>("RightPanel");
        Grid? bar = this.FindControl<Grid>("ActionBar");
        Border? activity = this.FindControl<Border>("ActivityCard");
        Grid? activityGrid = this.FindControl<Grid>("ActivityGrid");
        Border? terminal = this.FindControl<Border>("LogBorder");
        if (wide is null || wideScroll is null || narrowRoot is null || stack is null || left is null || right is null || bar is null
            || activity is null || activityGrid is null || terminal is null)
        {
            return;
        }

        isNarrow = narrow;
        if (narrow)
        {
            wide.Children.Remove(left);
            wide.Children.Remove(right);
            right.Children.Remove(bar);
            right.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
            right.VerticalAlignment = VerticalAlignment.Top;
            activity.VerticalAlignment = VerticalAlignment.Top;
            activityGrid.RowDefinitions = new RowDefinitions("Auto,70,Auto,27");
            ReflowActionBar(true);
            stack.Children.Add(left);
            stack.Children.Add(right);
            stack.Children.Add(bar);
        }
        else
        {
            stack.Children.Remove(left);
            stack.Children.Remove(right);
            stack.Children.Remove(bar);
            right.RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto");
            right.VerticalAlignment = VerticalAlignment.Stretch;
            activity.VerticalAlignment = VerticalAlignment.Stretch;
            activityGrid.RowDefinitions = new RowDefinitions("Auto,70,*,27");
            terminal.Height = double.NaN;
            ReflowActionBar(false);
            right.Children.Add(bar);
            wide.Children.Add(left);
            wide.Children.Add(right);
        }

        wideScroll.IsVisible = !narrow;
        narrowRoot.IsVisible = narrow;
    }

    private void OnActionLabelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not TextBlock label || e.NewSize.Width <= 0) return;
        var (full, compact) = label.Name switch
        {
            "ManualUpdateLabel" => ("Manual update", "Update"),
            "DefaultsLabel" => ("Set defaults", "Defaults"),
            _ => (string.Empty, string.Empty),
        };
        if (full.Length == 0) return;

        FormattedText measured = new(full, CultureInfo.InvariantCulture, label.FlowDirection,
            new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
            label.FontSize, Brushes.White);
        label.Text = Math.Ceiling(measured.Width) <= e.NewSize.Width ? full : compact;
    }

    // Action buttons occupy one row of four in landscape and two rows of two in portrait.
    private void ReflowActionBar(bool narrow)
    {
        Grid? bar = this.FindControl<Grid>("ActionBar");
        if (bar is null || bar.Children.Count < 4)
        {
            return;
        }

        if (narrow)
        {
            bar.ColumnDefinitions = new ColumnDefinitions("*,*");
            bar.RowDefinitions = new RowDefinitions("44,44");
            for (int i = 0; i < 4; i++)
            {
                Grid.SetRow(bar.Children[i], i / 2);
                Grid.SetColumn(bar.Children[i], i % 2);
            }
        }
        else
        {
            bar.ColumnDefinitions = new ColumnDefinitions("*,*,*,*");
            bar.RowDefinitions = new RowDefinitions("44");
            for (int i = 0; i < 4; i++)
            {
                Grid.SetRow(bar.Children[i], 0);
                Grid.SetColumn(bar.Children[i], i);
            }
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e) => await BrowseAsync();

    internal async Task BrowseAsync()
    {
        RatioTabViewModel? target = Vm;
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top == null || target == null)
            {
                return;
            }

            IStorageFolder? start = null;
            if (!string.IsNullOrEmpty(target.LastDirectory))
            {
                start = await top.StorageProvider.TryGetFolderFromPathAsync(target.LastDirectory);
            }

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a .torrent file",
                AllowMultiple = false,
                SuggestedStartLocation = start,
                FileTypeFilter =
                [
                    new FilePickerFileType("Torrent files") { Patterns = ["*.torrent"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
            });

            IStorageFile? file = files.FirstOrDefault();
            if (file is null || !target.InputsEnabled)
            {
                return; // user cancelled
            }

            string? path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                // Desktop: a real filesystem path — load it directly (also seeds LastDirectory + restore).
                target.SetTorrentPath(path);
                return;
            }

            // Android: the Storage Access Framework returns a content:// URI with no filesystem path, so
            // TryGetLocalPath() is null. Read the bytes through the storage stream (which DOES resolve the
            // content URI) and hand them to the VM. This branch has its OWN try/catch so a read failure is
            // reported instead of vanishing into the outer picker-cancelled catch (a silent empty box).
            byte[]? data;
            try
            {
                data = await ReadCappedAsync(file, MaxTorrentBytes);
            }
            catch
            {
                // e.g. a cloud/virtual document that can't be materialized, or a released read grant.
                target.StatusText = "Couldn't read the selected file.";
                return;
            }

            if (data is null)
            {
                // The "All files" filter lets the user pick anything, and a .torrent is tiny — the read is
                // capped so a huge pick can't exhaust memory even when the provider reports no size.
                target.StatusText = "That file is too large to be a .torrent.";
                return;
            }

            if (target?.InputsEnabled == true)
            {
                target.SetTorrentContent(data, file.Name);
            }
        }
        catch
        {
            // user cancelled or picker unavailable
        }
    }

    // A .torrent's metainfo is at most a few MB even for very large torrents; cap the in-memory read well
    // above that but low enough that an accidental pick (via the "All files" filter) can't exhaust memory.
    private const ulong MaxTorrentBytes = 64UL * 1024 * 1024;

    // Read a storage file fully into memory, but never buffer more than <paramref name="cap"/> bytes — the
    // cap is enforced DURING the read, so a provider that reports an unknown (null) size can't trick us into
    // slurping an unbounded file. Returns null if the content exceeds the cap.
    private static async Task<byte[]?> ReadCappedAsync(IStorageFile file, ulong cap)
    {
        using Stream s = await file.OpenReadAsync();
        using MemoryStream ms = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await s.ReadAsync(buffer)) > 0)
        {
            if ((ulong)ms.Length + (ulong)read > cap)
            {
                return null; // over the cap → stop before buffering the rest
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private async void OnCopyLogClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(vm.VisibleLogText); }
        catch (Exception ex) { RatioMaster.Services.NotificationHub.Show("Unable to copy log", ex.Message, error: true); }
    }

    private async void OnSaveLogClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        RatioTabViewModel? vm = Vm;
        if (top == null || vm == null)
        {
            return;
        }

        IStorageFile? file;
        try
        {
            file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save log",
                SuggestedFileName = "ratiomaster-log.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text file") { Patterns = ["*.txt"] }],
            });
        }
        catch
        {
            return; // picker unavailable / cancelled
        }

        if (file is null)
        {
            return;
        }

        // Report the write result — a failed save must not look like success.
        try
        {
            using Stream stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
            {
                stream.SetLength(0);
            }

            using StreamWriter writer = new(stream);
            await writer.WriteAsync(vm.VisibleLogText);
            await writer.FlushAsync();
            vm.StatusText = "Log saved to " + file.Name;
        }
        catch (Exception ex)
        {
            vm.StatusText = "Failed to save log: " + ex.Message;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Honestly reflect what a drop will do: OnDrop only accepts a .torrent, so show the Copy cursor
        // only for a .torrent — otherwise the affordance and the action disagree (cursor says "drop OK",
        // drop silently does nothing).
        string? path = e.DataTransfer?.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        bool acceptable = Vm?.InputsEnabled == true && !string.IsNullOrEmpty(path)
            && path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        e.DragEffects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is null || !Vm.InputsEnabled)
        {
            return;
        }

        IStorageItem? item = e.DataTransfer?.TryGetFiles()?.FirstOrDefault();
        string? path = item?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            Vm.SetTorrentPath(path);
        }
    }
}

/// <summary>Measures the settings column first so the activity column receives a finite, matching height.</summary>
public sealed class WideColumnsPanel : Panel
{
    private const double ColumnSpacing = 10;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2)
        {
            return default;
        }

        double columnWidth = Math.Max(0, (availableSize.Width - ColumnSpacing) / 2);
        Control left = Children[0];
        Control right = Children[1];
        left.Measure(new Size(columnWidth, double.PositiveInfinity));
        right.Measure(new Size(columnWidth, left.DesiredSize.Height));
        double desiredWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : left.DesiredSize.Width + ColumnSpacing + right.DesiredSize.Width;
        return new Size(desiredWidth, left.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 2)
        {
            double columnWidth = Math.Max(0, (finalSize.Width - ColumnSpacing) / 2);
            Children[0].Arrange(new Rect(0, 0, columnWidth, finalSize.Height));
            Children[1].Arrange(new Rect(columnWidth + ColumnSpacing, 0, columnWidth, finalSize.Height));
        }

        return finalSize;
    }
}
