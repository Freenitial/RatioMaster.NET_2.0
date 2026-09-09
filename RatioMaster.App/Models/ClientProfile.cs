namespace RatioMaster.Models;

using System;
using RatioMaster.Services;

internal enum ClientUrlEncoding
{
    Libtorrent,
    Rfc3986,
    Alphanumeric,
}

/// <summary>Announce formatting and one generated identity; PeerID contains raw Latin-1 bytes.</summary>
internal sealed class ClientProfile
{
    internal string Name { get; set; } = string.Empty;
    internal string Family { get; set; } = string.Empty;
    internal string Version { get; set; } = string.Empty;
    internal string HttpProtocol { get; set; } = "HTTP/1.1";
    internal bool HashUpperCase { get; set; }
    internal bool PeerIdUpperCase { get; set; }
    internal string PeerIdLiteralPrefix { get; set; } = string.Empty;
    internal ClientUrlEncoding UrlEncoding { get; set; } = ClientUrlEncoding.Libtorrent;
    internal string Key { get; set; } = string.Empty;
    internal int KeyLength { get; set; } = 8;
    internal bool KeyUpperCase { get; set; } = true;
    internal TimeSpan KeyRefreshInterval { get; set; }
    internal string Headers { get; set; } = string.Empty;
    internal string PeerID { get; set; } = string.Empty;
    internal string Query { get; set; } = string.Empty;
    internal int DefNumWant { get; set; } = 200;
    internal bool SupportsV2 { get; set; }

    internal ClientProfile Snapshot() => (ClientProfile)MemberwiseClone();

    internal string EncodePeerId(string raw)
    {
        RandomStringGenerator encoder = new();
        if (PeerIdLiteralPrefix.Length > 0 && raw.StartsWith(PeerIdLiteralPrefix, StringComparison.Ordinal))
            return PeerIdLiteralPrefix + encoder.UrlEncode(raw[PeerIdLiteralPrefix.Length..], PeerIdUpperCase, UrlEncoding);
        return encoder.UrlEncode(raw, PeerIdUpperCase, UrlEncoding);
    }
}
