namespace RatioMaster;

using System;
using System.Text.Json;
using RatioMaster.Models;
using RatioMaster.Services;
using RatioMaster.ViewModels;

/// <summary>Checks published native dependencies and generated serialization without loading user data.</summary>
internal static class RuntimeSelfTest
{
    internal static int Run()
    {
        try
        {
            if (ChangelogDocument.Releases.Count == 0 || ChangelogDocument.Releases[0].Version != AppInfo.Version)
                throw new InvalidOperationException("The embedded changelog does not match this release.");
            foreach (ClientFamily family in ClientCatalog.Families)
            {
                foreach (string version in family.Versions)
                {
                    ClientProfile profile = ClientCatalog.Create(family.Name, version);
                    if (!PeerIdentityText.TryParse(PeerIdentityText.Format(profile.PeerID), out string raw) || raw != profile.PeerID)
                        throw new InvalidOperationException("Client identity cannot round-trip.");
                }
            }
            SessionData saved = new() { Tabs = [new() { CustomKey = "12345678", KeyIsGenerated = true }] };
            string json = JsonSerializer.Serialize(saved, AppJsonContext.Default.SessionData);
            if (SessionStore.Decode(json).Tabs[0].KeyIsGenerated != true)
                throw new InvalidOperationException("Session serialization failed.");
            using SkiaSharp.SKBitmap bitmap = new(8, 8);
            bitmap.Erase(SkiaSharp.SKColors.Black);
            if (bitmap.GetPixel(0, 0) != SkiaSharp.SKColors.Black)
                throw new InvalidOperationException("Native graphics initialization failed.");
            const string sample = "RatioMaster";
            using HarfBuzzSharp.Buffer text = new();
            text.AddUtf8(sample);
            text.GuessSegmentProperties();
            if (text.Length != sample.Length)
                throw new InvalidOperationException("Native text initialization failed.");
            Console.WriteLine("Runtime checks passed: profiles, serialization, Skia and HarfBuzz. No user data or network used.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
