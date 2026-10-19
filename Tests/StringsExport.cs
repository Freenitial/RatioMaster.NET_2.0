#if DEBUG
namespace RatioMaster;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using RatioMaster.Localization;

/// <summary>
/// Debug tooling for translators: collects every key and its English template from <see cref="Strings"/>
/// (run: <c>RatioMaster.NET.exe --export-strings en.json</c>) and checks translations against it.
/// </summary>
internal static partial class StringsExport
{
    /// <summary>Every key with its English template, gathered by evaluating each member in recording mode.</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Debug-only tooling; parameter types are value types of this assembly.")]
    internal static SortedDictionary<string, string> English()
    {
        Dictionary<string, string> record = new(StringComparer.Ordinal);
        Strings.Recorder = record;
        try
        {
            foreach (PropertyInfo property in typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static))
                if (property.PropertyType == typeof(string)) property.GetValue(null);
            foreach (MethodInfo method in typeof(Strings).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.ReturnType != typeof(string) || method.IsSpecialName) continue;
                object?[] arguments = method.GetParameters()
                    .Select(parameter => parameter.ParameterType == typeof(string) ? string.Empty
                        : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null)
                    .ToArray();
                method.Invoke(null, arguments);
            }
        }
        finally
        {
            Strings.Recorder = null;
        }
        return new SortedDictionary<string, string>(record, StringComparer.Ordinal);
    }

    internal static int Run(string path)
    {
        File.WriteAllBytes(path, Serialize(English()));
        Console.WriteLine("Exported " + English().Count + " strings to " + path);
        return 0;
    }

    internal static byte[] Serialize(IEnumerable<KeyValuePair<string, string>> entries)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in entries) writer.WriteString(key, value);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    /// <summary>The placeholders a template uses ({0}, {1}…), as a sorted set.</summary>
    internal static string Placeholders(string template) =>
        string.Join(",", PlaceholderPattern().Matches(template).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal));

    /// <summary>Problems in one translation: missing, extra or empty keys and changed placeholders.</summary>
    internal static List<string> Validate(string code, IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> translation)
    {
        List<string> problems = [];
        foreach ((string key, string template) in english)
        {
            if (!translation.TryGetValue(key, out string? text)) problems.Add(code + ": missing " + key);
            else if (string.IsNullOrWhiteSpace(text) && template.Length > 0) problems.Add(code + ": empty " + key);
            else if (Placeholders(text) != Placeholders(template)) problems.Add(code + ": placeholders differ in " + key);
            else
            {
                try { _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, new object[] { "a", "b", "c" }); }
                catch (FormatException) { problems.Add(code + ": invalid format in " + key); }
            }
        }
        foreach (string key in translation.Keys)
            if (!english.ContainsKey(key)) problems.Add(code + ": unknown key " + key);
        return problems;
    }

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex PlaceholderPattern();
}
#endif
