namespace RatioMaster.Services;

using System;
using System.Linq;
using System.Text;

/// <summary>Represents arbitrary twenty-byte identities in an editable, reversible text field.</summary>
internal static class PeerIdentityText
{
    internal static string Format(string raw) => !raw.StartsWith("hex:", StringComparison.OrdinalIgnoreCase) && raw.All(c => c >= 32 && c <= 126)
        ? raw : "hex:" + Convert.ToHexString(Encoding.Latin1.GetBytes(raw));

    internal static bool TryParse(string text, out string raw)
    {
        raw = text;
        if (text.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        {
            try { raw = Encoding.Latin1.GetString(Convert.FromHexString(text[4..])); }
            catch (FormatException) { return false; }
        }
        return raw.Length == 20 && raw.All(c => c <= 255);
    }
}
