namespace RatioMaster.Services;

using System.Globalization;
using RatioMaster.Localization;

/// <summary>
/// Display formatting in the interface language. Units and the decimal separator come from the translation
/// (portable Linux/macOS builds have no culture data), and digits stay ASCII so values remain comparable.
/// </summary>
internal static class Format
{
    internal static string Countdown(int seconds)
    {
        seconds = System.Math.Max(0, seconds);
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    internal static string FileSize(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        return bytes switch
        {
            >= 0x10000000000 => Number(bytes / 1099511627776.0) + " " + Strings.UnitTiB,
            >= 0x40000000 => Number(bytes / 1073741824.0) + " " + Strings.UnitGiB,
            >= 0x100000 => Number(bytes / 1048576.0) + " " + Strings.UnitMiB,
            >= 0x400 => Number(bytes / 1024.0) + " " + Strings.UnitKiB,
            _ => Strings.UnitBytes(bytes),
        };
    }

    /// <summary>Two decimals with the language's separator.</summary>
    internal static string Number(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", Strings.DecimalSeparator, System.StringComparison.Ordinal);

    /// <summary>Completion such as "45.2%", never rounded up to 100 before the last byte.</summary>
    internal static string Percent(double value)
    {
        double clamped = double.IsFinite(value) ? System.Math.Clamp(value, 0, 100) : 0;
        double shown = clamped < 100 ? System.Math.Min(System.Math.Round(clamped, 1), 99.9) : 100;
        string number = shown.ToString(shown == System.Math.Floor(shown) ? "0" : "0.0", CultureInfo.InvariantCulture)
            .Replace(".", Strings.DecimalSeparator, System.StringComparison.Ordinal);
        return Strings.PercentValue(number);
    }

    internal static string Time(int seconds)
    {
        if (seconds < 3600)
        {
            return $"{seconds / 60:00}:{seconds % 60:00}";
        }

        return $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
    }

    internal static string ToHex(byte[] bytes)
    {
        const string digits = "0123456789ABCDEF";
        char[] chars = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = digits[bytes[i] >> 4];
            chars[i * 2 + 1] = digits[bytes[i] & 0xF];
        }

        return new string(chars);
    }
}
