using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Controls;

/// <summary>
/// The day / week view of the mockup: day headers (today as a blue pill), a row of all-day events, and the scrolling
/// grid of hours with the events placed side by side where they overlap and a red line at "now". A click on an event
/// opens it, a double click on free time starts a new event there (rounded to the half hour). Several instances on a
/// page work independently (no static state).
/// </summary>
public sealed class TimeGridView : UserControl
{
    private const double GutterWidth = 58;
    private const int MaxAllDayRows = 3;

    private readonly Grid _header = new();
    private readonly Grid _allDay = new();
    private readonly Grid _body = new();
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DispatcherTimer _nowTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly List<(DayColumnPanel Panel, DateTime Day)> _columns = [];
    private Control? _nowLine;
    private bool _scrolledOnce;

    public TimeGridView()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(_allDay, 1);
        _scroller.Content = _body;
        Grid.SetRow(_scroller, 2);
        root.Children.Add(_header);
        root.Children.Add(_allDay);
        root.Children.Add(_scroller);
        Content = root;
        _nowTimer.Tick += (_, _) => PlaceNowLine();
        AttachedToVisualTree += (_, _) => _nowTimer.Start();
        DetachedFromVisualTree += (_, _) => _nowTimer.Stop();
    }

    /// <summary>A click on an event.</summary>
    public event EventHandler<EventItem>? EventOpened;

    /// <summary>A double click on free time (start of the new event) or on the all-day row (a date, all day).</summary>
    public event EventHandler<(DateTime Start, bool IsAllDay)>? NewEventRequested;

    /// <summary>A click on a day header: show that day alone.</summary>
    public event EventHandler<DateTime>? DayChosen;

    public double HourHeight { get; set; } = 48;

    public int DayStartHour { get; set; } = 7;

    public DateTime FirstDay { get; private set; } = DateTime.Today;

    public int DayCount { get; private set; } = 7;

    /// <summary>Shows <paramref name="dayCount"/> days from <paramref name="firstDay"/> with these events.</summary>
    public void Show(DateTime firstDay, int dayCount, IReadOnlyList<EventItem> items)
    {
        FirstDay = firstDay.Date;
        DayCount = Math.Max(1, dayCount);
        BuildColumns(_header);
        BuildColumns(_allDay);
        BuildColumns(_body);
        _header.Children.Clear();
        _allDay.Children.Clear();
        _body.Children.Clear();
        _columns.Clear();

        BuildHeader();
        BuildAllDayRow(items);
        BuildBody(items);
        PlaceNowLine();

        if (!_scrolledOnce)
        {
            _scrolledOnce = true;
            Dispatcher.UIThread.Post(() => _scroller.Offset = new Vector(0, Math.Max(0, DayStartHour * HourHeight - 12)), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Scrolls so that the hour is at the top (e.g. "Heute" scrolls to the current time).</summary>
    public void ScrollToHour(double hour) =>
        Dispatcher.UIThread.Post(() => _scroller.Offset = new Vector(0, Math.Max(0, Math.Max(0, hour) * HourHeight - 12)), DispatcherPriority.Loaded);

    private void BuildColumns(Grid grid)
    {
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GutterWidth, GridUnitType.Pixel));
        for (var day = 0; day < DayCount; day++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }
    }

    private void BuildHeader()
    {
        for (var index = 0; index < DayCount; index++)
        {
            var day = FirstDay.AddDays(index);
            var isToday = day == DateTime.Today;
            var label = new TextBlock
            {
                Text = day.ToString("ddd d.", Ui.German),
                FontSize = 14.5,
                FontWeight = isToday ? FontWeight.SemiBold : FontWeight.Medium,
                Foreground = isToday ? Ui.Brush("OnAccentBrush") : day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Ui.Brush("TextMutedBrush") : Ui.Brush("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var pill = new Border
            {
                Background = isToday ? Ui.Brush("AccentBrush") : Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = label,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(pill, Ui.LongDate(day));
            pill.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(pill).Properties.IsLeftButtonPressed)
                {
                    DayChosen?.Invoke(this, day);
                }
            };
            var cell = new Border
            {
                BorderBrush = Ui.Brush("GridLineBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 6, 0, 8),
                Child = pill,
            };
            Grid.SetColumn(cell, index + 1);
            _header.Children.Add(cell);
        }

        var corner = new Border { BorderBrush = Ui.Brush("GridLineBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
        _header.Children.Add(corner);
    }

    private void BuildAllDayRow(IReadOnlyList<EventItem> items)
    {
        var allDay = items.Where(item => item.Event.IsAllDay).ToList();
        var label = new TextBlock { Text = "ganztägig", FontSize = 11, Foreground = Ui.Brush("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 8, 0) };
        var gutter = new Border { BorderBrush = Ui.Brush("GridLineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = label, MinHeight = 30 };
        _allDay.Children.Add(gutter);
        _allDay.IsVisible = true;
        for (var index = 0; index < DayCount; index++)
        {
            var day = FirstDay.AddDays(index);
            var stack = new StackPanel { Spacing = 2, Margin = new Thickness(3, 3, 3, 4) };
            var ofDay = allDay.Where(item => item.Event.Touches(day)).ToList();
            foreach (var item in ofDay.Take(MaxAllDayRows))
            {
                stack.Children.Add(EventVisuals.Chip(item, Open));
            }

            if (ofDay.Count > MaxAllDayRows)
            {
                var more = new Button { Theme = Ui.Theme("LinkButton"), Content = $"+{ofDay.Count - MaxAllDayRows} weitere", FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0) };
                more.Click += (_, _) => DayChosen?.Invoke(this, day);
                stack.Children.Add(more);
            }

            var cell = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Ui.Brush("GridLineBrush"),
                BorderThickness = new Thickness(1, 0, 0, 1),
                MinHeight = 30,
                Child = stack,
            };
            cell.DoubleTapped += (_, e) =>
            {
                if (e.Source == cell || e.Source == stack)
                {
                    NewEventRequested?.Invoke(this, (day, true));
                }
            };
            Grid.SetColumn(cell, index + 1);
            _allDay.Children.Add(cell);
        }
    }

    private void BuildBody(IReadOnlyList<EventItem> items)
    {
        var gutter = new Canvas { Height = HourHeight * 24 };
        for (var hour = 1; hour < 24; hour++)
        {
            var text = new TextBlock { Text = $"{hour:00}:00", FontSize = 12, Foreground = Ui.Brush("TextMutedBrush"), Width = GutterWidth - 12, TextAlignment = TextAlignment.Right };
            Canvas.SetTop(text, hour * HourHeight - 8);
            gutter.Children.Add(text);
        }

        _body.Children.Add(gutter);
        for (var index = 0; index < DayCount; index++)
        {
            var day = FirstDay.AddDays(index);
            var panel = new DayColumnPanel
            {
                HourHeight = HourHeight,
                LineBrush = Ui.Brush("GridLineBrush"),
                HalfLineBrush = Ui.Brush("GridHalfLineBrush"),
                Background = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Ui.Brush("WeekendBrush") : Brushes.Transparent,
            };
            foreach (var slot in EventLayout.Arrange(day, items))
            {
                var height = (slot.EndMinute - slot.StartMinute) / 60 * HourHeight;
                var block = EventVisuals.Block(slot.Item, height, Open);
                DayColumnPanel.Place(block, slot.StartMinute, slot.EndMinute, slot.Lane, slot.Lanes);
                panel.Children.Add(block);
            }

            panel.DoubleTapped += (_, e) =>
            {
                if (e.Source != panel)
                {
                    return;
                }

                var minute = Math.Floor(panel.MinuteAt(e.GetPosition(panel).Y) / 30) * 30;
                NewEventRequested?.Invoke(this, (day.AddMinutes(minute), false));
            };
            Grid.SetColumn(panel, index + 1);
            _body.Children.Add(panel);
            _columns.Add((panel, day));
        }
    }

    /// <summary>The red line of the current time with its dot, in today's column (if it is shown).</summary>
    private void PlaceNowLine()
    {
        if (_nowLine?.Parent is Panel old)
        {
            old.Children.Remove(_nowLine);
        }

        var today = _columns.FirstOrDefault(column => column.Day == DateTime.Today);
        if (today.Panel is null)
        {
            return;
        }

        var brush = Ui.Brush("NowBrush");
        var line = new Grid
        {
            Height = 10,
            IsHitTestVisible = false,
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, Fill = brush, Margin = new Thickness(-5, 0, 0, 0) },
                new Border { Height = 2, Background = brush, VerticalAlignment = VerticalAlignment.Center, [Grid.ColumnProperty] = 1 },
            },
        };
        DayColumnPanel.SetIsMarker(line, true);
        DayColumnPanel.Place(line, DateTime.Now.TimeOfDay.TotalMinutes, DateTime.Now.TimeOfDay.TotalMinutes);
        today.Panel.Children.Add(line);
        _nowLine = line;
    }

    private void Open(EventItem item) => EventOpened?.Invoke(this, item);
}
