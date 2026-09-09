namespace RatioMaster.Services;

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Serializes snapshots in request order and isolates fallback paths to their installation.</summary>
internal sealed class SessionRepository(string primaryPath, string? fallbackPath = null, string? legacyPath = null, string? migrationPath = null)
{
    internal const int MaxFileBytes = 16 * 1024 * 1024;
    private readonly object sync = new();
    private long requestedRevision;
    private readonly HashSet<string> damagedPaths = new(StringComparer.Ordinal);
    internal string PrimaryPath { get; } = primaryPath;
    internal string? LoadWarning { get; private set; }
    private string[] Paths => new[] { PrimaryPath, fallbackPath }.OfType<string>().Distinct().ToArray();

    internal void Save(SessionData data)
    {
        string json = Encode(data);
        long revision = Interlocked.Increment(ref requestedRevision);
        Write(json, revision);
    }

    internal Task SaveAsync(SessionData data)
    {
        string json = Encode(data);
        long revision = Interlocked.Increment(ref requestedRevision);
        return Task.Run(() => Write(json, revision));
    }

    private static string Encode(SessionData data)
    {
        if (data.Tabs is null || data.Tabs.Count > 512) throw new IOException("A saved session supports at most 512 tabs.");
        string json = JsonSerializer.Serialize(data, AppJsonContext.Default.SessionData);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new IOException("The saved session exceeds the 16 MiB limit.");
        return json;
    }

    private void Write(string json, long revision)
    {
        lock (sync)
        {
            if (revision != Volatile.Read(ref requestedRevision)) return;
            Exception? failure = null;
            foreach (string path in Paths)
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        file.Write(bytes);
                        file.Flush(flushToDisk: true);
                    }
                    if (damagedPaths.Contains(path)) File.Move(temporary, path, overwrite: true);
                    else if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                    else File.Move(temporary, path);
                    damagedPaths.Remove(path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            throw new IOException("Could not save the session. Check available disk space and folder permissions.", failure);
        }
    }

    internal SessionData? Load()
    {
        lock (sync)
        {
            LoadWarning = null;
            string[] candidates = Paths.Concat(new[] { migrationPath }.OfType<string>()).Distinct()
                .SelectMany(path => new[] { path, path + ".bak" })
                .OrderByDescending(LastWrite).ToArray();
            bool unreadable = false;
            foreach (string path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > MaxFileBytes) throw new JsonException("Session file exceeds the 16 MiB limit.");
                    using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    SessionData data = Decode(reader.ReadToEnd());
                    damagedPaths.Remove(path);
                    if (unreadable || path.EndsWith(".bak", StringComparison.Ordinal))
                        LoadWarning = "The latest session could not be read. A previous saved snapshot was restored.";
                    return data;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    damagedPaths.Add(path);
                    unreadable = true;
                }
            }
            if (unreadable) LoadWarning = "The saved session could not be read. Its files have been preserved.";
            else if (legacyPath is not null && File.Exists(legacyPath))
                LoadWarning = "A session exists in the shared temporary folder. It was preserved but not loaded because its installation cannot be identified.";
            return null;
        }
    }

    internal static SessionData Decode(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new JsonException("Session file is too large.");
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a session object.");
        if (!document.RootElement.TryGetProperty("Tabs", out JsonElement savedTabs) || savedTabs.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a session tab array.");
        SessionData data = JsonSerializer.Deserialize(json, AppJsonContext.Default.SessionData)
            ?? throw new JsonException("The session is empty.");
        if (data.FormatVersion is < 1 or > 2) throw new JsonException("This session format is not supported.");
        if (data.Tabs is null || data.Tabs.Count > 512) throw new JsonException("Invalid session tab list.");
        JsonElement.ArrayEnumerator rawTabs = default;
        bool hasRawTabs = document.RootElement.TryGetProperty("Tabs", out JsonElement tabs) && tabs.ValueKind == JsonValueKind.Array;
        if (hasRawTabs) rawTabs = tabs.EnumerateArray();
        foreach (TabState tab in data.Tabs)
        {
            if (tab is null) throw new JsonException("A session tab is empty.");
            Normalize(tab);
            if (hasRawTabs && rawTabs.MoveNext())
            {
                JsonElement raw = rawTabs.Current;
                if (!raw.TryGetProperty("RandomUpload", out _) && raw.TryGetProperty("RandUp", out JsonElement up)
                    && up.ValueKind is JsonValueKind.True or JsonValueKind.False) tab.RandomUpload = up.GetBoolean();
                if (!raw.TryGetProperty("RandomDownload", out _) && raw.TryGetProperty("RandDown", out JsonElement down)
                    && down.ValueKind is JsonValueKind.True or JsonValueKind.False) tab.RandomDownload = down.GetBoolean();
            }
        }
        data.FormatVersion = 2;
        return data;
    }

    private static void Normalize(TabState tab)
    {
        tab.TabName ??= "RM";
        tab.TorrentFilePath ??= string.Empty;
        tab.TorrentSourcePath ??= string.Empty;
        tab.LastDirectory ??= string.Empty;
        tab.TorrentHash ??= string.Empty;
        tab.Tracker ??= string.Empty;
        tab.UploadSpeed ??= "100";
        tab.DownloadSpeed ??= "0";
        tab.Interval ??= "1800";
        tab.Finished ??= "100";
        tab.StopWhen ??= "Never";
        tab.StopWhen = SessionSettings.NormalizeStopCondition(tab.StopWhen);
        tab.StopValue ??= string.Empty;
        tab.Family ??= "qBittorrent";
        tab.Version ??= "5.1.0";
        tab.CustomKey ??= string.Empty;
        tab.CustomPeerId ??= string.Empty;
        tab.CustomPort ??= string.Empty;
        tab.CustomPeers ??= string.Empty;
        tab.ProxyType ??= "None";
        tab.ProxyHost ??= string.Empty;
        tab.ProxyUser ??= string.Empty;
        tab.ProxyPass ??= string.Empty;
        tab.ProxyPort ??= string.Empty;
        tab.Uploaded = Math.Max(0, tab.Uploaded);
        tab.Downloaded = Math.Max(0, tab.Downloaded);
    }

    private static DateTime LastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}
