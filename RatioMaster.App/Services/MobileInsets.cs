namespace RatioMaster.Services;

using System;
using Avalonia;

/// <summary>
/// Bridge for the platform safe-area insets (status bar + navigation bar + display cutout + the mandatory
/// home-gesture zone) from the Android head to the shared <see cref="RatioMaster.Views.MainView"/>, in
/// PHYSICAL pixels.
///
/// <para>We do NOT rely on Avalonia 12's <c>InsetsManager.SafeAreaPadding</c> on Android (it applies insets
/// inconsistently and mis-measures — it was double-stacking with our own padding, giving a big top gap).
/// Instead the Android activity installs an AndroidX <c>WindowInsetsCompat</c> listener (works back to
/// API 21, unlike the platform API-30 <c>GetInsets</c>), reads the REAL per-orientation insets, forwards
/// them here without consuming them, and the view lays the UI out inside the safe area itself (root sets
/// <c>TopLevel.AutoSafeAreaPadding=False</c>).</para>
///
/// <para>No-op on desktop (the values are never set; they stay the default zero).</para>
/// </summary>
public static class MobileInsets
{
    private static Thickness physical;
    private static Thickness gesturesPhysical;
    private static Thickness mandatoryPhysical;

    /// <summary>System-bar (status + navigation) ∪ display-cutout insets in PHYSICAL pixels (divide by
    /// RenderScaling for DIPs). Per-edge union: covers the nav bar wherever the OS puts it and the notch.</summary>
    public static Thickness Physical
    {
        get => physical;
        set
        {
            if (physical.Equals(value))
            {
                return;
            }

            physical = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Plain system-GESTURE insets (side back-swipe zones) in PHYSICAL pixels. Used only for the
    /// LANDSCAPE left/right where a side nav bar may be reported only as a gesture inset.</summary>
    public static Thickness GesturesPhysical
    {
        get => gesturesPhysical;
        set
        {
            if (gesturesPhysical.Equals(value))
            {
                return;
            }

            gesturesPhysical = value;
            Changed?.Invoke();
        }
    }

    /// <summary>MANDATORY system-gesture insets in PHYSICAL pixels — the home / nav-indicator zone an app
    /// must not draw interactive content under, EXCLUDING the plain back-swipe side edges. On a gesture-nav
    /// device the visible home strip can be taller than the thin navigationBars inset, so the view reserves
    /// the max of this and <see cref="Physical"/> on the bottom edge.</summary>
    public static Thickness MandatoryPhysical
    {
        get => mandatoryPhysical;
        set
        {
            if (mandatoryPhysical.Equals(value))
            {
                return;
            }

            mandatoryPhysical = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised whenever an inset value changes (e.g. rotation moves the nav bar / cutout).</summary>
    public static event Action? Changed;
}
