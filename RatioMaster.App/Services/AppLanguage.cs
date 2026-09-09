namespace RatioMaster.Services;

using System.Globalization;

internal static class AppLanguage
{
    internal static void UseEnglishResources()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
