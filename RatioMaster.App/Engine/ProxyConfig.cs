namespace RatioMaster.Engine;

/// <summary>Supported tracker-connection proxy types (matches the UI dropdown order).</summary>
internal enum ProxyKind
{
    None = 0,
    HttpConnect = 1,
    Socks4 = 2,
    Socks4a = 3,
    Socks5 = 4,
}

internal sealed class ProxyConfig
{
    internal ProxyKind Kind { get; set; } = ProxyKind.None;

    internal string Host { get; set; } = string.Empty;

    internal int Port { get; set; }

    internal string User { get; set; } = string.Empty;

    internal string Password { get; set; } = string.Empty;
}
