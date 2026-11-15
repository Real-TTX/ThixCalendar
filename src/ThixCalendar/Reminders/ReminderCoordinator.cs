using Avalonia.Threading;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Platform;

namespace ThixCalendar.Reminders;

/// <summary>
/// Drives the reminders in the app: asks the business layer every 10 seconds what is due (off the UI thread), plays the
/// sound for new reminders and again while they wait (setting), keeps the reminder window and the tray in step, and
/// carries out the answers (join, open, snooze, dismiss). After sleep or a pause the next check catches up.
/// </summary>
internal static class ReminderCoordinator
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private static readonly List<DueReminder> Tests = [];
    private static ReminderWindow? _window;
    private static DateTimeOffset _lastSound = DateTimeOffset.MinValue;
    private static DateTime _lastCleanup = DateTime.MinValue;
    private static bool _isChecking;

    /// <summary>Raised on the UI thread when the waiting reminders changed (tray icon and menu).</summary>
    public static event EventHandler? Changed;

    /// <summary>What waits for an answer, test reminders included.</summary>
    public static IReadOnlyList<DueReminder> Waiting => [.. ReminderService.Active, .. Tests];

    public static void Start()
    {
        Timer.Tick += (_, _) => _ = CheckAsync();
        Timer.Start();
        ReminderService.Changed += (_, _) => Ui.Post(Refresh);
        _ = CheckAsync();
    }

    public static void Stop()
    {
        Timer.Stop();
        _window?.Close();
        _window = null;
    }

    /// <summary>Checks at once (after waking up, after a sync).</summary>
    public static async Task CheckAsync()
    {
        if (_isChecking)
        {
            return;
        }

        _isChecking = true;
        try
        {
            var now = DateTimeOffset.Now;
            var appeared = await Task.Run(() => ReminderService.Check(now));
            var settings = SettingsService.Get();
            if (appeared.Count > 0)
            {
                Log.Info($"{appeared.Count} reminder(s) due");
                PlaySound(now, settings);
            }
            else if (Waiting.Count > 0 && settings.RepeatSoundMinutes > 0 && now - _lastSound >= TimeSpan.FromMinutes(settings.RepeatSoundMinutes))
            {
                PlaySound(now, settings);
            }

            if (DateTime.Today != _lastCleanup)
            {
                _lastCleanup = DateTime.Today;
                await Task.Run(() => ReminderService.Cleanup(now));
            }

            Refresh();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("Reminder check", exception);
        }
        finally
        {
            _isChecking = false;
        }
    }

    /// <summary>Brings the waiting reminders up (tray, sidebar); without any, nothing happens.</summary>
    public static void ShowAll() => Refresh();

    /// <summary>A reminder for an invented event in 10 minutes (Einstellungen › Erinnerungen › Test-Erinnerung).</summary>
    public static void ShowTest()
    {
        var now = DateTimeOffset.Now;
        var calendar = new CalendarInfo { Id = -1, Name = "Test", Color = CalendarColors.Default };
        var account = new CalendarAccount { Id = -1, Kind = AccountKind.Local, Name = "ThixCalendar" };
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).AddMinutes(10);
        var calendarEvent = new CalendarEvent
        {
            Id = -1,
            CalendarId = -1,
            RemoteId = $"test-{Guid.NewGuid():N}",
            Title = "Test-Erinnerung: So sieht eine Erinnerung aus",
            Start = start,
            End = start.AddMinutes(30),
            Location = "Besprechungsraum 1",
            MeetingUrl = "https://teams.microsoft.com/l/meetup-join/test",
        };
        Tests.Add(new DueReminder(new EventItem(calendarEvent, calendar, account), ReminderStage.Before, now));
        PlaySound(now, SettingsService.Get());
        Refresh();
    }

    public static void Dismiss(DueReminder reminder)
    {
        if (!Tests.Remove(reminder))
        {
            ReminderService.Dismiss(reminder);
        }

        Refresh();
    }

    public static void DismissAll()
    {
        Tests.Clear();
        ReminderService.DismissAll();
        Refresh();
    }

    public static void Snooze(DueReminder reminder, DateTimeOffset until)
    {
        if (!Tests.Remove(reminder))
        {
            try
            {
                ReminderService.Snooze(reminder, until, DateTimeOffset.Now);
            }
            catch (BusinessException exception)
            {
                Log.Warning(exception.Message);
            }
        }

        Refresh();
    }

    /// <summary>"Beitreten": opens the meeting and counts as the answer.</summary>
    public static void Join(DueReminder reminder)
    {
        if (reminder.Event.MeetingUrl.Length > 0 && !Tests.Contains(reminder))
        {
            Ui.OpenExternal(reminder.Event.MeetingUrl);
        }

        Dismiss(reminder);
    }

    public static void Open(DueReminder reminder)
    {
        if (Tests.Contains(reminder))
        {
            App.Instance.ShowMainWindow();
            return;
        }

        App.Instance.ShowMainWindow(window => window.OpenEvent(reminder.Event.Id));
    }

    internal static void ResetForTests()
    {
        Tests.Clear();
        _window?.Close();
        _window = null;
    }

    private static void Refresh()
    {
        var waiting = Waiting;
        if (waiting.Count > 0)
        {
            _window ??= new ReminderWindow();
            _window.Show(waiting);
        }
        else
        {
            _window?.Show(waiting);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void PlaySound(DateTimeOffset now, AppSettings settings)
    {
        if (!settings.PlaySound)
        {
            return;
        }

        _lastSound = now;
        Chime.Play();
    }
}
