namespace RatioMaster.Services;

using System;
using System.IO;
using System.Text.Json;

/// <summary>
/// Portable session persistence. Writes <c>ratiomaster.session</c> next to the exe when
/// possible (portable), otherwise falls back to %TEMP%. AOT-safe (source-generated JSON).
/// </summary>
internal static class SessionStore
{
    private const string FileName = "ratiomaster.session";

    private static string PrimaryPath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static string FallbackPath => Path.Combine(Path.GetTempPath(), FileName);

    internal static void Save(SessionData data)
    {
        string json = JsonSerializer.Serialize(data, AppJsonContext.Default.SessionData);
        try
        {
            File.WriteAllText(PrimaryPath, json);
        }
        catch
        {
            try
            {
                File.WriteAllText(FallbackPath, json);
            }
            catch
            {
                // give up silently — persistence is best-effort
            }
        }
    }

    internal static SessionData? Load()
    {
        foreach (string path in new[] { PrimaryPath, FallbackPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.SessionData);
                }
            }
            catch
            {
                // corrupt / unreadable → ignore, try next
            }
        }

        return null;
    }

    internal static void Delete()
    {
        foreach (string path in new[] { PrimaryPath, FallbackPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
