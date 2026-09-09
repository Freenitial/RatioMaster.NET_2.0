#if DEBUG && !ANDROID
namespace RatioMaster;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RatioMaster.ViewModels;
using RatioMaster.Views;

/// <summary>Checks the terminal's visible rails and the larger native thumb input regions.</summary>
internal static class TerminalScrollBarSelfTest
{
    internal static void Run(Window window, RatioTabView view, RatioTabViewModel vm, Action<bool, string> check)
    {
        double savedWidth = window.Width;
        double savedHeight = window.Height;
        string savedText = vm.LogText;
        string savedFilter = vm.LogFilter;
        bool savedFollow = vm.FollowLog;
        TextBox log = view.FindControl<TextBox>("LogBox")!;
        int savedSelectionStart = log.SelectionStart;
        int savedSelectionEnd = log.SelectionEnd;
        ScrollViewer scroll = log.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Vector savedOffset = scroll.Offset;
        try
        {
            vm.FollowLog = false;
            vm.LogFilter = string.Empty;
            vm.LogText = string.Join("\n", Enumerable.Range(0, 700).Select(index => $"{index:D4} " + new string('x', 240)));
            foreach (double width in new[] { 1000.0, 660.0, 360.0 })
            {
                window.Width = width;
                window.Height = 740;
                Pump(window);
                view.FindControl<Border>("LogBorder")!.BringIntoView();
                Pump(window);
                log.SelectionStart = 4;
                log.SelectionEnd = 12;
                Pump(window);
                scroll.Offset = new Vector((scroll.Extent.Width - scroll.Viewport.Width) / 3,
                    (scroll.Extent.Height - scroll.Viewport.Height) / 3);
                Pump(window);

                ScrollContentPresenter presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
                ScrollBar vertical = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
                ScrollBar horizontal = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Horizontal);
                Rect viewport = RelativeBounds(presenter, scroll);
                Rect verticalBounds = RelativeBounds(vertical, scroll);
                Rect horizontalBounds = RelativeBounds(horizontal, scroll);
                log.Focus();
                Pump(window);
                Border terminalFrame = view.FindControl<Border>("LogBorder")!;
                Border textFrame = log.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_BorderElement");
                Panel corner = scroll.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "PART_ScrollBarsSeparator");
                Rect cornerBounds = RelativeBounds(corner, terminalFrame);
                Console.WriteLine($"[CORNER] focused={log.IsFocused}, innerBorder={textFrame.BorderThickness}, cornerBrush={corner.Background}, hit={corner.IsHitTestVisible}, corner={cornerBounds}, outer={terminalFrame.Bounds.Size}, frameBrush={terminalFrame.BorderBrush}");
                check(log.IsFocused && textFrame.BorderThickness == default
                    && corner.Background is ISolidColorBrush { Color.A: 0 } && !corner.IsHitTestVisible
                    && cornerBounds.Right <= terminalFrame.Bounds.Width - 1 + 0.01
                    && cornerBounds.Bottom <= terminalFrame.Bounds.Height - 1 + 0.01
                    && terminalFrame.BorderBrush is ISolidColorBrush focusBrush
                    && focusBrush.Color == ((ISolidColorBrush)view.FindResource("InputBorderFocusBrush")!).Color,
                    $"focused terminal keeps its scrollbar corner inside one uninterrupted outer frame at width {width}");
                check(!scroll.AllowAutoHide && presenter.ClipToBounds && log.ClipToBounds
                    && vertical.IsVisible && horizontal.IsVisible
                    && Near(vertical.Bounds.Width, 14) && Near(horizontal.Bounds.Height, 14)
                    && viewport.Right <= verticalBounds.Left + 0.01
                    && viewport.Bottom <= horizontalBounds.Top + 0.01,
                    $"terminal reserves both 14-unit input lanes without covering text at width {width}");

                foreach (ScrollBar bar in new[] { vertical, horizontal })
                {
                    bool isVertical = bar.Orientation == Orientation.Vertical;
                    Thumb thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
                    Border visual = thumb.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ThumbVisual");
                    check(Near(isVertical ? visual.Bounds.Width : visual.Bounds.Height, 3)
                        && Near(isVertical ? thumb.Bounds.Width : thumb.Bounds.Height, 14)
                        && visual.RenderTransform is null && thumb.RenderTransform is null,
                        $"{bar.Orientation} terminal rail stays 3 units inside its 14-unit thumb at width {width}");

                    Vector beforeHover = scroll.Offset;
                    Point grabPoint = thumb.TranslatePoint(isVertical
                        ? new Point(1, thumb.Bounds.Height / 2)
                        : new Point(thumb.Bounds.Width / 2, 1), window)!.Value;
                    window.MouseMove(grabPoint);
                    Pump(window);
                    IInputElement? hit = window.InputHitTest(grabPoint);
                    bool hitsThumb = ReferenceEquals(hit, thumb)
                        || hit is Visual hitVisual && hitVisual.GetVisualAncestors().Contains(thumb);
                    check(hitsThumb && thumb.IsPointerOver && scroll.Offset == beforeHover
                        && Near(isVertical ? visual.Bounds.Width : visual.Bounds.Height, 3),
                        $"{bar.Orientation} invisible thumb margin receives hover without expanding or scrolling at width {width}");

                    window.MouseDown(grabPoint, MouseButton.Left);
                    Pump(window);
                    check(Near(isVertical ? visual.Bounds.Width : visual.Bounds.Height, 3),
                        $"{bar.Orientation} pressed terminal rail remains thin at width {width}");
                    window.MouseMove(grabPoint + (isVertical ? new Vector(0, 12) : new Vector(12, 0)));
                    Pump(window);
                    window.MouseUp(grabPoint + (isVertical ? new Vector(0, 12) : new Vector(12, 0)), MouseButton.Left);
                    Pump(window);
                    check((isVertical ? scroll.Offset.Y > beforeHover.Y + 1 : scroll.Offset.X > beforeHover.X + 1)
                        && Near(isVertical ? scroll.Offset.X : scroll.Offset.Y, isVertical ? beforeHover.X : beforeHover.Y)
                        && log.SelectionStart == 4 && log.SelectionEnd == 12
                        && Near(isVertical ? visual.Bounds.Width : visual.Bounds.Height, 3),
                        $"{bar.Orientation} drag from invisible margin moves only its axis and preserves selection at width {width}");
                }
            }
        }
        finally
        {
            window.MouseMove(new Point(0, 0));
            vm.LogText = savedText;
            vm.LogFilter = savedFilter;
            vm.FollowLog = savedFollow;
            window.Width = savedWidth;
            window.Height = savedHeight;
            Pump(window);
            log.SelectionStart = savedSelectionStart;
            log.SelectionEnd = savedSelectionEnd;
            scroll.Offset = savedOffset;
            Pump(window);
        }
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) < 0.01;

    private static Rect RelativeBounds(Control control, Visual ancestor) =>
        new(control.TranslatePoint(default, ancestor)!.Value, control.Bounds.Size);

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
#endif
