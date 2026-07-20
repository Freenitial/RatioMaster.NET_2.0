namespace RatioMaster.Services;

using System;
using System.Threading;

/// <summary>
/// Tracks how many tabs are currently running, and raises <see cref="ActiveChanged"/> on the 0↔1 edges.
///
/// <para>Desktop ignores this: the process stays fully alive once minimised to the tray. Android does not —
/// a backgrounded activity becomes a cached process, so Doze and App Standby throttle its network and
/// timers and the system may kill it outright. The Android head therefore uses these edges to start and
/// stop a foreground service, which is the only supported way to keep announcing while backgrounded.</para>
///
/// <para>Counting rather than a bool matters: with several tabs running, the service must survive one of
/// them stopping and only shut down when the LAST one does.</para>
/// </summary>
internal static class SessionActivity
{
    private static int running;

    /// <summary>Raised with true when the first session starts, and false when the last one stops.</summary>
    internal static event Action<bool>? ActiveChanged;

    /// <summary>True while at least one tab is running.</summary>
    internal static bool IsActive => Volatile.Read(ref running) > 0;

    internal static void Entered()
    {
        if (Interlocked.Increment(ref running) == 1)
        {
            ActiveChanged?.Invoke(true);
        }
    }

    internal static void Exited()
    {
        // Clamp at zero: an unbalanced Exited (a stop path that runs twice) must not drive the count
        // negative, which would then swallow the next Entered and leave the service unstarted.
        int now = Interlocked.Decrement(ref running);
        if (now < 0)
        {
            Interlocked.Exchange(ref running, 0);
            return;
        }

        if (now == 0)
        {
            ActiveChanged?.Invoke(false);
        }
    }
}
