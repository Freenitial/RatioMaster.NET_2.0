// Android entry point, compiled only for the Android target.
#if ANDROID
using System;
using System.Runtime.Versioning;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Window;
using Avalonia;
using Avalonia.Android;
using RatioMaster.Services;

namespace RatioMaster;

/// <summary>
/// Initializes Avalonia's Android application and shared single-view lifetime.
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
/// Hosts the shared view and handles configuration changes without recreating it.
/// The AndroidX insets listener forwards safe-area changes to <see cref="MobileInsets"/>.
/// </summary>
[Activity(
    Label = "RatioMaster.NET",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/icon",
    MainLauncher = true,
    EnableOnBackInvokedCallback = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int NotificationPermissionRequestCode = 1;
    private const string PermissionPreferences = "ratiomaster.permissions";
    private const string NotificationPermissionAsked = "post_notifications_asked";
    private BackInvokedCallback? backCallback;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Forward inset changes, including rotation, without consuming the system's insets.
        if (Window?.DecorView is { } decor)
        {
            AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(decor, new SafeAreaInsetsListener());
        }

        ForegroundSessionBridge.Attach(this);
        RequestNotificationPermissionOnce();
    }

    protected override void OnResume()
    {
        base.OnResume();

        if (OperatingSystem.IsAndroidVersionAtLeast(33) && backCallback is null)
        {
            // Avalonia registers its callback in OnStart; register afterwards at the same priority.
            backCallback = new BackInvokedCallback(this);
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
                IOnBackInvokedDispatcher.PriorityDefault, backCallback);
        }
    }

    [ObsoletedOSPlatform("android33.0")]
    public override void OnBackPressed() => HandleBack();

    private void HandleBack()
    {
        if (RatioMaster.App.TryDismissDialog?.Invoke() == true)
        {
            return;
        }

        if (SessionActivity.IsActive)
        {
            MoveTaskToBack(true);
        }
        else
        {
            Finish();
        }
    }

    private void UnregisterBackCallback()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && backCallback is { } callback)
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(callback);
            backCallback = null;
            callback.Dispose();
        }
    }

    private void RequestNotificationPermissionOnce()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return;
        }

        using ISharedPreferences preferences = GetSharedPreferences(PermissionPreferences, FileCreationMode.Private)!;
        if (preferences.GetBoolean(NotificationPermissionAsked, false))
        {
            return;
        }

        // Apply updates memory immediately and schedules persistence before the activity is stopped.
        // Record the attempt before opening the system dialog, including cancellation or recreation.
        MarkNotificationPermissionAsked();

        // Notification permission does not gate starting a foreground service.
        if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.PostNotifications], NotificationPermissionRequestCode);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == NotificationPermissionRequestCode)
        {
            // A denial or an empty cancellation result must not trigger another automatic request.
            MarkNotificationPermissionAsked();
        }
    }

    private void MarkNotificationPermissionAsked()
    {
        using ISharedPreferences preferences = GetSharedPreferences(PermissionPreferences, FileCreationMode.Private)!;
        using ISharedPreferencesEditor editor = preferences.Edit()!;
        editor.PutBoolean(NotificationPermissionAsked, true);
        editor.Apply();
    }

    // Single-view hosts have no closing event; persist when the activity loses the foreground.
    protected override void OnPause()
    {
        UnregisterBackCallback();
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

    protected override void OnDestroy()
    {
        UnregisterBackCallback();
        base.OnDestroy();
    }

    [SupportedOSPlatform("android33.0")]
    private sealed class BackInvokedCallback(MainActivity activity) : Java.Lang.Object, IOnBackInvokedCallback
    {
        public void OnBackInvoked() => activity.HandleBack();
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
