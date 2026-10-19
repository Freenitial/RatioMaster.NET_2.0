#if DEBUG
namespace RatioMaster;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RatioMaster.Localization;
using RatioMaster.Services;

/// <summary>Every translation is complete and keeps the English placeholders; language detection and formatting.</summary>
internal static class LocalizationSelfTest
{
    internal static bool Run()
    {
        bool passed = true;
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("  [PASS] Localization: " + name); }
            catch (Exception error) { passed = false; Console.WriteLine("  [FAIL] Localization: " + name + " — " + error.Message); }
        }
        Check("English keys are collected from every Strings member", English);
        Check("every language file exists, is complete and keeps each placeholder", Translations);
        Check("system locale names map to the closest language", Matching);
        Check("units, decimal separator and percentages follow the language", Formatting);
        Check("switching language raises one change and English remains the fallback", Switching);
        Check("Android, macOS and Linux desktop list exactly the offered languages", PlatformLists);
        return passed;
    }

    private static void English()
    {
        SortedDictionary<string, string> english = StringsExport.English();
        Require(english.Count > 300, "Too few English strings were collected: " + english.Count);
        Require(english["ActionStart"] == "START" && english["UnitBytes"] == "{0} bytes", "English templates must be recorded verbatim.");
        Require(english.Values.All(value => value.Length > 0), "An English text is empty.");
    }

    private static void Translations()
    {
        SortedDictionary<string, string> english = StringsExport.English();
        List<string> problems = [];
        Require(Language.All.Select(language => language.Code).Distinct().Count() == Language.All.Count, "Language codes are distinct.");
        foreach (LanguageInfo language in Language.All.Where(language => language != Language.English))
        {
            IReadOnlyDictionary<string, string>? table = Language.Load(language.Code);
            if (table is null) { problems.Add(language.Code + ": translation file missing or invalid"); continue; }
            problems.AddRange(StringsExport.Validate(language.Code, english, table));
        }
        Require(problems.Count == 0, problems.Count + " problem(s): " + string.Join("; ", problems.Take(12)));
        Require(Language.All.Count(language => language.RightToLeft) == 3, "Arabic, Hebrew and Persian are written right to left.");
    }

    private static void Matching()
    {
        foreach ((string tag, string code) in new[]
        {
            ("fr_FR.UTF-8", "fr"), ("fr-CA", "fr"), ("de_DE@euro", "de"), ("pt_BR.UTF-8", "pt-BR"), ("pt", "pt-PT"), ("pt-PT", "pt-PT"),
            ("zh_CN.UTF-8", "zh-Hans"), ("zh-TW", "zh-Hant"), ("zh-Hant-HK", "zh-Hant"), ("zh-Hans-SG", "zh-Hans"),
            ("zh-Hans-HK", "zh-Hans"), ("zh-Hans-MO", "zh-Hans"), ("zh_HK", "zh-Hant"),
            ("sr_RS@latin", "sr-Latn"), ("sr-Latn-RS", "sr-Latn"), ("sr_RS", "sr-Cyrl"), ("nn_NO", "nb"), ("no", "nb"),
            ("iw-IL", "he"), ("in-ID", "id"), ("C", "en"), ("", "en"), ("xx-YY", "en"), ("fa-IR", "fa"), ("ko-KR", "ko"),
        })
            Require(Language.Match(tag).Code == code, tag + " must map to " + code + " but gave " + Language.Match(tag).Code);
    }

    private static void Formatting()
    {
        IReadOnlyDictionary<string, string> french = Language.Load("fr") ?? throw new Exception("French is missing.");
        Strings.Use(french);
        try
        {
            Require(Format.FileSize(1536L * 1024 * 1024).EndsWith(french["UnitGiB"], StringComparison.Ordinal), "Units must come from the language.");
            Require(Format.Number(1.5).Contains(french["DecimalSeparator"], StringComparison.Ordinal), "The decimal separator must come from the language.");
        }
        finally { Strings.Use(null); }
        Require(Format.FileSize(1536L * 1024 * 1024) == "1.50 GiB" && Format.FileSize(512) == "512 bytes", "English formatting is unchanged.");
        Require(Format.Percent(99.97) == "99.9%" && Format.Percent(100) == "100%" && Format.Percent(0) == "0%" && Format.Percent(double.NaN) == "0%",
            "Completion is never rounded up to 100%, and an unknown value is not shown as complete.");
    }

    private static void Switching()
    {
        int changes = 0;
        void Count() => changes++;
        Language.Changed += Count;
        try
        {
            Require(Language.Apply("ja") && Language.Current.Code == "ja" && Strings.ActionStart != "START", "Japanese must load.");
            Require(Language.Apply("ar") && Language.Current.RightToLeft, "Arabic must load and be right to left.");
            Require(Language.Apply("en") && Strings.ActionStart == "START", "English must restore the built-in texts.");
            Require(changes == 3, "Each effective switch raises one change: " + changes);
            Require(!Language.Apply("en") || changes == 3, "Re-applying the current language changes nothing.");
        }
        finally
        {
            Language.Changed -= Count;
            Language.ResetForTests();
        }
    }

    /// <summary>Platform files name the languages in their own conventions; each must map to every offered language once.</summary>
    private static void PlatformLists()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !(Directory.Exists(Path.Combine(root.FullName, "Installer")) && Directory.Exists(Path.Combine(root.FullName, "RatioMaster.App"))))
            root = root.Parent;
        if (root is null) { Console.WriteLine("    (source tree not found: platform lists not checked)"); return; }
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root.FullName, .. parts]));
        string[] expected = Language.All.Select(language => language.Code).Order(StringComparer.Ordinal).ToArray();
        void Compare(string platform, IEnumerable<string> tags)
        {
            string[] mapped = tags.Select(tag => Language.Match(tag).Code).Order(StringComparer.Ordinal).ToArray();
            Require(mapped.SequenceEqual(expected), platform + " lists " + string.Join(",", mapped) + " instead of the offered languages.");
        }
        Compare("Android", Regex.Matches(Read("RatioMaster.App", "Resources", "xml", "locales_config.xml"), "android:name=\"([^\"]+)\"").Select(match => match.Groups[1].Value));
        Compare("macOS", Regex.Match(Read("Installer", "Scripts", "package_macos.sh"), "for localization in ([^;]+);").Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Compare("Linux desktop", Regex.Matches(Read("Installer", "Data", "RatioMaster.desktop"), @"^Comment\[([^\]]+)\]", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).Append("en"));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
#endif
