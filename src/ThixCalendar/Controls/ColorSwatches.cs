using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Controls;

/// <summary>The colours of a calendar to choose from (the palette), the chosen one with a ring.</summary>
public sealed class ColorSwatches : UserControl
{
    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal };
    private string _selected = CalendarColors.Default;

    public ColorSwatches()
    {
        Content = _panel;
        Refresh();
    }

    public event EventHandler<string>? ColorChosen;

    public string SelectedColor
    {
        get => _selected;
        set
        {
            _selected = CalendarColors.Normalize(value) ?? CalendarColors.Default;
            Refresh();
        }
    }

    private void Refresh()
    {
        _panel.Children.Clear();
        var palette = CalendarColors.Palette.Contains(_selected, StringComparer.OrdinalIgnoreCase) ? CalendarColors.Palette : [.. CalendarColors.Palette, _selected];
        foreach (var color in palette)
        {
            var isSelected = string.Equals(color, _selected, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(isSelected ? 2 : 0),
                BorderBrush = Ui.Brush("Mat.Text"),
                Padding = new Thickness(isSelected ? 3 : 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = true,
                Child = new Border { CornerRadius = new CornerRadius(13), Background = Ui.SolidBrush(color) },
            };
            AutomationProperties.SetName(swatch, $"Farbe {color}");
            var chosen = color;
            swatch.PointerPressed += (_, _) => Choose(chosen);
            swatch.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    Choose(chosen);
                }
            };
            _panel.Children.Add(swatch);
        }
    }

    private void Choose(string color)
    {
        SelectedColor = color;
        ColorChosen?.Invoke(this, color);
    }
}
