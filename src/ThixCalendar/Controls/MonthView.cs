using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Controls;

/// <summary>
/// The month: six weeks of seven days, each day with its events as chips (all-day ones filled, timed ones with a dot
/// and their time). The number of a day opens it in the day view; "+n weitere" too. A double click on free space starts
/// a new event on that day.
/// </summary>
public sealed class MonthView : UserControl
{
    private const int ChipsPerDay = 4;

    private readonly Grid _grid = new();

    public MonthView()
    {
        Content = _grid;
    }

    public event EventHandler<EventItem>? EventOpened;

    public event EventHandler<(DateTime Start, bool IsAllDay)>? NewEventRequested;

    public event EventHandler<DateTime>? DayChosen;

    /// <summary>The first day shown: the Monday on or before the first of the month.</summary>
    public static DateTime FirstShownDay(DateTime month) => Ui.StartOfWeek(new DateTime(month.Year, month.Month, 1));

    public void Show(DateTime month, IReadOnlyList<EventItem> items)
    {
        var first = FirstShownDay(month);
        _grid.Children.Clear();
        _grid.RowDefinitions = new RowDefinitions("Auto,*,*,*,*,*,*");
        _grid.ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*");

        for (var column = 0; column < 7; column++)
        {
            var name = new TextBlock
            {
                Text = first.AddDays(column).ToString("ddd", Ui.German).TrimEnd('.'),
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = Ui.Brush("Mat.TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5),
            };
            Grid.SetColumn(name, column);
            _grid.Children.Add(name);
        }

        for (var index = 0; index < 42; index++)
        {
            var day = first.AddDays(index);
            var cell = Cell(day, month, items.Where(item => item.Event.Touches(day)).ToList());
            Grid.SetRow(cell, 1 + index / 7);
            Grid.SetColumn(cell, index % 7);
            _grid.Children.Add(cell);
        }
    }

    private Border Cell(DateTime day, DateTime month, List<EventItem> items)
    {
        var isToday = day == DateTime.Today;
        var inMonth = day.Month == month.Month;
        var number = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = isToday ? Ui.Brush("Mat.Accent") : Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = day.Day == 1 ? day.ToString("d. MMM", Ui.German) : day.Day.ToString(),
                FontSize = 13,
                FontWeight = isToday ? FontWeight.SemiBold : FontWeight.Medium,
                Foreground = isToday ? Ui.Brush("Mat.OnAccent") : inMonth ? Ui.Brush("Mat.Text") : Ui.Brush("Mat.TextFaint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (day.Day == 1 && !isToday)
        {
            number.Width = double.NaN;
            number.Padding = new Thickness(6, 0);
        }

        ToolTip.SetTip(number, Ui.LongDate(day));
        number.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(number).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                DayChosen?.Invoke(this, day);
            }
        };

        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(number);
        var ordered = items.OrderByDescending(item => item.Event.IsAllDay).ThenBy(item => item.Event.StartLocal).ToList();
        foreach (var item in ordered.Take(ChipsPerDay))
        {
            stack.Children.Add(EventVisuals.Chip(item, open => EventOpened?.Invoke(this, open)));
        }

        if (ordered.Count > ChipsPerDay)
        {
            var more = new Button { Theme = Ui.Theme("MatButton.Link"), Content = $"+{ordered.Count - ChipsPerDay} weitere", FontSize = 11.5, Margin = new Thickness(6, 1, 0, 0) };
            more.Click += (_, _) => DayChosen?.Invoke(this, day);
            stack.Children.Add(more);
        }

        var cell = new Border
        {
            Background = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || !inMonth ? Ui.Brush("Mat.Bg2") : Brushes.Transparent,
            BorderBrush = Ui.Brush("Mat.Border"),
            BorderThickness = new Thickness(day.DayOfWeek == DayOfWeek.Monday ? 0 : 1, 1, 0, 0),
            Padding = new Thickness(4),
            ClipToBounds = true,
            Child = stack,
        };
        cell.DoubleTapped += (_, e) =>
        {
            if (e.Source == cell || e.Source == stack)
            {
                NewEventRequested?.Invoke(this, (day.AddHours(9), false));
            }
        };
        return cell;
    }
}
