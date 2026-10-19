namespace RatioMaster.Engine;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.BitTorrent;
using RatioMaster.Localization;
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>One session owns its counters, transports, cancellation and announce schedule.</summary>
internal sealed class RatioEngine(
    IEngineHost host,
    TimeProvider? timeProvider = null,
    Func<string, CancellationToken, Task<ValueDictionary>>? announceTransport = null,
    bool automaticTicks = true,
    PeerListenerHub? listenerHub = null)
{
    private const int MinimumIntervalSeconds = 60;
    private const int MaximumIntervalSeconds = 86400;
    private const int RetryDelaySeconds = 60;
    private const int MaximumTrackerIdBytes = 1024;
    private const int MaximumWarningBytes = 4096;

    /// <summary>The refusal of the same completed announce that is treated as final; earlier ones are retried.</summary>
    private const int MaximumCompletionRefusals = 3;
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly PeerListenerHub hub = listenerHub ?? PeerListenerHub.Shared;
    private readonly object sync = new();
    private readonly SemaphoreSlim announceSignal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly RandomStringGenerator encoding = new();
    private readonly UdpTrackerClient udp = new();
    private SessionConfig cfg = null!;
    private ClientProfile profile = null!;
    private ProxyConfig sessionProxy = null!;
    private TrackerClient http = null!;
    private Uri tracker = null!;
    private bool initialized, active, userPaused, noLeechers, accepted, completedSent, completionPending, finishedSuccessfully;
    private bool announceRequested, isAnnouncing, stopRequested, trackerRejected, scrapeRefused;
    private ListenerState? listenerState;

    // have: bytes present (drives left). uploaded/downloaded: cumulative totals for display; announces report
    // the growth since this session started, as a client restarting a torrent reports its new session's transfer.
    private long have, uploaded, downloaded, uploadBase, downloadBase;
    private long nextAnnounce, lastTick, startedAt, stoppedAt, earliestAnnounce, keyCreatedAt;
    private byte[] infoHash = [];
    private string currentKey = string.Empty;
    private double upFraction, downFraction, upFactor = 0.85, downFactor = 0.85;
    private int interval, minimumInterval, seeders = -1, leechers = -1, completedRefusals;
    private string networkError = string.Empty;
    private string trackerWarning = string.Empty;
    private string trackerId = string.Empty;
    private string listenerProblem = string.Empty;
    private string stopError = string.Empty;
    private PeerListenerHub.Registration? peerRegistration;
    private Task loop = Task.CompletedTask, announcements = Task.CompletedTask;
    private TaskCompletionSource? stopped;
    private TaskCompletionSource<bool>? manualUpdate;

    internal event Action<string>? Log;
    internal event Action<EngineStats>? Stats;
    internal event Action<StopNotice>? Stopped;
    internal event Action<string>? KeyChanged;
    internal event Action<EngineAlert, string>? Alert;

    /// <summary>Raised after the tracker accepted an announce: a natural point to persist counters.</summary>
    internal event Action? Announced;
    internal bool IsRunning { get { lock (sync) return active; } }
    internal bool IsPaused { get { lock (sync) return active && userPaused; } }
    internal bool FinishedSuccessfully { get { lock (sync) return finishedSuccessfully; } }
    internal string StopError { get { lock (sync) return stopError; } }
    internal string CurrentKey { get { lock (sync) return currentKey; } }

    internal void Start(SessionConfig config)
    {
        lock (sync)
        {
            if (initialized) throw new InvalidOperationException(Strings.EngineAlreadyStarted);
            if (config.InfoHash.Length != 20) throw new ArgumentException(Strings.EngineInfoHashLength);
            if (Latin1.GetByteCount(config.PeerID) != 20 || config.PeerID.Any(c => c > 255))
                throw new ArgumentException(Strings.EnginePeerIdLength);
            if (!Uri.TryCreate(config.Tracker, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https" or "udp"))
                throw new ArgumentException(Strings.EngineTrackerScheme);
            if (uri.Scheme == "udp" && config.Proxy.Kind != ProxyKind.None)
                throw new ArgumentException(Strings.UdpProxyUnsupported);
            if (!ushort.TryParse(config.Port, out ushort port) || port == 0)
                throw new ArgumentException(Strings.EnginePortRange);
            if (config.TotalLength < 0) throw new ArgumentException(Strings.EngineInvalidSize);
            if (!double.IsFinite(config.FinishedPercent)) throw new ArgumentException(Strings.EngineFinishedFinite);
            cfg = config;
            profile = config.Client.Snapshot();
            sessionProxy = new ProxyConfig
            {
                Kind = config.Proxy.Kind, Host = config.Proxy.Host, Port = config.Proxy.Port,
                User = config.Proxy.User, Password = config.Proxy.Password,
            };
            infoHash = (byte[])config.InfoHash.Clone();
            currentKey = config.Key;
            tracker = uri;
            http = new TrackerClient(sessionProxy, message => Log?.Invoke(message));
            have = Math.Clamp(config.InitialHave
                ?? (long)(config.TotalLength * (decimal)Math.Clamp(config.FinishedPercent, 0, 100) / 100m), 0, config.TotalLength);
            uploaded = uploadBase = Math.Max(0, config.ResumeUploaded);
            downloaded = downloadBase = Math.Max(0, config.ResumeDownloaded);
            completedSent = have == config.TotalLength;
            interval = RegularInterval(Math.Clamp(config.Interval, MinimumIntervalSeconds, MaximumIntervalSeconds));
            lastTick = startedAt = keyCreatedAt = clock.GetTimestamp();
            Schedule(interval);
            initialized = active = true;
            QueueAnnounce();
            loop = automaticTicks ? Task.Run(TickLoopAsync) : Task.CompletedTask;
            announcements = Task.Run(AnnounceLoopAsync);
        }
        Log?.Invoke(Strings.LogEmulationInfo);
        Log?.Invoke(Strings.LogEmulationName(profile.Name));
        Log?.Invoke(Strings.LogEmulationPeerId(PeerIdentityText.Format(cfg.PeerID)));
        Log?.Invoke(Strings.LogEmulationPort(cfg.Port));
        RefreshListener();
        Publish();
    }

    internal void Pause() => SetUserPaused(true);
    internal void Resume() => SetUserPaused(false);

    private void SetUserPaused(bool value)
    {
        lock (sync)
        {
            if (!active || userPaused == value) return;
            Accumulate();
            userPaused = value;
        }
        Log?.Invoke(value ? Strings.AlertPausedByYou : Strings.LogResumed);
        Publish();
    }

    internal void ManualUpdate() => _ = ManualUpdateAsync();

    /// <summary>Refused while the tracker's minimum interval runs: trackers ignore or warn about earlier updates.</summary>
    internal Task<bool> ManualUpdateAsync()
    {
        lock (sync)
        {
            if (!active || stopRequested || isAnnouncing || announceRequested || clock.GetTimestamp() < earliestAnnounce)
                return Task.FromResult(false);
            manualUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            QueueAnnounce();
            return manualUpdate.Task;
        }
    }

    private async Task TickLoopAsync()
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1), clock);
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                Tick();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Fail(Strings.SessionError(ex.Message)); _ = StopAsync(Strings.StoppedSessionError, error: true); }
    }

    /// <summary>Accumulates elapsed time and submits work to the session's single announce worker.</summary>
    internal void Tick()
    {
        string? stopReason;
        lock (sync)
        {
            if (!active || stopRequested) return;
            Accumulate();
            bool completed = !completedSent && !completionPending && have == cfg.TotalLength;
            if (completed) completionPending = true;
            stopReason = StopReason();
            stopRequested = stopReason is not null;
            if (stopReason is null && (completed || (!isAnnouncing && clock.GetTimestamp() >= nextAnnounce)))
                QueueAnnounce();
        }
        if (stopReason is not null)
        {
            _ = StopAsync(stopReason, objectiveReached: true);
            return;
        }
        Publish();
    }

    private void Accumulate()
    {
        long now = clock.GetTimestamp();
        double seconds = Math.Max(0, clock.GetElapsedTime(lastTick, now).TotalSeconds);
        lastTick = now;
        if (!active || !accepted || userPaused) return;
        if (!noLeechers)
        {
            if (host.RandomUploadEnabled) upFactor = Drift(upFactor);
            uploaded = AddBytes(uploaded, host.UploadRateBytes, seconds, host.RandomUploadEnabled ? upFactor : 1, ref upFraction);
        }
        if (have < cfg.TotalLength)
        {
            if (host.RandomDownloadEnabled) downFactor = Drift(downFactor);
            long gained = Math.Min(cfg.TotalLength - have, AddBytes(0, host.DownloadRateBytes, seconds,
                host.RandomDownloadEnabled ? downFactor : 1, ref downFraction));
            have += gained;
            downloaded = downloaded > long.MaxValue - gained ? long.MaxValue : downloaded + gained;
        }
    }

    private static double Drift(double value) => Math.Clamp(value + (Random.Shared.NextDouble() - 0.5) * 0.12, 0.55, 1.15);
    private static long AddBytes(long total, long rate, double seconds, double factor, ref double fraction)
    {
        double amount = Math.Max(0, rate) * seconds * factor + fraction;
        if (!double.IsFinite(amount) || amount >= long.MaxValue - total) { fraction = 0; return long.MaxValue; }
        long whole = (long)amount;
        fraction = amount - whole;
        return total + whole;
    }

    private long Deadline(int seconds)
    {
        try { return checked(clock.GetTimestamp() + (long)Math.Max(1, seconds) * clock.TimestampFrequency); }
        catch (OverflowException) { throw new TrackerScheduleException(Strings.TrackerDelayUnrepresentable); }
    }
    private void Schedule(int seconds) => nextAnnounce = Deadline(seconds);
    private int Remaining() => SecondsUntil(nextAnnounce);
    private int SecondsUntil(long timestamp) => (int)Math.Clamp(Math.Ceiling((timestamp - clock.GetTimestamp()) / (double)clock.TimestampFrequency), 0, int.MaxValue);

    private void QueueAnnounce()
    {
        announceRequested = true;
        if (announceSignal.CurrentCount == 0) announceSignal.Release();
    }

    private async Task AnnounceLoopAsync()
    {
        TaskCompletionSource<bool>? request = null;
        try
        {
            while (true)
            {
                await announceSignal.WaitAsync(lifetime.Token).ConfigureAwait(false);
                string eventName;
                lock (sync)
                {
                    if (!active || stopRequested) break;
                    if (!announceRequested) continue;
                    announceRequested = false;
                    isAnnouncing = true;
                    request = manualUpdate;
                    manualUpdate = null;
                    eventName = !accepted ? "started" : completionPending ? "completed" : string.Empty;
                }
                bool success = await AnnounceAsync(eventName, lifetime.Token).ConfigureAwait(false);
                lock (sync)
                {
                    isAnnouncing = false;
                    if (active && !stopRequested && accepted && completionPending && eventName != "completed") QueueAnnounce();
                }
                request?.TrySetResult(success);
                request = null;
                Publish();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(Strings.SessionError(ex.Message));
            _ = StopAsync(Strings.StoppedSessionError, error: true);
        }
        finally
        {
            lock (sync)
            {
                isAnnouncing = announceRequested = false;
                request?.TrySetResult(false);
                manualUpdate?.TrySetResult(false);
                manualUpdate = null;
            }
        }
    }

    private async Task<bool> AnnounceAsync(string eventName, CancellationToken ct)
    {
        try
        {
            lock (sync)
            {
                if (!active || stopRequested) return false;
                if (eventName == "completed" && (!completionPending || completedSent)) return false;
                Accumulate();
                Schedule(Math.Max(interval, minimumInterval));
            }
            Publish();
            ValueDictionary dict = await SendAnnounceAsync(eventName, ct).ConfigureAwait(false);
            ThrowIfRejected(dict);
            lock (sync)
            {
                // Recorded before any cancellation check: a stop racing this response must neither resend
                // the accepted completed nor skip the stopped that a started tracker now expects.
                if (!accepted) lastTick = clock.GetTimestamp();
                accepted = true;
                if (eventName == "completed") { completedSent = true; completionPending = false; completedRefusals = 0; }
            }
            ct.ThrowIfCancellationRequested();
            if (!IsRunning) return false;
            int? responseInterval = ReadTrackerDelay(dict, "interval");
            int? responseMinimum = ReadTrackerDelay(dict, "min interval") ?? ReadTrackerDelay(dict, "min_interval");
            bool haveCounts;
            string warning;
            lock (sync)
            {
                if (!active) return false;
                networkError = string.Empty;
                if (responseInterval is { } seconds && seconds > 0) interval = RegularInterval(seconds);
                minimumInterval = responseMinimum ?? profile.DefaultMinIntervalSeconds;
                earliestAnnounce = minimumInterval > 0 ? Deadline(minimumInterval) : clock.GetTimestamp();
                Schedule(Math.Max(interval, minimumInterval));
                haveCounts = ApplyCounts(dict);
                warning = ApplyResponseMetadata(dict);
            }
            if (warning.Length > 0) Log?.Invoke(Strings.TrackerWarning(warning));
            Log?.Invoke(Strings.LogUpdatingInterval(Math.Max(interval, minimumInterval)));
            if (dict.Contains("peers")) LogPeers(dict["peers"]);
            if (dict.Contains("peers6")) LogPeers6(dict["peers6"]);
            Publish();
            Announced?.Invoke();
            if (!haveCounts && host.RequestScrape) await ScrapeAsync(ct).ConfigureAwait(false);
            return IsRunning;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TrackerRejectedException ex)
        {
            if (eventName != "completed" || !RetryRefusedCompletion(ex.Message)) Reject(ex.Message);
        }
        catch (TrackerScheduleException ex)
        {
            Fail(ex.Message);
            _ = StopAsync(Strings.StoppedScheduleError, error: true);
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (!active) return false;
                nextAnnounce = Math.Max(Deadline(RetryDelaySeconds), earliestAnnounce);
            }
            Fail(ex.Message);
            Log?.Invoke(Strings.LogWillRetry(Remaining()));
        }
        return false;
    }

    /// <summary>
    /// Some trackers (NexusPHP among them) turn a too-early completed announce into a failure reason. The torrent
    /// is still complete, so the event stays pending and is sent again once the tracker's minimum interval allows,
    /// instead of ending the session and losing the completion. Repeated refusals are final.
    /// </summary>
    private bool RetryRefusedCompletion(string message)
    {
        lock (sync)
        {
            // A session already stopping delivers the pending completed itself before leaving.
            if (!active) return true;
            if (++completedRefusals >= MaximumCompletionRefusals) return false;
            nextAnnounce = earliestAnnounce = Math.Max(Deadline(Math.Max(RetryDelaySeconds, minimumInterval)), earliestAnnounce);
            trackerWarning = Strings.CompletionRefused(message);
        }
        Log?.Invoke(Strings.CompletionRefused(message));
        Log?.Invoke(Strings.LogWillRetry(Remaining()));
        Publish();
        return true;
    }

    private void Reject(string message)
    {
        lock (sync) trackerRejected = true;
        if (!IsRunning) return;
        Fail(Strings.TrackerError(message));
        _ = StopAsync(Strings.StoppedTrackerRejection(message), false, error: true);
    }

    /// <summary>The tracker's regular interval, never shorter than the emulated client's own floor.</summary>
    private int RegularInterval(int seconds) => Math.Max(seconds, Math.Max(MinimumIntervalSeconds, profile.IntervalFloorSeconds));

    private static int? ReadCount(ValueDictionary dict, string name) =>
        dict.Contains(name) && long.TryParse(BEncode.String(dict[name]), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) && number >= 0
            ? (int)Math.Min(int.MaxValue, number) : null;

    private static int? ReadTrackerDelay(ValueDictionary response, string name)
    {
        if (!response.Contains(name)) return null;
        string? text = BEncode.String(response[name]);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            || seconds > int.MaxValue)
            throw new TrackerScheduleException(Strings.TrackerDelayOutOfRange(name));
        return seconds >= 0 ? (int)seconds : null;
    }

    private sealed class TrackerScheduleException(string message) : IOException(message);

    private string ApplyResponseMetadata(ValueDictionary response)
    {
        trackerWarning = ReadWarning(response);
        if (tracker.Scheme != "udp" && response.Contains("tracker id"))
        {
            if (response["tracker id"] is ValueString id && id.Length <= MaximumTrackerIdBytes)
                trackerId = id.String;
            else
            {
                trackerId = string.Empty;
                string notice = Strings.TrackerIdIgnored;
                trackerWarning = trackerWarning.Length > 0 ? trackerWarning + " " + notice : notice;
            }
        }
        return trackerWarning;
    }

    private static string ReadWarning(ValueDictionary response)
    {
        if (!response.Contains("warning message") || response["warning message"] is not ValueString value) return string.Empty;
        ReadOnlySpan<byte> bytes = value.Bytes.AsSpan(0, Math.Min(value.Length, MaximumWarningBytes));
        string text = Encoding.UTF8.GetString(bytes);
        StringBuilder display = new(text.Length);
        foreach (char character in text) display.Append(char.IsControl(character) ? ' ' : character);
        if (value.Length > MaximumWarningBytes) display.Append("…");
        return display.ToString();
    }

    /// <summary>Announce and scrape responses share the same pause/resume decision.</summary>
    private bool ApplyCounts(ValueDictionary dict)
    {
        int? complete = ReadCount(dict, "complete"), incomplete = ReadCount(dict, "incomplete");
        if (complete is { } seeds) seeders = seeds;
        if (incomplete is { } peers)
        {
            Accumulate();
            leechers = peers;
            bool pause = peers == 0;
            if (pause != noLeechers)
            {
                noLeechers = pause;
                Log?.Invoke(pause ? Strings.AlertNoLeechers : Strings.LogLeechersBack);
            }
        }
        return complete.HasValue && incomplete.HasValue;
    }

    private async Task<ValueDictionary> SendAnnounceAsync(string eventName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long up, down, left;
        string keyValue;
        string responseTrackerId;
        bool keyRotated = false;
        lock (sync)
        {
            up = Math.Max(0, uploaded - uploadBase);
            down = Math.Max(0, downloaded - downloadBase);
            left = Math.Max(0, cfg.TotalLength - have);
            if (cfg.KeyIsGenerated && profile.KeyRefreshInterval > TimeSpan.Zero
                && clock.GetElapsedTime(keyCreatedAt) >= profile.KeyRefreshInterval)
            {
                currentKey = ClientCatalog.GenerateKey(profile);
                keyCreatedAt = clock.GetTimestamp();
                keyRotated = true;
            }
            keyValue = currentKey;
            responseTrackerId = trackerId;
        }
        if (keyRotated) KeyChanged?.Invoke(keyValue);
        if (announceTransport is not null) return await announceTransport(eventName, ct).ConfigureAwait(false);
        string numWant = eventName == "stopped" ? "0" : cfg.NumWant;
        if (tracker.Scheme == "udp")
        {
            if (!uint.TryParse(keyValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint key))
                throw new IOException(Strings.UdpKeyFormat);
            return await udp.AnnounceAsync(tracker, new UdpAnnounceRequest
            {
                InfoHash = infoHash, PeerId = Latin1.GetBytes(cfg.PeerID), Downloaded = down,
                Left = left, Uploaded = up, Key = key, Port = ushort.Parse(cfg.Port, CultureInfo.InvariantCulture),
                NumWant = numWant.ParseIntOr(200),
                Event = eventName switch { "completed" => 1, "started" => 2, "stopped" => 3, _ => 0 },
            }, ct).ConfigureAwait(false);
        }
        string url = BuildAnnounceUrl(eventName, up, down, left, keyValue, numWant, responseTrackerId);
        return RequireResponse(await http.RequestAsync(url, profile, ct).ConfigureAwait(false));
    }

    internal string BuildAnnounceUrl(string eventName, long up, long down, long left, string keyValue, string numWant, string responseTrackerId) =>
        BuildAnnounceUrl(profile, cfg.Tracker, infoHash, cfg.PeerID, cfg.Port, eventName, up, down, left, keyValue, numWant, responseTrackerId, AnnouncedAddresses());

    /// <summary>The announce URL exactly as the emulated client writes it; pure, so request fixtures can pin every byte.</summary>
    internal static string BuildAnnounceUrl(ClientProfile profile, string trackerUrl, byte[] infoHash, string peerId, string port, string eventName,
        long up, long down, long left, string keyValue, string numWant, string responseTrackerId, IReadOnlyList<IPAddress> addresses)
    {
        RandomStringGenerator encoding = new();
        string query = profile.Query
            .Replace("{infohash}", encoding.UrlEncode(Latin1.GetString(infoHash), profile.HashUpperCase, profile.Unreserved))
            .Replace("{peerid}", profile.EncodePeerId(peerId))
            .Replace("{port}", port).Replace("{uploaded}", up.ToString(CultureInfo.InvariantCulture))
            .Replace("{downloaded}", down.ToString(CultureInfo.InvariantCulture)).Replace("{left}", left.ToString(CultureInfo.InvariantCulture))
            .Replace("{key}", encoding.UrlEncode(keyValue, false, profile.Unreserved)).Replace("{numwant}", encoding.UrlEncode(numWant, false, profile.Unreserved))
            .Replace("{event}", eventName.Length > 0 ? "&event=" + eventName : string.Empty);
        string url = AppendTrackerQuery(trackerUrl, query);
        if (responseTrackerId.Length > 0)
            url = SetQueryParameter(url, "trackerid", encoding.UrlEncode(responseTrackerId, false, profile.Unreserved));
        foreach (IPAddress address in addresses)
            url += (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "&ipv4=" : "&ipv6=")
                + encoding.UrlEncode(address.ToString(), profile.HashUpperCase, profile.Unreserved);
        return url;
    }

    /// <summary>
    /// The public addresses the emulated client adds to its announce (libtorrent: private torrents; Transmission
    /// 4.1: always). Never through a proxy: the proxy hides the real address, which the parameters would reveal.
    /// </summary>
    private IReadOnlyList<IPAddress> AnnouncedAddresses()
    {
        if (sessionProxy.Kind != ProxyKind.None) return [];
        try
        {
            return profile.GlobalAddresses switch
            {
                GlobalAddressMode.PrivateTorrents when cfg.IsPrivate => NetInfo.GetGlobalInterfaceAddresses(),
                GlobalAddressMode.Always => NetInfo.GetGlobalSourceAddresses(),
                _ => [],
            };
        }
        catch (Exception)
        {
            // Optional parameters: an unreadable network configuration simply leaves them out.
            return [];
        }
    }

    private static string WithoutFragment(string url)
    {
        int fragment = url.IndexOf('#');
        return fragment >= 0 ? url[..fragment] : url;
    }

    /// <summary>
    /// Preserves the original escaped path and query while adding tracker parameters. Like libtorrent and Transmission,
    /// an existing query is always followed by '&amp;', even when the URL already ends with '?' or '&amp;'.
    /// </summary>
    private static string AppendTrackerQuery(string url, string query)
    {
        url = WithoutFragment(url);
        if (query.Length == 0) return url;
        return url + (url.Contains('?') ? "&" : "?") + query;
    }

    private static string SetQueryParameter(string url, string name, string value)
    {
        int queryStart = url.IndexOf('?');
        if (queryStart < 0) return AppendTrackerQuery(url, name + "=" + value);
        List<string> parameters = [];
        bool replaced = false;
        foreach (string parameter in url[(queryStart + 1)..].Split('&'))
        {
            if (parameter == name || parameter.StartsWith(name + "=", StringComparison.Ordinal))
            {
                if (!replaced) parameters.Add(name + "=" + value);
                replaced = true;
            }
            else parameters.Add(parameter);
        }
        if (!replaced) return AppendTrackerQuery(url, name + "=" + value);
        return url[..(queryStart + 1)] + string.Join('&', parameters);
    }

    private static ValueDictionary RequireResponse(TrackerResponse? response)
    {
        if (response is null) throw new IOException(Strings.TrackerNoResponse);
        if (response.Dict is { } dict) ThrowIfRejected(dict);
        if (response.Error.Length > 0) throw new IOException(response.Error);
        if (response.StatusCode >= 400) throw new IOException(Strings.TrackerHttpStatus(response.StatusCode));
        if (response.Oversized) throw new IOException(Strings.TrackerResponseTooLarge);
        return response.Dict ?? throw new IOException(Strings.TrackerUndecodable);
    }

    private static void ThrowIfRejected(ValueDictionary response)
    {
        if (!response.Contains("failure reason")) return;
        string failure = response["failure reason"] is ValueString value ? value.String : string.Empty;
        throw new TrackerRejectedException(string.IsNullOrWhiteSpace(failure) ? Strings.TrackerRejectedEmpty : failure);
    }

    private async Task ScrapeAsync(CancellationToken ct)
    {
        lock (sync) { if (scrapeRefused) return; }
        try
        {
            ValueDictionary? result;
            if (tracker.Scheme == "udp") result = await udp.ScrapeAsync(tracker, infoHash, ct).ConfigureAwait(false);
            else
            {
                string original = WithoutFragment(cfg.Tracker);
                int query = original.IndexOf('?');
                string path = query >= 0 ? original[..query] : original;
                int slash = path.LastIndexOf('/');
                if (slash < 0 || !path[(slash + 1)..].StartsWith("announce", StringComparison.OrdinalIgnoreCase)) return;
                string scrape = path[..(slash + 1)] + "scrape" + path[(slash + 9)..] + (query >= 0 ? original[query..] : string.Empty);
                string url = AppendTrackerQuery(scrape, "info_hash=" + encoding.UrlEncode(Latin1.GetString(infoHash), profile.HashUpperCase, profile.Unreserved));
                ValueDictionary dict = RequireResponse(await http.RequestAsync(url, profile, ct).ConfigureAwait(false));
                string warning = ReadWarning(dict);
                if (warning.Length > 0)
                {
                    lock (sync) { if (active) trackerWarning = warning; }
                    Log?.Invoke(Strings.TrackerWarning(warning));
                }
                if (!dict.Contains("files") || dict["files"] is not ValueDictionary files) return;
                string key = Latin1.GetString(infoHash);
                result = files.Contains(key) ? files[key] as ValueDictionary : null;
            }
            ct.ThrowIfCancellationRequested();
            lock (sync) { if (active && result is not null) ApplyCounts(result); }
            Publish();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TrackerRejectedException ex)
        {
            // A tracker refusing scrapes still serves announces: give up scraping, never the session.
            lock (sync) scrapeRefused = true;
            if (IsRunning) Log?.Invoke(Strings.ScrapeUnavailable(ex.Message));
        }
        catch (Exception ex) { if (IsRunning) Log?.Invoke(Strings.ScrapeUnavailable(ex.Message)); }
    }

    internal void Stop() => _ = StopAsync();
    internal Task StopAsync(string? reason = null, bool sendStopped = true, bool objectiveReached = false, bool error = false)
    {
        StopNotice notice = new(reason ?? Strings.StatusStopped, error, reason is null);
        TaskCompletionSource completion;
        lock (sync)
        {
            if (stopped is not null) return stopped.Task;
            if (!initialized) return Task.CompletedTask;
            Accumulate();
            if (!completedSent && have == cfg.TotalLength) completionPending = true;
            active = false;
            stopRequested = true;
            stoppedAt = clock.GetTimestamp();
            finishedSuccessfully = false;
            completion = stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        lifetime.Cancel();
        CloseListener();
        Publish();
        Stopped?.Invoke(notice);
        _ = FinishStopAsync(completion, sendStopped, objectiveReached);
        return completion.Task;
    }

    private async Task FinishStopAsync(TaskCompletionSource completion, bool sendStopped, bool objectiveReached)
    {
        string? failure = null;
        string operation = Strings.OperationShutdown;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            await Task.WhenAll(loop, announcements).WaitAsync(timeout.Token).ConfigureAwait(false);
            bool rejected, started;
            lock (sync) (rejected, started) = (trackerRejected, accepted);
            if (rejected && objectiveReached) failure = Strings.RejectionPreventedCompletion;

            // libtorrent and Transmission only send stopped to a tracker that accepted the started announce.
            if (sendStopped && !rejected && started)
            {
                // A finished download announces completed before leaving, whatever ended the session:
                // a real client sends it as soon as the last piece arrives, so it is never skipped.
                if (completionPending && !completedSent)
                {
                    try
                    {
                        operation = Strings.OperationCompletion;
                        ValueDictionary completedResponse = await SendAnnounceAsync("completed", timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                        ThrowIfRejected(completedResponse);
                        lock (sync) { completedSent = true; completionPending = false; }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !timeout.IsCancellationRequested)
                    {
                        failure = Strings.CompletionNotDelivered(ex.Message);
                    }
                }
                operation = Strings.OperationStop;
                ValueDictionary response = await SendAnnounceAsync("stopped", timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                ThrowIfRejected(response);
            }
        }
        catch (Exception ex)
        {
            if (ex is TrackerRejectedException) Reject(ex.Message);
            string stopFailure = Strings.OperationFailed(operation, ex.Message);
            failure = failure is null ? stopFailure : failure + "; " + stopFailure;
        }
        finally
        {
            lock (sync)
            {
                finishedSuccessfully = objectiveReached && sendStopped && accepted && failure is null && !trackerRejected;
                if (failure is not null) stopError = failure;
            }
            if (failure is not null) Log?.Invoke(failure);
            Publish();
            completion.TrySetResult();
        }
    }

    internal EngineStats GetStats()
    {
        lock (sync)
        {
            long total = cfg?.TotalLength ?? 0;
            double percent = cfg is null || total == 0 ? 100 : have * 100.0 / total;
            long denominator = Math.Max(have, downloaded);
            var alert = CurrentAlert();
            return new EngineStats
            {
                IsActive = active,
                SampleElapsedSeconds = initialized ? Math.Max(0, clock.GetElapsedTime(startedAt, lastTick).TotalSeconds) : 0,
                AlertLevel = alert.Level, AlertMessage = alert.Message,
                Uploaded = uploaded, Downloaded = downloaded, Have = have, TotalSize = total,
                Left = Math.Max(0, total - have), FinishedPercent = percent,
                Ratio = denominator > 0 ? uploaded / (double)denominator : 0,
                Seeders = seeders, Leechers = leechers, IntervalRemaining = active ? Remaining() : 0,
                ManualUpdateWait = active ? SecondsUntil(earliestAnnounce) : 0,
                TotalRunSeconds = initialized ? (int)Math.Min(int.MaxValue, clock.GetElapsedTime(startedAt, active ? clock.GetTimestamp() : stoppedAt).TotalSeconds) : 0,
                NumPeers = cfg?.NumWant ?? "0", SeedMode = percent >= 100, IsPaused = active && userPaused,
                UploadPausedNoLeechers = active && noLeechers, IsAnnouncing = active && (isAnnouncing || announceRequested),
                CompletionPending = completionPending && !completedSent,
            };
        }
    }

    private void Publish()
    {
        EngineStats stats = GetStats();
        Stats?.Invoke(stats);
        if (!stats.IsActive) return;
        if (Alert is { } alert) alert(stats.AlertLevel, stats.AlertMessage);
        else host.ApplyAlert(stats.AlertLevel, stats.AlertMessage);
    }

    private (EngineAlert Level, string Message) CurrentAlert()
    {
        if (networkError.Length > 0) return (EngineAlert.Error, networkError);
        if (!active) return (EngineAlert.None, string.Empty);
        if (userPaused) return (EngineAlert.Warning, Strings.AlertPausedByYou);
        if (noLeechers) return (EngineAlert.Warning, Strings.AlertNoLeechers);
        if (trackerWarning.Length > 0) return (EngineAlert.Warning, trackerWarning);
        if (listenerProblem.Length > 0) return (EngineAlert.Warning, listenerProblem);
        return !accepted ? (EngineAlert.Warning, Strings.AlertWaitingForTracker) : (EngineAlert.Ok, Strings.AlertTrackerOk);
    }
    private void Fail(string message)
    {
        lock (sync) { if (!active) return; networkError = message; }
        Log?.Invoke(message);
        Publish();
    }

    private string? StopReason()
    {
        StopConditionSettings condition = host.StopCondition;
        double value = condition.Value.ParseDoubleOr(double.NaN);
        if (!double.IsFinite(value) || value < 0) return null;
        long denominator = Math.Max(have, downloaded);
        return condition.Condition switch
        {
            StopCondition.SeedersBelow when seeders >= 0 && seeders < value => Strings.StoppedSeedersBelow,
            StopCondition.LeechersBelow when leechers >= 0 && leechers < value => Strings.StoppedLeechersBelow,
            StopCondition.SwarmRatioBelow when seeders > 0 && leechers >= 0 && leechers / (double)seeders < value => Strings.StoppedSwarmRatioBelow,
            StopCondition.RatioAbove when denominator > 0 && uploaded / (double)denominator >= value => Strings.StoppedRatioReached,
            StopCondition.UploadAbove when uploaded / 1073741824.0 > value => Strings.StoppedUploadAbove,
            StopCondition.DownloadAbove when downloaded / 1073741824.0 > value => Strings.StoppedDownloadAbove,
            StopCondition.AfterTime when value > 0 && clock.GetElapsedTime(startedAt).TotalSeconds >= value => Strings.StoppedTimeLimit,
            _ => null,
        };
    }

    // ══════════════ Incoming peers ══════════════

    private void CloseListener()
    {
        PeerListenerHub.Registration? registration;
        lock (sync)
        {
            registration = peerRegistration;
            peerRegistration = null;
            listenerProblem = string.Empty;
            listenerState = null;
        }
        registration?.Dispose();
    }

    /// <summary>Joins or leaves the shared listener of this session's port as the listener option changes.</summary>
    internal void RefreshListener()
    {
        bool needed;
        lock (sync) needed = active && !stopRequested && host.UseTcpListener && sessionProxy.Kind == ProxyKind.None;
        if (!needed)
        {
            CloseListener();
            return;
        }
        lock (sync) { if (peerRegistration is not null) return; }
        int port = int.Parse(cfg.Port, CultureInfo.InvariantCulture);
        PeerTorrent torrent = new()
        {
            InfoHashes = cfg.PeerInfoHashes ?? [infoHash],
            PeerId = Latin1.GetBytes(cfg.PeerID),
            Wire = profile.PeerWire,
            AdvertisedPort = port,
            IsPrivate = cfg.IsPrivate,
            IsHybrid = cfg.IsHybrid,
            Metadata = cfg.Metadata,
            PieceCount = cfg.PieceCount,
            Realistic = cfg.Realistic,
            Progress = () => { lock (sync) return cfg.TotalLength == 0 ? 1 : have / (double)cfg.TotalLength; },
            Log = message => { if (IsRunning) Log?.Invoke(message); },
        };
        PeerListenerHub.Registration registration;
        try { registration = hub.Register(port, torrent, OnListenerState); }
        catch (Exception ex)
        {
            OnListenerState(ListenerState.Failed, ex.Message);
            return;
        }
        bool keep;
        lock (sync)
        {
            keep = active && !stopRequested && peerRegistration is null;
            if (keep) peerRegistration = registration;
        }
        if (!keep) registration.Dispose();
    }

    private void OnListenerState(ListenerState state, string detail)
    {
        string message = state switch
        {
            ListenerState.Listening => string.Empty,
            ListenerState.Duplicate => Strings.ListenerDuplicate(cfg.Port),
            ListenerState.Failed => Strings.ListenerUnavailable(cfg.Port, detail),
            _ => string.Empty,
        };
        bool changed;
        lock (sync)
        {
            changed = active && (listenerState != state || listenerProblem != message);
            if (changed) (listenerState, listenerProblem) = (state, message);
        }
        if (!changed || state == ListenerState.Starting) return;
        Log?.Invoke(message.Length > 0 ? message : Strings.ListenerStarted(cfg.Port));
        Publish();
    }

    private void LogPeers(IBEncodeValue peers)
    {
        try
        {
            if (peers is ValueString ps)
            {
                Log?.Invoke("peers: " + PeerList.FormatCompact(ps.Bytes, 4));
            }
            else if (peers is ValueList pl)
            {
                Log?.Invoke("peers: " + PeerList.FormatDictionary(pl));
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(Strings.PeersUnreadable(ex.Message));
        }
    }

    /// <summary>
    /// BEP 7 compact IPv6 peers use 18-byte records: a 16-byte address and a big-endian port.
    /// </summary>
    private void LogPeers6(IBEncodeValue peers)
    {
        try
        {
            if (peers is ValueString ps)
            {
                Log?.Invoke("peers6: " + PeerList.FormatCompact(ps.Bytes, 16));
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(Strings.PeersUnreadable(ex.Message));
        }
    }
}
