namespace RatioMaster.Services;

using System;

/// <summary>Decouples "something notable happened" from the window that shows the toast.</summary>
internal static class NotificationHub
{
    internal static event Action<string, string, bool>? Requested;

    /// <summary>Request a toast. <paramref name="error"/> picks the error styling.</summary>
    internal static void Show(string title, string message, bool error = false) =>
        Requested?.Invoke(title, message, error);
}
