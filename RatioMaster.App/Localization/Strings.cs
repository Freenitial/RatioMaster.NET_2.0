namespace RatioMaster.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

/// <summary>
/// Every user-visible text. The English text is written here, next to its key (the member name); other
/// languages are embedded JSON files mapping the same keys. Members are resolved at compile time from C# and
/// XAML (x:Static), so a missing key is a build error and no reflection is needed at run time (Native AOT).
/// Placeholders are positional ({0}, {1}) and are checked against the English text by the self-tests.
/// </summary>
public static partial class Strings
{
    private static IReadOnlyDictionary<string, string>? current;

    internal static void Use(IReadOnlyDictionary<string, string>? translations) => current = translations;

#if DEBUG
    /// <summary>Debug tooling: while set, every lookup records its key and English template instead of translating.</summary>
    internal static Dictionary<string, string>? Recorder { get; set; }
#endif

    private static string T(string english, [CallerMemberName] string key = "")
    {
#if DEBUG
        if (Recorder is { } record) { record[key] = english; return english; }
#endif
        return current is not null && current.TryGetValue(key, out string? text) ? text : english;
    }

    // Distinct names per arity: a string argument must never bind to the caller-name parameter.
    private static string Format(string english, object?[] arguments, string key)
    {
        string template = T(english, key);
        try { return string.Format(CultureInfo.InvariantCulture, template, arguments); }
        catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, english, arguments); }
    }

    private static string F1(string english, object? first, [CallerMemberName] string key = "") => Format(english, [first], key);
    private static string F2(string english, object? first, object? second, [CallerMemberName] string key = "") => Format(english, [first, second], key);
    private static string F3(string english, object? first, object? second, object? third, [CallerMemberName] string key = "") => Format(english, [first, second, third], key);
}
