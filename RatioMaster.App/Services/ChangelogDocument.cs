namespace RatioMaster.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using RatioMaster.Models;

/// <summary>Reads release sections from the changelog embedded in the application.</summary>
internal static class ChangelogDocument
{
    private static readonly Lazy<IReadOnlyList<ChangelogRelease>> Document = new(Load);
    internal static IReadOnlyList<ChangelogRelease> Releases => Document.Value;

    private static IReadOnlyList<ChangelogRelease> Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RatioMaster.CHANGELOG.md")
            ?? throw new InvalidOperationException("The embedded changelog is unavailable.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    internal static IReadOnlyList<ChangelogRelease> Parse(string markdown)
    {
        List<ChangelogRelease> releases = [];
        List<ChangelogItem> items = [];
        StringBuilder paragraph = new();
        string version = string.Empty;
        void FinishParagraph()
        {
            string text = PlainText(paragraph.ToString().Trim());
            paragraph.Clear();
            if (text.Length == 0) return;
            int separator = text.IndexOf(" — ", StringComparison.Ordinal);
            items.Add(separator > 0
                ? new ChangelogItem(text[..separator], text[(separator + 3)..])
                : new ChangelogItem(string.Empty, text));
        }
        void FinishRelease()
        {
            FinishParagraph();
            if (version.Length > 0 && items.Count > 0)
                releases.Add(new(version, items.ToArray(), releases.Count == 0));
            items.Clear();
        }
        using StringReader input = new(markdown);
        string? raw;
        while ((raw = input.ReadLine()) is not null)
        {
            string line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("---=== ", StringComparison.Ordinal))
            {
                FinishRelease();
                version = line.StartsWith("## ", StringComparison.Ordinal) ? PlainText(line[3..]) : line[7..].Replace(" ===---", "", StringComparison.Ordinal);
                continue;
            }
            if (version.Length == 0 || line.StartsWith("# ", StringComparison.Ordinal) || line == "---") continue;
            if (line.Length == 0)
            {
                FinishParagraph();
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FinishParagraph();
                line = line[2..];
            }
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line);
        }
        FinishRelease();
        return releases;
    }

    private static string PlainText(string text) => Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1", RegexOptions.CultureInvariant)
        .Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
}
