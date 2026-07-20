namespace RatioMaster;

using System.Globalization;

internal static class ParseExtensions
{
    internal static int ParseIntOr(this string? s, int defaultValue) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : defaultValue;

    internal static long ParseLongOr(this string? s, long defaultValue) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : defaultValue;

    internal static double ParseDoubleOr(this string? s, double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return defaultValue;
        }

        string normalized = s.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : defaultValue;
    }
}
