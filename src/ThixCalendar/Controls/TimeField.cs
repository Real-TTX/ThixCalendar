using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ThixCalendar.Controls;

/// <summary>
/// A time of day typed like on paper: "9", "930", "9:30", "9.30", "14:05" – shown as "09:30". ↑ / ↓ move it by 15
/// minutes. One code base for every time input (start and end of events); several on a page work independently.
/// </summary>
public sealed class TimeField : Avalonia.Controls.TextBox
{
    public static readonly StyledProperty<TimeSpan?> SelectedTimeProperty = AvaloniaProperty.Register<TimeField, TimeSpan?>(nameof(SelectedTime));

    private bool _isWriting;

    static TimeField()
    {
        SelectedTimeProperty.Changed.AddClassHandler<TimeField>((field, _) => field.ShowTime());
    }

    public TimeField()
    {
        Width = 92;
        Watermark = "hh:mm";
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
    }

    /// <summary>Raised when the user changed the time (not when the code set it).</summary>
    public event EventHandler? SelectedTimeChanged;

    public TimeSpan? SelectedTime
    {
        get => GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Avalonia.Controls.TextBox);

    /// <summary>"9" → 09:00, "930" → 09:30, "9:5" → 09:05, "24:00" is not a time of day; null when it cannot be read.</summary>
    public static TimeSpan? Parse(string? text)
    {
        var value = (text ?? "").Trim().Replace('.', ':').Replace(',', ':').Replace(" Uhr", "", StringComparison.OrdinalIgnoreCase);
        if (value.Length == 0)
        {
            return null;
        }

        int hours;
        var minutes = 0;
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                (parts[1].Length > 0 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)))
            {
                return null;
            }
        }
        else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            (hours, minutes) = value.Length <= 2 ? (number, 0) : (number / 100, number % 100);
        }
        else
        {
            return null;
        }

        return hours is >= 0 and < 24 && minutes is >= 0 and < 60 ? new TimeSpan(hours, minutes, 0) : null;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Commit();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            var current = Parse(Text) ?? SelectedTime ?? TimeSpan.FromHours(9);
            var step = TimeSpan.FromMinutes(e.Key == Key.Up ? 15 : -15);
            var rounded = TimeSpan.FromMinutes(Math.Round(current.TotalMinutes / 15) * 15);
            var next = (rounded == current ? current + step : rounded).Ticks;
            Change(TimeSpan.FromTicks(((next % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay));
            return;
        }

        if (e.Key == Key.Enter)
        {
            Commit();
        }

        base.OnKeyDown(e);
    }

    private void Commit()
    {
        if (Parse(Text) is { } time)
        {
            Change(time);
        }
        else
        {
            ShowTime(); // unreadable: back to the last good value
        }
    }

    private void Change(TimeSpan time)
    {
        var changed = SelectedTime != time;
        SelectedTime = time;
        ShowTime();
        if (changed)
        {
            SelectedTimeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowTime()
    {
        if (_isWriting)
        {
            return;
        }

        _isWriting = true;
        Text = SelectedTime is { } time ? $"{time.Hours:00}:{time.Minutes:00}" : "";
        _isWriting = false;
    }
}
