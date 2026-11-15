using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ThixCalendar.Controls;

/// <summary>
/// Paging below a list (one code base for every list): "1–50 von 123" and ‹ Seite 1 von 3 ›. Hidden while everything
/// fits on one page. Several pagers on a page work independently.
/// </summary>
public sealed class Pager : UserControl
{
    private readonly TextBlock _range = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    private readonly TextBlock _page = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Margin = new Thickness(8, 0) };
    private readonly Button _previous;
    private readonly Button _next;
    private int _total;

    public Pager()
    {
        _previous = Arrow("Icon.ChevronLeft", "Vorherige Seite", -1);
        _next = Arrow("Icon.ChevronRight", "Nächste Seite", 1);
        _range.Foreground = Ui.Brush("TextMutedBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _previous, _page, _next } };
        var row = new DockPanel { Children = { buttons, _range } };
        DockPanel.SetDock(buttons, Dock.Right);
        Content = row;
        Refresh();
    }

    /// <summary>Raised with the new page (0-based).</summary>
    public event EventHandler<int>? PageChanged;

    public int PageSize { get; set; } = 50;

    public int Page { get; private set; }

    public int PageCount => Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));

    /// <summary>Sets the number of rows; the page stays when it still exists.</summary>
    public void SetTotal(int total, bool resetPage = false)
    {
        _total = Math.Max(0, total);
        Page = resetPage ? 0 : Math.Min(Page, PageCount - 1);
        Refresh();
    }

    private Button Arrow(string icon, string name, int step)
    {
        var button = new Button { Theme = Ui.Theme("RoundButton"), Width = 32, Height = 32, Content = new IconView { Data = Ui.Icon(icon), Width = 16, Height = 16 } };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) =>
        {
            Page = Math.Clamp(Page + step, 0, PageCount - 1);
            Refresh();
            PageChanged?.Invoke(this, Page);
        };
        return button;
    }

    private void Refresh()
    {
        IsVisible = _total > PageSize;
        var first = _total == 0 ? 0 : Page * PageSize + 1;
        var last = Math.Min(_total, (Page + 1) * PageSize);
        _range.Text = $"{first}–{last} von {_total}";
        _page.Text = $"Seite {Page + 1} von {PageCount}";
        _previous.IsEnabled = Page > 0;
        _next.IsEnabled = Page < PageCount - 1;
    }
}
