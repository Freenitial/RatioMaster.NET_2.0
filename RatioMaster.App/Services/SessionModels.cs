namespace RatioMaster.Services;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum CloseBehavior
{
    Ask,
    Background,
    Quit,
}

/// <summary>Persistable state of the whole app (portable settings + resume).</summary>
internal sealed class SessionData
{
    public int FormatVersion { get; set; } = 2;

    public WindowPlacement? Window { get; set; }

    [JsonConverter(typeof(CloseBehaviorJsonConverter))]
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;

    public List<TabState> Tabs { get; set; } = [];
}

internal sealed class CloseBehaviorJsonConverter : JsonConverter<CloseBehavior>
{
    public override CloseBehavior Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        // An unrecognized preference must not prevent the tabs from being restored.
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() switch
            {
                "Background" => CloseBehavior.Background,
                "Quit" => CloseBehavior.Quit,
                _ => CloseBehavior.Ask,
            };
        }

        reader.Skip();
        return CloseBehavior.Ask;
    }

    public override void Write(Utf8JsonWriter writer, CloseBehavior value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Everything one tab needs to be restored, including resume counters.</summary>
internal sealed class TabState
{
    public string TabName { get; set; } = "RM 1";

    public string TorrentFilePath { get; set; } = string.Empty;

    // Real re-openable path to reload the metadata from (== TorrentFilePath on desktop; a private cache copy
    // on Android, where TorrentFilePath holds only the display name). Empty when no torrent is loaded.
    public string TorrentSourcePath { get; set; } = string.Empty;

    public string LastDirectory { get; set; } = string.Empty;

    public string TorrentHash { get; set; } = string.Empty;

    public string Tracker { get; set; } = string.Empty;

    public string UploadSpeed { get; set; } = "100";

    // Smooth speed curves are configured independently for each direction.
    // Missing JSON properties use these defaults; unmapped properties are ignored.
    public bool RandomUpload { get; set; } = true;

    public string DownloadSpeed { get; set; } = "0";

    public bool RandomDownload { get; set; } = true;

    public string Interval { get; set; } = "1800";

    public string Finished { get; set; } = "100";

    public string StopWhen { get; set; } = "When upload >";

    public string StopValue { get; set; } = "1000";

    public string Family { get; set; } = "qBittorrent";

    public string Version { get; set; } = "5.1.0";

    public string CustomKey { get; set; } = string.Empty;

    public bool? KeyIsGenerated { get; set; }

    public bool? PeerIdIsGenerated { get; set; }

    public string CustomPeerId { get; set; } = string.Empty;

    public string CustomPort { get; set; } = string.Empty;

    public string CustomPeers { get; set; } = string.Empty;

    public bool RealisticMode { get; set; } = true;

    public bool UseTcpListener { get; set; } = true;

    public bool RequestScrape { get; set; } = true;

    public string ProxyType { get; set; } = "None";

    public string ProxyHost { get; set; } = string.Empty;

    public string ProxyUser { get; set; } = string.Empty;

    public string ProxyPass { get; set; } = string.Empty;

    public string ProxyPort { get; set; } = string.Empty;

    public bool EnableLog { get; set; } = true;

    public bool FinishedSuccessfully { get; set; }

    // Resume counters (cumulative bytes at last save).
    public long Uploaded { get; set; }

    public long Downloaded { get; set; }
}

internal sealed class WindowPlacement
{
    public int LayoutVersion { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 668;
    public bool Maximized { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(SessionData))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
