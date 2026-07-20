namespace RatioMaster.Controls;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

/// <summary>
/// Minimal live area/line chart. Feed it a rolling <see cref="Values"/> array (e.g. upload
/// bytes/s) and it draws a cyan filled line auto-scaled to its own max. AOT-safe (custom Render).
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<double[]?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, double[]?>(nameof(Values));

    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush>(nameof(Stroke), new SolidColorBrush(Color.FromRgb(0x00, 0xB4, 0xDC)));

    public static readonly StyledProperty<IBrush> FillProperty =
        AvaloniaProperty.Register<Sparkline, IBrush>(nameof(Fill), new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x96, 0xC8)));

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, FillProperty);

    public double[]? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double[]? values = Values;
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (values == null || values.Length < 2 || w <= 1 || h <= 1)
        {
            return;
        }

        double max = 1;
        foreach (double v in values)
        {
            if (v > max)
            {
                max = v;
            }
        }

        double stepX = w / (values.Length - 1);
        double pad = 2;

        double Y(double v) => h - pad - (v / max) * (h - 2 * pad);

        StreamGeometry line = new();
        StreamGeometry area = new();
        using (StreamGeometryContext lc = line.Open())
        using (StreamGeometryContext ac = area.Open())
        {
            Point first = new(0, Y(values[0]));
            lc.BeginFigure(first, false);
            ac.BeginFigure(new Point(0, h), true);
            ac.LineTo(first);
            for (int i = 1; i < values.Length; i++)
            {
                Point p = new(i * stepX, Y(values[i]));
                lc.LineTo(p);
                ac.LineTo(p);
            }

            ac.LineTo(new Point(w, h));
            ac.EndFigure(true);
            lc.EndFigure(false);
        }

        context.DrawGeometry(Fill, null, area);
        context.DrawGeometry(null, new Pen(Stroke, 1.5), line);
    }
}
