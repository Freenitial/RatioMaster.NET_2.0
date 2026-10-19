using System;
using System.Collections.Generic;
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
using RatioMaster.Localization;
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
            "ManualUpdateLabel" => (Strings.ActionManualUpdate, Strings.ActionManualUpdateShort),
            "DefaultsLabel" => (Strings.ActionSetDefaults, Strings.ActionSetDefaultsShort),
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

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e) => await AddFolderAsync();

    private MainWindowViewModel? Window => this.FindAncestorOfType<MainView>()?.DataContext as MainWindowViewModel;

    /// <summary>Opens one or more .torrent files: the first fills this tab when it is empty, the others open new tabs.</summary>
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
                Title = Strings.PickTorrentsTitle,
                AllowMultiple = true,
                SuggestedStartLocation = start,
                FileTypeFilter =
                [
                    new FilePickerFileType(Strings.TorrentFiles) { Patterns = ["*.torrent"] },
                    new FilePickerFileType(Strings.AllFiles) { Patterns = ["*"] },
                ],
            });
            if (files.Count == 0) return; // user cancelled

            // One file replaces this tab's torrent, as before; several open in new tabs after this one.
            if (files.Count == 1)
            {
                if (target.InputsEnabled) await OpenSingleAsync(target, files[0]);
                return;
            }
            List<TorrentSource> sources = await ReadSourcesAsync(files);
            if (Window is { } window) window.RequestAddTorrents(sources, target);
        }
        catch
        {
            // user cancelled or picker unavailable
        }
    }

    /// <summary>Adds every .torrent of a folder, through the platform picker (Android storage included).</summary>
    internal async Task AddFolderAsync()
    {
        RatioTabViewModel? target = Vm;
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top == null || target == null || Window is not { } window) return;
            IStorageFolder? start = string.IsNullOrEmpty(target.LastDirectory) ? null
                : await top.StorageProvider.TryGetFolderFromPathAsync(target.LastDirectory);
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Strings.PickFolderTitle,
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });
            if (folders.FirstOrDefault() is not { } folder) return;
            if (folder.TryGetLocalPath() is { Length: > 0 } local)
            {
                IReadOnlyList<TorrentSource> found = MainWindowViewModel.ExpandPaths([local]);
                if (found.Count == 0) RatioMaster.Services.NotificationHub.Show(Strings.BatchTitle, Strings.FolderEmpty);
                else window.RequestAddTorrents(found, target);
                return;
            }
            List<IStorageFile> files = [];
            await foreach (IStorageItem item in folder.GetItemsAsync())
            {
                if (item is IStorageFile file && file.Name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) files.Add(file);
            }
            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            if (files.Count == 0)
            {
                RatioMaster.Services.NotificationHub.Show(Strings.BatchTitle, Strings.FolderEmpty);
                return;
            }
            window.RequestAddTorrents(await ReadSourcesAsync(files), target);
        }
        catch
        {
            // user cancelled or picker unavailable
        }
    }

    /// <summary>Picked files as sources; one that cannot be read becomes an empty source, counted as invalid in the summary.</summary>
    private static async Task<List<TorrentSource>> ReadSourcesAsync(IEnumerable<IStorageFile> files)
    {
        List<TorrentSource> sources = [];
        foreach (IStorageFile file in files)
        {
            if (file.TryGetLocalPath() is { Length: > 0 } path)
            {
                sources.Add(new TorrentSource(file.Name, path));
                continue;
            }
            byte[]? data;
            try { data = await ReadCappedAsync(file, MaxTorrentBytes); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { data = null; }
            sources.Add(new TorrentSource(file.Name, Data: data ?? []));
        }
        return sources;
    }

    private async Task OpenSingleAsync(RatioTabViewModel target, IStorageFile file)
    {
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
            target.ShowStatus(() => Strings.FileUnreadable);
            return;
        }

        if (data is null)
        {
            // The "All files" filter lets the user pick anything, and a .torrent is tiny — the read is
            // capped so a huge pick can't exhaust memory even when the provider reports no size.
            target.ShowStatus(() => Strings.FileTooLarge);
            return;
        }

        if (target.InputsEnabled)
        {
            target.SetTorrentContent(data, file.Name);
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
        catch (Exception ex) { RatioMaster.Services.NotificationHub.Show(Strings.CopyFailedTitle, ex.Message, error: true); }
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
                Title = Strings.SaveLogTitle,
                SuggestedFileName = "ratiomaster-log.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType(Strings.TextFile) { Patterns = ["*.txt"] }],
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
            string name = file.Name;
            vm.ShowStatus(() => Strings.LogSaved(name));
        }
        catch (Exception ex)
        {
            string reason = ex.Message;
            vm.ShowStatus(() => Strings.LogSaveFailed(reason));
        }
    }

    private static IReadOnlyList<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer?.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Honestly reflect what a drop will do: torrents and folders are accepted, anything else is not.
        // Raised on every pointer move: the cheap name test runs before the file-system call.
        bool acceptable = Vm is not null && DroppedPaths(e).Any(path => path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(path));
        e.DragEffects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } target)
        {
            return;
        }

        IReadOnlyList<string> paths = DroppedPaths(e);
        // One torrent dropped on a stopped tab replaces its torrent; anything else opens new tabs.
        if (paths.Count == 1 && !Directory.Exists(paths[0]) && paths[0].EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            && target.InputsEnabled)
        {
            target.SetTorrentPath(paths[0]);
            return;
        }
        if (Window is { } window) window.RequestAddTorrents(MainWindowViewModel.ExpandPaths(paths), target);
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
