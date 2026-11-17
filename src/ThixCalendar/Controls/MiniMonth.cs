using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;

namespace ThixCalendar.Controls;

/// <summary>
/// The small month of the sidebar (mockup): month name with ‹ ›, the days of six weeks; today in the accent, the range
/// the calendar shows tinted. A click on a day shows it in the calendar (<see cref="DayChosen"/>).
/// </summary>
public sealed class MiniMonth : UserControl
{
    private readonly TextBlock _title = new() { FontSize = 14.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _days = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedFrom = DateTime.Today;
    private DateTime _selectedTo = DateTime.Today;

    public MiniMonth()
    {
        var previous = Arrow("chevron-left", "Vorheriger Monat", -1);
        var next = Arrow("chevron-right", "Nächster Monat", 1);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(next, 2);
        header.Children.Add(previous);
        header.Children.Add(_title);
        header.Children.Add(next);
        Content = new StackPanel { Children = { header, _days } };
        Refresh();
    }

    public event EventHandler<DateTime>? DayChosen;

    /// <summary>The range the calendar shows (tinted); the month follows it.</summary>
    public void Select(DateTime from, DateTime to)
    {
        _selectedFrom = from.Date;
        _selectedTo = to.Date;
        var middle = from.Date.AddDays((to.Date - from.Date).TotalDays / 2);
        _month = new DateTime(middle.Year, middle.Month, 1);
        Refresh();
    }

    private Button Arrow(string icon, string name, int months)
    {
        var button = new Button
        {
            Theme = Ui.Theme("MatButton.Ghost"),
            Content = new MatIcon { Icon = icon, Size = 16 },
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) =>
        {
            _month = _month.AddMonths(months);
            Refresh();
        };
        return button;
    }

    private void Refresh()
    {
        _title.Text = _month.ToString("MMMM yyyy", Ui.German);
        _days.Children.Clear();
        _days.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto");
        var first = Ui.StartOfWeek(_month);
        for (var column = 0; column < 7; column++)
        {
            var name = new TextBlock
            {
                Text = first.AddDays(column).ToString("ddd", Ui.German).TrimEnd('.'),
                FontSize = 12,
                Foreground = Ui.Brush("Mat.TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            };
            Grid.SetColumn(name, column);
            _days.Children.Add(name);
        }

        for (var index = 0; index < 42; index++)
        {
            var day = first.AddDays(index);
            var selected = day >= _selectedFrom && day <= _selectedTo;
            var isToday = day == DateTime.Today;
            var number = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = isToday ? Ui.Brush("Mat.Accent") : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(),
                    FontSize = 13,
                    FontWeight = isToday || selected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = isToday ? Ui.Brush("Mat.OnAccent") : day.Month == _month.Month ? Ui.Brush("Mat.Text") : Ui.Brush("Mat.TextFaint"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            // The chosen range is one tinted band (rounded at its ends), like the mockup.
            var band = new Border
            {
                Background = selected ? Ui.Brush("Mat.AccentSoftStrong") : Brushes.Transparent,
                Margin = new Thickness(0, 2),
                CornerRadius = new CornerRadius(day == _selectedFrom || day.DayOfWeek == DayOfWeek.Monday ? 14 : 0, day == _selectedTo || day.DayOfWeek == DayOfWeek.Sunday ? 14 : 0,
                    day == _selectedTo || day.DayOfWeek == DayOfWeek.Sunday ? 14 : 0, day == _selectedFrom || day.DayOfWeek == DayOfWeek.Monday ? 14 : 0),
                Child = number,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(band, Ui.LongDate(day));
            band.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(band).Properties.IsLeftButtonPressed)
                {
                    DayChosen?.Invoke(this, day);
                }
            };
            Grid.SetRow(band, 1 + index / 7);
            Grid.SetColumn(band, index % 7);
            _days.Children.Add(band);
        }
    }
}
