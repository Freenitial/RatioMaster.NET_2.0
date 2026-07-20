using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RatioMaster.ViewModels;

namespace RatioMaster.Views;

public partial class RatioTabView : UserControl
{
    private TextBox? logBox;

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
    }

    /// <summary>
    /// Keep the newest log line visible. The log is a FIXED-height terminal (LogBorder MaxHeight) that
    /// scrolls internally, so new lines land below the fold. Moving the caret alone doesn't reliably scroll
    /// a read-only, unfocused TextBox, so we also drive its internal ScrollViewer to the bottom — posted at
    /// Background priority because during TextChanged the new text hasn't been measured yet (Extent is stale).
    /// </summary>
    private void ScrollLogToEnd()
    {
        if (logBox is null)
        {
            return;
        }

        logBox.CaretIndex = logBox.Text?.Length ?? 0;
        Dispatcher.UIThread.Post(
            () =>
            {
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

    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsive(e.NewSize.Width);

    private void ApplyResponsive(double width)
    {
        bool narrow = width > 0 && width < NarrowThreshold;
        if (isNarrow == narrow)
        {
            return;
        }

        Grid? wide = this.FindControl<Grid>("WideRoot");
        ScrollViewer? wideScroll = this.FindControl<ScrollViewer>("WideScroll");
        ScrollViewer? narrowRoot = this.FindControl<ScrollViewer>("NarrowRoot");
        StackPanel? stack = this.FindControl<StackPanel>("NarrowStack");
        Grid? left = this.FindControl<Grid>("LeftPanel");
        Grid? right = this.FindControl<Grid>("RightPanel");
        Grid? bar = this.FindControl<Grid>("ActionBar");
        if (wide is null || wideScroll is null || narrowRoot is null || stack is null || left is null || right is null || bar is null)
        {
            return;
        }

        isNarrow = narrow;
        if (narrow)
        {
            wide.Children.Remove(left);
            wide.Children.Remove(right);
            left.Children.Remove(bar);        // pull the START/STOP/… bar out of the left column
            ReflowActionBar(true);            // 2x2
            bar.Margin = new Thickness(0);    // NarrowStack.Spacing handles the gap
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 0);
            stack.Children.Add(left);
            stack.Children.Add(right);
            stack.Children.Add(bar);          // …and place it at the very bottom, below the terminal log
        }
        else
        {
            stack.Children.Remove(left);
            stack.Children.Remove(right);
            stack.Children.Remove(bar);
            ReflowActionBar(false);           // one row of 4
            bar.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetRow(bar, 1);              // back to the bottom row of the left column
            Grid.SetColumn(bar, 0);
            left.Children.Add(bar);
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            wide.Children.Add(left);
            wide.Children.Add(right);
        }

        wideScroll.IsVisible = !narrow;
        narrowRoot.IsVisible = narrow;
    }

    // 4 action buttons: one row of 4 when wide, a 2x2 grid when narrow (so labels never truncate on phones).
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
            bar.RowDefinitions = new RowDefinitions("Auto,Auto");
            for (int i = 0; i < 4; i++)
            {
                Grid.SetRow(bar.Children[i], i / 2);
                Grid.SetColumn(bar.Children[i], i % 2);
            }
        }
        else
        {
            bar.ColumnDefinitions = new ColumnDefinitions("*,*,*,*");
            bar.RowDefinitions = new RowDefinitions();
            for (int i = 0; i < 4; i++)
            {
                Grid.SetRow(bar.Children[i], 0);
                Grid.SetColumn(bar.Children[i], i);
            }
        }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top == null || Vm == null)
            {
                return;
            }

            IStorageFolder? start = null;
            if (!string.IsNullOrEmpty(Vm.LastDirectory))
            {
                start = await top.StorageProvider.TryGetFolderFromPathAsync(Vm.LastDirectory);
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
            if (file is null)
            {
                return; // user cancelled
            }

            string? path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                // Desktop: a real filesystem path — load it directly (also seeds LastDirectory + restore).
                Vm.SetTorrentPath(path);
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
                Vm.StatusText = "Couldn't read the selected file.";
                return;
            }

            if (data is null)
            {
                // The "All files" filter lets the user pick anything, and a .torrent is tiny — the read is
                // capped so a huge pick can't exhaust memory even when the provider reports no size.
                Vm.StatusText = "That file is too large to be a .torrent.";
                return;
            }

            Vm.SetTorrentContent(data, file.Name);
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

    private async void OnSaveLogClick(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top == null || Vm == null)
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

        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // Report the write result — a failed save must not look like success.
        try
        {
            await File.WriteAllTextAsync(path, Vm.LogText);
            Vm.StatusText = "Log saved to " + path;
        }
        catch (Exception ex)
        {
            Vm.StatusText = "Failed to save log: " + ex.Message;
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        // Honestly reflect what a drop will do: OnDrop only accepts a .torrent, so show the Copy cursor
        // only for a .torrent — otherwise the affordance and the action disagree (cursor says "drop OK",
        // drop silently does nothing).
        string? path = e.DataTransfer?.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        bool acceptable = !string.IsNullOrEmpty(path) && path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        e.DragEffects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null)
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
