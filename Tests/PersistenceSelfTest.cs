#if DEBUG
namespace RatioMaster;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RatioMaster.Services;

internal static class PersistenceSelfTest
{
    internal static async Task<bool> RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ratiomaster-storage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool ok = true;
        void Check(bool condition, string name)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            ok &= condition;
        }
        try
        {
            string path = Path.Combine(directory, "session.json");
            SessionRepository repository = new(path);
            SessionData Snapshot(long uploaded) => new()
            {
                Tabs = [new() { TorrentHash = new string('A', 40), Uploaded = uploaded, CustomKey = "ABCD", KeyIsGenerated = false },
                    new() { TorrentHash = new string('B', 40), Uploaded = 77, Tracker = "https://tracker.invalid/second" }],
            };
            repository.Save(Snapshot(1));
            repository.Save(Snapshot(2));
            Check(repository.Load() is { Tabs.Count: 2 } loaded && loaded.Tabs[0].Uploaded == 2
                && loaded.Tabs[1].Uploaded == 77 && loaded.Tabs[1].Tracker.EndsWith("/second"), "atomic snapshots preserve independent tabs and tracker overrides");
            File.WriteAllText(path, "{truncated");
            Check(repository.Load()?.Tabs[0].Uploaded == 1 && repository.LoadWarning is not null,
                "corrupt latest snapshot restores backup and reports recovery");
            repository.Save(Snapshot(3));
            File.WriteAllText(path, "{damaged again");
            Check(repository.Load()?.Tabs[0].Uploaded == 1,
                "saving after recovery preserves the healthy backup instead of replacing it with corrupt data");
            Task[] writes = Enumerable.Range(10, 30).Select(i => repository.SaveAsync(Snapshot(i))).ToArray();
            repository.Save(Snapshot(99));
            await Task.WhenAll(writes);
            Check(repository.Load()?.Tabs[0].Uploaded == 99, "older asynchronous saves cannot overwrite the final snapshot");
            string legacy = Path.Combine(directory, "shared.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(Snapshot(123), AppJsonContext.Default.SessionData));
            SessionRepository separate = new(Path.Combine(directory, "other", "session.json"), legacyPath: legacy);
            Check(separate.Load() is null && separate.LoadWarning is not null && File.Exists(legacy),
                "unowned shared temporary session is preserved without cross-installation restore");
            string blockedPath = Path.Combine(directory, "blocked");
            File.WriteAllText(blockedPath, "not a directory");
            SessionRepository fallback = new(Path.Combine(blockedPath, "session.json"), Path.Combine(directory, "fallback", "session.json"));
            fallback.Save(Snapshot(45));
            Check(fallback.Load()?.Tabs[0].Uploaded == 45, "unwritable primary uses its dedicated fallback");
            SessionData old = SessionStore.Decode("{\"Tabs\":[{\"RandUp\":false,\"RandDown\":false,\"Uploaded\":12,\"CustomKey\":\"saved\"}]}");
            Check(!old.Tabs[0].RandomUpload && !old.Tabs[0].RandomDownload && old.Tabs[0].Uploaded == 12
                && old.Tabs[0].KeyIsGenerated is null, "legacy random flags and ambiguous custom provenance are preserved");
            SessionData modern = SessionStore.Decode("{\"Tabs\":[{\"RandUp\":false,\"RandomUpload\":true,\"Family\":null,\"Downloaded\":-1}]}");
            Check(modern.Tabs[0].RandomUpload && modern.Tabs[0].Family == "qBittorrent" && modern.Tabs[0].Downloaded == 0,
                "modern fields win over legacy aliases and null/negative values normalize");
            SessionData oldStops = SessionStore.Decode("{\"Tabs\":[{\"StopWhen\":\"When uploaded >\"},{\"StopWhen\":\"When downloaded >\"}]}");
            Check(oldStops.Tabs[0].StopWhen == "When upload >" && oldStops.Tabs[1].StopWhen == "When download >",
                "saved stop conditions migrate to the compact labels without changing their meaning");
            foreach (string malformed in new[] { "null", "[]", "{\"Tabs\":null}", "{\"Tabs\":[null]}" })
            {
                bool rejected = false;
                try { SessionStore.Decode(malformed); } catch (JsonException) { rejected = true; }
                Check(rejected, "invalid session structure is rejected: " + malformed);
            }
            string raw = "-UT3600-" + Encoding.Latin1.GetString(new byte[] { 0, 1, 127, 128, 255, 4, 5, 6, 7, 8, 9, 10 });
            Check(PeerIdentityText.TryParse(PeerIdentityText.Format(raw), out string decoded) && decoded == raw,
                "binary peer identity round-trips through editable text");
            TerminalBuffer terminal = new();
            int dispatches = 0;
            for (int i = 0; i < 20000; i++) if (terminal.Enqueue($"line {i:D5} " + new string('x', 90) + "\n")) dispatches++;
            string log = terminal.Drain("");
            Check(dispatches == 1 && log.Length <= TerminalBuffer.Capacity && log.Contains("line 19999") && !log.Contains("line 00000"),
                "queued terminal traffic is bounded and schedules one UI batch");
            TransferRateHistory rates = new(4);
            rates.Reset(10000);
            Check(!rates.Sample(10500, 0.5) && rates.Sample(13000, 3) && rates.Snapshot()[^1] == 1000,
                "graph samples bytes per second over elapsed time without shifting on intermediate UI events");
            rates.Reset(13000, 3);
            Check(rates.Snapshot().All(value => value == 0) && rates.Sample(14000, 4) && rates.Snapshot()[^1] == 1000,
                "clearing graph history preserves its current counter baseline without a resume spike");
            Check(UpdateChecker.Parse(Encoding.UTF8.GetBytes("{\"tag_name\":\"v2.0.1\"}"), "2.1.0").ReleaseUri is null,
                "older release is never offered as an update");
            Check(UpdateChecker.Parse(Encoding.UTF8.GetBytes("{\"tag_name\":\"v2.1.0\"}"), "2.1.0.0").ReleaseUri is null,
                "equivalent version components compare equally");
            Check(UpdateChecker.Parse(Encoding.UTF8.GetBytes("{\"tag_name\":\"v2.1.1\",\"html_url\":\"https://example.invalid/file\"}"), "2.1.0").ReleaseUri?.AbsoluteUri == UpdateChecker.ReleasesUrl,
                "update links remain confined to the project releases");
        }
        catch (Exception ex) { Check(false, ex.ToString()); }
        finally { Directory.Delete(directory, recursive: true); }
        return ok;
    }
}
#endif
