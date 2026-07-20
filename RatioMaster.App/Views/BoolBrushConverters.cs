namespace RatioMaster.Views;

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RatioMaster.ViewModels;

/// <summary>Small state→brush converters for status dots and the alert line (AOT-safe, no reflection).</summary>
public static class BoolBrushConverters
{
    private static readonly IBrush Running = new SolidColorBrush(Color.FromRgb(0x50, 0xE6, 0x8C));
    private static readonly IBrush Idle = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5C));
    private static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x4A));
    private static readonly IBrush Error = new SolidColorBrush(Color.FromRgb(0xE8, 0x5C, 0x5C));

    public static readonly IValueConverter RunningDot =
        new FuncValueConverter<bool, IBrush>(running => running ? Running : Idle);

    /// <summary>Tab dot: grey idle, green running, yellow warning, red error.</summary>
    public static readonly IValueConverter TabDot =
        new FuncValueConverter<TabDotState, IBrush>(state => state switch
        {
            TabDotState.Error => Error,
            TabDotState.Warning => Warning,
            TabDotState.Running => Running,
            _ => Idle,
        });

    /// <summary>Alert-line text colour. <see cref="TabAlertLevel.None"/> never shows any text, so its
    /// brush is irrelevant — it reuses the idle grey.</summary>
    public static readonly IValueConverter AlertText =
        new FuncValueConverter<TabAlertLevel, IBrush>(level => level switch
        {
            TabAlertLevel.Error => Error,
            TabAlertLevel.Warning => Warning,
            TabAlertLevel.Ok => Running,
            _ => Idle,
        });
}
