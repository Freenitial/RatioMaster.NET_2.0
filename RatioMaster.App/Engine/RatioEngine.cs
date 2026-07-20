namespace RatioMaster.Engine;

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.BitTorrent;
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>
/// UI-agnostic ratio session: sends the started/update/completed/stopped announce
/// sequence to the tracker, grows the fake uploaded/downloaded counters every second,
/// scrapes seed/leech stats, and optionally answers incoming BitTorrent handshakes.
/// Raises <see cref="Log"/> / <see cref="Stats"/> / <see cref="Stopped"/>; the
/// ViewModel marshals those to the UI thread.
/// </summary>
internal sealed class RatioEngine(IEngineHost host)
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly IEngineHost host = host;
    private readonly RandomStringGenerator gen = new();
    private readonly Random rand = new();
    private readonly object sync = new();

    private SessionConfig cfg = null!;
    private TorrentInfo torrent = new();
    private TrackerClient trackerClient = null!;
    private System.Timers.Timer? tick;
    private TcpListener? listener;
    private CancellationTokenSource? cts;

    private int running;
    private bool seedMode;
    private volatile bool haveInitialPeers;

    // Runtime-only upload pause while the swarm has zero leechers. Deliberately NOT a user setting — see
    // the announce handler: zeroing the user's speed field here used to leak into the saved session.
    private volatile bool uploadPausedNoLeechers;
    private volatile bool scrapStatsUpdated;
    private int intervalRemaining;
    private int totalRunSeconds;
    private int seeders = -1;
    private int leechers = -1;
    private int announceBusy;
    // "Random" smooth-curve multipliers — one per direction so the two drift independently.
    private double uploadFactor = 0.85;
    private double downloadFactor = 0.85;

    // "On next update" level multipliers (1.0 = 100% of the configured rate), re-rolled at each announce.
    private double uploadStep = 1.0;
    private double downloadStep = 1.0;

    // Bumped on every Start so a late announce from a previous session can't stamp its state onto the
    // current one (see Alert / the announce handler).
    private int generation;

    internal event Action<string>? Log;

    internal event Action<EngineStats>? Stats;

    internal event Action<string>? Stopped;

    internal bool IsRunning => Running;

    private bool Running => Volatile.Read(ref running) == 1;

    internal void Start(SessionConfig config)
    {
        cfg = config;
        trackerClient = new TrackerClient(cfg.Proxy, s => Log?.Invoke(s));
        cts?.Dispose();
        cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        double fin = Math.Clamp(cfg.FinishedPercent, 0, 100);

        torrent = new TorrentInfo
        {
            Tracker = cfg.Tracker,
            TrackerUri = Uri.TryCreate(cfg.Tracker, UriKind.Absolute, out Uri? u) ? u : null,
            Hash = cfg.HashHex,
            Interval = Math.Clamp(cfg.Interval, 60, 3600),
            Port = cfg.Port,
            Key = cfg.Key,
            PeerID = cfg.PeerID,
            NumberOfPeers = cfg.NumWant,
            UploadRate = host.UploadRateBytes,
            DownloadRate = host.DownloadRateBytes,
        };

        // Coherent left/downloaded: TotalSize = the FULL torrent size; at a given
        // "finished %" the client has already downloaded that fraction, and "left" is
        // the remainder. Downloaded therefore reflects the real amount (so the tracker's
        // ratio = uploaded/downloaded is meaningful, and Resume can restore it).
        torrent.TotalSize = cfg.TotalLength;
        torrent.Downloaded = cfg.ResumeDownloaded > 0
            ? cfg.ResumeDownloaded
            : (long)(cfg.TotalLength * fin / 100);
        torrent.Uploaded = cfg.ResumeUploaded;
        torrent.Left = Math.Max(0, cfg.TotalLength - torrent.Downloaded);
        seedMode = torrent.Left <= 0;

        uploadFactor = 0.85;
        downloadFactor = 0.85;
        uploadStep = 1.0;
        downloadStep = 1.0;
        generation++;

        Interlocked.Exchange(ref running, 1);
        Interlocked.Exchange(ref announceBusy, 0);
        seeders = -1;
        leechers = -1;
        totalRunSeconds = 0;
        intervalRemaining = torrent.Interval;
        haveInitialPeers = false;
        uploadPausedNoLeechers = false;
        scrapStatsUpdated = false;

        LogClientInfo();
        OpenTcpListener();

        _ = Task.Run(async () =>
        {
            await SendEventAsync("&event=started", token).ConfigureAwait(false);
            if (Running)
            {
                await ScrapeAsync(token).ConfigureAwait(false);
            }
        });

        tick = new System.Timers.Timer(1000) { AutoReset = true };
        tick.Elapsed += (_, _) => OnTick();
        tick.Start();

        EmitStats();
    }

    internal void Stop() => StopInternal("stopped", sendStopped: true);

    private void StopExternally(string reason) => StopInternal(reason, sendStopped: false);

    private void StopInternal(string reason, bool sendStopped)
    {
        // Atomic + idempotent: only the first caller to flip running 1->0 proceeds,
        // so a UI Stop() racing a StopExternally() from a tracker thread cleans up once.
        if (Interlocked.Exchange(ref running, 0) == 0)
        {
            return;
        }

        System.Timers.Timer? t = tick;
        tick = null;
        t?.Stop();
        t?.Dispose();
        CloseTcpListener();

        if (sendStopped)
        {
            TorrentInfo snapshot;
            lock (sync)
            {
                snapshot = torrent;
            }

            _ = Task.Run(async () =>
            {
                // Own short-lived token so the 'stopped' announce still fires even
                // though we cancel the session token below.
                using CancellationTokenSource stopCts = new(TimeSpan.FromSeconds(15));
                try
                {
                    await SendEventAsync("&event=stopped", snapshot, stopCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // best effort
                }
            });
        }

        cts?.Cancel(); // cancels the periodic loop + accept loop (disposed on next Start)
        Stopped?.Invoke(reason);
    }

    internal void ManualUpdate()
    {
        if (Running)
        {
            OpenTcpListener();
            lock (sync)
            {
                intervalRemaining = 0;
            }
        }
    }

    private void OnTick()
    {
        if (!Running)
        {
            return;
        }

        bool fire = false;
        try
        {
            lock (sync)
            {
                if (haveInitialPeers)
                {
                    UpdateCounters();
                }

                totalRunSeconds++;

                if (intervalRemaining > 0)
                {
                    intervalRemaining--;
                }
                else
                {
                    intervalRemaining = NextInterval();
                    fire = true;
                }
            }

            CheckStopConditions();

            if (fire && Running)
            {
                RollNextUpdateSteps();
                OpenTcpListener();
                CancellationToken token = cts?.Token ?? CancellationToken.None;
                _ = Task.Run(async () =>
                {
                    await SendEventAsync(string.Empty, token).ConfigureAwait(false);
                    if (Running)
                    {
                        await ScrapeAsync(token).ConfigureAwait(false);
                    }
                });
            }

            EmitStats();
        }
        catch (Exception ex)
        {
            Log?.Invoke("Tick error: " + ex.Message);
        }
    }

    // Jittered announce interval so we never announce exactly on the period, like a real client.
    private int NextInterval()
    {
        int jitter = Math.Max(4, torrent.Interval / 12);
        int value = torrent.Interval + rand.Next(-jitter, jitter + 1);
        return Math.Clamp(value, 60, 3600);
    }

    // "Random": a slowly random-walking multiplier so the effective rate rises and dips believably
    // instead of being pinned to a constant. Upload and download each keep their OWN factor, so the two
    // curves drift independently (a shared one would make both directions move in lockstep, which is
    // exactly the artificial-looking pattern this is meant to avoid).
    private double UpdateUploadFactor()
    {
        uploadFactor = DriftFactor(uploadFactor);
        return uploadFactor;
    }

    private double UpdateDownloadFactor()
    {
        downloadFactor = DriftFactor(downloadFactor);
        return downloadFactor;
    }

    private double DriftFactor(double current) =>
        Math.Clamp(current + ((rand.NextDouble() - 0.5) * 0.12), 0.55, 1.15);

    /// <summary>
    /// "On next update — get random speeds": draw a fresh level for each enabled direction, as a percentage
    /// of the CONFIGURED rate. Applied internally only — deliberately does NOT write back into the user's
    /// speed field (the previous implementation did, which permanently overwrote the setting and then got
    /// persisted to the session file). The rolled percentage is logged so the change is still visible.
    /// </summary>
    private void RollNextUpdateSteps()
    {
        try
        {
            if (host.NextRandUpEnabled)
            {
                uploadStep = RandomPercent(host.NextRandUpMinPercent, host.NextRandUpMaxPercent);
                Log?.Invoke($"Next update: upload level {uploadStep * 100:0}% of the configured rate.");
            }

            if (host.NextRandDownEnabled)
            {
                downloadStep = RandomPercent(host.NextRandDownMinPercent, host.NextRandDownMaxPercent);
                Log?.Invoke($"Next update: download level {downloadStep * 100:0}% of the configured rate.");
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Failed to roll random speed levels: " + ex.Message);
        }
    }

    private double RandomPercent(double minPercent, double maxPercent) =>
        PercentToMultiplier(minPercent, maxPercent, rand.NextDouble());

    /// <summary>
    /// Percent range + a [0,1) sample → rate multiplier (50..150% → 0.5..1.5). Tolerates min/max entered
    /// the wrong way round and never goes negative. Split out from the random draw so the conversion —
    /// above all the ÷100, whose silent absence would multiply every rate by a hundred — is directly
    /// assertable; see SelfTest.
    /// </summary>
    internal static double PercentToMultiplier(double minPercent, double maxPercent, double sample)
    {
        double lo = Math.Max(0, Math.Min(minPercent, maxPercent));
        double hi = Math.Max(0, Math.Max(minPercent, maxPercent));
        return (lo + (sample * (hi - lo))) / 100.0;
    }

    // ── Counters ────────────────────────────────────────────────────────────
    private void UpdateCounters()
    {
        torrent.UploadRate = host.UploadRateBytes;
        torrent.DownloadRate = host.DownloadRateBytes;

        // "Random" is per direction and read live, so each curve can be toggled independently mid-session.
        // Unchecked = a flat, exact rate (factor 1.0). The "on next update" level multiplies on top of it,
        // and its enabled flag is read live too so unchecking takes effect immediately rather than lingering
        // until the next announce would have re-rolled it.
        double upFactor = (host.RandomUploadEnabled ? UpdateUploadFactor() : 1.0)
            * (host.NextRandUpEnabled ? uploadStep : 1.0);

        // Swarm has no leechers → nothing to upload to. Runtime pause only; the user's speed/random
        // settings are untouched, so growth resumes by itself as soon as a leecher shows up.
        long up = uploadPausedNoLeechers ? 0 : (long)(torrent.UploadRate * upFactor);
        if (up < 0)
        {
            up = 0;
        }

        torrent.Uploaded += up;

        if (!seedMode && torrent.DownloadRate > 0)
        {
            double downFactor = (host.RandomDownloadEnabled ? UpdateDownloadFactor() : 1.0)
                * (host.NextRandDownEnabled ? downloadStep : 1.0);
            long down = (long)(torrent.DownloadRate * downFactor);
            if (down < 0)
            {
                down = 0;
            }

            torrent.Downloaded += down;
            torrent.Left = torrent.TotalSize - torrent.Downloaded;
        }

        if (torrent.Left <= 0)
        {
            torrent.Downloaded = torrent.TotalSize;
            torrent.Left = 0;
            torrent.DownloadRate = 0;
            if (!seedMode)
            {
                seedMode = true;
                TorrentInfo snapshot = torrent;
                CancellationToken token = cts?.Token ?? CancellationToken.None;
                _ = Task.Run(async () =>
                {
                    await SendEventAsync("&event=completed", snapshot, token).ConfigureAwait(false);
                    if (Running)
                    {
                        await ScrapeAsync(token).ConfigureAwait(false);
                    }
                });
            }
        }
    }

    /// <summary>
    /// Push an alert, but ONLY while this session is still the live one. An announce can outlive its
    /// session in two ways: the <c>&amp;event=stopped</c> announce runs after <c>running</c> was cleared,
    /// and a cancelled in-flight announce can land later. Without this gate a late Error ("No connection
    /// to tracker") repainted a tab the user had just stopped, leaving it permanently red, and a late Ok
    /// undid the alert-clearing that Stop performs. The generation check additionally stops a stale
    /// announce from stamping state onto a session that has already been restarted.
    /// </summary>
    private void Alert(EngineAlert level, string message, int sessionGen)
    {
        if (Running && sessionGen == generation)
        {
            host.ApplyAlert(level, message);
        }
    }

    // ── Announce ────────────────────────────────────────────────────────────
    private Task SendEventAsync(string eventType, CancellationToken ct) => SendEventAsync(eventType, torrent, ct);

    private async Task SendEventAsync(string eventType, TorrentInfo info, CancellationToken ct)
    {
        // Only periodic updates (empty event) are skippable when one is in flight;
        // started / completed / stopped must always be delivered.
        bool guard = eventType.Length == 0;
        if (guard && Interlocked.CompareExchange(ref announceBusy, 1, 0) == 1)
        {
            return;
        }

        // Snapshot the session identity: this announce may outlive the session (the &event=stopped one runs
        // after Stop, and a cancelled announce can still land). Everything that touches live session state
        // or the UI below is gated on it.
        int sessionGen = generation;

        try
        {
            scrapStatsUpdated = false;
            string url = BuildAnnounceUrl(info, eventType);
            TrackerResponse? response = await trackerClient.RequestAsync(url, cfg.Client, ct).ConfigureAwait(false);
            if (response?.Dict == null)
            {
                Log?.Invoke("No connection to tracker.");
                Alert(EngineAlert.Error, "No connection to tracker.", sessionGen);
                return;
            }

            ValueDictionary dict = response.Dict;
            string failure = BEncode.String(dict["failure reason"]) ?? string.Empty;
            if (failure.Length > 0)
            {
                Log?.Invoke("Tracker Error: " + failure);
                Alert(EngineAlert.Error, "Tracker error: " + failure, sessionGen);

                // Gate the teardown on the session generation as well as the alert. The &event=stopped
                // announce runs on its own 15s token and outlives Stop, so if the user restarts within
                // that window a routine failure reply ("unregistered torrent", "rate limit") from the OLD
                // session would otherwise tear down the BRAND-NEW one — and with sendStopped:false, leave
                // it registered on the tracker forever.
                if (sessionGen == generation && !host.IgnoreFailureReason)
                {
                    StopExternally("Stopped because of tracker error!!!");
                }

                return;
            }

            // A successful (non-failure) announce means the tracker accepted us — start growing the
            // emulated counters now, regardless of whether the response carried a 'peers' key. Some
            // trackers omit 'peers' on a solo swarm; gating growth on peer-list presence would otherwise
            // leave the tool a silent no-op there. Gated on the generation so a late announce from a
            // previous session can't un-pause / re-arm a session that has since been restarted.
            if (sessionGen != generation)
            {
                return;
            }

            haveInitialPeers = true;

            foreach (string key in EnumerateKeys(dict))
            {
                // 'peers'/'peers6' are binary compact lists — logged separately in decoded form below.
                if (key != "failure reason" && key != "peers" && key != "peers6")
                {
                    Log?.Invoke(key + ": " + BEncode.String(dict[key]));
                }
            }

            if (dict.Contains("interval"))
            {
                UpdateInterval(BEncode.String(dict["interval"]) ?? string.Empty);
            }

            // 'complete'/'incomplete' are OPTIONAL in BEP 3 and some trackers emit them only intermittently.
            bool haveCounts = dict.Contains("complete") && dict.Contains("incomplete");
            string completeText = haveCounts ? BEncode.String(dict["complete"]) ?? string.Empty : string.Empty;
            string incompleteText = haveCounts ? BEncode.String(dict["incomplete"]) ?? string.Empty : string.Empty;
            if (haveCounts)
            {
                UpdateScrapeStats(completeText, incompleteText);
            }

            // Nobody leeching → nobody could be downloading from us, so pause the emulated upload. This is a
            // RUNTIME pause ONLY: it used to call ApplyUploadRateMb(0) + DisableRandomUpload(), permanently
            // overwriting the user's speed field and random checkbox — and the session file persisted those,
            // so they came back as 0/unchecked on the next launch with nothing explaining why. The engine
            // must never mutate user configuration.
            //
            // Note the pause decision is computed OUTSIDE the haveCounts branch: when a tracker stops sending
            // the counts we resume rather than stay paused, because a silently frozen upload is far worse
            // than briefly uploading into an empty swarm (leaving it latched here meant the counter stayed at
            // zero for the rest of the run while the UI happily showed "Tracker OK").
            bool noLeechers = haveCounts && incompleteText.ParseIntOr(0) == 0;
            if (noLeechers != uploadPausedNoLeechers)
            {
                uploadPausedNoLeechers = noLeechers;
                Log?.Invoke(noLeechers
                    ? "No leechers in the swarm - upload paused (your settings are kept)."
                    : "Leechers are back - upload resumed.");
            }

            // Re-assert the alert on EVERY successful announce, not just on a transition: otherwise a stale
            // Error from an earlier failed announce survives the recovery and stays on screen indefinitely.
            if (noLeechers)
            {
                Alert(EngineAlert.Warning, "No leechers in the swarm - upload paused.", sessionGen);
            }
            else if (haveCounts)
            {
                Alert(EngineAlert.Ok, $"Tracker OK - {completeText} seeders, {incompleteText} leechers.", sessionGen);
            }
            else
            {
                Alert(EngineAlert.Ok, "Tracker OK.", sessionGen);
            }

            if (dict.Contains("peers"))
            {
                LogPeers(dict["peers"]);
            }

            // BEP 7 IPv6 peer list. Trackers behind Cloudflare (and dual-stack swarms) routinely answer with
            // ONLY peers6 and no 'peers' key at all, so without this the log looked peer-less on IPv6 swarms.
            if (dict.Contains("peers6"))
            {
                LogPeers6(dict["peers6"]);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Error in SendEvent: " + ex.Message);
        }
        finally
        {
            if (guard)
            {
                Interlocked.Exchange(ref announceBusy, 0);
            }
        }
    }

    private string BuildAnnounceUrl(TorrentInfo info, string eventType)
    {
        lock (sync)
        {
            // Round only the ANNOUNCED figure — never write it back into the live counter. Rounding down
            // to a 16 KB boundary in place meant that whenever a whole announce interval grew the counter
            // by less than 16 KB (a slow rate, or the no-leechers pause), every announce reset it to the
            // same boundary and the total could never advance past it.
            string uploaded = info.Uploaded > 0 ? RoundByDenominator(info.Uploaded, 0x4000).ToString() : "0";
            string downloaded = info.Downloaded > 0 ? RoundByDenominator(info.Downloaded, 0x10).ToString() : "0";

            if (info.Left > 0)
            {
                info.Left = info.TotalSize - info.Downloaded;
            }

            string url = info.Tracker;
            url += url.Contains('?') ? "&" : "?";

            // Strip the natmapped/localip pair on 'started' only — from a LOCAL copy,
            // never the shared client template (later announces must keep it).
            string query = cfg.Client.Query;
            if (eventType.Contains("started"))
            {
                query = query.Replace("&natmapped=1&localip={localip}", string.Empty);
            }

            url += query;
            url = url.Replace("{infohash}", HashUrlEncode(cfg.InfoHash, cfg.Client.HashUpperCase));
            // The peer_id is stored RAW (the exact 20 bytes we put on the wire) and percent-encoded only
            // here, at query-build time. Storing it pre-encoded is what let the encoded form leak into the
            // handshake, so the peer_id we announced and the one we presented to peers disagreed.
            url = url.Replace("{peerid}", gen.UrlEncode(info.PeerID, upperCase: false));
            url = url.Replace("{port}", info.Port);
            url = url.Replace("{uploaded}", uploaded);
            url = url.Replace("{downloaded}", downloaded);
            url = url.Replace("{left}", info.Left.ToString());
            url = url.Replace("{event}", eventType);

            string numWant = info.NumberOfPeers;
            if (numWant == "0" && !eventType.ToLowerInvariant().Contains("stopped"))
            {
                numWant = "200";
            }

            url = url.Replace("{numwant}", numWant);
            url = url.Replace("{key}", info.Key);
            // Only resolve the local IP if a profile actually asks for it. NetInfo.GetLocalIp does a
            // BLOCKING Dns.GetHostEntry, and this runs while lock(sync) is held — on a host whose own name
            // doesn't resolve (VPN, Docker, Android) it stalled for the resolver timeout, freezing the 1s
            // tick (counters and countdown included) and letting queued ticks pile up and re-enter.
            // No shipped profile contains {localip} today, so this is normally skipped entirely.
            if (url.Contains("{localip}", StringComparison.Ordinal))
            {
                url = url.Replace("{localip}", NetInfo.GetLocalIp());
            }
            return url;
        }
    }

    private void UpdateInterval(string param)
    {
        if (int.TryParse(param, out int temp))
        {
            temp = Math.Clamp(temp, 60, 3600);
            lock (sync)
            {
                torrent.Interval = temp;
            }

            Log?.Invoke("Updating interval: " + temp);
        }
    }

    // ── Scrape ──────────────────────────────────────────────────────────────
    private async Task ScrapeAsync(CancellationToken ct)
    {
        if (!host.RequestScrape || scrapStatsUpdated)
        {
            return;
        }

        try
        {
            string url = BuildScrapeUrl();
            if (url.Length == 0)
            {
                Log?.Invoke("This tracker doesn't seem to support scrape");
                return;
            }

            TrackerResponse? response = await trackerClient.RequestAsync(url, cfg.Client, ct).ConfigureAwait(false);
            if (response?.Dict == null)
            {
                return;
            }

            string failure = BEncode.String(response.Dict["failure reason"]) ?? string.Empty;
            if (failure.Length > 0)
            {
                Log?.Invoke("Tracker Error: " + failure);
                return;
            }

            if (response.Dict["files"] is not ValueDictionary files)
            {
                return;
            }

            string key = Latin1.GetString(cfg.InfoHash);
            if (files[key] is ValueDictionary entry)
            {
                Log?.Invoke("---------- Scrape Info -----------");
                Log?.Invoke("complete: " + BEncode.String(entry["complete"]));
                Log?.Invoke("downloaded: " + BEncode.String(entry["downloaded"]));
                Log?.Invoke("incomplete: " + BEncode.String(entry["incomplete"]));
                UpdateScrapeStats(BEncode.String(entry["complete"]), BEncode.String(entry["incomplete"]));
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Scrape Error: " + ex.Message);
        }
    }

    private string BuildScrapeUrl()
    {
        string url = torrent.Tracker;
        int index = url.LastIndexOf('/');
        if (index < 0 || index + 9 > url.Length)
        {
            return string.Empty;
        }

        string segment = url.Substring(index + 1, Math.Min(8, url.Length - index - 1));
        if (!segment.Equals("announce", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        url = url.Substring(0, index + 1) + "scrape" + url.Substring(index + 9);
        url += url.Contains('?') ? "&" : "?";
        return url + "info_hash=" + HashUrlEncode(cfg.InfoHash, cfg.Client.HashUpperCase);
    }

    private void UpdateScrapeStats(string? complete, string? incomplete)
    {
        seeders = (complete ?? string.Empty).ParseIntOr(-1);
        leechers = (incomplete ?? string.Empty).ParseIntOr(-1);
        scrapStatsUpdated = true;
    }

    private void CheckStopConditions()
    {
        try
        {
            string when = host.StopWhen;
            if (when == "Never" || string.IsNullOrEmpty(host.StopValue))
            {
                return;
            }

            switch (when)
            {
                case "When seeders <":
                    if (seeders > -1 && seeders < host.StopValue.ParseIntOr(int.MinValue))
                    {
                        StopExternally("Stopped: seeders below threshold");
                    }

                    break;
                case "When leechers <":
                    if (leechers > -1 && leechers < host.StopValue.ParseIntOr(int.MinValue))
                    {
                        StopExternally("Stopped: leechers below threshold");
                    }

                    break;
                case "When ratio >":
                    {
                        double target = host.StopValue.ParseDoubleOr(-1);
                        long baseline = torrent.Downloaded > 0 ? torrent.Downloaded : cfg.TotalLength;
                        if (target >= 0 && baseline > 0 && torrent.Uploaded >= target * baseline)
                        {
                            StopExternally("Stopped: target ratio reached");
                        }

                        break;
                    }

                case "When uploaded >":
                    {
                        long gb = host.StopValue.ParseLongOr(-1);
                        if (gb >= 0 && torrent.Uploaded > gb * 1024L * 1024L * 1024L)
                        {
                            StopExternally("Stopped: uploaded above threshold");
                        }

                        break;
                    }

                case "When downloaded >":
                    {
                        long gb = host.StopValue.ParseLongOr(-1);
                        if (gb >= 0 && torrent.Downloaded > gb * 1024L * 1024L * 1024L)
                        {
                            StopExternally("Stopped: downloaded above threshold");
                        }

                        break;
                    }
                case "After time:":
                    if (host.StopValue.ParseIntOr(0) > 0 && totalRunSeconds >= host.StopValue.ParseIntOr(int.MaxValue))
                    {
                        StopExternally("Stopped: time limit reached");
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Error in stop module: " + ex.Message);
        }
    }

    // ── TCP listener (answers incoming BitTorrent handshakes) ───────────────
    private void OpenTcpListener()
    {
        // Open/close serialized under sync so an in-flight OnTick that resumes after
        // Stop() cannot re-bind the port (checks Running inside the same lock Stop uses).
        lock (sync)
        {
            if (!Running || !host.UseTcpListener || listener != null || cfg.Proxy.Kind != ProxyKind.None)
            {
                return;
            }

            try
            {
                if (!int.TryParse(torrent.Port, out int p))
                {
                    return;
                }

                listener = new TcpListener(IPAddress.Any, p);
                listener.Start();
                Log?.Invoke("Started TCP listener on port " + torrent.Port);
                _ = Task.Run(AcceptLoopAsync);
            }
            catch (Exception ex)
            {
                Log?.Invoke("TCP listener not started (port busy or client running): " + ex.Message);
                listener = null;
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        TcpListener? local = listener;
        if (local == null)
        {
            return;
        }

        try
        {
            while (Running && listener != null)
            {
                Socket socket = await local.AcceptSocketAsync(cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                _ = Task.Run(() => HandleHandshake(socket));
            }
        }
        catch
        {
            // listener stopped
        }
    }

    private void HandleHandshake(Socket socket)
    {
        try
        {
            using (socket)
            {
                socket.ReceiveTimeout = 1000;
                byte[] buffer = new byte[68];
                int read = 0;
                try
                {
                    read = socket.Receive(buffer);
                }
                catch
                {
                    // ignore
                }

                string received = Latin1.GetString(buffer, 0, Math.Max(read, 0));
                if (received.Contains("BitTorrent protocol") && received.Contains(Latin1.GetString(cfg.InfoHash)))
                {
                    socket.Send(BuildHandshake());

                    // Realistic mode: also send a full bitfield (we're a seed) so the
                    // connecting peer sees a credible, complete seeder — as a real client would.
                    if (cfg.Realistic && cfg.PieceCount > 0)
                    {
                        byte[]? bitfield = BuildFullBitfield();
                        if (bitfield != null)
                        {
                            socket.Send(bitfield);
                        }
                    }
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    private byte[] BuildHandshake()
    {
        const string protocol = "BitTorrent protocol";
        byte[] buffer = new byte[68];
        int idx = 0;
        buffer[idx++] = (byte)protocol.Length;
        Latin1.GetBytes(protocol, 0, protocol.Length, buffer, idx);
        idx += protocol.Length;
        idx += 8; // reserved
        Buffer.BlockCopy(cfg.InfoHash, 0, buffer, idx, cfg.InfoHash.Length);
        idx += cfg.InfoHash.Length;
        // Exactly the 20 raw bytes we announced. A shorter custom peer_id is zero-padded (the handshake
        // field is fixed-width); a longer one is truncated. Both were previously silent, and the truncation
        // in particular hid the encoded-peer_id bug — a "%18w…" string simply got cut at 20 characters.
        byte[] peer = Latin1.GetBytes(cfg.PeerID);
        if (peer.Length != 20)
        {
            Log?.Invoke($"Peer ID is {peer.Length} bytes, not 20 — the handshake will not match the announce.");
        }

        Buffer.BlockCopy(peer, 0, buffer, idx, Math.Min(peer.Length, 20));
        return buffer;
    }

    // Wire "bitfield" message claiming we hold every piece: <len><id=5><bits...>.
    private byte[]? BuildFullBitfield()
    {
        int pieces = cfg.PieceCount;
        if (pieces <= 0)
        {
            return null;
        }

        int numBytes = (pieces + 7) / 8;
        int len = 1 + numBytes;
        byte[] msg = new byte[4 + len];
        msg[0] = (byte)(len >> 24);
        msg[1] = (byte)(len >> 16);
        msg[2] = (byte)(len >> 8);
        msg[3] = (byte)len;
        msg[4] = 5; // bitfield message id
        for (int i = 0; i < pieces; i++)
        {
            msg[5 + (i / 8)] |= (byte)(0x80 >> (i % 8));
        }

        return msg;
    }

    private void CloseTcpListener()
    {
        lock (sync)
        {
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                    // ignore
                }

                listener = null;
                Log?.Invoke("TCP listener closed");
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private void EmitStats()
    {
        EngineStats snapshot;
        lock (sync)
        {
            double finishedPercent;
            if (torrent.TotalSize == 0)
            {
                finishedPercent = 100;
            }
            else
            {
                finishedPercent = (double)(torrent.TotalSize - torrent.Left) / torrent.TotalSize * 100.0;
                if (finishedPercent > 100)
                {
                    finishedPercent = 100;
                }
            }

            string ratio = torrent.Downloaded / 1024 < 100
                ? "NaN"
                : (torrent.Uploaded / (double)torrent.Downloaded).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

            snapshot = new EngineStats
            {
                Uploaded = torrent.Uploaded,
                Downloaded = torrent.Downloaded,
                Left = torrent.Left,
                TotalSize = torrent.TotalSize,
                FinishedPercent = finishedPercent,
                Ratio = ratio,
                Seeders = seeders,
                Leechers = leechers,
                IntervalRemaining = intervalRemaining,
                TotalRunSeconds = totalRunSeconds,
                NumPeers = torrent.NumberOfPeers,
                SeedMode = seedMode,
            };
        }

        Stats?.Invoke(snapshot);
    }

    private void LogClientInfo()
    {
        Log?.Invoke("CLIENT EMULATION INFO:");
        Log?.Invoke("Name: " + cfg.Client.Name);
        Log?.Invoke("HttpProtocol: " + cfg.Client.HttpProtocol);
        Log?.Invoke("Key: " + cfg.Key);
        Log?.Invoke("PeerID: " + cfg.PeerID);
        Log?.Invoke("Port: " + cfg.Port);
        Log?.Invoke("Query: " + cfg.Client.Query);
    }

    private void LogPeers(IBEncodeValue peers)
    {
        try
        {
            if (peers is ValueString ps)
            {
                byte[] bytes = Latin1.GetBytes(ps.String);
                using BinaryReader reader = new(new MemoryStream(bytes));
                PeerList list = [];
                for (int i = 0; i + 6 <= bytes.Length; i += 6)
                {
                    list.Add(new Peer(reader.ReadBytes(4), reader.ReadInt16()));
                }

                Log?.Invoke("peers: " + list);
            }
            else if (peers is ValueList pl)
            {
                PeerList list = [];
                foreach (object entry in pl)
                {
                    if (entry is ValueDictionary d)
                    {
                        list.Add(new Peer(
                            BEncode.String(d["ip"]) ?? string.Empty,
                            BEncode.String(d["port"]) ?? "0",
                            BEncode.String(d["peer id"]) ?? string.Empty));
                    }
                }

                Log?.Invoke("peers: " + list);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Error parsing peers: " + ex.Message);
        }
    }

    /// <summary>
    /// BEP 7 compact IPv6 peer list: a flat byte string of 18-byte records (16-byte address followed by a
    /// 2-byte BIG-ENDIAN port). Read straight from the raw bytes — decoding the port here means the Peer
    /// ctor must NOT byte-swap again (that's the ushort overload).
    /// </summary>
    private void LogPeers6(IBEncodeValue peers)
    {
        try
        {
            if (peers is ValueString ps)
            {
                byte[] bytes = ps.Bytes;
                PeerList list = [];
                for (int i = 0; i + 18 <= bytes.Length; i += 18)
                {
                    byte[] ip = new byte[16];
                    Array.Copy(bytes, i, ip, 0, 16);
                    ushort port = (ushort)((bytes[i + 16] << 8) | bytes[i + 17]);
                    list.Add(new Peer(ip, port));
                }

                Log?.Invoke("peers6: " + list);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Error parsing peers6: " + ex.Message);
        }
    }

    private string HashUrlEncode(byte[] infoHash, bool upperCase) =>
        gen.UrlEncode(Latin1.GetString(infoHash), upperCase);

    private static long RoundByDenominator(long value, long denominator) => denominator * (value / denominator);

    private static System.Collections.Generic.List<string> EnumerateKeys(ValueDictionary dict)
    {
        System.Collections.Generic.List<string> keys = [];
        foreach (object? key in dict.Keys)
        {
            if (key is string s)
            {
                keys.Add(s);
            }
        }

        return keys;
    }
}
