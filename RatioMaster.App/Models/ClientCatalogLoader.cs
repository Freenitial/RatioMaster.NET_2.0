namespace RatioMaster.Models;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

/// <summary>Parses a bounded declarative catalogue without reflection or executable extensions.</summary>
internal static class ClientCatalogLoader
{
    private static readonly string[] ProfileFields =
    [
        "family", "version", "base", "httpProtocol", "headers", "query", "peerIdPrefixHex", "peerIdLiteralPrefix",
        "peerIdTail", "peerIdByteMinimum", "peerIdByteMaximum", "urlEncoding", "hashUpperCase",
        "peerIdUpperCase", "keyLength", "keyUpperCase", "keyRefreshSeconds", "numWant", "supportsV2",
    ];
    private static readonly string[] Tokens =
    [
        "infohash", "peerid", "port", "uploaded", "downloaded", "left", "key", "event", "numwant",
    ];

    internal static ClientCatalogSnapshot Parse(ReadOnlyMemory<byte> json, ClientCatalogSnapshot builtIn)
    {
        if (json.Length > ClientCatalog.MaximumUserFileBytes) throw Invalid("The client catalogue exceeds 1 MiB.");
        if (json.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        RequireObject(root, "catalogue", ["format", "default", "clients"]);
        if (Integer(root, "format", 0) != 1) throw Invalid("Unsupported catalogue format; expected 1.");
        if (!root.TryGetProperty("clients", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            throw Invalid("clients must be an array.");
        if (entries.GetArrayLength() > 128) throw Invalid("At most 128 user profiles are allowed.");
        List<ClientProfileDefinition> result = new(builtIn.Definitions);
        HashSet<string> supplied = new(StringComparer.Ordinal);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            RequireObject(entry, "profile", ProfileFields);
            string family = RequiredString(entry, "family");
            string version = RequiredString(entry, "version");
            ValidateLabel(family, "family");
            ValidateLabel(version, "version");
            string name = family + " " + version;
            if (!supplied.Add(name)) throw Invalid("Duplicate profile: " + name);
            string? baseName = OptionalString(entry, "base");
            ClientProfileDefinition? template = baseName is null
                ? builtIn.Definitions.FirstOrDefault(profile => profile.Name == name)
                : builtIn.Definitions.FirstOrDefault(profile => profile.Name == baseName);
            if (baseName is not null && template is null) throw Invalid("Unknown built-in base: " + baseName);
            template ??= new ClientProfileDefinition
            {
                Family = family, Version = version, Headers = "", Query = "", PeerIdPrefix = "",
            };
            string? prefixHex = OptionalString(entry, "peerIdPrefixHex");
            string prefix = template.PeerIdPrefix;
            if (prefixHex is not null)
            {
                if (prefixHex.Length > 40) throw Invalid("peerIdPrefixHex cannot exceed 20 bytes.");
                try { prefix = Encoding.Latin1.GetString(Convert.FromHexString(prefixHex)); }
                catch (FormatException) { throw Invalid("peerIdPrefixHex must contain complete hexadecimal bytes."); }
            }
            ClientProfileDefinition profile = template with
            {
                Family = family, Version = version, PeerIdPrefix = prefix,
                HttpProtocol = OptionalString(entry, "httpProtocol") ?? template.HttpProtocol,
                Headers = ReadHeaders(entry) ?? template.Headers,
                Query = OptionalString(entry, "query") ?? template.Query,
                PeerIdLiteralPrefix = OptionalString(entry, "peerIdLiteralPrefix") ?? template.PeerIdLiteralPrefix,
                PeerIdTail = ParseTail(OptionalString(entry, "peerIdTail"), template.PeerIdTail),
                PeerIdByteMinimum = Integer(entry, "peerIdByteMinimum", template.PeerIdByteMinimum),
                PeerIdByteMaximum = Integer(entry, "peerIdByteMaximum", template.PeerIdByteMaximum),
                UrlEncoding = ParseEncoding(OptionalString(entry, "urlEncoding"), template.UrlEncoding),
                HashUpperCase = Boolean(entry, "hashUpperCase", template.HashUpperCase),
                PeerIdUpperCase = Boolean(entry, "peerIdUpperCase", template.PeerIdUpperCase),
                KeyLength = Integer(entry, "keyLength", template.KeyLength),
                KeyUpperCase = Boolean(entry, "keyUpperCase", template.KeyUpperCase),
                KeyRefreshSeconds = Integer(entry, "keyRefreshSeconds", template.KeyRefreshSeconds),
                NumWant = Integer(entry, "numWant", template.NumWant),
                SupportsV2 = Boolean(entry, "supportsV2", template.SupportsV2),
            };
            Validate(profile);
            int position = result.FindIndex(existing => existing.Name == profile.Name);
            if (position < 0) result.Add(profile);
            else result[position] = profile;
        }
        string defaultName = OptionalString(root, "default") ?? builtIn.Default.Name;
        if (!result.Any(profile => profile.Name == defaultName)) throw Invalid("The default profile does not exist: " + defaultName);
        return new ClientCatalogSnapshot(result, defaultName);
    }

    private static void Validate(ClientProfileDefinition profile)
    {
        if (profile.HttpProtocol is not ("HTTP/1.0" or "HTTP/1.1")) throw Invalid("httpProtocol must be HTTP/1.0 or HTTP/1.1.");
        if (profile.PeerIdPrefix.Length is < 1 or > 20) throw Invalid("The peer ID prefix must contain 1 to 20 bytes.");
        if (profile.PeerIdTail == PeerIdTailKind.Transmission && profile.PeerIdPrefix.Length != 8)
            throw Invalid("The Transmission checksum generator requires an 8-byte prefix.");
        if (profile.PeerIdByteMinimum is < 0 or > 255 || profile.PeerIdByteMaximum < profile.PeerIdByteMinimum
            || profile.PeerIdByteMaximum > 255) throw Invalid("The peer ID byte range must lie inside 0 through 255.");
        if (!profile.PeerIdPrefix.StartsWith(profile.PeerIdLiteralPrefix, StringComparison.Ordinal)
            || profile.PeerIdLiteralPrefix.Any(value => !IsLabelByte(value)))
            throw Invalid("peerIdLiteralPrefix must be a URL-safe beginning of the raw prefix.");
        if (profile.KeyLength is < 1 or > 8) throw Invalid("keyLength must be between 1 and 8 hexadecimal digits.");
        if (profile.KeyRefreshSeconds != 0 && (profile.KeyRefreshSeconds is < 60 or > 86400))
            throw Invalid("keyRefreshSeconds must be 0 or between 60 and 86400.");
        if (profile.NumWant is < 0 or > 200) throw Invalid("numWant must be between 0 and 200.");
        ValidateHeaders(profile.Headers);
        ValidateQuery(profile.Query);
    }

    private static void ValidateHeaders(string headers)
    {
        if (headers.Length is < 1 or > 8192) throw Invalid("Headers must contain 1 to 8192 characters.");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Any(value => value < ' ' || value > '~')) throw Invalid("Header lines must contain printable ASCII only.");
            int separator = line.IndexOf(':');
            if (separator < 1) throw Invalid("Each header needs a name and a value.");
            string name = line[..separator];
            string value = line[(separator + 1)..].Trim();
            if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                throw Invalid("Invalid header name: " + name);
            if (!names.Add(name)) throw Invalid("Duplicate header: " + name);
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                throw Invalid("This header is not part of a client profile: " + name);
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (value != "{host}") throw Invalid("Host must be {host}.");
            }
            else if (value.Contains('{') || value.Contains('}')) throw Invalid("Only Host may contain the {host} placeholder.");
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("close", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
                throw Invalid("Unsupported Connection header.");
            if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) && value.Length == 0)
                throw Invalid("User-Agent cannot be empty.");
            if (name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                ValidateAcceptEncoding(value);
        }
        if (!names.Contains("Host") || !names.Contains("User-Agent")) throw Invalid("Host and User-Agent headers are required.");
    }

    private static void ValidateAcceptEncoding(string value)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in value.Split(','))
        {
            string[] parts = entry.Trim().Split(';');
            string name = parts[0].Trim().ToLowerInvariant();
            if (name is not ("gzip" or "x-gzip" or "deflate" or "x-deflate" or "br" or "identity") || !seen.Add(name))
                throw Invalid("Unsupported or duplicate Accept-Encoding: " + name);
            if (parts.Length == 1) continue;
            if (parts.Length != 2) throw Invalid("Invalid Accept-Encoding parameters.");
            string quality = parts[1].Trim();
            if (!quality.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                || !decimal.TryParse(quality.AsSpan(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal weight)
                || weight is < 0 or > 1) throw Invalid("Invalid Accept-Encoding quality.");
        }
    }

    private static void ValidateQuery(string query)
    {
        if (query.Length is < 1 or > 8192 || query.Any(value => value < '!' || value > '~' || value == '#'))
            throw Invalid("The announce query must contain 1 to 8192 ASCII characters without spaces or fragments.");
        for (int index = 0; index < query.Length; index++)
        {
            if (query[index] == '}') throw Invalid("Unmatched query placeholder.");
            if (query[index] != '{') continue;
            int end = query.IndexOf('}', index + 1);
            if (end < 0 || !Tokens.Contains(query[(index + 1)..end], StringComparer.Ordinal))
                throw Invalid("Unknown or incomplete query placeholder.");
            index = end;
        }
        foreach (string required in new[] { "infohash", "peerid", "port", "uploaded", "downloaded", "left", "key", "event" })
            if (query.Split("{" + required + "}", StringSplitOptions.None).Length != 2)
                throw Invalid("The query must contain {" + required + "} exactly once.");
        HashSet<string> parameters = new(StringComparer.Ordinal);
        foreach (string parameter in query.Replace("{event}", "", StringComparison.Ordinal).Split('&'))
        {
            int separator = parameter.IndexOf('=');
            if (separator < 1) throw Invalid("Every query parameter must have a name and a value.");
            string name = parameter[..separator];
            if (!name.All(IsLabelByte) || !parameters.Add(name)) throw Invalid("Invalid or duplicate query parameter: " + name);
            if (name == "numwant" && parameter[(separator + 1)..] != "{numwant}")
                throw Invalid("numwant must use {numwant} so stop announcements can request zero peers.");
        }
        foreach ((string name, string token) in new[]
        {
            ("info_hash", "infohash"), ("peer_id", "peerid"), ("port", "port"), ("uploaded", "uploaded"),
            ("downloaded", "downloaded"), ("left", "left"), ("key", "key"),
        })
        {
            string expected = name + "={" + token + "}";
            if (!query.Replace("{event}", "", StringComparison.Ordinal).Split('&').Contains(expected, StringComparer.Ordinal))
                throw Invalid("The " + name + " parameter must use {" + token + "}.");
        }
    }

    private static string? ReadHeaders(JsonElement entry)
    {
        if (!entry.TryGetProperty("headers", out JsonElement headers)) return null;
        if (headers.ValueKind != JsonValueKind.Array || headers.GetArrayLength() is < 1 or > 32)
            throw Invalid("headers must be an array containing 1 to 32 lines.");
        StringBuilder result = new();
        foreach (JsonElement line in headers.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.String) throw Invalid("Header lines must be strings.");
            string value = line.GetString()!;
            if (value.Contains('\r') || value.Contains('\n')) throw Invalid("Each header entry must contain exactly one line.");
            result.Append(value).Append("\r\n");
        }
        return result.ToString();
    }

    private static void RequireObject(JsonElement element, string name, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(name + " must be an object.");
        HashSet<string> found = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                throw Invalid("Unknown or duplicate " + name + " property: " + property.Name);
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw Invalid(name + " is required.");

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid(name + " must be a string.");
        return value.GetString()!;
    }

    private static int Integer(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw Invalid(name + " must be a 32-bit integer.");
        return result;
    }

    private static bool Boolean(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(name + " must be true or false.");
        return value.GetBoolean();
    }

    private static PeerIdTailKind ParseTail(string? name, PeerIdTailKind fallback) => name switch
    {
        null => fallback,
        "libtorrent" => PeerIdTailKind.Libtorrent,
        "binary" => PeerIdTailKind.Binary,
        "transmission" => PeerIdTailKind.Transmission,
        _ => throw Invalid("Unknown peerIdTail: " + name),
    };

    private static ClientUrlEncoding ParseEncoding(string? name, ClientUrlEncoding fallback) => name switch
    {
        null => fallback,
        "libtorrent" => ClientUrlEncoding.Libtorrent,
        "rfc3986" => ClientUrlEncoding.Rfc3986,
        "alphanumeric" => ClientUrlEncoding.Alphanumeric,
        _ => throw Invalid("Unknown urlEncoding: " + name),
    };

    private static bool IsLabelByte(char value) => char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or '~';

    private static void ValidateLabel(string value, string name)
    {
        if (value.Length is < 1 or > 96 || value.Trim() != value || value.Any(character => character < ' ' || character > '~'))
            throw Invalid(name + " must contain 1 to 96 printable ASCII characters without surrounding whitespace.");
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
