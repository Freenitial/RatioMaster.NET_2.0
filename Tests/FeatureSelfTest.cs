#if DEBUG && !ANDROID
namespace RatioMaster;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RatioMaster.Engine;
using RatioMaster.Localization;
using RatioMaster.Models;
using RatioMaster.Services;
using RatioMaster.ViewModels;
using RatioMaster.Views;

/// <summary>Tab-level features: several torrents at once, duplicates, migrations, live language switching.</summary>
internal static class FeatureSelfTest
{
    internal static void Run(Action<bool, string> check)
    {
        Batches(check);
        Duplicates(check);
        Migrations(check);
        Progress(check);
        Languages(check);
        Reopening(check);
    }

    /// <summary>Renders the main window in each language (wide and phone width) for visual review of layout and glyphs.</summary>
    internal static void LanguageSnapshots(string directory, IEnumerable<string> codes)
    {
        Directory.CreateDirectory(directory);
        string before = Language.Preference;
        try
        {
            foreach (string code in codes)
            {
                Language.Apply(code);
                using MainWindowViewModel model = new(usePersistence: false);
                RatioTabViewModel tab = model.Tabs[0];
                tab.SetTorrentContent(Torrent("https://tracker.example/announce?passkey=0123456789", 99), "Example.Linux.ISO.torrent");
                tab.SeedersCountText = "257";
                tab.LeechersCountText = "6";
                tab.AppendSampleLog();
                foreach ((double width, double height, string name) in new[] { (1000.0, 668.0, "wide"), (360.0, 740.0, "phone") })
                {
                    Window window = new() { Width = width, Height = height, Content = new MainView { DataContext = model } };
                    window.FlowDirection = Language.Current.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                    window.Show();
                    for (int pass = 0; pass < 3; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                    using Avalonia.Media.Imaging.RenderTargetBitmap frame = new(new Avalonia.PixelSize((int)width, (int)height), new Avalonia.Vector(96, 96));
                    frame.Render(window);
                    frame.Save(Path.Combine(directory, code + "-" + name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    (window.Content as MainView)?.Detach();
                    window.Close();
                }
            }
        }
        finally { Language.ResetForTests(); }
    }

    /// <summary>A minimal v1 single-file torrent; <paramref name="seed"/> makes its info-hash unique.</summary>
    internal static byte[] Torrent(string announce, int seed, bool isPrivate = false)
    {
        using MemoryStream output = new();
        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        string name = "fixture" + seed + ".bin";
        Write("d8:announce" + announce.Length + ":" + announce + "4:infod6:lengthi" + (1048576 + seed) + "e4:name" + name.Length + ":" + name
            + "12:piece lengthi1048576e6:pieces40:");
        output.Write(Enumerable.Range(0, 40).Select(value => (byte)(value + seed)).ToArray());
        Write((isPrivate ? "7:privatei1e" : string.Empty) + "ee");
        return output.ToArray();
    }

    private static void Batches(Action<bool, string> check)
    {
        using MainWindowViewModel window = new(usePersistence: false);
        RatioTabViewModel first = window.Tabs[0];
        byte[] a = Torrent("http://tracker.invalid/announce", 1), b = Torrent("http://tracker.invalid/announce", 2);
        var result = window.AddTorrents([new("a.torrent", Data: a), new("b.torrent", Data: b), new("again.torrent", Data: a),
            new("broken.torrent", Data: [1, 2, 3])], first);
        Dispatcher.UIThread.RunJobs();
        check(result == new MainWindowViewModel.BatchResult(2, 1, 1, 0) && window.Tabs.Count == 2 && first.HasTorrent,
            "several torrents fill the empty tab first, then open new tabs, skipping duplicates and unreadable files");
        check(ReferenceEquals(window.SelectedTab, window.Tabs[1]) && window.Tabs.Count(tab => tab.IsActive) == 1,
            "the last added torrent is selected and only one tab is active");
        result = window.AddTorrents([new("a.torrent", Data: a)], window.Tabs[1]);
        check(result.Duplicates == 1 && window.Tabs.Count == 2, "a torrent already open for the same tracker is not opened again");

        List<TorrentSource> many = Enumerable.Range(10, MainWindowViewModel.BatchConfirmationThreshold + 1)
            .Select(seed => new TorrentSource("t" + seed + ".torrent", Data: Torrent("http://tracker.invalid/announce", seed))).ToList();
        window.RequestAddTorrents(many, null);
        check(window.HasPendingBatch && window.PendingBatchCount == many.Count && window.Tabs.Count == 2,
            "a large addition waits for confirmation");
        window.CancelBatchCommand.Execute(null);
        check(!window.HasPendingBatch && window.Tabs.Count == 2, "cancelling a large addition opens nothing");
        window.RequestAddTorrents(many, null);
        window.RequestAddTorrents([new TorrentSource("late.torrent", Data: Torrent("http://tracker.invalid/announce", 99))], null);
        check(window.PendingBatchCount == many.Count + 1, "files arriving while a large addition waits join it instead of replacing it");
        window.ConfirmBatchCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        check(window.Tabs.Count == 3 + many.Count, "confirming a large addition opens every torrent");

        string folder = Path.Combine(Path.GetTempPath(), "rm-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "b.torrent"), b);
            File.WriteAllBytes(Path.Combine(folder, "A.TORRENT"), a);
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a torrent");
            Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllBytes(Path.Combine(folder, "nested", "c.torrent"), Torrent("http://tracker.invalid/announce", 3));
            IReadOnlyList<TorrentSource> expanded = MainWindowViewModel.ExpandPaths([folder]);
            check(expanded.Select(source => source.Name).SequenceEqual(["A.TORRENT", "b.torrent"]),
                "a folder contributes its own .torrent files in name order, without subfolders or other files");
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = folder;
                check(MainWindowViewModel.ExpandPaths(["b.torrent"]) is [{ Path: { } absolute }] && absolute == Path.Combine(folder, "b.torrent"),
                    "a relative command-line path is saved as an absolute path");
            }
            finally { Environment.CurrentDirectory = previous; }
            string renamed = Path.Combine(folder, "downloaded.bin");
            File.WriteAllBytes(renamed, Torrent("http://tracker.invalid/announce", 41));
            using MainWindowViewModel picker = new(usePersistence: false);
            var picked = picker.AddTorrents([new TorrentSource("downloaded.bin", renamed)], picker.Tabs[0]);
            check(picked.Added == 1 && picker.Tabs[0].HasTorrent && picker.Tabs[0].TorrentFilePath == renamed,
                "a torrent picked with the all-files filter opens whatever its extension");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Duplicates(Action<bool, string> check)
    {
        using MainWindowViewModel window = new(usePersistence: false);
        RatioTabViewModel first = window.Tabs[0];
        byte[] torrent = Torrent("HTTP://Tracker.Invalid:80/announce?passkey=AbC#fragment", 7);
        first.SetTorrentContent(torrent, "one.torrent");
        window.AddTabCommand.Execute(null);
        RatioTabViewModel second = window.Tabs[1];
        second.SetTorrentContent(Torrent("http://tracker.invalid/announce?passkey=AbC", 7), "two.torrent");
        check(second.AlertLevel == TabAlertLevel.Warning && second.AlertMessage.Contains(first.TabName, StringComparison.Ordinal),
            "opening a torrent already open for the same tracker warns and names the other tab");
        check(RatioTabViewModel.NormalizeTracker("HTTP://Tracker.Invalid:80/announce?passkey=AbC#x") == "http://tracker.invalid/announce?passkey=AbC"
            && RatioTabViewModel.NormalizeTracker("https://t.invalid:8443/a") == "https://t.invalid:8443/a",
            "tracker identity folds scheme, host and default port but keeps the passkey");
        first.IsRunning = true;
        second.StartCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        check(!second.IsRunning && second.AlertLevel == TabAlertLevel.Error && second.AlertMessage.Contains(first.TabName, StringComparison.Ordinal),
            "a second session for a torrent another tab announces to the same tracker is refused");
        first.IsRunning = false;
        RatioTabViewModel third = new("Other tracker") { FindTwin = window.FindTwin };
        third.SetTorrentContent(Torrent("http://other.invalid/announce", 7), "three.torrent");
        check(third.AlertLevel == TabAlertLevel.None, "the same torrent on another tracker is not a duplicate");
    }

    private static void Migrations(Action<bool, string> check)
    {
        RatioTabViewModel retired = new("Retired");
        retired.ApplyState(new TabState { Family = "BitTorrent", Version = "7.10.3 (44429)", KeyIsGenerated = true, PeerIdIsGenerated = true,
            CustomKey = "0123ABCD", CustomPeerId = "hex:2D4254376133532D8DAD0102030405060708090A" });
        check(retired.SelectedFamily == "BitTorrent" && retired.SelectedVersion == "7.11.0 (47255)"
            && retired.CustomPeerId.StartsWith("hex:2D4254376230532D97B8", StringComparison.Ordinal) && retired.AlertLevel == TabAlertLevel.Warning,
            "a retired version moves to the current version of the same client with a matching identity");

        RatioTabViewModel rebuilt = new("Rebuilt");
        rebuilt.ApplyState(new TabState { Family = "uTorrent", Version = "3.6.0", KeyIsGenerated = true, PeerIdIsGenerated = true,
            CustomKey = "0123ABCD", CustomPeerId = "hex:2D5554333630532DECB6010203040506070809FF" });
        check(rebuilt.CustomPeerId.StartsWith("hex:2D5554333630532D96B8", StringComparison.Ordinal) && rebuilt.AlertLevel == TabAlertLevel.None,
            "a generated identity of an earlier build is regenerated for the current build");

        string route = ListeningPortBook.Route(new ProxyConfig());
        RatioTabViewModel legacy = new("Legacy");
        legacy.ApplyState(new TabState { Family = "qBittorrent", Version = "4.6.5", CustomPort = "43210", CustomPeers = "200" }, formatVersion: 2);
        check(legacy.CustomPort.Length == 0 && legacy.CustomPeers.Length == 0 && ListeningPortBook.Peek("qBittorrent 4.6.5", route) == 43210,
            "ports and peer counts filled automatically by earlier versions become automatic again and keep the client's port");
        check(legacy.PortPlaceholder.Contains(ListeningPortBook.Peek("qBittorrent 4.6.5", route)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "the empty port field shows the client's shared port");
        RatioTabViewModel custom = new("Custom");
        custom.ApplyState(new TabState { Family = "qBittorrent", Version = "4.6.5", CustomPort = "43211", CustomPeers = "30" });
        check(custom.CustomPort == "43211" && custom.CustomPeers == "30", "current sessions keep a port and peer count typed by the user");
        custom.RegenerateValuesCommand.Execute(null);
        check(custom.CustomPort == "43211" && custom.CustomPeers == "30", "Regenerate renews the identity without touching port or peer count");
    }

    private static void Progress(Action<bool, string> check)
    {
        byte[] torrent = Torrent("http://tracker.invalid/announce", 30);
        long size = 1048576 + 30;
        string hash = Convert.ToHexString(new BitTorrent.Torrent(new MemoryStream(torrent)).InfoHash);
        RatioTabViewModel legacy = new("Legacy progress");
        legacy.SetTorrentContent(torrent, "legacy.torrent");
        legacy.ApplyState(new TabState { TorrentHash = hash, Finished = "25", Downloaded = size, Uploaded = 10, TorrentSourcePath = string.Empty }, formatVersion: 2);
        check(legacy.CaptureState().Downloaded == size - (long)(size * 0.25m) && legacy.FinishedText == "100",
            "earlier sessions' downloaded bytes lose the initially present share and Finished follows the reached progress");

        RatioTabViewModel missing = new("Missing torrent");
        missing.ApplyState(new TabState { TorrentHash = hash, Finished = "25", Downloaded = size, Uploaded = 10 }, formatVersion: 2);
        check(missing.CaptureState() is { LegacyCounters: true } kept && kept.Downloaded == size,
            "counters of an earlier session whose torrent is missing wait, unchanged, for the torrent");
        missing.SetTorrentContent(torrent, "found.torrent");
        check(missing.CaptureState() is { LegacyCounters: false } migrated && migrated.Downloaded == size - (long)(size * 0.25m),
            "they are migrated once the torrent is loaded again");

        RatioTabViewModel resumed = new("Resumed");
        resumed.SetTorrentContent(torrent, "resumed.torrent");
        resumed.ApplyState(new TabState { TorrentHash = hash, Finished = "49.99", ProgressFinished = "49.99", Have = size / 2, Downloaded = 5 });
        check(resumed.CaptureState().Have == size / 2 && resumed.CaptureState().ProgressFinished == "49.99",
            "exact progress is restored while Finished still shows it");
        resumed.FinishedText = "10";
        check(resumed.CaptureState().ProgressFinished != resumed.CaptureState().Finished,
            "an edited Finished no longer matches the stored progress, so the next start uses the edit");

        RatioTabViewModel chosen = new("Chosen");
        chosen.SetTorrentContent(torrent, "chosen.torrent");
        chosen.ApplyState(new TabState { TorrentHash = hash, Finished = "49.99", ProgressFinished = "49.99", Have = size / 2, FinishedChoice = "10" });
        chosen.SetTorrentContent(Torrent("http://tracker.invalid/announce", 31), "next.torrent");
        check(chosen.FinishedText == "10", "another torrent starts from the percentage the user chose, not from the progress of the previous one");
    }

    private static void Reopening(Action<bool, string> check)
    {
        // A retired version hands its port to its successor, as an upgraded client keeps its settings.
        string route = ListeningPortBook.Route(new ProxyConfig());
        ListeningPortBook.Adopt(new ClientProfileDefinition { Family = "BitTorrent", Version = "7.10.3 (44429)", Headers = "", Query = "", PeerIdPrefix = "",
            PortRanges = [new PortRange(10000, 64999)] }, route, 23456);
        RatioTabViewModel upgraded = new("Upgraded");
        upgraded.ApplyState(new TabState { Family = "BitTorrent", Version = "7.10.3 (44429)" });
        check(ListeningPortBook.Peek("BitTorrent 7.11.0 (47255)", route) == 23456, "the current version inherits the port of the retired version");

        // The duplicate warning follows the tracker finally restored, not the one named in the file.
        using MainWindowViewModel window = new(usePersistence: false);
        byte[] torrent = Torrent("http://tracker.invalid/announce", 50);
        string hash = Convert.ToHexString(new BitTorrent.Torrent(new MemoryStream(torrent)).InfoHash);
        window.Tabs[0].SetTorrentContent(torrent, "first.torrent");
        window.AddTabCommand.Execute(null);
        RatioTabViewModel second = window.Tabs[1];
        second.SetTorrentContent(torrent, "second.torrent");
        second.ApplyState(new TabState { TorrentHash = hash, Tracker = "http://other.invalid/announce" });
        check(second.AlertLevel == TabAlertLevel.None, "a restored tab announcing to another tracker is not reported as a duplicate");

        // Ratio, progress and status texts follow a change of interface language.
        IReadOnlyDictionary<string, string> french = Language.Load("fr")!;
        RatioTabViewModel tab = new("Texts") { Ratio = 1.5, ProgressPercent = 12.5 };
        try
        {
            Language.Apply("fr");
            tab.RefreshLocalizedOptions();
            check(tab.RatioText == "1" + french["DecimalSeparator"] + "50" && tab.ProgressText.Contains("12" + french["DecimalSeparator"] + "5", StringComparison.Ordinal)
                && tab.StatusText == french["StatusIdle"], "ratio, progress and status are written in the interface language");
        }
        finally { Language.ResetForTests(); }
        check(tab.RatioText == "1.50" && tab.StatusText == Strings.StatusIdle, "they return to English with the language");
    }

    private static void Languages(Action<bool, string> check)
    {
        IReadOnlyDictionary<string, string>? french = Language.Load("fr");
        check(french is not null, "the French translation is embedded");
        RatioTabViewModel tab = new("Language");
        try
        {
            Language.Apply("fr");
            tab.RefreshLocalizedOptions();
            check(tab.StopWhenOptions.First(option => option.Value == StopCondition.UploadAbove).Label == french!["StopUploadAbove"]
                && tab.PrimaryActionText == french["ActionStart"] && tab.SelectedStopWhen == StopCondition.UploadAbove,
                "switching language relabels options without changing the selected rule");
            Language.Apply("ar");
            using MainWindowViewModel arabic = new(usePersistence: false);
            MainView view = new() { DataContext = arabic };
            check(view.FlowDirection == FlowDirection.RightToLeft, "Arabic lays the window out from right to left");
            RatioTabView page = new() { DataContext = tab };
            check(page.FindControl<TextBox>("LogBox")!.FlowDirection == FlowDirection.LeftToRight,
                "the terminal, hashes and identities stay left to right in a right-to-left language");
            view.Detach();
        }
        finally
        {
            Language.ResetForTests();
            tab.RefreshLocalizedOptions();
        }
        check(tab.PrimaryActionText == "START", "returning to English restores the English texts");
    }
}
#endif
