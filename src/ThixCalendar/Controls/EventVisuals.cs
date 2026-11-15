using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar.Controls;

/// <summary>
/// The pictures of an event, the same everywhere (one code base): the block of the time grid (tinted card with the
/// calendar's colour bar, as in the mockup), the chip of the month and all-day rows, and the tool tip with the details.
/// Declined or tentative events are paler, cancelled ones struck through.
/// </summary>
internal static class EventVisuals
{
    /// <summary>A block in the time grid: title and time, shortened to one line when the block is low.</summary>
    public static Border Block(EventItem item, double heightHint, Action<EventItem> open)
    {
        var calendarEvent = item.Event;
        var compact = heightHint < 38;
        var title = new TextBlock
        {
            Text = calendarEvent.Title,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = compact ? 1 : Math.Max(1, (int)((heightHint - 22) / 16)),
            TextDecorations = calendarEvent.IsCancelled ? TextDecorations.Strikethrough : null,
        };
        var content = new StackPanel { Spacing = 1 };
        if (compact)
        {
            title.Text = $"{calendarEvent.Title}, {calendarEvent.StartLocal:HH:mm}";
            content.Children.Add(title);
        }
        else
        {
            content.Children.Add(title);
            // The time, with a camera when the event is an online meeting.
            var time = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            time.Children.Add(new TextBlock { Text = Ui.TimeRange(calendarEvent), FontSize = 11.5, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (calendarEvent.MeetingUrl.Length > 0)
            {
                time.Children.Add(new IconView { Data = Ui.Icon("Icon.Video"), Width = 13, Height = 13, StrokeWidth = 2.2, Foreground = Ui.SolidBrush(item.Calendar.Color) });
            }

            content.Children.Add(time);
            if (heightHint > 70 && calendarEvent.Location.Length > 0)
            {
                content.Children.Add(new TextBlock { Text = calendarEvent.Location, FontSize = 11.5, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            }
        }

        var block = new Border
        {
            Background = Ui.Tint(item.Calendar.Color),
            BorderBrush = Ui.SolidBrush(item.Calendar.Color),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = compact ? new Thickness(7, 1, 4, 1) : new Thickness(8, 4, 6, 4),
            Child = content,
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = Paleness(calendarEvent),
        };
        Attach(block, item, open);
        return block;
    }

    /// <summary>A one-line chip (month view, all-day row): all-day events filled, timed events with a dot and their time.</summary>
    public static Border Chip(EventItem item, Action<EventItem> open)
    {
        var calendarEvent = item.Event;
        Control content;
        Border chip;
        if (calendarEvent.IsAllDay)
        {
            content = new TextBlock
            {
                Text = calendarEvent.Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextDecorations = calendarEvent.IsCancelled ? TextDecorations.Strikethrough : null,
            };
            chip = new Border
            {
                Background = Ui.Tint(item.Calendar.Color, Ui.IsDark ? 0.4 : 0.24),
                BorderBrush = Ui.SolidBrush(item.Calendar.Color),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2),
                Child = content,
            };
        }
        else
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = Ui.SolidBrush(item.Calendar.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var time = new TextBlock { Text = calendarEvent.StartLocal.ToString("HH:mm"), FontSize = 12, Foreground = Ui.Brush("TextMutedBrush"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = calendarEvent.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(time, Dock.Left);
            content = new DockPanel { Children = { dot, time, title } };
            chip = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(5), Padding = new Thickness(4, 2), Child = content };
        }

        chip.Cursor = new Cursor(StandardCursorType.Hand);
        chip.Opacity = Paleness(calendarEvent);
        Attach(chip, item, open);
        return chip;
    }

    /// <summary>The details shown on hover.</summary>
    public static Control ToolTip(EventItem item)
    {
        var calendarEvent = item.Event;
        var lines = new StackPanel { Spacing = 4, MaxWidth = 340 };
        lines.Children.Add(new TextBlock { Text = calendarEvent.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        lines.Children.Add(new TextBlock { Text = $"{Ui.DayName(calendarEvent.StartLocal)} · {Ui.TimeRange(calendarEvent)}", FontSize = 12.5 });
        if (calendarEvent.Location.Length > 0)
        {
            lines.Children.Add(new TextBlock { Text = calendarEvent.Location, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        }

        if (calendarEvent.MeetingUrl.Length > 0)
        {
            lines.Children.Add(new TextBlock { Text = $"Online: {MeetingLinks.ServiceName(calendarEvent.MeetingUrl)}", FontSize = 12.5 });
        }

        var status = calendarEvent.IsDeclined ? " · abgelehnt" : calendarEvent.Response == ResponseStatus.Tentative ? " · mit Vorbehalt" : calendarEvent.Response == ResponseStatus.NotResponded ? " · nicht beantwortet" : "";
        lines.Children.Add(new TextBlock { Text = $"{item.Calendar.Name} · {item.Account.Name}{status}", FontSize = 12, Opacity = 0.7 });
        return lines;
    }

    private static double Paleness(CalendarEvent calendarEvent) =>
        calendarEvent.IsDeclined || calendarEvent.IsCancelled ? 0.5 : calendarEvent.Response is ResponseStatus.Tentative or ResponseStatus.NotResponded || calendarEvent.Status == EventStatus.Tentative ? 0.8 : 1;

    private static void Attach(Control control, EventItem item, Action<EventItem> open)
    {
        Avalonia.Controls.ToolTip.SetTip(control, ToolTip(item));
        Avalonia.Controls.ToolTip.SetShowDelay(control, 450);
        Avalonia.Automation.AutomationProperties.SetName(control, $"{item.Event.Title}, {Ui.TimeRange(item.Event)}");
        control.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                open(item);
            }
        };
    }
}
