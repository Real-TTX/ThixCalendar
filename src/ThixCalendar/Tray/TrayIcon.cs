using Avalonia.Controls;
using Avalonia.Platform;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Reminders;
using AvaloniaTrayIcon = Avalonia.Controls.TrayIcon;

namespace ThixCalendar.Tray;

/// <summary>
/// The icon in the notification area (Windows) or the menu bar (macOS) – ThixCalendar's home while the window is closed.
/// Waiting reminders put a red badge on it; the tool tip names the next event. Menu: open, today's next events, open
/// reminders, new event, pause reminders, sync, settings, update, quit. Windows: a click opens the window, the right
/// button the menu. macOS: the glyph is a template image (the menu bar colours it), every click opens the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int UpcomingCount = 6;

    private readonly AvaloniaTrayIcon _trayIcon;
    private readonly NativeMenu _menu = new();
    private readonly App _app;
    private readonly WindowIcon _normalIcon;
    private readonly WindowIcon _alertIcon;
    private readonly WindowIcon _pausedIcon;
    private readonly Avalonia.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _updateAvailable;

    public TrayIcon(App app)
    {
        _app = app;
        var asset = new Uri(OperatingSystem.IsMacOS() ? "avares://ThixCalendar/Assets/tray-macos.png" : "avares://ThixCalendar/Assets/tray-windows.ico");
        _normalIcon = new WindowIcon(AssetLoader.Open(asset));
        _alertIcon = TrayIconImages.WithBadge(asset, isTemplate: OperatingSystem.IsMacOS(), TrayBadge.Alert) ?? _normalIcon;
        _pausedIcon = TrayIconImages.WithBadge(asset, isTemplate: OperatingSystem.IsMacOS(), TrayBadge.Paused) ?? _normalIcon;
        _trayIcon = new AvaloniaTrayIcon { Icon = _normalIcon, ToolTipText = "ThixCalendar", Menu = _menu, IsVisible = true };
        if (OperatingSystem.IsMacOS())
        {
            MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
        }

        _trayIcon.Clicked += (_, _) =>
        {
            if (ReminderCoordinator.Waiting.Count > 0)
            {
                _app.ShowReminders();
            }

            _app.ShowMainWindow();
        };
        _menu.Opening += (_, _) => BuildMenu();
        AvaloniaTrayIcon.SetIcons(app, [_trayIcon]);
        ReminderCoordinator.Changed += OnChanged;
        SettingsService.Changed += OnSettingsChanged;
        CalendarService.Changed += OnChanged;
        _clock.Tick += (_, _) => Refresh();
        _clock.Start();
        Refresh();
        BuildMenu();
    }

    public void SetUpdateAvailable(bool available)
    {
        _updateAvailable = available;
        BuildMenu();
    }

    public void ShowNotification(string title, string text, bool isWarning = false) =>
        NotificationToast.Show(title, text, isWarning, () => _app.ShowMainWindow());

    public void Dispose()
    {
        _clock.Stop();
        ReminderCoordinator.Changed -= OnChanged;
        SettingsService.Changed -= OnSettingsChanged;
        CalendarService.Changed -= OnChanged;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        Refresh();
        BuildMenu();
    });

    private void OnSettingsChanged(object? sender, AppSettings e) => OnChanged(sender, EventArgs.Empty);

    /// <summary>Badge and tool tip: waiting reminders, a pause, or the next event.</summary>
    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        var waiting = ReminderCoordinator.Waiting.Count;
        var paused = SettingsService.Get().RemindersPausedUntil is { } until && until > now ? until : (DateTimeOffset?)null;
        _trayIcon.Icon = waiting > 0 ? _alertIcon : paused is not null ? _pausedIcon : _normalIcon;
        string tip;
        if (waiting > 0)
        {
            tip = waiting == 1 ? "ThixCalendar – 1 offene Erinnerung" : $"ThixCalendar – {waiting} offene Erinnerungen";
        }
        else if (paused is { } end)
        {
            tip = $"ThixCalendar – Erinnerungen pausiert bis {end.ToLocalTime():HH:mm}";
        }
        else
        {
            var next = ReminderService.Upcoming(now, 1).FirstOrDefault(item => !item.Event.IsAllDay);
            tip = next is null ? "ThixCalendar – keine Termine in Sicht" : $"ThixCalendar – {next.Event.StartLocal:HH:mm} {Shorten(next.Event.Title, 40)} ({Ui.Relative(next.Event, now)})";
        }

        _trayIcon.ToolTipText = tip;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var now = DateTimeOffset.Now;
        _menu.Items.Add(Item("ThixCalendar öffnen", () => _app.ShowMainWindow()));
        var waiting = ReminderCoordinator.Waiting.Count;
        if (waiting > 0)
        {
            _menu.Items.Add(Item(waiting == 1 ? "1 offene Erinnerung anzeigen" : $"{waiting} offene Erinnerungen anzeigen", _app.ShowReminders));
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        var upcoming = ReminderService.Upcoming(now, UpcomingCount);
        _menu.Items.Add(new NativeMenuItem("Als Nächstes") { IsEnabled = false });
        if (upcoming.Count == 0)
        {
            _menu.Items.Add(new NativeMenuItem("Keine Termine in den nächsten 7 Tagen") { IsEnabled = false });
        }

        foreach (var item in upcoming)
        {
            var calendarEvent = item.Event;
            var day = calendarEvent.StartLocal.Date == DateTime.Today ? "" : calendarEvent.StartLocal.ToString("ddd ", Ui.German);
            var time = calendarEvent.IsAllDay ? "ganztägig" : calendarEvent.StartLocal.ToString("HH:mm");
            var id = calendarEvent.Id;
            _menu.Items.Add(Item($"{day}{time}\t{Shorten(calendarEvent.Title, 44)}", () => _app.ShowMainWindow(window => window.OpenEvent(id))));
            if (calendarEvent.MeetingUrl.Length > 0 && !calendarEvent.IsAllDay && calendarEvent.Start - now < TimeSpan.FromMinutes(30) && calendarEvent.End > now)
            {
                var url = calendarEvent.MeetingUrl;
                _menu.Items.Add(Item($"      ↳ {Core.Providers.MeetingLinks.ServiceName(url)} beitreten", () => Ui.OpenExternal(url)));
            }
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item("Neuer Termin …", () => _app.ShowMainWindow(window => window.NewEvent())));
        _menu.Items.Add(ReminderService.IsPaused(now)
            ? Item($"Erinnerungen fortsetzen (pausiert bis {SettingsService.Get().RemindersPausedUntil!.Value.ToLocalTime():HH:mm})", () => _ = _app.ToggleReminderPauseAsync())
            : Item("Erinnerungen pausieren …", () => _ = _app.ToggleReminderPauseAsync()));
        if (CalendarService.Accounts().Any(account => account.Kind != AccountKind.Local))
        {
            _menu.Items.Add(Item("Jetzt synchronisieren", SyncService.Kick));
        }

        _menu.Items.Add(Item("Einstellungen …", () => _app.ShowMainWindow(window => window.ShowSettings())));
        if (_updateAvailable)
        {
            _menu.Items.Add(Item("Update installieren …", () => _app.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.UpdatesTab))));
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item("Beenden", () => _app.ExitApplication()));
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static NativeMenuItem Item(string header, Action action) => new(header) { Command = new RelayCommand(action) };

    /// <summary>Menu items run their action through a command (works on Windows and in the macOS menu bar).</summary>
    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Ui.Post(action);
    }
}
