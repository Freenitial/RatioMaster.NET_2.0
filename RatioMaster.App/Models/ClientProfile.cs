namespace RatioMaster.Models;

/// <summary>
/// A hardcoded BitTorrent-client emulation profile: the exact User-Agent headers,
/// peer-id prefix, announce query string and key format a given client sends to a
/// tracker. This is what lets RatioMaster impersonate a client with no client running.
/// </summary>
internal sealed class ClientProfile
{
    internal string Name { get; set; } = string.Empty;

    internal string HttpProtocol { get; set; } = "HTTP/1.1";

    internal bool HashUpperCase { get; set; }

    internal string Key { get; set; } = string.Empty;

    internal string Headers { get; set; } = string.Empty;

    internal string PeerID { get; set; } = string.Empty;

    internal string Query { get; set; } = string.Empty;

    internal int DefNumWant { get; set; } = 200;
}
