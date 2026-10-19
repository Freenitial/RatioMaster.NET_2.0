namespace RatioMaster.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>One interface language. <see cref="Code"/> names its embedded translation file.</summary>
public sealed record LanguageInfo(string Code, string NativeName, string EnglishName, bool RightToLeft = false)
{
    public override string ToString() => NativeName;
}

/// <summary>
/// Chooses and loads the interface language. The system language is read without ICU (portable Linux and
/// macOS builds use invariant globalization): Windows UI culture, POSIX locale variables, the macOS preferred
/// languages and the Android locale. A saved choice overrides it and can be changed while the program runs.
/// </summary>
internal static partial class Language
{
    internal const string SystemPreference = "";
    private const int MaximumFileBytes = 512 * 1024;
    private static string? systemTag;

    internal static readonly LanguageInfo English = new("en", "English", "English");

    internal static IReadOnlyList<LanguageInfo> All { get; } =
    [
        English,
        new("ar", "العربية", "Arabic", RightToLeft: true),
        new("bg", "Български", "Bulgarian"),
        new("ca", "Català", "Catalan"),
        new("cs", "Čeština", "Czech"),
        new("da", "Dansk", "Danish"),
        new("de", "Deutsch", "German"),
        new("el", "Ελληνικά", "Greek"),
        new("es", "Español", "Spanish"),
        new("et", "Eesti", "Estonian"),
        new("fa", "فارسی", "Persian", RightToLeft: true),
        new("fi", "Suomi", "Finnish"),
        new("fr", "Français", "French"),
        new("he", "עברית", "Hebrew", RightToLeft: true),
        new("hi", "हिन्दी", "Hindi"),
        new("hr", "Hrvatski", "Croatian"),
        new("hu", "Magyar", "Hungarian"),
        new("id", "Bahasa Indonesia", "Indonesian"),
        new("it", "Italiano", "Italian"),
        new("ja", "日本語", "Japanese"),
        new("ko", "한국어", "Korean"),
        new("lt", "Lietuvių", "Lithuanian"),
        new("lv", "Latviešu", "Latvian"),
        new("nb", "Norsk bokmål", "Norwegian"),
        new("nl", "Nederlands", "Dutch"),
        new("pl", "Polski", "Polish"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new("pt-PT", "Português (Portugal)", "Portuguese (Portugal)"),
        new("ro", "Română", "Romanian"),
        new("ru", "Русский", "Russian"),
        new("sk", "Slovenčina", "Slovak"),
        new("sl", "Slovenščina", "Slovenian"),
        new("sq", "Shqip", "Albanian"),
        new("sr-Cyrl", "Српски", "Serbian (Cyrillic)"),
        new("sr-Latn", "Srpski", "Serbian (Latin)"),
        new("sv", "Svenska", "Swedish"),
        new("th", "ไทย", "Thai"),
        new("tr", "Türkçe", "Turkish"),
        new("uk", "Українська", "Ukrainian"),
        new("vi", "Tiếng Việt", "Vietnamese"),
        new("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new("zh-Hant", "繁體中文", "Chinese (Traditional)"),
    ];

    /// <summary>The language in use now.</summary>
    internal static LanguageInfo Current { get; private set; } = English;

    /// <summary>The saved choice: a language code, or empty to follow the system.</summary>
    internal static string Preference { get; private set; } = SystemPreference;

    /// <summary>The displayed language changed: texts must be rebuilt.</summary>
    internal static event Action? Changed;

    /// <summary>The saved choice changed, even when the displayed language stays the same.</summary>
    internal static event Action? PreferenceChanged;

    /// <summary>Reads the system language once, before the process switches its own culture to invariant.</summary>
    internal static void CaptureSystemLanguage() => systemTag ??= DetectSystemTag() ?? string.Empty;

    internal static LanguageInfo SystemLanguage => Match(systemTag ?? DetectSystemTag());

#if DEBUG
    /// <summary>Tests: back to the state a test run starts in (English texts, system preference), without events.</summary>
    internal static void ResetForTests()
    {
        Preference = SystemPreference;
        Current = English;
        Strings.Use(null);
    }
#endif

    /// <summary>
    /// The system language changed while the program runs (Android settings, including the per-app language of
    /// Android 13+, which <paramref name="applicationTag"/> carries: empty for "system default").
    /// </summary>
    internal static void FollowSystem(string? applicationTag = null)
    {
        systemTag = DetectSystemTag() ?? string.Empty;
        string preference = applicationTag is null ? Preference
            : applicationTag.Length == 0 ? SystemPreference : Match(applicationTag).Code;
        Apply(preference);
    }

    /// <summary>Selects a language; returns false (and keeps English) when its translation cannot be loaded.</summary>
    internal static bool Apply(string? preference)
    {
        string previous = Preference;
        Preference = All.Any(language => language.Code == preference) ? preference! : SystemPreference;
        LanguageInfo target = Preference.Length > 0 ? All.First(language => language.Code == Preference) : SystemLanguage;
        IReadOnlyDictionary<string, string>? table = null;
        bool loaded = true;
        if (target != English)
        {
            table = Load(target.Code);
            loaded = table is not null;
            if (!loaded) target = English;
        }
        bool changed = !ReferenceEquals(target, Current);
        Current = target;
        Strings.Use(table);
        if (changed) Changed?.Invoke();
        if (Preference != previous)
        {
#if ANDROID
            PublishPreference();
#endif
            PreferenceChanged?.Invoke();
        }
        return loaded;
    }

#if ANDROID
    /// <summary>Android 13+: the per-app language shown in the system settings mirrors the choice made here.</summary>
    private static void PublishPreference()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        try
        {
            if (Android.App.Application.Context.GetSystemService(Android.Content.Context.LocaleService) is not Android.App.LocaleManager manager) return;
            Android.OS.LocaleList wanted = Preference.Length == 0 ? Android.OS.LocaleList.EmptyLocaleList : Android.OS.LocaleList.ForLanguageTags(Preference);
            if (manager.ApplicationLocales.ToLanguageTags() != wanted.ToLanguageTags()) manager.ApplicationLocales = wanted;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The system setting is a convenience; the choice saved with the session remains authoritative.
        }
    }
#endif

    internal static IReadOnlyDictionary<string, string>? Load(string code)
    {
        try
        {
            using Stream? stream = typeof(Language).Assembly.GetManifestResourceStream("RatioMaster.Localization." + code + ".json");
            if (stream is null || stream.Length > MaximumFileBytes) return null;
            byte[] bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Parse(bytes);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A flat JSON object of string values; keys starting with "_" are comments for translators.</summary>
    internal static Dictionary<string, string> Parse(ReadOnlySpan<byte> json)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (json.StartsWith("﻿"u8)) json = json[3..];
        Utf8JsonReader reader = new(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 2 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Expected a JSON object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new InvalidDataException("Expected a property name.");
            string key = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new InvalidDataException("Translations must be strings.");
            if (!key.StartsWith('_')) result[key] = reader.GetString()!;
        }
        return result;
    }

    /// <summary>Maps a BCP 47 or POSIX locale name to the closest available language.</summary>
    internal static LanguageInfo Match(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return English;
        string value = tag.Trim();
        int encoding = value.IndexOf('.');
        string modifier = string.Empty;
        int at = value.IndexOf('@');
        if (at >= 0) { modifier = value[(at + 1)..].ToLowerInvariant(); value = value[..at]; }
        if (encoding >= 0 && (at < 0 || encoding < at)) value = value[..Math.Min(encoding, value.Length)];
        string[] parts = value.Replace('_', '-').ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return English;
        string primary = parts[0] switch { "iw" => "he", "in" => "id", "no" or "nn" => "nb", _ => parts[0] };
        string code = primary switch
        {
            // An explicit script wins over the region: zh-Hans-HK is simplified Chinese.
            "zh" => parts.Contains("hans") ? "zh-Hans" : parts.Skip(1).Any(part => part is "hant" or "tw" or "hk" or "mo") ? "zh-Hant" : "zh-Hans",
            "sr" => parts.Skip(1).Contains("latn") || modifier == "latin" ? "sr-Latn" : "sr-Cyrl",
            "pt" => parts.Skip(1).Contains("br") ? "pt-BR" : "pt-PT",
            _ => primary,
        };
        return All.FirstOrDefault(language => language.Code == code) ?? English;
    }

    private static string? DetectSystemTag()
    {
        try
        {
#if ANDROID
            return Java.Util.Locale.Default?.ToLanguageTag();
#else
            if (OperatingSystem.IsWindows()) return CultureInfo.CurrentUICulture.Name;
            foreach (string name in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(value)) continue;
                string first = value.Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                if (first is "C" or "POSIX" || first.StartsWith("C.", StringComparison.Ordinal)) continue;
                return first;
            }
            return OperatingSystem.IsMacOS() ? MacPreferredLanguage() : null;
#endif
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

#if !ANDROID
    // CoreFoundation: the first entry of the user's preferred languages (GUI apps get no LANG variable on macOS).
    private static string? MacPreferredLanguage()
    {
        IntPtr languages = CFLocaleCopyPreferredLanguages();
        if (languages == IntPtr.Zero) return null;
        try
        {
            if (CFArrayGetCount(languages) < 1) return null;
            IntPtr first = CFArrayGetValueAtIndex(languages, 0);
            Span<byte> buffer = stackalloc byte[64];
            unsafe
            {
                fixed (byte* pointer = buffer)
                {
                    if (!CFStringGetCString(first, pointer, buffer.Length, 0x08000100)) return null;
                }
            }
            int length = buffer.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(buffer[..(length < 0 ? buffer.Length : length)]);
        }
        finally { CFRelease(languages); }
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFLocaleCopyPreferredLanguages();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool CFStringGetCString(IntPtr value, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);
#endif
}
