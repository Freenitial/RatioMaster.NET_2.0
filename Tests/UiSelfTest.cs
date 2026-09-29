#if DEBUG && !ANDROID
namespace RatioMaster;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RatioMaster.Services;
using RatioMaster.Models;
using RatioMaster.ViewModels;
using RatioMaster.Views;

internal static class UiSelfTest
{
    internal static int Run()
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont().SetupWithoutStarting();
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("[PASS] " + message); count++; }
        void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        RatioTabViewModel vm = new("Test");
        RatioTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1000, Height = 740, Content = view };
        window.Show(); Pump(window);
        try
        {
            Grid bar = view.FindControl<Grid>("ActionBar")!;
            Grid left = view.FindControl<Grid>("LeftPanel")!;
            Grid right = view.FindControl<Grid>("RightPanel")!;
            TextBox log = view.FindControl<TextBox>("LogBox")!;
            Button primary = bar.Children[0].GetVisualDescendants().OfType<Button>().First();
            Border custom = view.FindControl<Border>("CustomClientCard")!;
            Border activity = view.FindControl<Border>("ActivityCard")!;
            Border terminal = view.FindControl<Border>("LogBorder")!;
            Border graph = view.FindControl<Border>("GraphBorder")!;
            Control[] layoutControls = [left, right, custom, activity, terminal, graph, bar];
            Rect[] LayoutBounds() => layoutControls.Select(control => control.Bounds).ToArray();
            double Top(Control control) => control.TranslatePoint(default, view)!.Value.Y;
            double Bottom(Control control) => Top(control) + control.Bounds.Height;
            bool Near(double a, double b) => Math.Abs(a - b) < 0.01;
            string longLog = string.Join("\n", Enumerable.Range(0, 1500).Select(i => new string('x', 160) + i));
            foreach (double width in new[] { 1000.0, 800.0, 660.0, 360.0, 1000.0 })
            {
                window.Width = width;
                foreach (double viewportHeight in new[] { 740.0, 360.0, 660.0, 1300.0 })
                {
                    window.Height = viewportHeight; Pump(window);
                    bool narrow = width < 640;
                    TextBlock updateLabel = view.FindControl<TextBlock>("ManualUpdateLabel")!;
                    TextBlock defaultsLabel = view.FindControl<TextBlock>("DefaultsLabel")!;
                    if (width is 660 or 1000 or 360)
                    {
                        Check(updateLabel.Text == (width == 660 ? "Update" : "Manual update")
                            && defaultsLabel.Text == (width == 660 ? "Defaults" : "Set defaults")
                            && updateLabel.TextLayout.Width <= updateLabel.Bounds.Width
                            && defaultsLabel.TextLayout.Width <= defaultsLabel.Bounds.Width
                            && updateLabel.TextLayout.Height <= updateLabel.Bounds.Height
                            && defaultsLabel.TextLayout.Height <= defaultsLabel.Bounds.Height,
                            $"action labels fit completely and restore after reflow at {width}x{viewportHeight}");
                    }
                    ScrollViewer outer = view.FindControl<ScrollViewer>(narrow ? "NarrowRoot" : "WideScroll")!;
                    outer.Offset = default;
                    vm.LogText = ""; Pump(window);
                    Rect[] emptyBounds = LayoutBounds();
                    Size emptyExtent = outer.Extent;
                    Size emptyDesired = ((Control)outer.Content!).DesiredSize;
                    foreach (string text in new[] { "Short log.", longLog, "" })
                    {
                        vm.LogText = text; Pump(window);
                        if (!LayoutBounds().SequenceEqual(emptyBounds) || outer.Extent != emptyExtent
                            || ((Control)outer.Content!).DesiredSize != emptyDesired)
                        {
                            Console.WriteLine($"[MEASURE] extent={emptyExtent} -> {outer.Extent}, desired={emptyDesired} -> {((Control)outer.Content!).DesiredSize}");
                            for (int i = 0; i < layoutControls.Length; i++)
                                Console.WriteLine($"[BOUNDS] {layoutControls[i].Name}: {emptyBounds[i]} -> {layoutControls[i].Bounds}");
                        }
                        Check(LayoutBounds().SequenceEqual(emptyBounds) && outer.Extent == emptyExtent
                            && ((Control)outer.Content!).DesiredSize == emptyDesired,
                            $"log length cannot resize content or its desired size at {width}x{viewportHeight} ({text.Length} chars)");
                    }
                    vm.LogText = longLog; Pump(window);
                    ScrollViewer scroll = log.GetVisualDescendants().OfType<ScrollViewer>().First();
                    Check(log.Text == vm.LogText && log.IsReadOnly && scroll.Viewport.Height > 0
                        && scroll.Extent.Height > scroll.Viewport.Height && scroll.Extent.Width > scroll.Viewport.Width
                        && log.Bounds.Height < terminal.Bounds.Height
                        && Near(scroll.Offset.Y, scroll.Extent.Height - scroll.Viewport.Height),
                        "bound terminal scrolls internally to the end in both layouts");
                    if (narrow)
                    {
                        double expectedTerminal = Math.Clamp(viewportHeight * 0.25 - 40, 118, 272);
                        StackPanel stack = view.FindControl<StackPanel>("NarrowStack")!;
                        Check(stack.Children.SequenceEqual(new Control[] { left, right, bar })
                            && bar.ColumnDefinitions.Count == 2 && bar.RowDefinitions.Count == 2
                            && bar.Children.Select((child, i) => Grid.GetRow(child) == i / 2 && Grid.GetColumn(child) == i % 2).All(ok => ok),
                            "portrait keeps settings, activity, then the 2x2 actions");
                        Check(left.Bounds.X == 0 && left.Bounds.Y == 0 && Near(left.Bounds.Width, 344)
                            && Near(right.Bounds.Width, left.Bounds.Width) && Near(bar.Bounds.Width, left.Bounds.Width)
                            && Near(right.Bounds.Y - left.Bounds.Bottom, 8)
                            && Near(bar.Bounds.Y - right.Bounds.Bottom, 8) && Near(bar.Bounds.Height, 94)
                            && Near(terminal.Bounds.Height, expectedTerminal)
                            && Near(log.Bounds.Height, expectedTerminal - 2)
                            && Near(outer.Extent.Height, Math.Max(outer.Viewport.Height, bar.Bounds.Bottom + 14)),
                            $"portrait stacks its content without gaps or overflow at 360x{viewportHeight}");
                    }
                    else
                    {
                        Check(ReferenceEquals(bar.Parent, right) && Near(bar.Bounds.Height, 44)
                            && bar.ColumnDefinitions.Count == 4 && bar.RowDefinitions.Count == 1
                            && bar.Children.Select((child, i) => Grid.GetRow(child) == 0 && Grid.GetColumn(child) == i).All(ok => ok),
                            "wide actions occupy one fixed row under the activity column");
                        Check(Near(left.Bounds.Width, (width - 26) / 2) && Near(right.Bounds.Width, left.Bounds.Width)
                            && Near(right.Bounds.X - left.Bounds.Right, 10)
                            && Near(left.Bounds.Height, right.Bounds.Height)
                            && Near(Bottom(custom), Bottom(bar))
                            && bar.Children.All(child => Near(Bottom(child), Bottom(custom)))
                            && Near(Top(bar) - Bottom(activity), 6),
                            $"wide columns and all four action bottoms align exactly at {width}");
                        Check(terminal.Bounds.Height >= 118 && Near(graph.Bounds.Height, 64)
                            && Near(outer.Extent.Height, left.Bounds.Height + 14),
                            "wide terminal fills the remaining height independently of window height");
                    }
                    Check(outer.Extent.Width <= outer.Viewport.Width, "page has no horizontal overflow");
                    Console.WriteLine($"[LAYOUT] {width}x{viewportHeight}: columns={left.Bounds.Width}/{right.Bounds.Width}, left={left.Bounds.Height}, right={right.Bounds.Height}, terminal={terminal.Bounds.Height}, log={log.Bounds.Height}, actions={bar.Bounds.Height}, leftBottom={Bottom(custom)}, actionBottom={Bottom(bar)}, extent={outer.Extent.Height}, viewport={outer.Viewport.Height}");
                    if (outer.Extent.Height > outer.Viewport.Height)
                    {
                        outer.Offset = new Vector(0, outer.Extent.Height - outer.Viewport.Height); Pump(window);
                        Check(Bottom(bar) <= outer.Bounds.Bottom && Top(bar) >= outer.Bounds.Top,
                            "short windows can scroll all action buttons into view");
                        outer.Offset = default; Pump(window);
                    }
                }
            }
            window.Width = 1000; window.Height = 740; Pump(window);
            log.Focus(); log.SelectionStart = 3; log.SelectionEnd = 17; Pump(window);
            ScrollViewer terminalScroll = log.GetVisualDescendants().OfType<ScrollViewer>().First();
            terminalScroll.Offset = new Vector(40, 0); Pump(window);
            vm.LogText += "\nAppended line."; Pump(window);
            Check(log.SelectionStart == 3 && log.SelectionEnd == 17 && Near(terminalScroll.Offset.X, 40)
                && Near(terminalScroll.Offset.Y, terminalScroll.Extent.Height - terminalScroll.Viewport.Height),
                "appending a log preserves selection and horizontal position while following the tail");
            foreach (double width in new[] { 360.0, 1000.0 })
            {
                window.Width = width; Pump(window);
                log.Focus(); log.SelectionStart = 3; log.SelectionEnd = 17;
                Check(ReferenceEquals(log, view.FindControl<TextBox>("LogBox"))
                    && log.Text == vm.LogText && log.SelectedText == vm.LogText.Substring(3, 14)
                    && log.CaretBrush is ISolidColorBrush { Color.A: 0 },
                    "responsive reparenting retains the bound terminal with selectable text and a transparent caret");
            }
            Rect[] idleBounds = LayoutBounds();
            vm.IsRunning = true; vm.IsPaused = true;
            vm.SetAlert(TabAlertLevel.Warning, new string('W', 300)); Pump(window);
            Check(LayoutBounds().SequenceEqual(idleBounds) && ReferenceEquals(terminal.Child, log)
                && view.FindControl<TextBlock>("SessionAlert")!.Text == vm.AlertMessage && terminalScroll.Viewport.Height > 0,
                "session alerts appear below the terminal without consuming its viewport");
            vm.IsRunning = false; vm.IsPaused = false; vm.SetAlert(TabAlertLevel.None, ""); Pump(window);
            Check(LayoutBounds().SequenceEqual(idleBounds), "clearing session alerts preserves column geometry");
            Check(!view.GetVisualDescendants().OfType<NumericUpDown>().Any(), "no next-update percentage controls");
            double Y(string name) => view.FindControl<Control>(name)!.TranslatePoint(default, view)!.Value.Y;
            Check(Math.Abs(Y("ProxyHostBox") - Y("ProxyPortBox")) < 2 && Math.Abs(Y("ProxyHostBox") - Y("ProxyTypeBox")) < 2, "proxy type, host and port share a row");
            Check(Math.Abs(Y("ProxyUserBox") - Y("ProxyPassBox")) < 2 && Y("ProxyPassBox") > Y("ProxyHostBox"), "proxy user and password share second row");
            log.Focus(); log.SelectionStart = 0; log.SelectionEnd = 10;
            Check(log.SelectionEnd == 10 && log.CaretBrush is ISolidColorBrush { Color.A: 0 }, "read-only log remains selectable without visible caret");
            Check(vm.PrimaryActionText == "START", "idle primary action is START");
            vm.IsRunning = true; Pump(window);
            Check(vm.PrimaryActionText == "Pause" && primary.IsEnabled, "running primary action is enabled Pause");
            vm.IsPaused = true; Pump(window);
            Check(vm.PrimaryActionText == "Resume" && primary.IsEnabled, "manual pause exposes enabled Resume");
            vm.SetAlert(TabAlertLevel.Warning, "Paused by you."); Pump(window);
            Check(view.FindControl<TextBlock>("SessionAlert")!.Text == "Paused by you.", "manual pause uses the common alert below the terminal");
            Check(primary.Background is ISolidColorBrush bg && bg.Color == ((ISolidColorBrush)view.FindResource("StateWarningBrush")!).Color, "Resume has yellow background");
            vm.IsPaused = false; vm.UploadPausedNoLeechers = true; Pump(window);
            vm.SetAlert(TabAlertLevel.Warning, "Paused: no leechers."); Pump(window);
            Check(view.FindControl<TextBlock>("SessionAlert")!.Text == "Paused: no leechers." && vm.PrimaryActionText == "Pause", "automatic pause uses the same alert without impersonating user pause");
            vm.IsAnnouncing = false;
            vm.ManualUpdateCommand.Execute(null); Pump(window);
            Check(vm.ManualUpdatePending, "manual action requires confirmation");
            vm.CancelManualUpdateCommand.Execute(null);
            vm.ManualUpdateCommand.Execute(null); vm.IsRunning = false; Pump(window);
            Check(!vm.ManualUpdatePending && vm.PrimaryActionText == "START" && !vm.IsPaused, "Stop clears pause and pending confirmation");
            vm.SmallTorrentWarning = true; vm.TorrentSize = "4.00 GB"; Pump(window);
            TextBox size = view.FindControl<TextBox>("SizeBox")!;
            Point point = size.TranslatePoint(new Point(8, 8), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump(window);
            Check(ToolTip.GetIsOpen(size), "size tooltip opens on touch/click");
            ToolTip.SetIsOpen(size, false);
            TerminalScrollBarSelfTest.Run(window, view, vm, Check);
            MainWindowViewModel all = new(usePersistence: false);
            MainView main = new() { DataContext = all }; window.Content = main; Pump(window);
            MainWindow defaults = new();
            Check(defaults.Width == 1000 && defaults.Height == 668 && defaults.MinWidth == 360 && defaults.MinHeight == 360,
                "desktop grows by eight units with the taller status bar");
            window.Width = defaults.Width; window.Height = defaults.Height; Pump(window);
            RatioTabView shellTab = main.GetVisualDescendants().OfType<RatioTabView>().First();
            ScrollViewer shellScroll = shellTab.FindControl<ScrollViewer>("WideScroll")!;
            Check(shellScroll.Extent.Height <= shellScroll.Viewport.Height,
                "default window fits the full page without vertical overflow");
            Console.WriteLine($"[SHELL] window={window.ClientSize}, tab={shellTab.Bounds.Size}, extent={shellScroll.Extent}, viewport={shellScroll.Viewport}");
            RatioTabViewModel tab = all.SelectedTab!;
            Button TabButton() => main.FindControl<ItemsControl>("TabStrip")!.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("tab") && ReferenceEquals(b.DataContext, tab));
            Color TabColor() => ((ISolidColorBrush)TabButton().GetVisualDescendants().OfType<ContentPresenter>()
                .First(p => p.Name == "PART_ContentPresenter").Background!).Color;
            tab.IsRunning = true; Pump(window);
            Check(TabColor() == Color.Parse("#164D7A"), "running background is actually blue in the template");
            Check(TabButton().Classes.Contains("running") && !TabButton().Classes.Contains("paused"), "running tab uses blue state");
            tab.IsPaused = true; Pump(window);
            Check(TabButton().Classes.Contains("paused") && !TabButton().Classes.Contains("running"), "manual pause selects yellow pulse state");
            tab.IsPaused = false; tab.UploadPausedNoLeechers = true; Pump(window);
            Check(TabButton().Classes.Contains("paused"), "automatic pause selects yellow pulse state");
            tab.IsRunning = false; tab.HasFinished = true; Pump(window);
            Check(TabButton().Classes.Contains("finished") && !TabButton().Classes.Contains("paused"), "successful objective selects green state");
            Check(TabColor() == Color.Parse("#1E5638"), "finished background is actually green in the template");
            tab.TorrentFilePath = "" + Guid.NewGuid() + ".torrent"; Pump(window);
            Check(!tab.HasFinished && !TabButton().Classes.Contains("finished"), "changing torrent clears green state");
            tab.HasFinished = true; tab.IsRunning = true; Pump(window);
            Check(!tab.HasFinished && TabButton().Classes.Contains("running"), "starting again replaces green with blue");
            tab.IsRunning = false;
            var choice = main.ShowCloseDialogAsync(true); Pump(window);
            Check(main.FindControl<Border>("CloseOverlay")!.IsVisible, "close confirmation is shown in shared view");
            main.CancelCloseDialog(); Check(choice.IsCompleted && choice.Result is null, "close confirmation can be cancelled");
            choice = main.ShowCloseDialogAsync(false); Pump(window);
            Check(!main.FindControl<Button>("BackgroundCloseButton")!.IsVisible, "no background button without a tray");
            main.CancelCloseDialog();
            RatioTabViewModel resume = new("Resume test");
            bool previousActivity = SessionActivity.IsActive;
            resume.IsPreparing = true;
            Check(!resume.InputsEnabled && !resume.StartCommand.CanExecute(null) && SessionActivity.IsActive == previousActivity,
                "permission preparation locks session inputs without starting background session activity");
            resume.IsPreparing = false;
            var metadata = TorrentFormatSelfTest.V2Fixture();
            resume.ApplyState(new TabState { TorrentHash = Convert.ToHexString(metadata.InfoHash), Uploaded = 12345, Downloaded = 3 });
            resume.LoadTorrentMetadataFromBytes(TorrentFormatSelfTest.V2FixtureBytes());
            Check(resume.CaptureState().Uploaded == 12345, "same torrent retains persisted counters");
            resume.HasFinished = true;
            resume.SeedersText = "Seeders: 5";
            resume.LeechersText = "Leechers: 3";
            resume.TotalTimeText = "12:34";
            resume.TimerText = "01:23";
            resume.NextUpdateCountdown = "00:01:23";
            resume.LoadTorrentMetadataFromBytes([1, 2, 3]);
            Check(!resume.HasFinished && resume.HashHex.Length == 0 && resume.AlertLevel == TabAlertLevel.Error
                && resume.StatusText.StartsWith("Failed to load torrent:", StringComparison.Ordinal),
                "invalid torrent selection clears successful completion and reports its current failure");
            Check(resume.SeedersText == "Seeders: -" && resume.LeechersText == "Leechers: -"
                && resume.TotalTimeText == "00:00" && resume.TimerText == "idle" && resume.NextUpdateCountdown == "00:00:00",
                "clearing torrent counters also clears the previous torrent's swarm and timing statistics");
            resume.LoadTorrentMetadataFromBytes(TorrentFormatSelfTest.V2FixtureBytes());
            Check(resume.CaptureState().Uploaded == 0, "invalid file clears unrelated resume counters");
            Check(resume.AlertLevel == TabAlertLevel.None && resume.AlertMessage.Length == 0,
                "a valid torrent selection clears the preceding load failure");
            MainWindowViewModel limited = new(usePersistence: false);
            for (int index = 1; index <= SessionRepository.MaxTabs; index++) limited.AddTabCommand.Execute(null);
            Check(limited.Tabs.Count == SessionRepository.MaxTabs && !limited.AddTabCommand.CanExecute(null),
                "adding tabs respects the storage limit before a session becomes unsaveable");
            Task closeLimited = limited.CloseTabCommand.ExecuteAsync(limited.SelectedTab);
            Check(closeLimited.IsCompletedSuccessfully && limited.AddTabCommand.CanExecute(null),
                "closing a tab below the storage limit enables adding tabs again");
            foreach (RatioTabViewModel limitedTab in limited.Tabs) limitedTab.ReleaseResources();
            string reselectPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ratiomaster-reselect-" + Guid.NewGuid().ToString("N") + ".torrent");
            try
            {
                System.IO.File.WriteAllBytes(reselectPath, TorrentFormatSelfTest.V2FixtureBytes());
                resume.SetTorrentPath(reselectPath);
                System.IO.File.WriteAllBytes(reselectPath, [1, 2, 3]);
                resume.SetTorrentPath(reselectPath);
                Check(resume.HashHex.Length == 0 && resume.AlertLevel == TabAlertLevel.Error,
                    "reselecting the same path reloads its current bytes instead of retaining stale metadata");
            }
            finally { System.IO.File.Delete(reselectPath); }
            resume.ApplyState(new TabState { TorrentHash = new string('F', 40), Uploaded = 99, Downloaded = 3 });
            resume.LoadTorrentMetadataFromBytes(TorrentFormatSelfTest.V2FixtureBytes());
            Check(resume.CaptureState().Uploaded == 0, "different torrent cannot reuse saved counters");
            resume.ReleaseResources();
            resume.SetTorrentPath("closed-tab.torrent");
            Check(!resume.InputsEnabled && !resume.StartCommand.CanExecute(null) && resume.CaptureState().TorrentSourcePath.Length == 0
                && resume.TorrentFilePath != "closed-tab.torrent",
                "a released tab rejects a delayed file-picker result and cannot restart");
            SessionData settings = new() { CloseBehavior = CloseBehavior.Background };
            RatioTabViewModel identity = new("Identity");
            identity.ApplyState(new TabState { Family = "uTorrent", Version = "3.5.5",
                CustomKey = "1234ABCD", CustomPeerId = "-UT3550-savedvalue01", CustomPort = "43210", CustomPeers = "30" });
            Check(identity.CustomKey == "1234ABCD" && identity.CustomPeerId == "-UT3550-savedvalue01"
                && identity.CustomPort == "43210" && identity.CustomPeers == "30",
                "restoring a profile retains saved custom identity values");
            identity.SelectedFamily = "qBittorrent";
            Check(identity.CustomPeerId.StartsWith("-qB5100-", StringComparison.Ordinal) && identity.CustomKey != "1234ABCD",
                "changing client family always generates matching identity values");
            identity.CustomPeerId = "sentinel";
            identity.SelectedVersion = "5.0.3";
            Check(identity.CustomPeerId.StartsWith("-qB5030-", StringComparison.Ordinal),
                "changing client version always generates matching identity values");
            string json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonContext.Default.SessionData);
            Check(System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.SessionData)!.CloseBehavior == CloseBehavior.Background, "remembered close behavior round-trips through session JSON");
            RatioTabViewModel reset = new("Reset");
            reset.ApplyState(new TabState { TorrentHash = Convert.ToHexString(metadata.InfoHash), Uploaded = 12345, Downloaded = 3,
                Tracker = "https://fixture.invalid/custom", Family = "qBittorrent", Version = "5.1.0" });
            reset.LogText = "A previous exchange";
            reset.GraphValues = new[] { 1.0, 2.0, 3.0 };
            reset.ClearLogCommand.Execute(null);
            Check(reset.LogText.Length == 0 && reset.GraphValues.All(value => value == 0) && reset.CaptureState().Uploaded == 12345,
                "Clear resets terminal and graph while preserving torrent counters");
            reset.LogText = "Another exchange";
            reset.GraphValues = new[] { 1.0, 2.0 };
            reset.SetDefaultsCommand.Execute(null);
            Check(reset.LogText.Length == 0 && reset.GraphValues.All(value => value == 0), "Set defaults also clears terminal and graph");
            reset.SelectedStopWhen = "After time:";
            var stopSettings = ((RatioMaster.Engine.IEngineHost)reset).StopCondition;
            Check(stopSettings.Condition == "After time:" && stopSettings.Value == "3600",
                "live stop settings publish the condition and its threshold as one immutable pair");
            reset.StopValue = "120";
            Check(((RatioMaster.Engine.IEngineHost)reset).StopCondition.Value == "120" && stopSettings.Value == "3600",
                "editing a stop threshold does not mutate a snapshot being read by the engine");
            Check(reset.CaptureState().Uploaded == 12345 && reset.CaptureState().Downloaded == 3,
                "Set defaults preserves this torrent's saved counters");
            Check(reset.CaptureState().Tracker == "https://fixture.invalid/custom", "tracker override survives persistence and settings reset");
            reset.CustomKey = "00112233";
            Check(reset.CaptureState().KeyIsGenerated == false, "typing a custom key disables generated-key rotation");
            reset.RegenerateValuesCommand.Execute(null);
            Check(reset.CaptureState().KeyIsGenerated == true, "explicit regeneration records generated-key provenance");
            reset.ApplyState(new TabState { Family = "Missing", Version = "1.2.3", CustomKey = "saved-custom", CustomPeerId = "saved-peer" });
            Check(reset.SelectedFamily == ClientCatalog.DefaultFamily && reset.SelectedVersion == ClientCatalog.DefaultVersion
                && reset.CustomKey == "saved-custom" && reset.CustomPeerId == "saved-peer" && reset.AlertLevel == TabAlertLevel.Warning,
                "unknown profile falls back visibly while retaining custom identity");
            all.AddTabCommand.Execute(null);
            RatioTabViewModel neighbour = all.SelectedTab!;
            neighbour.CustomKey = "neighbour-key";
            string neighbourBefore = System.Text.Json.JsonSerializer.Serialize(neighbour.CaptureState(), AppJsonContext.Default.TabState);
            tab.SetDefaultsCommand.Execute(null);
            Check(System.Text.Json.JsonSerializer.Serialize(neighbour.CaptureState(), AppJsonContext.Default.TabState) == neighbourBefore,
                "resetting one tab leaves its neighbour's persisted state identical");
            all.ShowAboutCommand.Execute(null); Pump(window);
            Check(main.FindControl<Border>("AboutOverlay")!.IsVisible && !main.FindControl<Grid>("MainContent")!.IsEnabled
                && all.UpdateMessage.Contains("only when requested"), "About is modal and does not check updates on opening");
            Check(main.TryDismissDialog() && !all.AboutOpen, "About supports Escape and Android dialog dismissal");
            Pump(window);
            Check(main.FindControl<Grid>("MainContent")!.IsEnabled, "closing About restores the active tab controls");
            all.ShowAboutCommand.Execute(null);
            all.ShowChangelogCommand.Execute(null);
            foreach ((double width, double height) in new[] { (1000.0, 668.0), (660.0, 500.0), (360.0, 740.0), (740.0, 360.0) })
            {
                window.Width = width; window.Height = height; Pump(window);
                ScrollViewer history = main.FindControl<ScrollViewer>("ChangelogScroll")!;
                Border historyOverlay = main.FindControl<Border>("ChangelogOverlay")!;
                Check(historyOverlay.IsVisible && !main.FindControl<Grid>("MainContent")!.IsEnabled
                    && history.Viewport.Height > 0 && history.Extent.Width <= history.Viewport.Width + 0.01,
                    $"embedded changelog wraps and scrolls inside the dialog at {width}x{height}");
                Button back = main.FindControl<Button>("ChangelogBackButton")!;
                Point backBottom = back.TranslatePoint(new Point(0, back.Bounds.Height), main)!.Value;
                Check(backBottom.Y <= main.Bounds.Height && all.ChangelogReleases.Count > 1
                    && all.ChangelogReleases[0].Version == AppInfo.Version, "changelog has the current release and a visible touch-sized Back button");
            }
            Check(main.TryDismissDialog() && all.AboutOpen && !all.ChangelogOpen, "Back from changelog returns to the information panel");
            Check(main.TryDismissDialog() && !all.HasInfoOverlay, "the information panel can then be dismissed");
            foreach (double width in new[] { 1000.0, 360.0 })
            {
                window.Width = width; window.Height = 668; Pump(window);
                Border status = main.FindControl<Border>("StatusBar")!;
                WrapPanel stats = main.FindControl<WrapPanel>("StatsPanel")!;
                Check(status.Bounds.Height >= 34 && stats.Children.All(child => child.Bounds.Right <= stats.Bounds.Width + 0.01
                    && child.Bounds.Bottom <= stats.Bounds.Height + 0.01), "larger status values remain inside the status bar at " + width);
            }
            Check(((Avalonia.Controls.Documents.Run)main.FindControl<TextBlock>("SeedersStatus")!.Inlines![1]).Foreground
                    is ISolidColorBrush seedsBrush && seedsBrush.Color == ((ISolidColorBrush)main.FindResource("StateSuccessBrush")!).Color
                && ((Avalonia.Controls.Documents.Run)main.FindControl<TextBlock>("LeechersStatus")!.Inlines![1]).Foreground
                    is ISolidColorBrush peersBrush && peersBrush.Color == ((ISolidColorBrush)main.FindResource("StateErrorBrush")!).Color,
                "seeder counts are green and leecher counts are red");
            vm.LogText = "First response\nWARNING tracker\nLast response\n";
            vm.LogFilter = "warning";
            Check(vm.VisibleLogText == "WARNING tracker" && vm.LogText.Contains("First response"),
                "log filtering is case-insensitive and preserves the complete retained journal");
            vm.ClearLogFilterCommand.Execute(null);
            Check(vm.VisibleLogText == vm.LogText, "clearing the log filter restores all retained lines");
            string? imageDirectory = Environment.GetEnvironmentVariable("RM_UI_SNAPSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(imageDirectory))
            {
                System.IO.Directory.CreateDirectory(imageDirectory);
                all.SelectedTab!.SeedersText = "Seeders: 257";
                all.SelectedTab.LeechersText = "Leechers: 6";
                all.SelectedTab.LogText = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"[{i:00}:00:00] Example tracker response: interval=1800 peers=200 " + new string('x', 90)));
                foreach ((double width, double height, string name) in new[] { (1000.0, 668.0, "landscape"), (360.0, 740.0, "portrait") })
                {
                    window.Width = width; window.Height = height; Pump(window);
                    using Avalonia.Media.Imaging.RenderTargetBitmap frame = new(new PixelSize((int)width, (int)height), new Vector(96, 96));
                    frame.Render(window);
                    frame.Save(System.IO.Path.Combine(imageDirectory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                all.ShowChangelogCommand.Execute(null);
                foreach ((double width, double height, string name) in new[] { (1000.0, 668.0, "changelog-wide"), (360.0, 740.0, "changelog-phone") })
                {
                    window.Width = width; window.Height = height; Pump(window);
                    using Avalonia.Media.Imaging.RenderTargetBitmap frame = new(new PixelSize((int)width, (int)height), new Vector(96, 96));
                    frame.Render(window);
                    frame.Save(System.IO.Path.Combine(imageDirectory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            Console.WriteLine($"{count} UI checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[FAIL] " + ex); return 1; }
        finally { vm.IsRunning = false; window.Close(); }
    }
}
#endif
