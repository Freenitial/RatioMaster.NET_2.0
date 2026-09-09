namespace RatioMaster.Services;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

/// <summary>Portable desktop storage and app-private Android storage with atomic replacement.</summary>
internal static class SessionStore
{
    private const string FileName = "ratiomaster.session";
    private static readonly SessionRepository Repository = CreateRepository();
    internal static string SettingsDirectory => Path.GetDirectoryName(Repository.PrimaryPath)!;
    internal static string? LoadWarning => Repository.LoadWarning;

    private static SessionRepository CreateRepository()
    {
        string privateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RatioMaster");
        if (OperatingSystem.IsAndroid())
            return new SessionRepository(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FileName));
        if (OperatingSystem.IsMacOS())
            return new SessionRepository(Path.Combine(privateRoot, FileName), migrationPath: Path.Combine(AppContext.BaseDirectory, FileName));
        string location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        string normalized = OperatingSystem.IsWindows() ? location.ToUpperInvariant() : location;
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        return new SessionRepository(Path.Combine(location, FileName),
            Path.Combine(privateRoot, "Portable", identity, FileName), Path.Combine(Path.GetTempPath(), FileName));
    }

    internal static void Save(SessionData data) => Repository.Save(data);
    internal static Task SaveAsync(SessionData data) => Repository.SaveAsync(data);
    internal static SessionData? Load() => Repository.Load();
    internal static SessionData Decode(string json) => SessionRepository.Decode(json);
}
