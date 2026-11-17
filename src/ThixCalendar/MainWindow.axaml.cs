using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Pages;

namespace ThixCalendar;

/// <summary>
/// The main window in MatDesktop's chrome: MatTitleBar (DESK-03: logo, word mark, the search over all events, "Neuer Termin")
/// and MatNav (DESK-04: the sections, Settings below the line, at its foot "Meine Kalender", the small month, the state of the
/// reminders and of MatCloud, the version = About, the fold to a rail). Editors (event, account) open in place of the page
/// and "Zurück" returns to where the user came from. Keyboard:
///   Ctrl/Cmd+1/2/3 Kalender / Termine / Konten, +, Einstellungen, +N neuer Termin, +F suchen, +B Menü ein-/ausklappen,
///   on the calendar: ← → previous / next, T today, D / A / W / M day, work week, week, month; Esc goes back.
/// </summary>
public partial class MainWindow : MatWindow
{
    /// <summary>The small month needs this much room in the menu; below it, "Meine Kalender" keeps the space.</summary>
    private const double MiniMonthHeight = 262;

    private readonly CalendarPage _calendar = new();
    private readonly Lazy<EventsPage> _events = new(() => new EventsPage());
    private readonly Lazy<AccountsPage> _accounts = new(() => new AccountsPage());
    private readonly Lazy<SettingsPage> _settings = new(() => new SettingsPage());
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _minuteTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Stack<Control> _back = new();
    private readonly MatNavItem _navCalendar;
    private readonly MatNavItem _navEvents;
    private readonly MatNavItem _navAccounts;
    private readonly MatNavItem _navSettings;
    private bool _calendarsCollapsed;
    private bool _refreshPosted;

    public MainWindow()
    {
        InitializeComponent();
        UseOwnTitleBar();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        // The title bar: the X only hides, ThixCalendar keeps reminding from the tray / menu bar (DESK-06).
        TitleBar.Logo = Ui.Logo;
        TitleBar.CloseRequested += (_, e) =>
        {
            e.Handled = true;
            HideToTray();
        };

        // The menu: the sections, Settings below the line, the version at the foot (= About), folded or open as last time.
        var command = Ui.CommandKey.Name;
        _navCalendar = new MatNavItem("calendar", "Kalender", $"{command}+1") { IsCurrent = true };
        _navEvents = new MatNavItem("search", "Termine", $"{command}+2");
        _navAccounts = new MatNavItem("users", "Konten", $"{command}+3");
        _navSettings = new MatNavItem("settings", "Einstellungen", $"{command}+,");
        Sidebar.Items.AddRange([_navCalendar, _navEvents, _navAccounts]);
        Sidebar.Settings = _navSettings;
        Sidebar.Version = AppInfo.Version;
        Sidebar.Channel = AppInfo.Channel;
        Sidebar.TitleBar = TitleBar;
        Sidebar.IsFolded = settings.IsSidebarCollapsed;
        Sidebar.FoldShortcut = $"{command}+B";
        Sidebar.ItemInvoked += (_, item) => OnNavInvoked(item);
        Sidebar.AboutRequested += (_, _) => ShowAbout();
        Sidebar.UpdateRequested += (_, _) => ShowAbout();
        Sidebar.FoldChanged += (_, folded) =>
        {
            SettingsService.Remember(current => current with { IsSidebarCollapsed = folded });
            ApplySidebar();
        };
        BuildMenuFoot();

        PageHost.Content = _calendar;
        _calendar.RangeChanged += (_, range) => MiniMonth.Select(range.From, range.To);
        MiniMonth.DayChosen += (_, day) => ShowCalendar(day);
        MiniMonth.Select(_calendar.Range.From, _calendar.Range.To);

        // The search of the title bar searches every event: typing shows the list "Termine" with the result.
        SearchBox.TextChanged += (_, _) =>
        {
            var text = SearchBox.Text ?? "";
            if (PageHost.Content != _events.Value && text.Length > 0)
            {
                Navigate(_events.Value, _navEvents, keepHistory: false);
            }

            if (_events.IsValueCreated)
            {
                _events.Value.SetSearch(text);
            }
        };
        ToolTip.SetTip(NewEventButton, $"Neuer Termin ({command}+N)");

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
        };
        SizeChanged += (_, _) => FitMenuFoot();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        CalendarService.Changed += (_, _) => PostRefresh();
        SettingsService.Changed += (_, _) => PostRefresh();
        ReminderService.Changed += (_, _) => Ui.Post(RefreshReminderStatus);
        CloudService.StatusChanged += (_, _) => Ui.Post(RefreshCloudStatus);
        UpdateCoordinator.Checked += (_, _) => Ui.Post(ApplySidebar);
        _minuteTimer.Tick += (_, _) => RefreshReminderStatus();
        _minuteTimer.Start();

        RefreshCalendarList();
        ApplySidebar();
    }

    /// <summary>Set by App when the application ends: then the window really closes.</summary>
    public bool IsExiting { get; set; }

    /// <summary>The menu at the left is folded to its icons (Ctrl/Cmd+B, the button at its foot); remembered across starts.</summary>
    public bool IsSidebarCollapsed => Sidebar.IsFolded;

    /// <summary>The button at the foot of the menu that folds and opens it.</summary>
    public Button SidebarToggle => Sidebar.FoldButton;

    /// <summary>The version at the foot of the menu (<c>Version 1.0.0 · release</c>, SETUP-07): it opens About.</summary>
    public Button VersionButton => Sidebar.VersionButton;

    /// <summary>The page shown right now (tests, self test).</summary>
    public Control? CurrentPage => PageHost.Content as Control;

    public CalendarPage CalendarPage => _calendar;

    /// <summary>The state of MatCloud at the foot of the menu (its words hidden while the menu is folded).</summary>
    public TextBlock CloudText { get; private set; } = null!;

    private MiniMonth MiniMonth { get; set; } = null!;

    private Border MiniMonthCard { get; set; } = null!;

    private DockPanel CalendarsSection { get; set; } = null!;

    private ScrollViewer CalendarsScroller { get; set; } = null!;

    private StackPanel CalendarList { get; set; } = null!;

    private MatIcon CalendarsChevron { get; set; } = null!;

    private Button ReminderStatus { get; set; } = null!;

    private MatIcon ReminderIcon { get; set; } = null!;

    private Ellipse ReminderDot { get; set; } = null!;

    private TextBlock ReminderText { get; set; } = null!;

    private Button CloudStatus { get; set; } = null!;

    private Ellipse CloudDot { get; set; } = null!;

    public void ToggleSidebar() => Sidebar.IsFolded = !Sidebar.IsFolded;

    /// <summary>Tray, second start, Dock, a reminder: bring the window up.</summary>
    public void ShowFromTray()
    {
        BringToFront();
        Core.Platform.WindowActivation.BringToFront(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    public void ShowCalendar(DateTime? day = null, CalendarViewMode? mode = null)
    {
        Navigate(_calendar, _navCalendar, keepHistory: false);
        if (day is not null || mode is not null)
        {
            _calendar.GoTo(day ?? _calendar.Anchor, mode);
        }
    }

    public void ShowEvents(string? search = null)
    {
        Navigate(_events.Value, _navEvents, keepHistory: false);
        if (search is not null)
        {
            SearchBox.Text = search;
        }

        _events.Value.SetSearch(SearchBox.Text ?? "");
    }

    /// <summary>Ctrl/Cmd+F: into the search of the title bar.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ShowAccounts() => Navigate(_accounts.Value, _navAccounts, keepHistory: false);

    public void ShowSettings(string? tab = null)
    {
        Navigate(_settings.Value, _navSettings, keepHistory: false);
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

        Navigate(new EventEditorPage(item.Event), Sidebar.Current, keepHistory: true);
    }

    /// <summary>A new event at <paramref name="start"/> (Ctrl/Cmd+N: the next full hour).</summary>
    public void NewEvent(DateTime? start = null, bool isAllDay = false)
    {
        try
        {
            var begin = start ?? DateTime.Now.Date.AddHours(DateTime.Now.Hour + 1);
            Navigate(new EventEditorPage(CalendarService.NewEvent(begin, isAllDay)), Sidebar.Current, keepHistory: true);
        }
        catch (BusinessException exception)
        {
            _ = Dialogs.ConfirmDialog.ShowMessageAsync(this, "Kein Kalender", exception.Message);
        }
    }

    /// <summary>An account in its editor (or a new ICS subscription / local account: <paramref name="newKind"/>).</summary>
    public void OpenAccount(long? accountId, AccountKind? newKind = null) =>
        Navigate(new AccountEditorPage(accountId, newKind), _navAccounts, keepHistory: true);

    /// <summary>"Zurück" of an editor: the page the user came from.</summary>
    public void GoBack()
    {
        var previous = _back.Count > 0 ? _back.Pop() : _calendar;
        PageHost.Content = previous;
        Sidebar.Select(previous switch
        {
            EventsPage => _navEvents,
            AccountsPage => _navAccounts,
            SettingsPage => _navSettings,
            Pages.CalendarPage => _navCalendar,
            _ => null,
        });
        (previous as IRefreshable)?.Refresh();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Alt+F4 only hides as well: reminders keep coming from the tray / menu bar.
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
            MatNotification.Show("ThixCalendar läuft weiter",
                OperatingSystem.IsMacOS()
                    ? "Erinnerungen kommen weiterhin. Das Fenster öffnest du über das Symbol in der Menüleiste."
                    : "Erinnerungen kommen weiterhin. Das Fenster öffnest du über das Symbol im Infobereich der Taskleiste.",
                isWarning: false, onClick: ShowFromTray);
        }
    }

    private void Navigate(Control page, MatNavItem? nav, bool keepHistory)
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
        Sidebar.Select(nav);
        (page as IRefreshable)?.Refresh();
    }

    private void OnNavInvoked(MatNavItem item)
    {
        if (item == _navEvents)
        {
            ShowEvents();
        }
        else if (item == _navAccounts)
        {
            ShowAccounts();
        }
        else if (item == _navSettings)
        {
            ShowSettings();
        }
        else
        {
            ShowCalendar();
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
            RefreshCloudStatus();
            if (PageHost.Content is IRefreshable page and not (EventEditorPage or AccountEditorPage))
            {
                page.Refresh();
            }
        }, DispatcherPriority.Background);
    }

    // ===== The foot of the menu =====

    /// <summary>"Meine Kalender", the small month, the state of the reminders and of MatCloud – above the version.</summary>
    private void BuildMenuFoot()
    {
        CalendarsChevron = new MatIcon { Icon = "chevron-down", Size = 16 };
        DockPanel.SetDock(CalendarsChevron, Dock.Right);
        var header = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4),
            Content = new DockPanel { Children = { CalendarsChevron, new TextBlock { Text = "Meine Kalender", FontWeight = FontWeight.SemiBold } } },
        }.Themed("MatButton.Ghost");
        header.Click += (_, _) =>
        {
            _calendarsCollapsed = !_calendarsCollapsed;
            CalendarsScroller.IsVisible = !_calendarsCollapsed;
            CalendarsChevron.Icon = _calendarsCollapsed ? "chevron-right" : "chevron-down";
            FitMenuFoot();
        };
        AutomationProperties.SetName(header, "Meine Kalender ein- und ausklappen");
        DockPanel.SetDock(header, Dock.Top);
        CalendarList = new StackPanel { Spacing = 6 };
        CalendarsScroller = new ScrollViewer { Content = CalendarList, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
        CalendarsSection = new DockPanel { Children = { header, CalendarsScroller } };

        MiniMonth = new MiniMonth();
        MiniMonthCard = new Border { Padding = new Thickness(10, 12), Child = MiniMonth }.Themed("MatCard");

        ReminderIcon = new MatIcon { Icon = "bell", Size = 18 };
        ReminderDot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, -2, 0) };
        ReminderText = new TextBlock { FontSize = MatTokens.Type.FsSm, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
        ReminderStatus = StatusButton(new Panel { Children = { ReminderIcon, ReminderDot } }, ReminderText);
        ReminderStatus.Click += async (_, _) =>
        {
            if (ReminderService.Active.Count > 0)
            {
                App.Instance.ShowReminders();
                return;
            }

            await App.Instance.ToggleReminderPauseAsync(this);
        };

        CloudDot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -1) };
        CloudText = new TextBlock { FontSize = MatTokens.Type.FsSm, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
        CloudStatus = StatusButton(new Panel { Children = { new MatIcon { Icon = "cloud", Size = 18 }, CloudDot } }, CloudText);
        CloudStatus.Click += (_, _) => ShowSettings(SettingsPage.CloudTab);

        Sidebar.Foot = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 8),
            Children = { CalendarsSection, MiniMonthCard, new StackPanel { Spacing = 2, Children = { ReminderStatus, CloudStatus } } },
        };
    }

    private static Button StatusButton(Control icon, TextBlock text) => new Button
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(8, 6),
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Children = { icon, text } },
    }.Themed("MatButton.Ghost");

    /// <summary>
    /// The menu's foot shares the height with the sections: "Meine Kalender" scrolls in what is left, and the small month is
    /// there only when it does not push the calendars out (a low window, many calendars).
    /// </summary>
    private void FitMenuFoot()
    {
        if (CalendarsSection is null)
        {
            return;
        }

        var folded = IsSidebarCollapsed;
        CalendarsSection.IsVisible = !folded;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        // title bar 68, the sections and the line ~250, the states ~90, the version ~56, margins ~40
        var room = height - 68 - 250 - 90 - 56 - 40;
        CalendarList.Measure(new Size(MatNav.OpenWidth - 24, double.PositiveInfinity));
        var calendars = _calendarsCollapsed ? 0 : Math.Min(CalendarList.DesiredSize.Height + 6, 6 * 40);
        var showMonth = !folded && room - 40 - calendars >= MiniMonthHeight;
        MiniMonthCard.IsVisible = showMonth;
        CalendarsScroller.MaxHeight = Math.Max(72, room - 40 - (showMonth ? MiniMonthHeight : 0));
    }

    /// <summary>"Meine Kalender": per account its calendars with colour and switch (tinted rows like the mockup).</summary>
    private void RefreshCalendarList()
    {
        CalendarList.Children.Clear();
        var accounts = CalendarService.Accounts();
        var calendars = CalendarService.Calendars();
        if (accounts.Count == 0)
        {
            var add = new Button { Content = "Konto verbinden …", Margin = new Thickness(8, 4) }.Themed("MatButton.Link");
            add.Click += (_, _) => ShowAccounts();
            CalendarList.Children.Add(add);
            FitMenuFoot();
            return;
        }

        foreach (var account in accounts)
        {
            var ofAccount = calendars.Where(calendar => calendar.AccountId == account.Id).ToList();
            if (accounts.Count > 1 || ofAccount.Count > 1 || account.State == AccountState.SignedOut)
            {
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(8, 6, 0, 0) };
                title.Children.Add(new AccountMark { Kind = account.Kind, Width = 13, Height = 13 });
                title.Children.Add(new TextBlock { Text = account.Name, FontSize = MatTokens.Type.FsXs, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 }.Themed("MatText.Muted"));
                if (account.State == AccountState.SignedOut)
                {
                    var warning = new MatIcon { Icon = "alert", Size = 13 };
                    warning.Use(MatIcon.ForegroundProperty, "Mat.Danger");
                    ToolTip.SetTip(warning, account.LastError.Length > 0 ? account.LastError : "Abgemeldet – unter Konten neu anmelden");
                    title.Children.Add(warning);
                }

                CalendarList.Children.Add(title);
            }

            foreach (var calendar in ofAccount)
            {
                CalendarList.Children.Add(CalendarRow(account, calendar));
            }
        }

        FitMenuFoot();
    }

    private static Border CalendarRow(CalendarAccount account, CalendarInfo calendar)
    {
        var toggle = new ToggleSwitch
        {
            IsChecked = calendar.IsVisible,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The switch takes the calendar's colour when on (like the mockup): the colour is data, not a colour of the theme.
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
        var name = new TextBlock { Text = calendar.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        var row = new DockPanel { Children = { dot, toggle, name } };
        if (!calendar.RemindersEnabled)
        {
            var off = new MatIcon { Icon = "bell-off", Size = 13, Margin = new Thickness(6, 0) };
            off.Use(MatIcon.ForegroundProperty, "Mat.TextFaint");
            ToolTip.SetTip(off, "Erinnerungen für diesen Kalender sind aus");
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

    /// <summary>How many reminders wait, or the pause, or the next event.</summary>
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
            dot = "Mat.Warning";
            ReminderIcon.Icon = "bell-off";
        }
        else if (active > 0)
        {
            text = active == 1 ? "1 Erinnerung offen" : $"{active} Erinnerungen offen";
            dot = "Mat.Danger";
            ReminderIcon.Icon = "bell-ring";
        }
        else
        {
            var next = ReminderService.Upcoming(now, 1).FirstOrDefault(item => !item.Event.IsAllDay);
            text = next is null ? "Keine Termine in Sicht" : $"Als Nächstes: {next.Event.StartLocal.ToString(next.Event.StartLocal.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", Ui.German)} {next.Event.Title}";
            ReminderIcon.Icon = "bell";
        }

        ReminderText.Text = text;
        ReminderDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            ReminderDot.Use(Shape.FillProperty, dot);
        }

        ToolTip.SetTip(ReminderStatus, IsSidebarCollapsed ? text : active > 0 ? "Offene Erinnerungen anzeigen" : "Erinnerungen pausieren / fortsetzen");
        AutomationProperties.SetName(ReminderStatus, text);
    }

    /// <summary>The connection to MatCloud: synchronised, offline, signed out – or the way to connect.</summary>
    private void RefreshCloudStatus()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = CloudService.Status;
        (CloudText.Text, var dot) = status.State switch
        {
            CloudState.Online => ($"MatCloud · {cloud.UserName}", "Mat.Success"),
            CloudState.Syncing => ("Synchronisiere …", "Mat.Accent"),
            CloudState.Connecting => ("Verbinde mit MatCloud …", "Mat.Accent"),
            CloudState.Offline => ("MatCloud nicht erreichbar", "Mat.Warning"),
            CloudState.SignedOut => ("MatCloud: neu anmelden", "Mat.Danger"),
            _ => cloud.IsConnected ? ($"MatCloud · {cloud.UserName}", "Mat.Success") : ("Mit MatCloud verbinden", (string?)null),
        };
        CloudDot.IsVisible = dot is not null;
        if (dot is not null)
        {
            CloudDot.Use(Shape.FillProperty, dot);
        }

        var tip = status.LastSync is { } last ? $"{status.Message} (zuletzt {last.ToLocalTime():HH:mm:ss})" : status.Message;
        ToolTip.SetTip(CloudStatus, IsSidebarCollapsed ? $"{CloudText.Text}\n{tip}" : tip);
        AutomationProperties.SetName(CloudStatus, CloudText.Text);
    }

    private void ApplySidebar()
    {
        var folded = IsSidebarCollapsed;
        ReminderText.IsVisible = !folded;
        CloudText.IsVisible = !folded;
        ReminderStatus.HorizontalContentAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        CloudStatus.HorizontalContentAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        // An update waiting: MatNav's link above the version (an icon when folded); a click shows About with the way to install it.
        Sidebar.UpdateVersion = UpdateService.LastResult is { IsUpdateAvailable: true, Manifest: { } manifest } ? manifest.Version : null;
        FitMenuFoot();
        RefreshReminderStatus();
        RefreshCloudStatus();
    }

    private void OnNewEventClick(object? sender, RoutedEventArgs e) => NewEvent(PageHost.Content == _calendar ? _calendar.SuggestedStart() : null);
}

/// <summary>A page that reloads its data when it comes back into view or the data changed.</summary>
public interface IRefreshable
{
    void Refresh();
}
