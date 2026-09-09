namespace RatioMaster.Services;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Checks public releases only when the user requests it.</summary>
internal static class UpdateChecker
{
    internal const string ReleasesUrl = "https://github.com/Freenitial/RatioMaster.NET_2.0/releases";
    private const string Endpoint = "https://api.github.com/repos/Freenitial/RatioMaster.NET_2.0/releases/latest";
    private static readonly HttpClient Client = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    internal static async Task<UpdateResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd("RatioMaster.NET-update-check");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return new("No published release was found.", null);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return new("GitHub is limiting requests. Try again later.", null);
        response.EnsureSuccessStatusCode();
        using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream body = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + count > 1024 * 1024) throw new IOException("GitHub's response exceeds the 1 MiB limit.");
            body.Write(buffer, 0, count);
        }
        return Parse(body.ToArray(), currentVersion);
    }

    internal static UpdateResult Parse(ReadOnlyMemory<byte> json, string currentVersion)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected release information.");
        if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True
            || root.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.ValueKind == JsonValueKind.True)
            return new("No newer stable release was found.", null);
        if (!root.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String
            || !TryVersion(tag.GetString(), out Version? available) || !TryVersion(currentVersion, out Version? current))
            throw new JsonException("The release version could not be read.");
        if (available <= current) return new($"Version {currentVersion} is up to date with published releases.", null);
        string url = ReleasesUrl;
        if (root.TryGetProperty("html_url", out JsonElement address) && address.ValueKind == JsonValueKind.String
            && Uri.TryCreate(address.GetString(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith("/Freenitial/RatioMaster.NET_2.0/releases/tag/", StringComparison.Ordinal)) url = uri.AbsoluteUri;
        return new($"Version {tag.GetString()} is available.", new Uri(url));
    }

    private static bool TryVersion(string? text, out Version? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out Version? parsed)) return false;
        result = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }
}

internal sealed record UpdateResult(string Message, Uri? ReleaseUri);
