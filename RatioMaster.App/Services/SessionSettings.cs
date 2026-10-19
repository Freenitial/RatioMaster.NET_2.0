namespace RatioMaster.Services;

using RatioMaster.Engine;

/// <summary>
/// Stable identifiers persisted in sessions. They are the English labels earlier versions displayed and saved,
/// so older sessions load unchanged; the display text is localized separately.
/// </summary>
internal static class SessionSettings
{
    internal static StopCondition ParseStopCondition(string? value) => value switch
    {
        "When ratio >" => StopCondition.RatioAbove,
        "When upload >" or "When uploaded >" => StopCondition.UploadAbove,
        "When download >" or "When downloaded >" => StopCondition.DownloadAbove,
        "After time:" => StopCondition.AfterTime,
        "When seeders <" => StopCondition.SeedersBelow,
        "When leechers <" => StopCondition.LeechersBelow,
        "When leechers / seeders <" => StopCondition.SwarmRatioBelow,
        _ => StopCondition.Never,
    };

    internal static string FormatStopCondition(StopCondition condition) => condition switch
    {
        StopCondition.RatioAbove => "When ratio >",
        StopCondition.UploadAbove => "When upload >",
        StopCondition.DownloadAbove => "When download >",
        StopCondition.AfterTime => "After time:",
        StopCondition.SeedersBelow => "When seeders <",
        StopCondition.LeechersBelow => "When leechers <",
        StopCondition.SwarmRatioBelow => "When leechers / seeders <",
        _ => "Never",
    };

    internal static ProxyKind ParseProxy(string? value) => value switch
    {
        "HTTP" => ProxyKind.HttpConnect,
        "Socks4" => ProxyKind.Socks4,
        "Socks4a" => ProxyKind.Socks4a,
        "Socks5" => ProxyKind.Socks5,
        _ => ProxyKind.None,
    };

    internal static string FormatProxy(ProxyKind kind) => kind switch
    {
        ProxyKind.HttpConnect => "HTTP",
        ProxyKind.Socks4 => "Socks4",
        ProxyKind.Socks4a => "Socks4a",
        ProxyKind.Socks5 => "Socks5",
        _ => "None",
    };

}
