namespace RatioMaster.Engine;

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RatioMaster.BitTorrent;
using RatioMaster.Models;
using RatioMaster.Services;

/// <summary>One session owns its counters, transports, cancellation and announce schedule.</summary>
internal sealed class RatioEngine(
    IEngineHost host,
    TimeProvider? timeProvider = null,
    Func<string, CancellationToken, Task<ValueDictionary>>? announceTransport = null,
    bool automaticTicks = true)
{
    private const int MinimumIntervalSeconds = 60;
    private const int MaximumIntervalSeconds = 86400;
    private const int RetryDelaySeconds = 60;
    private const int MaximumTrackerIdBytes = 1024;
    private const int MaximumWarningBytes = 4096;
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly SemaphoreSlim announceSignal = new(0, 1);
    private readonly SemaphoreSlim peerSlots = new(8, 8);
    private readonly CancellationTokenSource lifetime = new();
    private readonly RandomStringGenerator encoding = new();
    private readonly UdpTrackerClient udp = new();
    private SessionConfig cfg = null!;
    private ClientProfile profile = null!;
    private ProxyConfig sessionProxy = null!;
    private TrackerClient http = null!;
    private Uri tracker = null!;
    private bool initialized, active, userPaused, noLeechers, accepted, completedSent, completionPending, finishedSuccessfully;
    private bool announceRequested, isAnnouncing, stopRequested, trackerRejected;
    private long uploaded, downloaded, nextAnnounce, lastTick, startedAt, stoppedAt, earliestAnnounce, keyCreatedAt;
    private byte[] infoHash = [];
    private string currentKey = string.Empty;
    private double upFraction, downFraction, upFactor = 0.85, downFactor = 0.85;
    private int interval, minimumInterval, seeders = -1, leechers = -1;
    private string networkError = string.Empty;
    private string trackerWarning = string.Empty;
    private string trackerId = string.Empty;
    private string listenerProblem = string.Empty;
    private string stopError = string.Empty;
    private TcpListener? listener;
    private CancellationTokenSource? listenerLifetime;
    private readonly List<CancellationTokenSource> listenerLifetimes = [];
    private readonly HashSet<Task> listenerTasks = [];
    private Task loop = Task.CompletedTask, announcements = Task.CompletedTask;
    private TaskCompletionSource? stopped;
    private TaskCompletionSource<bool>? manualUpdate;

    internal event Action<string>? Log;
    internal event Action<EngineStats>? Stats;
    internal event Action<string>? Stopped;
    internal event Action<string>? KeyChanged;
    internal event Action<EngineAlert, string>? Alert;
    internal bool IsRunning { get { lock (sync) return active; } }
    internal bool IsPaused { get { lock (sync) return active && userPaused; } }
    internal bool FinishedSuccessfully { get { lock (sync) return finishedSuccessfully; } }
    internal string StopError { get { lock (sync) return stopError; } }
    internal string CurrentKey { get { lock (sync) return currentKey; } }

    internal void Start(SessionConfig config)
    {
        lock (sync)
        {
            if (initialized) throw new InvalidOperationException("Create a session before starting another torrent.");
            if (config.InfoHash.Length != 20) throw new ArgumentException("Tracker info-hash must be 20 bytes.");
            if (Latin1.GetByteCount(config.PeerID) != 20 || config.PeerID.Any(c => c > 255))
                throw new ArgumentException("Peer ID must contain exactly 20 single-byte characters.");
            if (!Uri.TryCreate(config.Tracker, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https" or "udp"))
                throw new ArgumentException("Use an HTTP, HTTPS or UDP tracker URL.");
            if (uri.Scheme == "udp" && config.Proxy.Kind != ProxyKind.None)
                throw new ArgumentException("UDP trackers cannot use this TCP proxy. Choose an HTTP(S) tracker or disable the proxy.");
            if (!ushort.TryParse(config.Port, out ushort port) || port == 0)
                throw new ArgumentException("Listening port must be between 1 and 65535.");
            if (config.TotalLength < 0) throw new ArgumentException("Invalid torrent size.");
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
            uploaded = Math.Max(0, config.ResumeUploaded);
            downloaded = Math.Clamp(config.ResumeDownloaded > 0 ? config.ResumeDownloaded
                : (long)(config.TotalLength * Math.Clamp(config.FinishedPercent, 0, 100) / 100), 0, config.TotalLength);
            completedSent = downloaded == config.TotalLength;
            interval = Math.Clamp(config.Interval, MinimumIntervalSeconds, MaximumIntervalSeconds);
            lastTick = startedAt = keyCreatedAt = clock.GetTimestamp();
            Schedule(interval);
            initialized = active = true;
            QueueAnnounce();
            loop = automaticTicks ? Task.Run(TickLoopAsync) : Task.CompletedTask;
            announcements = Task.Run(AnnounceLoopAsync);
        }
        Log?.Invoke("CLIENT EMULATION INFO:");
        Log?.Invoke("Name: " + profile.Name);
        Log?.Invoke("PeerID: " + PeerIdentityText.Format(cfg.PeerID));
        Log?.Invoke("Port: " + cfg.Port);
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
        Log?.Invoke(value ? "Paused by you." : "Resumed.");
        Publish();
    }

    internal void ManualUpdate() => _ = ManualUpdateAsync();
    internal Task<bool> ManualUpdateAsync()
    {
        lock (sync)
        {
            if (!active || stopRequested || isAnnouncing || announceRequested) return Task.FromResult(false);
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
        catch (Exception ex) { Fail("Session error: " + ex.Message); _ = StopAsync("Stopped: session error"); }
    }

    /// <summary>Accumulates elapsed time and submits work to the session's single announce worker.</summary>
    internal void Tick()
    {
        string? stopReason;
        lock (sync)
        {
            if (!active || stopRequested) return;
            Accumulate();
            bool completed = !completedSent && !completionPending && downloaded == cfg.TotalLength;
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
        RefreshListener();
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
        if (downloaded < cfg.TotalLength)
        {
            if (host.RandomDownloadEnabled) downFactor = Drift(downFactor);
            downloaded = Math.Min(cfg.TotalLength, AddBytes(downloaded, host.DownloadRateBytes, seconds,
                host.RandomDownloadEnabled ? downFactor : 1, ref downFraction));
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
        catch (OverflowException) { throw new TrackerScheduleException("The tracker delay cannot be represented by the session clock."); }
    }
    private void Schedule(int seconds) => nextAnnounce = Deadline(seconds);
    private int Remaining() => (int)Math.Clamp(Math.Ceiling((nextAnnounce - clock.GetTimestamp()) / (double)clock.TimestampFrequency), 0, int.MaxValue);

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
            Fail("Session error: " + ex.Message);
            _ = StopAsync("Stopped: session error");
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
            ct.ThrowIfCancellationRequested();
            if (!IsRunning) return false;
            int? responseInterval = ReadTrackerDelay(dict, "interval");
            int? responseMinimum = ReadTrackerDelay(dict, "min interval") ?? ReadTrackerDelay(dict, "min_interval");
            bool haveCounts;
            string warning;
            lock (sync)
            {
                if (!active) return false;
                if (!accepted) lastTick = clock.GetTimestamp();
                accepted = true;
                if (eventName == "completed") { completedSent = true; completionPending = false; }
                networkError = string.Empty;
                if (responseInterval is { } seconds && seconds > 0) interval = Math.Max(seconds, MinimumIntervalSeconds);
                minimumInterval = responseMinimum ?? 0;
                earliestAnnounce = minimumInterval > 0 ? Deadline(minimumInterval) : clock.GetTimestamp();
                Schedule(Math.Max(interval, minimumInterval));
                haveCounts = ApplyCounts(dict);
                warning = ApplyResponseMetadata(dict);
            }
            if (warning.Length > 0) Log?.Invoke("Tracker warning: " + warning);
            Log?.Invoke("Updating interval: " + Math.Max(interval, minimumInterval));
            if (dict.Contains("peers")) LogPeers(dict["peers"]);
            if (dict.Contains("peers6")) LogPeers6(dict["peers6"]);
            Publish();
            RefreshListener(retryFailed: true);
            if (!haveCounts && host.RequestScrape) await ScrapeAsync(ct).ConfigureAwait(false);
            return IsRunning;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TrackerRejectedException ex) { Reject(ex.Message); }
        catch (TrackerScheduleException ex)
        {
            Fail(ex.Message);
            _ = StopAsync("Stopped: tracker schedule error");
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (!active) return false;
                nextAnnounce = Math.Max(Deadline(RetryDelaySeconds), earliestAnnounce);
            }
            Fail(ex.Message);
            Log?.Invoke("Will retry in " + Remaining() + " seconds.");
        }
        return false;
    }

    private void Reject(string message)
    {
        lock (sync) trackerRejected = true;
        if (!IsRunning) return;
        Fail("Tracker error: " + message);
        _ = StopAsync("Stopped: tracker rejection - " + message, false);
    }

    private static int? ReadCount(ValueDictionary dict, string name) =>
        long.TryParse(BEncode.String(dict[name]), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) && number >= 0
            ? (int)Math.Min(int.MaxValue, number) : null;

    private static int? ReadTrackerDelay(ValueDictionary response, string name)
    {
        string? text = BEncode.String(response[name]);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            || seconds > int.MaxValue)
            throw new TrackerScheduleException("Tracker " + name + " is outside the supported integer-second range; automatic announces have been stopped.");
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
                string notice = "Tracker ID ignored: expected at most 1024 bytes.";
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
                Log?.Invoke(pause ? "Paused: no leechers." : "Leechers available: upload resumed.");
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
            up = uploaded;
            down = downloaded;
            left = Math.Max(0, cfg.TotalLength - down);
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
                throw new IOException("UDP tracker key must be a 32-bit hexadecimal value.");
            return await udp.AnnounceAsync(tracker, new UdpAnnounceRequest
            {
                InfoHash = infoHash, PeerId = Latin1.GetBytes(cfg.PeerID), Downloaded = down,
                Left = left, Uploaded = up, Key = key, Port = ushort.Parse(cfg.Port, CultureInfo.InvariantCulture),
                NumWant = numWant.ParseIntOr(200),
                Event = eventName switch { "completed" => 1, "started" => 2, "stopped" => 3, _ => 0 },
            }, ct).ConfigureAwait(false);
        }
        string query = profile.Query
            .Replace("{infohash}", encoding.UrlEncode(Latin1.GetString(infoHash), profile.HashUpperCase, profile.UrlEncoding))
            .Replace("{peerid}", profile.EncodePeerId(cfg.PeerID))
            .Replace("{port}", cfg.Port).Replace("{uploaded}", up.ToString(CultureInfo.InvariantCulture))
            .Replace("{downloaded}", down.ToString(CultureInfo.InvariantCulture)).Replace("{left}", left.ToString(CultureInfo.InvariantCulture))
            .Replace("{key}", encoding.UrlEncode(keyValue, false, profile.UrlEncoding)).Replace("{numwant}", encoding.UrlEncode(numWant, false, profile.UrlEncoding))
            .Replace("{event}", eventName.Length > 0 ? "&event=" + eventName : string.Empty);
        string url = AppendTrackerQuery(cfg.Tracker, query);
        if (responseTrackerId.Length > 0)
            url = SetQueryParameter(url, "trackerid", encoding.UrlEncode(responseTrackerId, false, profile.UrlEncoding));
        return RequireResponse(await http.RequestAsync(url, profile, ct).ConfigureAwait(false));
    }

    private static string WithoutFragment(string url)
    {
        int fragment = url.IndexOf('#');
        return fragment >= 0 ? url[..fragment] : url;
    }

    /// <summary>Preserves the original escaped path and query while adding tracker parameters.</summary>
    private static string AppendTrackerQuery(string url, string query)
    {
        url = WithoutFragment(url);
        if (query.Length == 0) return url;
        string separator = !url.Contains('?') ? "?" : url.EndsWith('?') || url.EndsWith('&') ? string.Empty : "&";
        return url + separator + query;
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
        if (response is null) throw new IOException("No usable response from tracker.");
        if (response.Dict is { } dict) ThrowIfRejected(dict);
        if (response.Error.Length > 0) throw new IOException(response.Error);
        if (response.StatusCode >= 400) throw new IOException($"Tracker returned HTTP {response.StatusCode}.");
        if (response.Oversized) throw new IOException("Tracker response exceeds the size limit.");
        return response.Dict ?? throw new IOException("Could not decode the tracker response.");
    }

    private static void ThrowIfRejected(ValueDictionary response)
    {
        if (!response.Contains("failure reason")) return;
        string failure = response["failure reason"] is ValueString value ? value.String : string.Empty;
        throw new TrackerRejectedException(string.IsNullOrWhiteSpace(failure) ? "Tracker explicitly rejected the announce." : failure);
    }

    private async Task ScrapeAsync(CancellationToken ct)
    {
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
                string url = AppendTrackerQuery(scrape, "info_hash=" + encoding.UrlEncode(Latin1.GetString(infoHash), profile.HashUpperCase, profile.UrlEncoding));
                ValueDictionary dict = RequireResponse(await http.RequestAsync(url, profile, ct).ConfigureAwait(false));
                string warning = ReadWarning(dict);
                if (warning.Length > 0)
                {
                    lock (sync) { if (active) trackerWarning = warning; }
                    Log?.Invoke("Tracker warning: " + warning);
                }
                if (dict["files"] is not ValueDictionary files) return;
                result = files[Latin1.GetString(infoHash)] as ValueDictionary;
            }
            ct.ThrowIfCancellationRequested();
            lock (sync) { if (active && result is not null) ApplyCounts(result); }
            Publish();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (TrackerRejectedException ex) { Reject(ex.Message); }
        catch (Exception ex) { if (IsRunning) Log?.Invoke("Scrape unavailable: " + ex.Message); }
    }

    internal void Stop() => _ = StopAsync();
    internal Task StopAsync(string reason = "stopped", bool sendStopped = true, bool objectiveReached = false)
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            if (stopped is not null) return stopped.Task;
            if (!initialized) return Task.CompletedTask;
            Accumulate();
            if (!completedSent && downloaded == cfg.TotalLength) completionPending = true;
            active = false;
            stopRequested = true;
            stoppedAt = clock.GetTimestamp();
            finishedSuccessfully = false;
            completion = stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        lifetime.Cancel();
        lock (sync) CloseListener();
        Publish();
        Stopped?.Invoke(reason);
        _ = FinishStopAsync(completion, sendStopped, objectiveReached);
        return completion.Task;
    }

    private async Task FinishStopAsync(TaskCompletionSource completion, bool sendStopped, bool objectiveReached)
    {
        string? failure = null;
        string operation = "Session shutdown";
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            Task[] listeners;
            lock (sync) listeners = listenerTasks.ToArray();
            await Task.WhenAll(listeners.Append(loop).Append(announcements)).WaitAsync(timeout.Token).ConfigureAwait(false);
            bool rejected;
            lock (sync) rejected = trackerRejected;
            if (rejected && objectiveReached) failure = "Tracker rejection prevented successful completion.";
            if (sendStopped && !rejected)
            {
                if (objectiveReached && completionPending && !completedSent)
                {
                    try
                    {
                        operation = "Completion notification";
                        ValueDictionary completedResponse = await SendAnnounceAsync("completed", timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                        ThrowIfRejected(completedResponse);
                        lock (sync) { completedSent = true; completionPending = false; }
                    }
                    catch (TrackerRejectedException) { throw; }
                    catch (Exception ex)
                    {
                        failure = "Completion notification not delivered: " + ex.Message;
                    }
                }
                operation = "Stop notification";
                ValueDictionary response = await SendAnnounceAsync("stopped", timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                ThrowIfRejected(response);
            }
        }
        catch (Exception ex)
        {
            if (ex is TrackerRejectedException) Reject(ex.Message);
            string stopFailure = operation + " failed: " + ex.Message;
            failure = failure is null ? stopFailure : failure + "; " + stopFailure;
        }
        finally
        {
            lock (sync)
            {
                finishedSuccessfully = objectiveReached && sendStopped && failure is null && !trackerRejected;
                if (failure is not null) stopError = failure;
                foreach (CancellationTokenSource source in listenerLifetimes) source.Dispose();
                listenerLifetimes.Clear();
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
            double percent = cfg is null || cfg.TotalLength == 0 ? 100 : downloaded * 100.0 / cfg.TotalLength;
            var alert = CurrentAlert();
            return new EngineStats
            {
                IsActive = active,
                SampleElapsedSeconds = initialized ? Math.Max(0, clock.GetElapsedTime(startedAt, lastTick).TotalSeconds) : 0,
                AlertLevel = alert.Level, AlertMessage = alert.Message,
                Uploaded = uploaded, Downloaded = downloaded, TotalSize = cfg?.TotalLength ?? 0,
                Left = Math.Max(0, (cfg?.TotalLength ?? 0) - downloaded), FinishedPercent = percent,
                Ratio = downloaded > 0 ? (uploaded / (double)downloaded).ToString("0.00", CultureInfo.InvariantCulture) : "0.00",
                Seeders = seeders, Leechers = leechers, IntervalRemaining = active ? Remaining() : 0,
                TotalRunSeconds = initialized ? (int)Math.Min(int.MaxValue, clock.GetElapsedTime(startedAt, active ? clock.GetTimestamp() : stoppedAt).TotalSeconds) : 0,
                NumPeers = cfg?.NumWant ?? "0", SeedMode = percent >= 100, IsPaused = active && userPaused,
                UploadPausedNoLeechers = active && noLeechers, IsAnnouncing = active && (isAnnouncing || announceRequested),
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
        if (userPaused) return (EngineAlert.Warning, "Paused by you.");
        if (noLeechers) return (EngineAlert.Warning, "Paused: no leechers.");
        if (trackerWarning.Length > 0) return (EngineAlert.Warning, trackerWarning);
        if (listenerProblem.Length > 0) return (EngineAlert.Warning, listenerProblem);
        return !accepted ? (EngineAlert.Warning, "Waiting for tracker.") : (EngineAlert.Ok, "Tracker OK");
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
        return SessionSettings.NormalizeStopCondition(condition.Condition) switch
        {
            "When seeders <" when seeders >= 0 && seeders < value => "Stopped: seeders below threshold",
            "When leechers <" when leechers >= 0 && leechers < value => "Stopped: leechers below threshold",
            "When leechers / seeders <" when seeders > 0 && leechers >= 0 && leechers / (double)seeders < value => "Stopped: leechers/seeders below threshold",
            "When ratio >" when downloaded > 0 && uploaded / (double)downloaded >= value => "Stopped: target ratio reached",
            "When upload >" when uploaded / 1073741824.0 > value => "Stopped: uploaded above threshold",
            "When download >" when downloaded / 1073741824.0 > value => "Stopped: downloaded above threshold",
            "After time:" when value > 0 && clock.GetElapsedTime(startedAt).TotalSeconds >= value => "Stopped: time limit reached",
            _ => null,
        };
    }

    private void CloseListener()
    {
        listenerLifetime?.Cancel();
        listenerLifetime = null;
        listener?.Stop();
        listener = null;
    }

    private void RefreshListener(bool retryFailed = false)
    {
        lock (sync)
        {
            bool needed = active && !stopRequested && host.UseTcpListener && sessionProxy.Kind == ProxyKind.None;
            if (!needed) { CloseListener(); listenerFailed = false; listenerProblem = string.Empty; return; }
            if (listener is not null || (listenerFailed && !retryFailed)) return;
            try
            {
                TcpListener candidate = new(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, int.Parse(cfg.Port, CultureInfo.InvariantCulture));
                candidate.Server.ExclusiveAddressUse = true;
                if (Socket.OSSupportsIPv6) candidate.Server.DualMode = true;
                try { candidate.Start(8); }
                catch { candidate.Stop(); throw; }
                listener = candidate;
                listenerLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                listenerLifetimes.Add(listenerLifetime);
                listenerFailed = false;
                listenerProblem = string.Empty;
                Log?.Invoke("Started TCP listener on port " + cfg.Port);
                TrackListenerTask(AcceptAsync(candidate, listenerLifetime.Token));
            }
            catch (Exception ex)
            {
                listenerProblem = "TCP listener unavailable: " + ex.Message;
                Log?.Invoke(listenerProblem);
                // Bind retries follow accepted announces or an explicit listener toggle.
                listenerFailed = true;
            }
        }
    }
    private bool listenerFailed;
    private void TrackListenerTask(Task task)
    {
        lock (sync) listenerTasks.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (sync) listenerTasks.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task AcceptAsync(TcpListener server, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket = await server.AcceptSocketAsync(ct).ConfigureAwait(false);
                if (!await peerSlots.WaitAsync(0).ConfigureAwait(false)) { socket.Dispose(); continue; }
                lock (sync)
                {
                    if (!active || ct.IsCancellationRequested || !ReferenceEquals(listener, server))
                    {
                        socket.Dispose();
                        peerSlots.Release();
                        continue;
                    }
                    TrackListenerTask(HandshakeAsync(socket, ct));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task HandshakeAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            using (socket)
            using (NetworkStream stream = new(socket, ownsSocket: false))
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                byte[] request = new byte[68];
                await stream.ReadExactlyAsync(request, timeout.Token).ConfigureAwait(false);
                if (request[0] != 19 || !request.AsSpan(1, 19).SequenceEqual("BitTorrent protocol"u8)
                    || !request.AsSpan(28, 20).SequenceEqual(infoHash)) return;
                byte[] reply = new byte[68];
                reply[0] = 19;
                "BitTorrent protocol"u8.CopyTo(reply.AsSpan(1));
                infoHash.CopyTo(reply, 28);
                Latin1.GetBytes(cfg.PeerID).CopyTo(reply, 48);
                await stream.WriteAsync(reply, timeout.Token).ConfigureAwait(false);
                if (cfg.Realistic && GetStats().SeedMode && cfg.PieceCount > 0)
                {
                    int count = checked((int)((cfg.PieceCount + 7L) / 8));
                    if (count > 1024 * 1024) return;
                    byte[] bits = new byte[count + 5];
                    BinaryPrimitives.WriteInt32BigEndian(bits, count + 1);
                    bits[4] = 5;
                    bits.AsSpan(5).Fill(255);
                    int remainder = cfg.PieceCount % 8;
                    if (remainder != 0) bits[^1] = (byte)(255 << (8 - remainder));
                    await stream.WriteAsync(bits, timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { peerSlots.Release(); }
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

}
