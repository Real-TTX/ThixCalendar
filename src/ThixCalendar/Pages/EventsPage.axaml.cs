using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Dialogs;

namespace ThixCalendar.Pages;

/// <summary>
/// The list of events with search, calendar, period and sort above it and paging below. Search runs on every key (the
/// events are local); a double click or Enter opens the event, Delete removes it (after asking).
/// </summary>
public partial class EventsPage : UserControl, IRefreshable
{
    private static readonly (string Name, int FromDays, int ToDays)[] Periods =
    [
        ("Heute", 0, 1),
        ("Nächste 7 Tage", 0, 7),
        ("Nächste 30 Tage", 0, 30),
        ("Nächste 90 Tage", 0, 90),
        ("Letzte 30 Tage", -30, 0),
        ("Alle geladenen", -SyncService.WindowDaysBack - 365, SyncService.WindowDaysAhead + 365),
    ];

    private static readonly string[] Sorts = ["Beginn ↑", "Beginn ↓", "Titel", "Kalender"];

    private List<CalendarInfo> _calendars = [];
    private List<EventItem> _items = [];
    private bool _isLoading;
    private string _search = "";

    public EventsPage()
    {
        InitializeComponent();
        PeriodFilter.ItemsSource = Periods.Select(period => period.Name).ToList();
        PeriodFilter.SelectedIndex = 2;
        SortChoice.ItemsSource = Sorts;
        SortChoice.SelectedIndex = 0;
        CalendarFilter.SelectionChanged += (_, _) => Reload(resetPage: true);
        PeriodFilter.SelectionChanged += (_, _) => Reload(resetPage: true);
        SortChoice.SelectionChanged += (_, _) => Reload(resetPage: true);
        Pager.PageChanged += (_, _) => ShowPage();
        EventList.SelectionChanged += (_, _) => RefreshActions();
        EventList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>() is not null)
            {
                OpenSelected();
            }
        };
        EventList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OpenSelected();
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                _ = DeleteSelectedAsync();
            }
        };
        Refresh();
    }

    private MainWindow? Window => Ui.WindowOf(this) as MainWindow;

    private EventItem? Selected => (EventList.SelectedItem as Control)?.Tag as EventItem;

    /// <summary>The words of the header search (MainWindow); the list filters at once.</summary>
    public void SetSearch(string text)
    {
        if (_search == text)
        {
            return;
        }

        _search = text;
        Reload(resetPage: true);
    }

    public void Refresh()
    {
        _isLoading = true;
        try
        {
            var selected = CalendarFilter.SelectedIndex > 0 && CalendarFilter.SelectedIndex - 1 < _calendars.Count ? _calendars[CalendarFilter.SelectedIndex - 1].Id : (long?)null;
            var accounts = CalendarService.Accounts().ToDictionary(account => account.Id);
            _calendars = CalendarService.Calendars().ToList();
            CalendarFilter.ItemsSource = new[] { "Alle sichtbaren Kalender" }
                .Concat(_calendars.Select(calendar => accounts.TryGetValue(calendar.AccountId, out var account) ? $"{calendar.Name} · {account.Name}" : calendar.Name))
                .ToList();
            var index = selected is { } id ? _calendars.FindIndex(calendar => calendar.Id == id) : -1;
            CalendarFilter.SelectedIndex = index >= 0 ? index + 1 : 0;
        }
        finally
        {
            _isLoading = false;
        }

        Reload(resetPage: false);
    }

    private void Reload(bool resetPage)
    {
        if (_isLoading || PeriodFilter.SelectedIndex < 0 || SortChoice.SelectedIndex < 0)
        {
            return;
        }

        var period = Periods[PeriodFilter.SelectedIndex];
        var calendarId = CalendarFilter.SelectedIndex > 0 && CalendarFilter.SelectedIndex - 1 < _calendars.Count ? _calendars[CalendarFilter.SelectedIndex - 1].Id : (long?)null;
        var start = period.FromDays == 0 ? DateTime.Now : DateTime.Today.AddDays(period.FromDays);
        _items = CalendarService.Query(new EventQuery
        {
            From = period.Name == "Heute" ? DateTime.Today : start,
            To = DateTime.Today.AddDays(period.ToDays),
            Search = _search,
            CalendarId = calendarId,
            Sort = (EventSort)SortChoice.SelectedIndex,
        }).ToList();
        Pager.SetTotal(_items.Count, resetPage);
        CountText.Text = _items.Count == 1 ? "1 Termin" : $"{_items.Count} Termine";
        ShowPage();
    }

    private void ShowPage()
    {
        var keep = Selected?.Event.Id;
        var rows = _items.Skip(Pager.Page * Pager.PageSize).Take(Pager.PageSize).Select(Row).ToList();
        EventList.ItemsSource = rows;
        EmptyText.IsVisible = rows.Count == 0;
        EmptyText.Text = string.IsNullOrWhiteSpace(_search) ? "Keine Termine in diesem Zeitraum." : $"Nichts gefunden für „{_search}“.";
        EventList.SelectedItem = rows.FirstOrDefault(row => (row.Tag as EventItem)?.Event.Id == keep) ?? rows.FirstOrDefault();
        RefreshActions();
    }

    /// <summary>One row: colour bar, day and time, title with place, calendar.</summary>
    private static Control Row(EventItem item)
    {
        var calendarEvent = item.Event;
        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = Ui.SolidBrush(item.Calendar.Color), Margin = new Thickness(0, 2, 14, 2) };
        var when = new StackPanel
        {
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = Ui.DayName(calendarEvent.StartLocal), FontWeight = FontWeight.SemiBold, FontSize = 13.5 },
                new TextBlock { Text = Ui.TimeRange(calendarEvent), FontSize = 12.5, Foreground = Ui.Brush("TextMutedBrush") },
            },
        };
        var details = calendarEvent.Location.Length > 0 ? calendarEvent.Location : calendarEvent.MeetingUrl.Length > 0 ? Core.Providers.MeetingLinks.ServiceName(calendarEvent.MeetingUrl) : "";
        var what = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = calendarEvent.Title, FontSize = 14.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis,
                                TextDecorations = calendarEvent.IsCancelled ? TextDecorations.Strikethrough : null },
                new TextBlock { Text = details, FontSize = 12.5, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = details.Length > 0 },
            },
        };
        var calendar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
            Children =
            {
                new AccountMark { Kind = item.Account.Kind, Width = 14, Height = 14 },
                new TextBlock { Text = item.Calendar.Name, FontSize = 12.5, Foreground = Ui.Brush("TextMutedBrush"), MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        if (calendarEvent.MeetingUrl.Length > 0)
        {
            calendar.Children.Insert(0, new IconView { Data = Ui.Icon("Icon.Video"), Width = 15, Height = 15, Foreground = Ui.Brush("AccentBrush") });
        }

        DockPanel.SetDock(bar, Dock.Left);
        DockPanel.SetDock(when, Dock.Left);
        DockPanel.SetDock(calendar, Dock.Right);
        var row = new Border
        {
            Classes = { "row" },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Opacity = calendarEvent.IsDeclined ? 0.55 : 1,
            Child = new DockPanel { Children = { bar, when, calendar, what } },
            Tag = item,
        };
        ToolTip.SetTip(row, EventVisuals.ToolTip(item));
        ToolTip.SetShowDelay(row, 700);
        return row;
    }

    private void RefreshActions()
    {
        var item = Selected;
        OpenButton.IsEnabled = item is not null;
        ShowInCalendarButton.IsEnabled = item is not null;
        JoinButton.IsVisible = item?.Event.MeetingUrl.Length > 0;
        DeleteButton.IsEnabled = item is { IsReadOnly: false };
        ToolTip.SetTip(DeleteButton, item is { IsReadOnly: true } ? "Dieser Kalender kann nur gelesen werden" : "Termin löschen (Entf)");
    }

    private void OpenSelected()
    {
        if (Selected is { } item)
        {
            Window?.OpenEvent(item.Event.Id);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { IsReadOnly: false } item || Window is not { } window)
        {
            return;
        }

        var text = item.Account.IsOnline
            ? $"„{item.Event.Title}“ wird auch in {item.Account.KindName} gelöscht{(item.Event.IsRecurring ? " (nur dieser Termin der Serie)" : "")}."
            : $"„{item.Event.Title}“ wird gelöscht.";
        if (!await ConfirmDialog.AskAsync(window, "Termin löschen?", text, "Löschen", isDestructive: true))
        {
            return;
        }

        try
        {
            await CalendarService.DeleteEventAsync(item.Event.Id);
            Message.Show("Der Termin wurde gelöscht.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void OnNewClick(object? sender, RoutedEventArgs e) => Window?.NewEvent();

    private void OnOpenClick(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OnJoinClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { Event.MeetingUrl.Length: > 0 } item)
        {
            Ui.OpenExternal(item.Event.MeetingUrl);
        }
    }

    private void OnShowInCalendarClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } item)
        {
            Window?.ShowCalendar(item.Event.StartLocal.Date);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e) => await DeleteSelectedAsync();
}
