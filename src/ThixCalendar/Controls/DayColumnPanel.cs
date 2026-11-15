using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ThixCalendar.Controls;

/// <summary>
/// One day of the time grid: draws the hour lines (and half-hour lines, the weekend tint) and places its children – the
/// event blocks and the line of "now" – by minutes of the day (<see cref="StartMinuteProperty"/>) and lane.
/// </summary>
public sealed class DayColumnPanel : Panel
{
    public static readonly AttachedProperty<double> StartMinuteProperty = AvaloniaProperty.RegisterAttached<DayColumnPanel, Control, double>("StartMinute");

    public static readonly AttachedProperty<double> EndMinuteProperty = AvaloniaProperty.RegisterAttached<DayColumnPanel, Control, double>("EndMinute");

    public static readonly AttachedProperty<int> LaneProperty = AvaloniaProperty.RegisterAttached<DayColumnPanel, Control, int>("Lane");

    public static readonly AttachedProperty<int> LanesProperty = AvaloniaProperty.RegisterAttached<DayColumnPanel, Control, int>("Lanes", 1);

    /// <summary>A child that spans the full width and sits on its start minute (the line of "now").</summary>
    public static readonly AttachedProperty<bool> IsMarkerProperty = AvaloniaProperty.RegisterAttached<DayColumnPanel, Control, bool>("IsMarker");

    public static readonly StyledProperty<double> HourHeightProperty = AvaloniaProperty.Register<DayColumnPanel, double>(nameof(HourHeight), 48);

    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<DayColumnPanel, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> HalfLineBrushProperty = AvaloniaProperty.Register<DayColumnPanel, IBrush?>(nameof(HalfLineBrush));

    static DayColumnPanel()
    {
        AffectsParentArrange<DayColumnPanel>(StartMinuteProperty, EndMinuteProperty, LaneProperty, LanesProperty, IsMarkerProperty);
        AffectsMeasure<DayColumnPanel>(HourHeightProperty);
    }

    public DayColumnPanel()
    {
        // Panel.Render is sealed: the lines are a child of their own, always first (below the events) and full size.
        Children.Add(new HourLines(this));
    }

    public double HourHeight
    {
        get => GetValue(HourHeightProperty);
        set => SetValue(HourHeightProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? HalfLineBrush
    {
        get => GetValue(HalfLineBrushProperty);
        set => SetValue(HalfLineBrushProperty, value);
    }

    public static void Place(Control child, double startMinute, double endMinute, int lane = 0, int lanes = 1)
    {
        child.SetValue(StartMinuteProperty, startMinute);
        child.SetValue(EndMinuteProperty, endMinute);
        child.SetValue(LaneProperty, lane);
        child.SetValue(LanesProperty, Math.Max(1, lanes));
    }

    public static void SetIsMarker(Control child, bool value) => child.SetValue(IsMarkerProperty, value);

    /// <summary>The minute of the day under a point of the panel (for "new event here").</summary>
    public double MinuteAt(double y) => Math.Clamp(y / HourHeight * 60, 0, 24 * 60 - 1);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = HourHeight * 24;
        var width = double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width;
        foreach (var child in Children)
        {
            if (child is HourLines)
            {
                child.Measure(new Size(width, height));
                continue;
            }

            var (slot, _) = SlotOf(child, width);
            child.Measure(new Size(slot.Width, slot.Height));
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (child is HourLines)
            {
                child.Arrange(new Rect(finalSize));
                continue;
            }

            var (slot, isMarker) = SlotOf(child, finalSize.Width);
            child.Arrange(isMarker ? new Rect(slot.X, slot.Y - child.DesiredSize.Height / 2, slot.Width, child.DesiredSize.Height) : slot);
        }

        return finalSize;
    }

    private (Rect Slot, bool IsMarker) SlotOf(Control child, double width)
    {
        var start = child.GetValue(StartMinuteProperty);
        var y = start / 60 * HourHeight;
        if (child.GetValue(IsMarkerProperty))
        {
            return (new Rect(0, y, width, 0), true);
        }

        var end = Math.Max(child.GetValue(EndMinuteProperty), start + 15);
        var lanes = Math.Max(1, child.GetValue(LanesProperty));
        var lane = Math.Clamp(child.GetValue(LaneProperty), 0, lanes - 1);
        const double gap = 2;
        var usable = Math.Max(0, width - 6);
        var laneWidth = usable / lanes;
        var x = 3 + lane * laneWidth;
        return (new Rect(x, y + 1, Math.Max(0, laneWidth - gap), Math.Max(14, (end - start) / 60 * HourHeight - 2)), false);
    }

    /// <summary>The hour lines (solid) and half-hour lines (dashed) of a day column.</summary>
    private sealed class HourLines(DayColumnPanel owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var width = Bounds.Width;
            if (owner.HalfLineBrush is { } half)
            {
                var pen = new Pen(half, 1, new DashStyle([3, 3], 0));
                for (var hour = 0; hour < 24; hour++)
                {
                    var y = Math.Round(hour * owner.HourHeight + owner.HourHeight / 2) + 0.5;
                    context.DrawLine(pen, new Point(0, y), new Point(width, y));
                }
            }

            if (owner.LineBrush is { } line)
            {
                var pen = new Pen(line, 1);
                for (var hour = 1; hour < 24; hour++)
                {
                    var y = Math.Round(hour * owner.HourHeight) + 0.5;
                    context.DrawLine(pen, new Point(0, y), new Point(width, y));
                }

                context.DrawLine(pen, new Point(0.5, 0), new Point(0.5, Bounds.Height));
            }
        }
    }
}
