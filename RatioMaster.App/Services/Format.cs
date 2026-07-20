namespace RatioMaster.Services;

using System.Globalization;

internal static class Format
{
    internal static string FileSize(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        return bytes switch
        {
            >= 0x40000000 => string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", bytes / 1073741824.0),
            >= 0x100000 => string.Format(CultureInfo.InvariantCulture, "{0:0.00} MB", bytes / 1048576.0),
            >= 0x400 => string.Format(CultureInfo.InvariantCulture, "{0:0.00} KB", bytes / 1024.0),
            _ => bytes + " bytes",
        };
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
