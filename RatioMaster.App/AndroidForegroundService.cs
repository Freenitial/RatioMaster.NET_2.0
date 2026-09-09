// Android foreground-service integration, compiled only for the Android target.
#if ANDROID
using System;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using RatioMaster.Services;

namespace RatioMaster;

/// <summary>
/// Provides foreground-service status and an ongoing notification while sessions are active, including
/// paused sessions. Android or the user can still stop the process. This service acquires no wake lock
/// and does not exempt session work from device sleep or network restrictions.
/// </summary>
[Service(
    Exported = false,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeSpecialUse)]
internal sealed class RatioForegroundService : Service
{
    internal const string ChannelId = "ratiomaster.session";
    private const int NotificationId = 1;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureChannel(this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            // Complete a pending foreground-start request even if its session has already stopped.
            StartForeground(NotificationId, BuildNotification());
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("RatioMaster", $"Unable to enter foreground-service mode: {exception}");
            StopSessionService(startId);
            return StartCommandResult.NotSticky;
        }

        if (!SessionActivity.IsActive)
        {
            StopSessionService(startId);
        }

        return StartCommandResult.NotSticky;
    }

    private void StopSessionService(int startId)
    {
        StopForeground(StopForegroundFlags.Remove);
        StopSelf(startId);
    }

    public override void OnDestroy()
    {
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
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
            "Active sessions",
            NotificationImportance.Low)
        {
            Description = "Shown while RatioMaster sessions are running or paused.",
        };

        channel.SetShowBadge(false);
        (context.GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        // Reuse the activity in its existing task when the notification is opened.
        Intent open = new(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        PendingIntent? tap = PendingIntent.GetActivity(
            this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        // Builder setters mutate in place and have nullable return annotations.
        NotificationCompat.Builder builder = new(this, ChannelId);
        builder.SetContentTitle("RatioMaster.NET");
        builder.SetContentText("Sessions active (running or paused). Tap to open.");
        builder.SetSmallIcon(Resource.Mipmap.icon);
        builder.SetContentIntent(tap);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetShowWhen(false);
        builder.SetPriority((int)NotificationPriority.Low);

        // StartForeground requires a notification with a channel and a small icon.
        return builder.Build()!;
    }
}

/// <summary>Starts/stops <see cref="RatioForegroundService"/> from the shared <see cref="SessionActivity"/>
/// edges, so the ViewModel layer stays platform-agnostic.</summary>
internal static class ForegroundSessionBridge
{
    private static readonly object Sync = new();
    private static Context? app;

    internal static void Attach(Context context)
    {
        lock (Sync)
        {
            if (app is null)
            {
                app = context.ApplicationContext ?? Android.App.Application.Context;
                SessionActivity.ActiveChanged += OnActiveChanged;
            }

            SynchronizeService();
        }
    }

    private static void OnActiveChanged(bool _)
    {
        lock (Sync)
        {
            SynchronizeService();
        }
    }

    // Called under Sync: serialize service requests and read current state instead of a stale event value.
    private static void SynchronizeService()
    {
        if (app is null)
        {
            return;
        }

        try
        {
            Intent intent = new(app, typeof(RatioForegroundService));
            if (SessionActivity.IsActive)
            {
                // Android can reject a foreground-service start when background-start restrictions apply.
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
        catch (Exception exception)
        {
            // Service-management failures must not interrupt the session's own start or stop operation.
            Android.Util.Log.Warn("RatioMaster", $"Unable to synchronize the session service: {exception}");
        }
    }
}
#endif
