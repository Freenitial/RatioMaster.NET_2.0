// Android-only: keeps emulation sessions alive while the app is in the background. Compiled only for the
// net11.0-android target (the ANDROID symbol), so this file is empty on every desktop build.
#if ANDROID
using System;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using RatioMaster.Services;

namespace RatioMaster;

/// <summary>
/// Foreground service with a persistent notification — the Android counterpart of minimising to the
/// Windows tray. Without it, a backgrounded activity is a cached process: Doze and App Standby throttle
/// its network and timers, and the system reclaims it under memory pressure, so a running session simply
/// dies with no indication. A foreground service is the only supported way to keep working.
///
/// <para>The service type is <c>specialUse</c> rather than the more obvious <c>dataSync</c>: since
/// Android 15, dataSync foreground services are capped at roughly 6 hours per day and then force-stopped,
/// which is useless for a tool meant to seed for hours or days. specialUse has no such cap. (It does need
/// a justification if the app is ever submitted to Google Play; that is not a concern for a sideloaded
/// APK, and this app would not be Play-eligible anyway.)</para>
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeSpecialUse)]
internal sealed class RatioForegroundService : Service
{
    internal const string ChannelId = "ratiomaster.session";
    private const int NotificationId = 1;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartForeground(NotificationId, BuildNotification());

        // Sticky: if Android does reclaim us under extreme pressure, it recreates the service. The
        // sessions themselves are restored from the persisted session file when the app comes back.
        return StartCommandResult.Sticky;
    }

    internal static void EnsureChannel(Context context)
    {
        // Channels are API 26+. Below that, a notification simply carries its own settings.
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        NotificationChannel channel = new(
            ChannelId,
            "Running sessions",
            NotificationImportance.Low) // Low: persistent but silent — no sound or heads-up for a status line
        {
            Description = "Shown while RatioMaster is emulating a torrent client in the background.",
        };

        channel.SetShowBadge(false);
        (context.GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        // Tapping the notification returns to the app rather than launching a second task.
        Intent open = new(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        PendingIntent? tap = PendingIntent.GetActivity(
            this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        // Called as statements rather than chained: every NotificationCompat.Builder setter is bound as
        // returning a NULLABLE builder, so a fluent chain trips the nullable analyser on each link. The
        // setters mutate in place, so this is equivalent.
        NotificationCompat.Builder builder = new(this, ChannelId);
        builder.SetContentTitle("RatioMaster.NET");
        builder.SetContentText("Session running — announcing to the tracker.");
        builder.SetSmallIcon(Resource.Mipmap.icon);
        builder.SetContentIntent(tap);
        builder.SetOngoing(true); // not swipe-dismissible while the session runs
        builder.SetShowWhen(false);
        builder.SetPriority((int)NotificationPriority.Low);

        // Bound as nullable, but Build() cannot return null for a builder with a channel and icon set —
        // and StartForeground requires a real notification, so there is no useful fallback anyway.
        return builder.Build()!;
    }
}

/// <summary>Starts/stops <see cref="RatioForegroundService"/> from the shared <see cref="SessionActivity"/>
/// edges, so the ViewModel layer stays platform-agnostic.</summary>
internal static class ForegroundSessionBridge
{
    private static Context? app;

    internal static void Attach(Context context)
    {
        app = context.ApplicationContext ?? context;
        RatioForegroundService.EnsureChannel(app);
        SessionActivity.ActiveChanged += OnActiveChanged;
    }

    private static void OnActiveChanged(bool active)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            Intent intent = new(app, typeof(RatioForegroundService));
            if (active)
            {
                // API 26+ requires StartForegroundService when the app may already be backgrounded;
                // the service then has a few seconds to call StartForeground, which it does immediately.
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    app.StartForegroundService(intent);
                }
                else
                {
                    app.StartService(intent);
                }
            }
            else
            {
                app.StopService(intent);
            }
        }
        catch (Exception)
        {
            // Never let a service-management failure break starting or stopping a session: the session is
            // still valid in the foreground, it just loses its background guarantee.
        }
    }
}
#endif
