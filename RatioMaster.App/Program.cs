using System;
using Avalonia;
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
#if DEBUG
        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            return SelfTest.Run();
        }
#endif

        // One instance per user session. Launching again just surfaces the running window — which is the
        // whole point, since closing the window only minimises the app to the tray, so "already running"
        // normally means "running invisibly". Tabs cover the multi-torrent case, and a second process
        // would clobber the shared session file.
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExistingInstance();
            return 0;
        }

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
