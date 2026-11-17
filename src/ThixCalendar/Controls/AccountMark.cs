using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Controls;

/// <summary>
/// The sign of an account kind, drawn (no foreign logo files): Microsoft's four squares, Google's four-coloured ring,
/// an orange feed sign for ICS subscriptions, a house for calendars on this computer.
/// </summary>
public sealed class AccountMark : Control
{
    public static readonly StyledProperty<AccountKind> KindProperty = AvaloniaProperty.Register<AccountMark, AccountKind>(nameof(Kind));

    static AccountMark()
    {
        AffectsRender<AccountMark>(KindProperty);
        WidthProperty.OverrideDefaultValue<AccountMark>(18);
        HeightProperty.OverrideDefaultValue<AccountMark>(18);
        VerticalAlignmentProperty.OverrideDefaultValue<AccountMark>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public AccountKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var scale = side / 24.0;
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - side) / 2, (Bounds.Height - side) / 2));
        switch (Kind)
        {
            case AccountKind.Microsoft:
                context.FillRectangle(new SolidColorBrush(Color.Parse("#F25022")), new Rect(2, 2, 9.5, 9.5));
                context.FillRectangle(new SolidColorBrush(Color.Parse("#7FBA00")), new Rect(12.5, 2, 9.5, 9.5));
                context.FillRectangle(new SolidColorBrush(Color.Parse("#00A4EF")), new Rect(2, 12.5, 9.5, 9.5));
                context.FillRectangle(new SolidColorBrush(Color.Parse("#FFB900")), new Rect(12.5, 12.5, 9.5, 9.5));
                break;
            case AccountKind.Google:
                DrawGoogle(context);
                break;
            case AccountKind.Ics:
                DrawIcon(context, "rss", "#F97316");
                break;
            default:
                DrawIcon(context, "home", "#16A34A");
                break;
        }
    }

    /// <summary>A ring in Google's four colours with the bar of the "G".</summary>
    private static void DrawGoogle(DrawingContext context)
    {
        var center = new Point(12, 12);
        const double radius = 8;
        void Arc(double fromDegrees, double toDegrees, string color)
        {
            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(PointAt(center, radius, fromDegrees), false);
                stream.ArcTo(PointAt(center, radius, toDegrees), new Size(radius, radius), 0, toDegrees - fromDegrees > 180, SweepDirection.Clockwise);
                stream.EndFigure(false);
            }

            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse(color)), 3.6, lineCap: PenLineCap.Flat), geometry);
        }

        Arc(-40, 45, "#4285F4");
        Arc(45, 135, "#34A853");
        Arc(135, 210, "#FBBC05");
        Arc(210, 320, "#EA4335");
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#4285F4")), 3.4), new Point(12, 12), new Point(20.2, 12));
    }

    private static Point PointAt(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static void DrawIcon(DrawingContext context, string key, string color)
    {
        if (Application.Current?.TryGetResource(key, null, out var value) == true && value is Geometry geometry)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse(color)), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
