using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Pages;

namespace ThixCalendar;

/// <summary>
/// The main window: sidebar with the sections, "Meine Kalender" and the small month; the page at the right. Editors
/// (event, account) open in place of the page and "Zurück" returns to where the user came from. Keyboard:
///   Ctrl/Cmd+1/2/3 Kalender / Termine / Konten, +, Einstellungen, +N neuer Termin, +F suchen, +B Menü ein-/ausklappen,
///   on the calendar: ← → previous / next, T today, D / A / W / M day, work week, week, month; Esc goes back.
/// </summary>
public partial class MainWindow : ThemedWindow
{
    private const double FoldedSidebarWidth = 68;

    private readonly CalendarPage _calendar = new();
    private readonly Lazy<EventsPage> _events = new(() => new EventsPage());
    private readonly Lazy<AccountsPage> _accounts = new(() => new AccountsPage());
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _minuteTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Stack<Control> _back = new();
    private bool _calendarsCollapsed;
    private bool _refreshPosted;

    public MainWindow()
    {
        InitializeComponent();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        IsSidebarCollapsed = settings.IsSidebarCollapsed;

        if (OperatingSystem.IsMacOS())
        {
            // The traffic lights stay where macOS draws them; the name moves right of them.
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 68;
            CaptionButtons.IsVisible = false;
            HeaderGrid.Margin = new Thickness(84, 0, 18, 0);
            HeaderGrid.ColumnDefinitions[0].Width = new GridLength(186);
        }
        else
        {
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 0;
        }

        PageHost.Content = _calendar;
        NavCalendar.IsChecked = true;
        _calendar.RangeChanged += (_, range) => MiniMonth.Select(range.From, range.To);
        MiniMonth.DayChosen += (_, day) => ShowCalendar(day);
        MiniMonth.Select(_calendar.Range.From, _calendar.Range.To);

        // The search of the header searches every event: typing shows the list "Termine" with the result.
        SearchBox.TextChanged += (_, _) =>
        {
            var text = SearchBox.Text ?? "";
            if (PageHost.Content != _events.Value && text.Length > 0)
            {
                Navigate(_events.Value, NavEvents, keepHistory: false);
            }

            if (_events.IsValueCreated)
            {
                _events.Value.SetSearch(text);
            }
        };
        ToolTip.SetTip(NewEventButton, $"Neuer Termin ({Ui.CommandKey.Name}+N)");

        _sizeTimer.Tick += (_, _) =>
        {
            _sizeTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                SettingsService.Remember(current => current with { WindowWidth = Width, WindowHeight = Height });
            }
        };
        Resized += (_, _) =>
        {
            _sizeTimer.Stop();
            _sizeTimer.Start();
            MaximizeIcon.Data = Ui.Icon(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");
        };
        SizeChanged += (_, e) =>
        {
            MiniMonthCard.IsVisible = !IsSidebarCollapsed && e.NewSize.Height >= 700;
            ApplySidebarWidth(e.NewSize.Width);
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        CalendarService.Changed += (_, _) => PostRefresh();
        SettingsService.Changed += (_, _) => PostRefresh();
        ReminderService.Changed += (_, _) => Ui.Post(RefreshReminderStatus);
        UpdateCoordinator.Checked += (_, _) => Ui.Post(ApplySidebar);
        _minuteTimer.Tick += (_, _) => RefreshReminderStatus();
        _minuteTimer.Start();

        var command = Ui.CommandKey.Name;
        ToolTip.SetTip(NavCalendar, $"Kalender ({command}+1)");
        ToolTip.SetTip(NavEvents, $"Termine suchen und auflisten ({command}+2)");
        ToolTip.SetTip(NavAccounts, $"Konten: Microsoft 365, Google, ICS ({command}+3)");
        ToolTip.SetTip(NavSettings, $"Einstellungen ({command}+,)");

        RefreshCalendarList();
        RefreshReminderStatus();
        ApplySidebar();
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    public bool IsSidebarCollapsed { get; private set; }

    /// <summary>The page shown right now (tests, self test).</summary>
    public Control? CurrentPage => PageHost.Content as Control;

    public CalendarPage CalendarPage => _calendar;

    public void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        ApplySidebar();
        SettingsService.Remember(settings => settings with { IsSidebarCollapsed = IsSidebarCollapsed });
    }

    /// <summary>Tray, second start, Dock, a reminder: bring the window up.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Core.Platform.WindowActivation.BringToFront(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    public void ShowCalendar(DateTime? day = null, CalendarViewMode? mode = null)
    {
        Navigate(_calendar, NavCalendar, keepHistory: false);
        if (day is not null || mode is not null)
        {
            _calendar.GoTo(day ?? _calendar.Anchor, mode);
        }
    }

    public void ShowEvents(string? search = null)
    {
        Navigate(_events.Value, NavEvents, keepHistory: false);
        if (search is not null)
        {
            SearchBox.Text = search;
        }

        _events.Value.SetSearch(SearchBox.Text ?? "");
    }

    /// <summary>Ctrl/Cmd+F: into the search of the header.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ShowAccounts() => Navigate(_accounts.Value, NavAccounts, keepHistory: false);

    public void ShowSettings(string? tab = null)
    {
        Navigate(_settings.Value, NavSettings, keepHistory: false);
        if (tab is not null)
        {
            _settings.Value.SelectTab(tab);
        }
    }

    public void ShowAbout() => Navigate(_about.Value, null, keepHistory: false);

    /// <summary>Opens an event in the editor (from the calendar, the list, a reminder).</summary>
    public void OpenEvent(long eventId)
    {
        if (CalendarService.GetEvent(eventId) is not { } item)
        {
            return;
        }

        Navigate(new EventEditorPage(item.Event), CurrentNav(), keepHistory: true);
    }

    /// <summary>A new event at <paramref name="start"/> (Ctrl/Cmd+N: the next full hour).</summary>
    public void NewEvent(DateTime? start = null, bool isAllDay = false)
    {
        try
        {
            var begin = start ?? DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
            Navigate(new EventEditorPage(CalendarService.NewEvent(begin, isAllDay)), CurrentNav(), keepHistory: true);
        }
        catch (BusinessException exception)
        {
            _ = Dialogs.ConfirmDialog.ShowMessageAsync(this, "Kein Kalender", exception.Message);
        }
    }

    /// <summary>An account in its editor (or a new ICS subscription / local account: <paramref name="newKind"/>).</summary>
    public void OpenAccount(long? accountId, AccountKind? newKind = null) =>
        Navigate(new AccountEditorPage(accountId, newKind), NavAccounts, keepHistory: true);

    /// <summary>"Zurück" of an editor: the page the user came from.</summary>
    public void GoBack()
    {
        var previous = _back.Count > 0 ? _back.Pop() : _calendar;
        PageHost.Content = previous;
        SelectNav(previous switch
        {
            EventsPage => NavEvents,
            AccountsPage => NavAccounts,
            SettingsPage => NavSettings,
            Pages.CalendarPage => NavCalendar,
            _ => null,
        });
        (previous as IRefreshable)?.Refresh();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The X (and Alt+F4) only hides: reminders keep coming from the tray / menu bar.
        if (!IsExiting)
        {
            e.Cancel = true;
            HideToTray();
        }

        base.OnClosing(e);
    }

    private void HideToTray()
    {
        Hide();
        if (!SettingsService.Get().TrayHintShown)
        {
            SettingsService.Remember(settings => settings with { TrayHintShown = true });
            Tray.NotificationToast.Show("ThixCalendar läuft weiter",
                OperatingSystem.IsMacOS()
                    ? "Erinnerungen kommen weiterhin. Das Fenster öffnest du über das Symbol in der Menüleiste."
                    : "Erinnerungen kommen weiterhin. Das Fenster öffnest du über das Symbol im Infobereich der Taskleiste.",
                isWarning: false, ShowFromTray);
        }
    }

    private void Navigate(Control page, ToggleButton? nav, bool keepHistory)
    {
        if (PageHost.Content is Control current && current != page)
        {
            if (keepHistory)
            {
                if (current is not (EventEditorPage or AccountEditorPage))
                {
                    _back.Push(current);
                }
            }
            else
            {
                _back.Clear();
            }
        }

        PageHost.Content = page;
        SelectNav(nav);
        (page as IRefreshable)?.Refresh();
    }

    private ToggleButton? CurrentNav() => new[] { NavCalendar, NavEvents, NavAccounts, NavSettings }.FirstOrDefault(item => item.IsChecked == true);

    private void SelectNav(ToggleButton? selected)
    {
        foreach (var item in new[] { NavCalendar, NavEvents, NavAccounts, NavSettings })
        {
            item.IsChecked = item == selected;
        }
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.Tag as string)
        {
            case "Events":
                ShowEvents();
                break;
            case "Accounts":
                ShowAccounts();
                break;
            case "Settings":
                ShowSettings();
                break;
            default:
                ShowCalendar();
                break;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var withCommand = e.KeyModifiers.HasFlag(Ui.CommandKey.Modifier);
        var focused = FocusManager?.GetFocusedElement();
        var typing = focused is TextBox or ComboBox or ComboBoxItem or CalendarDatePicker;

        switch (e.Key)
        {
            case Key.D1 when withCommand:
                e.Handled = true;
                ShowCalendar();
                return;
            case Key.D2 when withCommand:
                e.Handled = true;
                ShowEvents();
                return;
            case Key.D3 when withCommand:
                e.Handled = true;
                ShowAccounts();
                return;
            case Key.OemComma when withCommand:
                e.Handled = true;
                ShowSettings();
                return;
            case Key.N when withCommand:
                e.Handled = true;
                NewEvent(PageHost.Content == _calendar ? _calendar.SuggestedStart() : null);
                return;
            case Key.F when withCommand:
                e.Handled = true;
                FocusSearch();
                return;
            case Key.Escape when SearchBox.IsFocused && !string.IsNullOrEmpty(SearchBox.Text):
                e.Handled = true;
                SearchBox.Text = "";
                return;
            case Key.B when withCommand:
                e.Handled = true;
                ToggleSidebar();
                return;
            case Key.Escape when OwnedWindows.Count == 0 && (PageHost.Content is EventEditorPage || PageHost.Content is AccountEditorPage):
                e.Handled = true;
                GoBack();
                return;
        }

        if (PageHost.Content == _calendar && !typing && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = _calendar.HandleKey(e.Key);
        }
    }

    /// <summary>Changes arrive in bursts (a sync): one refresh per burst.</summary>
    private void PostRefresh()
    {
        if (_refreshPosted)
        {
            return;
        }

        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            RefreshCalendarList();
            RefreshReminderStatus();
            if (PageHost.Content is IRefreshable page and not (EventEditorPage or AccountEditorPage))
            {
                page.Refresh();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>"Meine Kalender": per account its calendars with colour and switch (tinted rows like the mockup).</summary>
    private void RefreshCalendarList()
    {
        CalendarList.Children.Clear();
        var accounts = CalendarService.Accounts();
        var calendars = CalendarService.Calendars();
        if (accounts.Count == 0)
        {
            var add = new Button { Theme = Ui.Theme("LinkButton"), Content = "Konto verbinden …", Margin = new Thickness(8, 4) };
            add.Click += (_, _) => ShowAccounts();
            CalendarList.Children.Add(add);
            return;
        }

        foreach (var account in accounts)
        {
            var ofAccount = calendars.Where(calendar => calendar.AccountId == account.Id).ToList();
            if (accounts.Count > 1 || ofAccount.Count > 1)
            {
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(8, 6, 0, 0) };
                title.Children.Add(new AccountMark { Kind = account.Kind, Width = 13, Height = 13 });
                title.Children.Add(new TextBlock { Text = account.Name, FontSize = 12, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 });
                if (account.State == AccountState.SignedOut)
                {
                    title.Children.Add(new IconView { Data = Ui.Icon("Icon.Warning"), Width = 13, Height = 13, Foreground = Ui.Brush("DangerBrush") });
                }

                CalendarList.Children.Add(title);
            }

            foreach (var calendar in ofAccount)
            {
                CalendarList.Children.Add(CalendarRow(account, calendar));
            }
        }
    }

    private Border CalendarRow(CalendarAccount account, CalendarInfo calendar)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = calendar.IsVisible,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The switch takes the calendar's colour when on (like the mockup).
        toggle.Resources["ToggleSwitchFillOn"] = Ui.SolidBrush(calendar.Color);
        toggle.Resources["ToggleSwitchFillOnPointerOver"] = Ui.SolidBrush(calendar.Color);
        toggle.Resources["ToggleSwitchFillOnPressed"] = Ui.SolidBrush(calendar.Color);
        AutomationProperties.SetName(toggle, $"{calendar.Name} anzeigen");
        var calendarId = calendar.Id;
        toggle.IsCheckedChanged += (_, _) =>
        {
            try
            {
                CalendarService.SetCalendarVisible(calendarId, toggle.IsChecked == true);
            }
            catch (BusinessException exception)
            {
                Log.Warning(exception.Message);
            }
        };
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(4), Background = Ui.SolidBrush(calendar.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var name = new TextBlock { Text = calendar.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (!calendar.RemindersEnabled)
        {
            ToolTip.SetTip(name, "Erinnerungen für diesen Kalender sind aus");
        }

        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        var row = new DockPanel { Children = { dot, toggle, name } };
        if (!calendar.RemindersEnabled)
        {
            var off = new IconView { Data = Ui.Icon("Icon.BellOff"), Width = 13, Height = 13, Foreground = Ui.Brush("TextFaintBrush"), Margin = new Thickness(6, 0) };
            DockPanel.SetDock(off, Dock.Right);
            row.Children.Insert(2, off);
        }

        return new Border
        {
            Background = Ui.Tint(calendar.Color, Ui.IsDark ? 0.16 : 0.08),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 4, 3),
            Opacity = account.IsVisible ? 1 : 0.55,
            Child = row,
        };
    }

    /// <summary>Foot of the sidebar: how many reminders wait, or the pause, or the next event.</summary>
    private void RefreshReminderStatus()
    {
        var now = DateTimeOffset.Now;
        var active = ReminderService.Active.Count;
        var pausedUntil = SettingsService.Get().RemindersPausedUntil;
        string text;
        string? dot = null;
        if (pausedUntil is { } until && until > now)
        {
            text = $"Erinnerungen pausiert bis {until.ToLocalTime():HH:mm}";
            dot = "WarningBrush";
            ReminderIcon.Data = Ui.Icon("Icon.BellOff");
        }
        else if (active > 0)
        {
            text = active == 1 ? "1 Erinnerung offen" : $"{active} Erinnerungen offen";
            dot = "AlertBrush";
            ReminderIcon.Data = Ui.Icon("Icon.BellRing");
        }
        else
        {
            var next = ReminderService.Upcoming(now, 1).FirstOrDefault(item => !item.Event.IsAllDay);
            text = next is null ? "Keine Termine in Sicht" : $"Als Nächstes: {next.Event.StartLocal.ToString(next.Event.StartLocal.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", Ui.German)} {next.Event.Title}";
            ReminderIcon.Data = Ui.Icon("Icon.Bell");
        }

        ReminderText.Text = text;
        ReminderDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            ReminderDot.Fill = Ui.Brush(dot);
        }

        ToolTip.SetTip(ReminderStatus, IsSidebarCollapsed ? text : active > 0 ? "Offene Erinnerungen anzeigen" : "Erinnerungen pausieren / fortsetzen");
        AutomationProperties.SetName(ReminderStatus, text);
    }

    private void ApplySidebar()
    {
        var folded = IsSidebarCollapsed;
        foreach (var item in new[] { NavCalendar, NavEvents, NavAccounts, NavSettings })
        {
            ((IconLabel)item.Content!).IsTextVisible = !folded;
        }

        CalendarsSection.IsVisible = !folded;
        BrandName.IsVisible = !folded || OperatingSystem.IsMacOS();
        MiniMonthCard.IsVisible = !folded && Bounds.Height is 0 or >= 700;
        ReminderText.IsVisible = !folded;
        ReminderStatus.HorizontalContentAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        var update = UpdateService.LastResult is { IsUpdateAvailable: true, Manifest: { } manifest } ? manifest.Version : null;
        VersionButton.Content = folded
            ? new Panel
            {
                Children =
                {
                    new IconView { Data = Ui.Icon(update is null ? "Icon.Info" : "Icon.Download"), Width = 18, Height = 18 },
                    new Avalonia.Controls.Shapes.Ellipse
                    {
                        Width = 7, Height = 7, Fill = Ui.Brush("AccentBrush"), IsVisible = update is not null,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0),
                    },
                },
            }
            : update is null ? $"Version {AppInfo.Version}" : $"Version {AppInfo.Version}  ·  Update";
        ToolTip.SetTip(VersionButton, update is not null ? $"Update auf {update} verfügbar – Über ThixCalendar" : folded ? $"Über ThixCalendar – Version {AppInfo.Version}" : "Über ThixCalendar");
        VersionButton.Margin = folded ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        VersionButton.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Grid.SetColumnSpan(VersionButton, folded ? 2 : 1);
        Grid.SetRow(SidebarToggle, folded ? 1 : 0);
        Grid.SetColumn(SidebarToggle, folded ? 0 : 1);
        Grid.SetColumnSpan(SidebarToggle, folded ? 2 : 1);
        SidebarToggle.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SidebarToggle.Margin = folded ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        var action = folded ? "Menü ausklappen" : "Menü einklappen";
        ToolTip.SetTip(SidebarToggle, $"{action} ({Ui.CommandKey.Name}+B)");
        AutomationProperties.SetName(SidebarToggle, action);
        ApplySidebarWidth(Bounds.Width > 0 ? Bounds.Width : Width);
        RefreshReminderStatus();
    }

    /// <summary>The sidebar and the brand above it share one width, so the search stays in line with the page (as in ThixClip).</summary>
    private void ApplySidebarWidth(double windowWidth)
    {
        var width = IsSidebarCollapsed ? FoldedSidebarWidth : windowWidth < 1000 ? 232 : 262;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(width);
        if (OperatingSystem.IsMacOS())
        {
            return; // the traffic lights keep the header as it is
        }

        HeaderGrid.ColumnDefinitions[0].Width = new GridLength(width);
        HeaderGrid.Margin = new Thickness(IsSidebarCollapsed ? 18 : 22, 0, 12, 0);
    }

    private void OnNewEventClick(object? sender, RoutedEventArgs e) => NewEvent(PageHost.Content == _calendar ? _calendar.SuggestedStart() : null);

    private void OnCalendarsHeaderClick(object? sender, RoutedEventArgs e)
    {
        _calendarsCollapsed = !_calendarsCollapsed;
        CalendarsScroller.IsVisible = !_calendarsCollapsed;
        CalendarsChevron.Data = Ui.Icon(_calendarsCollapsed ? "Icon.ChevronRight" : "Icon.ChevronDown");
    }

    private async void OnReminderStatusClick(object? sender, RoutedEventArgs e)
    {
        if (ReminderService.Active.Count > 0)
        {
            App.Instance.ShowReminders();
            return;
        }

        await App.Instance.ToggleReminderPauseAsync(this);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not TextBox && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Border or Grid or StackPanel or Image)
        {
            OnMaximizeClick(sender, e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => HideToTray();

    private void OnVersionClick(object? sender, RoutedEventArgs e) => ShowAbout();

    private void OnSidebarToggleClick(object? sender, RoutedEventArgs e) => ToggleSidebar();
}

/// <summary>A page that reloads its data when it comes back into view or the data changed.</summary>
public interface IRefreshable
{
    void Refresh();
}
