using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Business;

/// <summary>Which reminder of an event is due.</summary>
public enum ReminderStage
{
    /// <summary>The lead time before the start ("in 10 Min.").</summary>
    Before = 0,

    /// <summary>The event begins now (or began while nobody was there).</summary>
    Start = 1,

    /// <summary>An all-day event: once on its first day at the chosen time.</summary>
    AllDay = 2,
}

/// <summary>A reminder that waits for an answer.</summary>
public sealed record DueReminder(EventItem Item, ReminderStage Stage, DateTimeOffset Trigger)
{
    public CalendarEvent Event => Item.Event;

    public string Key => Item.Event.OccurrenceKey;

    /// <summary>The event has begun (the reminder says "läuft seit …" and the start alert may come up).</summary>
    public bool HasStarted(DateTimeOffset now) => !Event.IsAllDay && now >= Event.Start;
}

/// <summary>
/// The reminders – the reason ThixCalendar exists: nothing may be missed.
///  - Every event of a calendar with reminders gets one before its start (its own lead time, else its calendar's, else
///    the setting) and – unless switched off – a second one when it begins. All-day events get one on their first day.
///  - A reminder stays until the user answers it (Schließen, Später); it is never just gone after a few seconds.
///  - Answers are stored per occurrence: a restart, a sync or a moved series does not bring a closed reminder back,
///    but the start reminder still comes after the lead-time reminder was closed.
///  - Missed while the computer slept or was off: shown when it wakes up, as long as the event still runs (up to
///    <see cref="AppSettings.MissedReminderMinutes"/> after its start).
///  - Declined invitations and cancelled events remind nobody (setting); a pause ("Nicht stören") holds reminders back,
///    they come when it ends.
/// The app asks <see cref="Check"/> every few seconds; it shows the reminders and plays the sound.
/// </summary>
public static class ReminderService
{
    /// <summary>Events further ahead than this cannot have a due reminder (longest lead time offered: 4 weeks).</summary>
    private static readonly TimeSpan LookAhead = TimeSpan.FromDays(29);

    private static readonly object Gate = new();
    private static readonly HashSet<string> Shown = [];
    private static List<DueReminder> _active = [];

    /// <summary>Raised when the list of waiting reminders changed (any thread).</summary>
    public static event EventHandler? Changed;

    /// <summary>The reminders waiting for an answer, earliest event first.</summary>
    public static IReadOnlyList<DueReminder> Active
    {
        get
        {
            lock (Gate)
            {
                return _active;
            }
        }
    }

    public static bool IsPaused(DateTimeOffset now) => SettingsService.Get().RemindersPausedUntil is { } until && until > now;

    /// <summary>
    /// Works out what is due now. Returns the reminders that appeared since the last check (the app plays the sound for
    /// them and brings the reminder window up).
    /// </summary>
    public static IReadOnlyList<DueReminder> Check(DateTimeOffset now)
    {
        var settings = SettingsService.Get();
        var items = settings.RemindersPausedUntil is { } until && until > now
            ? []
            : CalendarService.Query(new EventQuery
            {
                From = now.LocalDateTime.Date.AddDays(-2),
                To = (now + LookAhead).LocalDateTime,
                IncludeHidden = true, // a hidden calendar still reminds; only its reminder switch silences it
            });
        var states = CalendarStore.LoadReminderStates();

        List<DueReminder> appeared;
        bool changed;
        lock (Gate)
        {
            var due = ComputeDue(now, items, settings, states, Shown.Contains);
            appeared = due.Where(reminder => !_active.Any(active => active.Key == reminder.Key && active.Stage == reminder.Stage)).ToList();
            changed = appeared.Count > 0 || due.Count != _active.Count;
            _active = due;
            foreach (var reminder in due)
            {
                Shown.Add(reminder.Key);
            }
        }

        if (changed)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }

        return appeared;
    }

    /// <summary>"Schließen": this reminder (and every earlier stage) is answered; a later stage (the start) still comes.</summary>
    public static void Dismiss(DueReminder reminder)
    {
        var states = CalendarStore.LoadReminderStates();
        var state = states.GetValueOrDefault(reminder.Key) ?? new ReminderState(reminder.Key, null, null, null);
        CalendarStore.SaveReminderState(state with { DismissedTrigger = reminder.Trigger, SnoozedUntil = null, SnoozedAt = null });
        Remove(reminder.Key);
    }

    /// <summary>"Alle schließen".</summary>
    public static void DismissAll()
    {
        foreach (var reminder in Active)
        {
            Dismiss(reminder);
        }
    }

    /// <summary>"Später": back at <paramref name="until"/> – or earlier, when the next stage (the start) falls due first.</summary>
    public static void Snooze(DueReminder reminder, DateTimeOffset until, DateTimeOffset now)
    {
        if (until <= now)
        {
            throw new BusinessException("Die Erinnerung kann nur auf später verschoben werden.");
        }

        var states = CalendarStore.LoadReminderStates();
        var state = states.GetValueOrDefault(reminder.Key) ?? new ReminderState(reminder.Key, null, null, null);
        CalendarStore.SaveReminderState(state with { SnoozedUntil = until, SnoozedAt = now });
        Remove(reminder.Key);
    }

    /// <summary>The "Später" choices for a reminder: a few minutes, and "bis zum Beginn" while the event has not begun.</summary>
    public static IReadOnlyList<(string Label, DateTimeOffset Until)> SnoozeChoices(DueReminder reminder, DateTimeOffset now)
    {
        var choices = new List<(string, DateTimeOffset)>
        {
            ("1 Minute", now.AddMinutes(1)),
            ("5 Minuten", now.AddMinutes(5)),
            ("10 Minuten", now.AddMinutes(10)),
            ("15 Minuten", now.AddMinutes(15)),
            ("30 Minuten", now.AddMinutes(30)),
            ("1 Stunde", now.AddHours(1)),
        };
        if (!reminder.Event.IsAllDay && reminder.Event.Start > now.AddMinutes(1))
        {
            choices.Insert(0, ("Bis zum Beginn", reminder.Event.Start));
            choices.Insert(1, ("5 Min. vor Beginn", reminder.Event.Start.AddMinutes(-5)));
            choices.RemoveAll(choice => choice.Item2 <= now);
        }

        if (reminder.Event.IsAllDay)
        {
            choices.Add(("Morgen früh", new DateTimeOffset(now.LocalDateTime.Date.AddDays(1).AddHours(8))));
        }

        return choices;
    }

    /// <summary>The next events that have not ended (tray tool tip, tray menu): today and the following days.</summary>
    public static IReadOnlyList<EventItem> Upcoming(DateTimeOffset now, int count) =>
        CalendarService.Query(new EventQuery { From = now.LocalDateTime, To = now.LocalDateTime.Date.AddDays(7) })
            .Where(item => !item.Event.IsCancelled && !item.Event.IsDeclined && (item.Event.IsAllDay ? item.Event.EndLocal > now.LocalDateTime : item.Event.End > now))
            .OrderBy(item => item.Event.IsAllDay ? item.Event.StartLocal.Date.AddHours(23.9) : item.Event.StartLocal)
            .Take(count)
            .ToList();

    /// <summary>Forgets answers to reminders of events long past (once a day).</summary>
    public static void Cleanup(DateTimeOffset now) => CalendarStore.PurgeReminderStates(now.AddDays(-45));

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Shown.Clear();
            _active = [];
        }
    }

    /// <summary>
    /// The pure rule (tests): which reminders are due at <paramref name="now"/> for these events, settings and answers.
    /// <paramref name="wasShown"/> tells which occurrences this run of the app already showed: those stay until they are
    /// answered or the event is over, a reminder missed long ago (computer off) is not dug out.
    /// </summary>
    internal static List<DueReminder> ComputeDue(DateTimeOffset now, IEnumerable<EventItem> items, AppSettings settings,
        IReadOnlyDictionary<string, ReminderState> states, Func<string, bool> wasShown)
    {
        var due = new List<DueReminder>();
        var missedWindow = TimeSpan.FromMinutes(settings.MissedReminderMinutes);
        foreach (var item in items)
        {
            var calendarEvent = item.Event;
            if (!item.Calendar.RemindersEnabled || calendarEvent.IsCancelled || (settings.SkipDeclined && calendarEvent.IsDeclined))
            {
                continue;
            }

            if (LatestStage(calendarEvent, item.Calendar, settings, now) is not { } stage)
            {
                continue;
            }

            // Over: the event has ended (an event without length: the missed window after its start).
            var end = calendarEvent.IsAllDay ? CalendarEvent.FromLocal(calendarEvent.EndLocal) : calendarEvent.End;
            var expires = end > calendarEvent.Start || calendarEvent.IsAllDay ? end : calendarEvent.Start + missedWindow;
            if (now >= expires)
            {
                continue;
            }

            if (now - stage.Trigger > missedWindow && !wasShown(calendarEvent.OccurrenceKey))
            {
                continue;
            }

            if (states.TryGetValue(calendarEvent.OccurrenceKey, out var state))
            {
                if (state.DismissedTrigger is { } dismissed && dismissed >= stage.Trigger)
                {
                    continue;
                }

                var snoozedForThisStage = state.SnoozedAt is { } snoozedAt && snoozedAt >= stage.Trigger;
                if (snoozedForThisStage && state.SnoozedUntil is { } snoozedUntil && now < snoozedUntil)
                {
                    continue;
                }
            }

            due.Add(new DueReminder(item, stage.Stage, stage.Trigger));
        }

        return due.OrderBy(reminder => reminder.Event.IsAllDay ? 1 : 0).ThenBy(reminder => reminder.Event.Start).ThenBy(reminder => reminder.Event.Title).ToList();
    }

    /// <summary>The lead time that applies to an event: its own, its calendar's, the setting.</summary>
    public static int LeadMinutes(CalendarEvent calendarEvent, CalendarInfo calendar, AppSettings settings) =>
        calendarEvent.ReminderMinutes ?? calendar.DefaultReminderMinutes ?? settings.DefaultReminderMinutes;

    /// <summary>The latest stage whose time has come, or null.</summary>
    private static (ReminderStage Stage, DateTimeOffset Trigger)? LatestStage(CalendarEvent calendarEvent, CalendarInfo calendar, AppSettings settings, DateTimeOffset now)
    {
        if (calendarEvent.IsAllDay)
        {
            if (settings.AllDayReminderMinuteOfDay is not { } minuteOfDay)
            {
                return null;
            }

            var trigger = CalendarEvent.FromLocal(calendarEvent.StartLocal.Date.AddMinutes(minuteOfDay));
            return trigger <= now ? (ReminderStage.AllDay, trigger) : null;
        }

        if (settings.RemindAtStart || LeadMinutes(calendarEvent, calendar, settings) <= 0)
        {
            if (calendarEvent.Start <= now)
            {
                return (ReminderStage.Start, calendarEvent.Start);
            }
        }

        var lead = LeadMinutes(calendarEvent, calendar, settings);
        if (lead > 0)
        {
            var trigger = calendarEvent.Start.AddMinutes(-lead);
            if (trigger <= now)
            {
                return (ReminderStage.Before, trigger);
            }
        }

        return null;
    }

    private static void Remove(string key)
    {
        lock (Gate)
        {
            _active = _active.Where(reminder => reminder.Key != key).ToList();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
