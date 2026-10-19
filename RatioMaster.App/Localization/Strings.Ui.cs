namespace RatioMaster.Localization;

using RatioMaster.Engine;

/// <summary>Interface texts: labels, tooltips, statuses, dialogs and notifications.</summary>
public static partial class Strings
{
    // ── Formatting ──
    public static string DecimalSeparator => T(".");
    public static string UnitBytes(long count) => F1("{0} bytes", count);
    public static string UnitKiB => T("KiB");
    public static string UnitMiB => T("MiB");
    public static string UnitGiB => T("GiB");
    public static string UnitTiB => T("TiB");
    public static string PercentValue(string number) => F1("{0}%", number);
    public static string UnitRatio => T("ratio");
    public static string UnitSeconds => T("s");

    // ── Window ──
    public static string WindowTitle(string version) => F1("Ratiomaster.NET {0}   -   Created by NikolayIT  -  Forked by HdiaSaad  -  Re-forked by Freenitial", version);
    public static string LocalIp => T("Local IP");
    public static string AboutTip => T("About, language, diagnostics and updates");
    public static string NewTab => T("New RatioMaster tab");
    public static string CloseTab => T("Close tab");
    public static string ScrollTabsBack => T("Scroll tabs back");
    public static string ScrollTabsForward => T("Scroll tabs forward");

    // ── Status bar ──
    public static string StatRatio => T("Ratio");
    public static string StatDone => T("Done");
    public static string StatDoneTip => T("Share of the torrent's data present now: what the tracker learns through the remaining bytes.");
    public static string StatUploadedTip => T("Uploaded in total for this torrent.");
    public static string StatDownloadedTip => T("Downloaded by RatioMaster for this torrent; data that was already present is not counted.");
    public static string StatSeeders => T("Seeders:");
    public static string StatLeechers => T("Leechers:");
    public static string StatTime => T("Time");
    public static string StatNext => T("Next");

    // ── Groups and labels ──
    public static string GroupTorrentFile => T("Torrent file");
    public static string TorrentPathPlaceholder => T("Drag & drop, paste a path, or Browse…");
    public static string Browse => T("Browse…");
    public static string BrowseTip => T("Choose one .torrent file for this tab, or several to open each in its own tab (Ctrl+O).");
    public static string AddFolder => T("Folder…");
    public static string AddFolderTip => T("Open every .torrent file of a folder, each in its own tab (Ctrl+Shift+O).");
    public static string GroupTorrentInfo => T("Torrent info");
    public static string LabelTracker => T("Tracker");
    public static string TrackerTip => T("Tracker URL used by this tab. Editable while stopped.");
    public static string LabelHash => T("Hash");
    public static string LabelSize => T("Size");
    public static string SizeHint => T("10+ GiB is better!");
    public static string SizeTip => T("Total size of the torrent contents. A torrent of 10 GiB or more is preferable. Size alone does not prevent tracker detection or bans.");
    public static string SizeTipSmall => T("This torrent is smaller than 8 GiB. Consider a torrent of 10 GiB or more. Size is only a guideline; it does not prevent tracker detection or bans.");
    public static string GroupSpeed => T("Speed options");
    public static string LabelUploadSpeed => T("Upload MiB/s");
    public static string LabelDownloadSpeed => T("Download MiB/s");
    public static string Random => T("Random");
    public static string RandomUploadTip => T("Vary the upload rate along a smooth, gradually drifting curve (about ×0.55 to ×1.15 of the value on the left) instead of a perfectly flat line. Unchecked = an exact, constant rate.");
    public static string RandomDownloadTip => T("Same smooth variation for the download rate, independent of the upload one.");
    public static string GroupOptions => T("Options");
    public static string LabelInterval => T("Interval (s)");
    public static string LabelFinished => T("Finished (%)");
    public static string FinishedTip => T("How much of the torrent is already present when the session starts. After a session it shows the progress reached, so the next start carries on from there; type another value to start from it instead.");
    public static string LabelClient => T("Client");
    public static string LabelVersion => T("Version");
    public static string LabelStop => T("Stop");
    public static string StopValueName => T("Stop threshold");
    public static string GroupIdentity => T("Custom client simulation");
    public static string LabelKey => T("Client key");
    public static string LabelPort => T("Port");
    public static string PortTip => T("Leave empty to use this client's own port, shared by all its tabs like a real client listening on one port. A typed port applies to this tab only.");
    public static string LabelPeerId => T("Peer ID");
    public static string LabelPeers => T("Peers");
    public static string PeersTip => T("Number of peers requested from the tracker. Leave empty for the client's default.");
    public static string AutoValue(string value) => F1("auto · {0}", value);
    public static string AutoPort => T("auto");
    public static string RealisticMode => T("Realistic mode");
    public static string RealisticModeTip => T("Answer connecting peers the way the emulated client does: its extension handshake, piece availability and keep-alives, encrypted connections included. Speed variation is controlled by the Random checkboxes in Speed options.");
    public static string Regenerate => T("Regenerate");
    public static string RegenerateTip => T("Generate a new client key and peer ID.");
    public static string GroupOther => T("Other settings");
    public static string UseListener => T("Use TCP listener");
    public static string UseListenerTip => T("Accept incoming BitTorrent connections on the client's port. Tabs of one client share the port. Another application using that port prevents listening; it is retried automatically.");
    public static string RequestScrape => T("Request scrape");
    public static string RequestScrapeTip => T("Request seeder and leecher counts from the tracker when the announce response does not include them. Some trackers do not support scrape.");
    public static string GroupProxy => T("Proxy server settings");
    public static string LabelProxyType => T("Type");
    public static string LabelProxyHost => T("Host");
    public static string ProxyPortName => T("Proxy port");
    public static string LabelProxyUser => T("User");
    public static string LabelProxyPassword => T("Pass");
    public static string GroupActivity => T("Activity");
    public static string EnableLog => T("Enable log");
    public static string EnableLogTip => T("Show tracker requests, responses and session events in the terminal.");
    public static string Clear => T("Clear");
    public static string ClearTip => T("Clear the terminal log and graph without resetting torrent counters.");
    public static string Save => T("Save");
    public static string SaveLogTip => T("Save displayed log lines, including the current filter, to a text file.");
    public static string LogOptions => T("Filter, copy and follow the log");
    public static string FilterLines => T("Filter displayed lines");
    public static string SearchLog => T("Search log…");
    public static string ClearFilter => T("Clear filter");
    public static string FollowLog => T("Follow new log entries");
    public static string CopyLines => T("Copy displayed lines");
    public static string UploadRate => T("Upload rate");

    // ── Actions ──
    public static string ActionStart => T("START");
    public static string ActionPause => T("Pause");
    public static string ActionResume => T("Resume");
    public static string ActionStop => T("STOP");
    public static string ActionManualUpdate => T("Manual update");
    public static string ActionManualUpdateShort => T("Update");
    public static string ActionSetDefaults => T("Set defaults");
    public static string ActionSetDefaultsShort => T("Defaults");
    public static string StartTip => T("Start, pause or resume this session. Pausing keeps scheduled tracker updates running.");
    public static string StopTip => T("Stop this torrent session.");
    public static string ManualUpdateTip => T("Review the warning and time remaining before sending a manual tracker update.");
    public static string ManualUpdateAvailableIn(string countdown) => F1("The tracker's minimum interval allows a manual update in {0}.", countdown);
    public static string SetDefaultsTip => T("Restore default settings for this tab.");
    public static string ManualConfirmTitle => T("Confirm manual update");
    public static string ManualConfirmWarning => T("Repeated manual updates can make your activity stand out to the tracker and may lead to a ban.");
    public static string ManualConfirmAdvice => T("If the ratio on your tracker profile has not changed, wait for the next scheduled update. The website may also take time to refresh.");
    public static string ManualConfirmNext => T("Next scheduled update in:");
    public static string KeepWaiting => T("Keep waiting");
    public static string KeepWaitingTip => T("Cancel this request and keep the scheduled update.");
    public static string UpdateNow => T("Update now");
    public static string UpdateNowTip => T("Send one manual update after confirming the warning.");
    public static string ManualUpdateRefused => T("An update is already in progress, the tracker's minimum interval has not elapsed, or the tracker did not respond.");

    // ── Stop rules and proxy types ──
    public static string StopLabel(StopCondition condition) => condition switch
    {
        StopCondition.RatioAbove => StopRatioAbove,
        StopCondition.UploadAbove => StopUploadAbove,
        StopCondition.DownloadAbove => StopDownloadAbove,
        StopCondition.AfterTime => StopAfterTime,
        StopCondition.SeedersBelow => StopSeedersBelow,
        StopCondition.LeechersBelow => StopLeechersBelow,
        StopCondition.SwarmRatioBelow => StopSwarmRatioBelow,
        _ => StopNever,
    };

    public static string StopUnit(StopCondition condition) => condition switch
    {
        StopCondition.RatioAbove or StopCondition.SwarmRatioBelow => UnitRatio,
        StopCondition.AfterTime => UnitSeconds,
        StopCondition.UploadAbove or StopCondition.DownloadAbove => UnitGiB,
        _ => string.Empty,
    };

    public static string StopNever => T("Never");
    public static string StopRatioAbove => T("When ratio >");
    public static string StopUploadAbove => T("When upload >");
    public static string StopDownloadAbove => T("When download >");
    public static string StopAfterTime => T("After time:");
    public static string StopSeedersBelow => T("When seeders <");
    public static string StopLeechersBelow => T("When leechers <");
    public static string StopSwarmRatioBelow => T("When leechers / seeders <");

    public static string ProxyLabel(ProxyKind kind) => kind switch
    {
        ProxyKind.HttpConnect => "HTTP CONNECT",
        ProxyKind.Socks4 => "SOCKS4",
        ProxyKind.Socks4a => "SOCKS4a",
        ProxyKind.Socks5 => "SOCKS5",
        _ => ProxyNone,
    };

    public static string ProxyNone => T("None");

    // ── Statuses ──
    public static string StatusIdle => T("Idle");
    public static string StatusRunning => T("Running");
    public static string StatusSeeding => T("Seeding");
    public static string StatusDownloading => T("Downloading");
    public static string StatusAnnouncing => T("Announcing…");
    public static string StatusPausedByYou => T("Paused by you");
    public static string StatusPausedNoLeechers => T("Upload paused: no leechers");
    public static string StatusLoaded(string name) => F1("Loaded: {0}", name);
    public static string StatusSelectTorrent => T("Select a valid torrent file");
    public static string StatusSelectTorrentFirst => T("Select a valid torrent file first");
    public static string StatusInvalidTracker => T("Invalid tracker URL");
    public static string StatusCheckingNetwork => T("Checking local network access…");
    public static string StatusStartCancelled => T("Session start cancelled");
    public static string StatusLocalPermission => T("Local network permission required");
    public static string TimerIdle => T("idle");
    public static string TimerUpdating => T("updating...");
    public static string TimerStopped => T("stopped");

    // ── Tab alerts ──
    public static string AlertNoTorrent => T("No torrent loaded - select a .torrent file first.");
    public static string LogSelectTorrent => T("Please select a valid torrent file!");
    public static string InvalidTracker(string url) => F1("Invalid tracker URL: {0}", url);
    public static string ProfileUnavailable => T("The selected client profile is unavailable. Choose a listed family and version.");
    public static string ProfileNoV2 => T("This client profile does not support pure v2 torrents. Choose a compatible profile such as qBittorrent.");
    public static string ProxyInvalid => T("Choose a valid proxy type, host and port (1-65535).");
    public static string SpeedsInvalid => T("Enter finite, non-negative upload and download speeds.");
    public static string PeerIdInvalid => T("Peer ID must contain 20 single-byte characters or hex: followed by 40 hexadecimal digits.");
    public static string PeerIdMismatch(string profile) => F1("This peer ID could not have been generated by {0} (prefix or check bytes differ). Trackers comparing it with the User-Agent may notice. Use Regenerate for a matching identity.", profile);
    public static string PeerIdOldFormat => T("The saved peer ID differs from this profile's build format. It was retained as custom. Use Regenerate to generate this profile's identity.");
    public static string ProfileMissing => T("The saved client profile is unavailable. The default profile was selected; custom identity values were retained.");
    public static string ProfileRetired(string saved, string replacement) => F2("{0} is no longer emulated; this tab now uses {1}, the current version of the same client.", saved, replacement);
    public static string PortDiffersFromClient(string port, int shared) => F2("Port {0} differs from the port {1} other tabs of this client use; a tracker sees two ports for one client.", port, shared);
    public static string PortSharedWithClient(string port, string clients) => F2("Port {0} is also assigned to {1}; a tracker sees two clients on one address and port.", port, clients);
    public static string DuplicateOpen(string tab) => F1("This torrent is already open for the same tracker in {0}. Running both would look like two peers of one account.", tab);
    public static string DuplicateRunning(string tab) => F1("{0} is already announcing this torrent to the same tracker. Stop it first: two sessions look like two peers of one account.", tab);
    public static string TorrentLoadFailed(string detail) => F1("Failed to load torrent: {0}", detail);
    public static string LocalNetworkRequired => T("Allow Nearby devices in Android Settings > Apps > RatioMaster.NET > Permissions to use this local tracker or proxy.");
    public static string LocalPeersUnavailableTitle => T("Local peers unavailable");
    public static string LocalPeersUnavailable => T("Public trackers remain available. To allow local peers, enable Nearby devices in Android Settings > Apps > RatioMaster.NET > Permissions.");
    public static string LocalNetworkTitle => T("Local network access");

    // ── Files ──
    public static string PickTorrentsTitle => T("Select .torrent files");
    public static string PickFolderTitle => T("Select a folder of .torrent files");
    public static string TorrentFiles => T("Torrent files");
    public static string AllFiles => T("All files");
    public static string TextFile => T("Text file");
    public static string SaveLogTitle => T("Save log");
    public static string LogSaved(string name) => F1("Log saved to {0}", name);
    public static string LogSaveFailed(string detail) => F1("Failed to save log: {0}", detail);
    public static string FileUnreadable => T("Couldn't read the selected file.");
    public static string FileTooLarge => T("That file is too large to be a .torrent.");
    public static string FolderEmpty => T("This folder contains no .torrent file.");
    public static string BatchTitle => T("Add torrents");
    public static string BatchConfirm(int count) => F1("Open {0} torrents, each in its own tab?", count);
    public static string BatchAdd => T("Add");
    public static string BatchSummary(int added, int duplicates, int invalid, int overLimit) =>
        F2("Added: {0}. Already open: {1}.", added, duplicates)
        + (invalid > 0 ? " " + BatchInvalid(invalid) : string.Empty)
        + (overLimit > 0 ? " " + BatchOverLimit(overLimit) : string.Empty);
    public static string BatchInvalid(int count) => F1("Unreadable: {0}.", count);
    public static string BatchOverLimit(int count) => F1("Not opened, tab limit reached: {0}.", count);

    // ── About, language, updates ──
    public static string AboutTitle => T("About RatioMaster.NET");
    public static string LanguageTitle => T("Language");
    public static string LanguageSystem(string language) => F1("System language ({0})", language);
    public static string LanguageUnavailable => T("This translation could not be loaded; English is used instead.");
    public static string CheckUpdates => T("Check for updates");
    public static string Changelog => T("Changelog");
    public static string ChangelogSubtitle => T("What's new in RatioMaster.NET");
    public static string OpenRelease => T("Open release");
    public static string ReportBug => T("Report a bug");
    public static string Close => T("Close");
    public static string Back => T("Back");
    public static string Cancel => T("Cancel");
    public static string UpdatesOnRequest => T("Updates are checked only when requested.");
    public static string UpdateChecking => T("Checking GitHub…");
    public static string UpdateTimedOut => T("The update check timed out. Try again later.");
    public static string UpdateCancelled => T("Update check cancelled.");
    public static string UpdateFailed(string detail) => F1("Unable to check for updates: {0}", detail);
    public static string UpdateNoRelease => T("No published release was found.");
    public static string UpdateRateLimited => T("GitHub is limiting requests. Try again later.");
    public static string UpdateResponseTooLarge => T("GitHub's response exceeds the 1 MiB limit.");
    public static string UpdateNoNewer => T("No newer stable release was found.");
    public static string UpdateVersionUnreadable => T("The release version could not be read.");
    public static string UpdateUpToDate(string version) => F1("Version {0} is up to date with published releases.", version);
    public static string UpdateAvailable(string version) => F1("Version {0} is available.", version);

    // ── Closing and tray ──
    public static string ActiveSessionsTitle => T("Active sessions");
    public static string ActiveSessionsText => T("Some sessions are running, paused, or changing state. Quitting stops all sessions.");
    public static string TrayUnavailableText => T("The system tray is unavailable. Keep this window open to access your sessions.");
    public static string MinimizeToTray => T("Minimize to tray");
    public static string QuitAnyway => T("Quit anyway");
    public static string RememberChoice => T("Remember this setting");
    public static string TrayShow => T("Show");
    public static string TrayStartPause => T("Start / pause / resume current tab");
    public static string TrayStop => T("Stop current tab");
    public static string TrayExit => T("Exit");
    public static string TrayTooltip(int active, int tabs) => F2("RatioMaster.NET — {0} active / {1} tabs", active, tabs);
    public static string TrayUnavailableTitle => T("Tray unavailable");
    public static string TrayUnavailableToast => T("Keep this window open to access your sessions.");

    // ── Notifications ──
    public static string SavedSettingsTitle => T("Saved settings");
    public static string SaveFailedTitle => T("Unable to save");
    public static string CloseTabFailedTitle => T("Unable to close tab");
    public static string CloseFailedTitle => T("Unable to close");
    public static string QuitFailedTitle => T("Unable to quit");
    public static string SessionActionFailedTitle => T("Session action failed");
    public static string ActionFailedTitle => T("Unable to complete action");
    public static string OpenLinkFailedTitle => T("Unable to open link");
    public static string CopyFailedTitle => T("Unable to copy log");
    public static string CatalogIgnored(string detail) => F1("Client catalogue ignored: {0}", detail);
    public static string SessionRestoredBackup => T("The latest session could not be read. A previous saved snapshot was restored.");
    public static string SessionUnreadable => T("The saved session could not be read. Its files have been preserved.");
    public static string SessionSharedTemporary => T("A session exists in the shared temporary folder. It was preserved but not loaded because its installation cannot be identified.");
    public static string SessionTooManyTabs(int maximum) => F1("A saved session supports at most {0} tabs.", maximum);
    public static string SessionTooLarge => T("The saved session exceeds the 16 MiB limit.");
    public static string SessionSaveFailed => T("Could not save the session. Check available disk space and folder permissions.");

    // ── Theme texts (text box context menu, managed file dialogs) ──
    public static string ContextCut => T("Cut");
    public static string ContextCopy => T("Copy");
    public static string ContextPaste => T("Paste");
    public static string DialogOk => T("OK");
    public static string DialogReplace => T("Replace");
    public static string ShowHiddenFiles => T("Show hidden files");

    // ── Android ──
    public static string AndroidChannelName => T("Active sessions");
    public static string AndroidChannelDescription => T("Shown while RatioMaster sessions are running or paused.");
    public static string AndroidNotificationText => T("Sessions active (running or paused). Tap to open.");
}
