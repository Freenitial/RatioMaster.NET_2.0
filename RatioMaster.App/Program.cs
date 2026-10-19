using System;
using System.Linq;
using Avalonia;
using RatioMaster.Localization;
using RatioMaster.Services;

namespace RatioMaster;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // The system language is read before the process switches its own formatting culture to invariant.
        Language.CaptureSystemLanguage();
        AppLanguage.UseEnglishResources();
        if (Array.IndexOf(args, "--runtime-selftest") >= 0) return RuntimeSelfTest.Run();
#if DEBUG
        if (Array.IndexOf(args, "--ui-selftest") >= 0) return UiSelfTest.Run();
        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            return SelfTest.Run();
        }
        if (Array.IndexOf(args, "--export-strings") is int export and >= 0 && export + 1 < args.Length)
        {
            return StringsExport.Run(args[export + 1]);
        }
        if (Array.IndexOf(args, "--write-request-goldens") is int goldens and >= 0 && goldens + 1 < args.Length)
        {
            return ProfileSelfTest.WriteGoldens(args[goldens + 1]);
        }
#endif

        // .torrent files and folders given on the command line (file association, "Open with", drag onto the icon).
        string[] files = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();

        // One instance per user session. Launching again surfaces the running window and hands it the files —
        // tabs cover the multi-torrent case, and a second process would clobber the shared session file.
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExistingInstance(files);
            return 0;
        }

        SingleInstance.StartListener();
        App.StartupFiles = files;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>().UsePlatformDetect();

        // Windows: software renderer → zero GPU dependency (no ANGLE / opengl32 / Vulkan at runtime),
        // and the linker can dead-code-strip GL/Vulkan from the statically-linked Skia. Fine for this UI
        // (only the pulse opacity animates). On Linux/macOS, leave UsePlatformDetect's default backend.
        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } });
        }

        return builder.WithInterFont().LogToTrace();
    }
}
