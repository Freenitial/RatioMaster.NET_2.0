// Android entry point — compiled ONLY for the net11.0-android target (multi-target gated on
// -p:IncludeAndroid=true, which build_RatioMaster_setup.bat's apk/aab phase sets). The ANDROID symbol
// is auto-defined for net11.0-android, so on every desktop build this whole file is empty and pulls in
// no Avalonia.Android reference. ONE project + ONE shared UI (App + MainView); Android adds only this
// Activity + a manifest (both inert on desktop).
#if ANDROID
using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace RatioMaster;

/// <summary>
/// Avalonia 12 moved the Android bootstrap onto the <c>Android.App.Application</c> subclass
/// (<see cref="AvaloniaAndroidApplication{TApp}"/>): <c>CustomizeAppBuilder</c> lives here, and the
/// activity is the non-generic <see cref="AvaloniaMainActivity"/>. Avalonia drives the single-view
/// lifetime, so the shared <c>App.OnFrameworkInitializationCompleted</c> takes its
/// <c>ISingleViewApplicationLifetime</c> branch and shows <c>MainView</c>.
/// </summary>
[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    // Android instantiates the Application via this (handle, ownership) ctor through JNI.
    public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();
}

/// <summary>
/// We DO declare Orientation/ScreenSize in ConfigurationChanges so a rotation does NOT recreate the
/// activity — RatioMaster keeps running emulation sessions + open tabs, which an activity restart would
/// throw away. The safe area refreshes via the AndroidX WindowInsets listener below (re-fires on any inset
/// change, incl. rotation), which feeds <see cref="RatioMaster.Services.MobileInsets"/>.
/// </summary>
[Activity(
    Label = "RatioMaster.NET",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Read the REAL per-orientation safe-area insets and forward them to the shared view (MobileInsets)
        // WITHOUT consuming them (the OS keeps its own positioning). AndroidX WindowInsetsCompat works back
        // to API 21 — the platform WindowInsets.GetInsets(type) is API 30+, which would leave Android 7-10
        // (our minSdk 24) with no safe area at all. The listener also re-fires on any inset change, so a
        // no-recreate rotation still refreshes the safe area.
        if (Window?.DecorView is { } decor)
        {
            AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(decor, new SafeAreaInsetsListener());
        }

        // Wire the foreground service to the shared session counter, so a running session survives the app
        // being backgrounded instead of being throttled and killed.
        ForegroundSessionBridge.Attach(this);

        // POST_NOTIFICATIONS is a runtime permission from Android 13. Ask once, up front rather than at the
        // moment a session starts, so the dialog never interrupts a Start. Denial is not fatal: the
        // foreground service still runs, the user just doesn't get its notification.
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], 1);
        }
    }

    // Single-view hosts raise no shutdown/closing event, so persist the session when Android backgrounds
    // the activity (the last reliable point before the process may be killed). Best-effort.
    protected override void OnPause()
    {
        base.OnPause();
        try
        {
            RatioMaster.App.PersistSession?.Invoke();
        }
        catch
        {
            // best-effort persistence
        }
    }
}

/// <summary>Reads the live safe-area insets via AndroidX <c>WindowInsetsCompat</c> (all API levels) and
/// forwards them to the shared view WITHOUT consuming them. Two sets: system-bars ∪ cutout (status + nav +
/// notch, per-edge union) and the mandatory home-gesture zone (see <see cref="RatioMaster.Services.MobileInsets"/>).</summary>
internal sealed class SafeAreaInsetsListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
{
    // Signature matches the AndroidX interface's nullable annotations (both params nullable) — the framework
    // only ever passes non-null here, but declaring them nullable silences the override-nullability warnings.
    public AndroidX.Core.View.WindowInsetsCompat? OnApplyWindowInsets(Android.Views.View? v, AndroidX.Core.View.WindowInsetsCompat? insets)
    {
        if (insets is null)
        {
            return insets;
        }

        int bars = AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout();
        AndroidX.Core.Graphics.Insets i = insets.GetInsets(bars)!;
        RatioMaster.Services.MobileInsets.Physical = new Avalonia.Thickness(i.Left, i.Top, i.Right, i.Bottom);
        AndroidX.Core.Graphics.Insets g = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemGestures())!;
        RatioMaster.Services.MobileInsets.GesturesPhysical = new Avalonia.Thickness(g.Left, g.Top, g.Right, g.Bottom);
        AndroidX.Core.Graphics.Insets m = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.MandatorySystemGestures())!;
        RatioMaster.Services.MobileInsets.MandatoryPhysical = new Avalonia.Thickness(m.Left, m.Top, m.Right, m.Bottom);
        return insets; // pass through unconsumed — leave the OS's own positioning intact
    }
}
#endif
