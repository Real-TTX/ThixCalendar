using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Pages;

/// <summary>
/// The calendar: one period (day, work week, week, month) around <see cref="Anchor"/>. Reads through CalendarService only;
/// a period outside the synchronised window is fetched on demand (SyncService.EnsureRangeAsync).
/// </summary>
public partial class CalendarPage : UserControl, IRefreshable
{
    private CalendarViewMode _mode;

    public CalendarPage()
    {
        InitializeComponent();
        _mode = SettingsService.Get().ViewMode;
        TimeGrid.DayStartHour = SettingsService.Get().DayStartHour;
        TimeGrid.EventOpened += (_, item) => OpenEvent(item);
        Month.EventOpened += (_, item) => OpenEvent(item);
        TimeGrid.NewEventRequested += (_, request) => Window?.NewEvent(request.Start, request.IsAllDay);
        Month.NewEventRequested += (_, request) => Window?.NewEvent(request.Start, request.IsAllDay);
        TimeGrid.DayChosen += (_, day) => GoTo(day, CalendarViewMode.Day);
        Month.DayChosen += (_, day) => GoTo(day, CalendarViewMode.Day);
        SyncService.StatusChanged += (_, _) => Ui.Post(RefreshSyncState);
        ((Avalonia.Controls.Primitives.PopupFlyoutBase)FilterButton.Flyout!).Opening += (_, _) => BuildFilter();
        ToolTip.SetTip(PreviousButton, "Zurück (←)");
        ToolTip.SetTip(NextButton, "Weiter (→)");
        ToolTip.SetTip(TodayButton, "Heute (T)");
        ToolTip.SetTip(DayMode, "Tag (D)");
        ToolTip.SetTip(WorkWeekMode, "Arbeitswoche Mo–Fr (A)");
        ToolTip.SetTip(WeekMode, "Woche (W)");
        ToolTip.SetTip(MonthMode, "Monat (M)");
        Refresh();
    }

    /// <summary>The period shown changed (the small month in the sidebar follows).</summary>
    public event EventHandler<(DateTime From, DateTime To)>? RangeChanged;

    /// <summary>The day the period is built around.</summary>
    public DateTime Anchor { get; private set; } = DateTime.Today;

    public CalendarViewMode Mode => _mode;

    /// <summary>The first and the last day shown.</summary>
    public (DateTime From, DateTime To) Range => _mode switch
    {
        CalendarViewMode.Day => (Anchor, Anchor),
        CalendarViewMode.WorkWeek => (Ui.StartOfWeek(Anchor), Ui.StartOfWeek(Anchor).AddDays(4)),
        CalendarViewMode.Month => (MonthView.FirstShownDay(Anchor), MonthView.FirstShownDay(Anchor).AddDays(41)),
        _ => (Ui.StartOfWeek(Anchor), Ui.StartOfWeek(Anchor).AddDays(6)),
    };

    private MainWindow? Window => Ui.WindowOf(this) as MainWindow;

    public void GoTo(DateTime day, CalendarViewMode? mode = null)
    {
        Anchor = day.Date;
        if (mode is { } newMode && newMode != _mode)
        {
            _mode = newMode;
            SettingsService.Remember(settings => settings with { ViewMode = newMode });
        }

        Refresh();
    }

    /// <summary>A sensible start for Ctrl/Cmd+N: the next full hour on the shown day (today: from now).</summary>
    public DateTime SuggestedStart()
    {
        var (from, to) = Range;
        if (DateTime.Today >= from && DateTime.Today <= to)
        {
            return DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
        }

        return (_mode == CalendarViewMode.Day ? Anchor : from).AddHours(Math.Max(9, SettingsService.Get().DayStartHour + 2));
    }

    /// <summary>The keys of the calendar (no modifier, no text field focused). True when handled.</summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Left:
                Step(-1);
                return true;
            case Key.Right:
                Step(1);
                return true;
            case Key.T:
                GoToday();
                return true;
            case Key.D:
                GoTo(Anchor, CalendarViewMode.Day);
                return true;
            case Key.A:
                GoTo(Anchor, CalendarViewMode.WorkWeek);
                return true;
            case Key.W:
                GoTo(Anchor, CalendarViewMode.Week);
                return true;
            case Key.M:
                GoTo(Anchor, CalendarViewMode.Month);
                return true;
            default:
                return false;
        }
    }

    public void Refresh()
    {
        var (from, to) = Range;
        var items = CalendarService.Range(from, to.AddDays(1));
        DayMode.IsChecked = _mode == CalendarViewMode.Day;
        WorkWeekMode.IsChecked = _mode == CalendarViewMode.WorkWeek;
        WeekMode.IsChecked = _mode == CalendarViewMode.Week;
        MonthMode.IsChecked = _mode == CalendarViewMode.Month;

        if (_mode == CalendarViewMode.Month)
        {
            TimeGrid.IsVisible = false;
            Month.IsVisible = true;
            Month.Show(Anchor, items);
        }
        else
        {
            Month.IsVisible = false;
            TimeGrid.IsVisible = true;
            TimeGrid.Show(from, (to - from).Days + 1, items);
        }

        RefreshTitle(from, to);
        RefreshSyncState();
        RefreshHint();
        RangeChanged?.Invoke(this, _mode == CalendarViewMode.Month ? (new DateTime(Anchor.Year, Anchor.Month, 1), new DateTime(Anchor.Year, Anchor.Month, 1).AddMonths(1).AddDays(-1)) : (from, to));
        _ = LoadRangeAsync(from, to);
    }

    private async Task LoadRangeAsync(DateTime from, DateTime to)
    {
        try
        {
            await SyncService.EnsureRangeAsync(from, to); // CalendarService.Changed refreshes when something arrived
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            Log.Warning($"Range not loaded: {exception.Message}");
        }
    }

    private void RefreshTitle(DateTime from, DateTime to)
    {
        switch (_mode)
        {
            case CalendarViewMode.Day:
                RangeTitle.Text = Anchor.ToString("dddd, d. MMMM yyyy", Ui.German);
                RangeSubtitle.Text = $"KW {Ui.Week(Anchor)}{(Anchor == DateTime.Today ? " · Heute" : "")}";
                break;
            case CalendarViewMode.Month:
                RangeTitle.Text = Anchor.ToString("MMMM yyyy", Ui.German);
                RangeSubtitle.Text = $"KW {Ui.Week(from)} – {Ui.Week(to)}";
                break;
            default:
                RangeTitle.Text = from.Month == to.Month
                    ? $"{from:%d}. – {to.ToString("d. MMM yyyy", Ui.German)}"
                    : from.Year == to.Year
                        ? $"{from.ToString("d. MMM", Ui.German)} – {to.ToString("d. MMM yyyy", Ui.German)}"
                        : $"{from.ToString("d. MMM yyyy", Ui.German)} – {to.ToString("d. MMM yyyy", Ui.German)}";
                RangeSubtitle.Text = $"KW {Ui.Week(from)}";
                break;
        }
    }

    private void RefreshSyncState()
    {
        var status = SyncService.Status;
        var brush = status.SignedOutAccounts > 0 ? "DangerBrush" : status.FailedAccounts > 0 ? "WarningBrush" : status.IsSyncing ? "AccentBrush" : status.LastSync is not null ? "SuccessBrush" : null;
        SyncDot.IsVisible = brush is not null;
        if (brush is not null)
        {
            SyncDot.Fill = Ui.Brush(brush);
        }

        ToolTip.SetTip(SyncButton, $"Jetzt synchronisieren – {status.Message}");
    }

    private void RefreshHint()
    {
        if (CalendarService.Accounts().Count == 0)
        {
            Hint.Show("Noch kein Kalender verbunden. Unter „Konten“ verbindest du Microsoft 365, Google oder ein ICS-Abo – oder legst einen Kalender auf diesem Computer an.", InfoSeverity.Info);
            return;
        }

        var signedOut = CalendarService.Accounts().Where(account => account.State == AccountState.SignedOut).ToList();
        if (signedOut.Count > 0)
        {
            Hint.Show($"{string.Join(", ", signedOut.Select(account => account.Name))}: die Anmeldung ist abgelaufen – ohne neue Anmeldung kommen von dort keine Termine und Erinnerungen. Unter „Konten“ › „Neu anmelden“.", InfoSeverity.Warning);
            return;
        }

        Hint.Hide();
    }

    /// <summary>The filter: every account (all its calendars at once), then every calendar.</summary>
    private void BuildFilter()
    {
        FilterList.Children.Clear();
        var accounts = CalendarService.Accounts();
        FilterList.Children.Add(new TextBlock { Text = "Konten", FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0, 0, 4) });
        foreach (var account in accounts)
        {
            var box = new CheckBox
            {
                IsChecked = account.IsVisible,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new AccountMark { Kind = account.Kind, Width = 14, Height = 14 },
                        new TextBlock { Text = account.Name, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            var accountId = account.Id;
            box.IsCheckedChanged += (_, _) => CalendarService.SetAccountVisible(accountId, box.IsChecked == true);
            FilterList.Children.Add(box);
        }

        FilterList.Children.Add(new TextBlock { Text = "Kalender", FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 10, 0, 4) });
        var names = accounts.ToDictionary(account => account.Id, account => account.Name);
        foreach (var calendar in CalendarService.Calendars())
        {
            var box = new CheckBox
            {
                IsChecked = calendar.IsVisible,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Ui.SolidBrush(calendar.Color), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = names.TryGetValue(calendar.AccountId, out var name) ? $"{calendar.Name} · {name}" : calendar.Name, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            var calendarId = calendar.Id;
            box.IsCheckedChanged += (_, _) => CalendarService.SetCalendarVisible(calendarId, box.IsChecked == true);
            FilterList.Children.Add(box);
        }
    }

    private void Step(int direction)
    {
        Anchor = _mode switch
        {
            CalendarViewMode.Day => Anchor.AddDays(direction),
            CalendarViewMode.Month => new DateTime(Anchor.Year, Anchor.Month, 1).AddMonths(direction),
            _ => Anchor.AddDays(7 * direction),
        };
        Refresh();
    }

    private void GoToday()
    {
        Anchor = DateTime.Today;
        Refresh();
        TimeGrid.ScrollToHour(Math.Max(0, DateTime.Now.Hour - 1));
    }

    private void OpenEvent(EventItem item) => Window?.OpenEvent(item.Event.Id);

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => Step(-1);

    private void OnNextClick(object? sender, RoutedEventArgs e) => Step(1);

    private void OnTodayClick(object? sender, RoutedEventArgs e) => GoToday();

    private void OnSyncClick(object? sender, RoutedEventArgs e) => SyncService.Kick();

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag && Enum.TryParse<CalendarViewMode>(tag, out var mode))
        {
            GoTo(Anchor, mode);
        }
    }
}
