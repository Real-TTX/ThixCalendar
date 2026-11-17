using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar.Core.Business;

/// <summary>
/// The calendars and their events: what the views and lists show, and every change to an event. A change of an online
/// event goes to its provider first (Microsoft 365, Google); only what the provider accepted is stored. Validation and
/// the permission checks (policy, read-only calendars) live here, whatever the UI offers.
/// </summary>
public static class CalendarService
{
    /// <summary>Raised after accounts, calendars or events changed (any thread).</summary>
    public static event EventHandler? Changed;

    public static IReadOnlyList<CalendarAccount> Accounts() => CalendarStore.LoadAccounts();

    public static IReadOnlyList<CalendarInfo> Calendars() => CalendarStore.LoadCalendars();

    /// <summary>The calendars new events can go into (visible ones first, the primary one of each account first).</summary>
    public static IReadOnlyList<(CalendarInfo Calendar, CalendarAccount Account)> WritableCalendars()
    {
        var accounts = Accounts().ToDictionary(account => account.Id);
        return Calendars()
            .Where(calendar => !calendar.IsReadOnly && accounts.TryGetValue(calendar.AccountId, out var account) && account.IsWritable && account.State != AccountState.SignedOut)
            .Select(calendar => (calendar, accounts[calendar.AccountId]))
            .OrderByDescending(pair => pair.calendar.IsVisible && pair.Item2.IsVisible)
            .ThenBy(pair => pair.Item2.Id)
            .ThenByDescending(pair => pair.calendar.IsPrimary)
            .ThenBy(pair => pair.calendar.SortOrder)
            .ToList();
    }

    /// <summary>The events of the visible calendars that touch [from, to) (local days), for the calendar view.</summary>
    public static IReadOnlyList<EventItem> Range(DateTime from, DateTime to) =>
        Query(new EventQuery { From = from, To = to, Sort = EventSort.StartAscending });

    /// <summary>Events for lists and search: range, words, one calendar, sorted.</summary>
    public static IReadOnlyList<EventItem> Query(EventQuery query)
    {
        var accounts = Accounts().ToDictionary(account => account.Id);
        var calendars = Calendars().ToDictionary(calendar => calendar.Id);
        var words = query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = new List<EventItem>();
        // All-day events are stored at midnight UTC: widen the database window by a day on each side, then filter locally.
        foreach (var calendarEvent in CalendarStore.QueryEvents(CalendarEvent.FromLocal(query.From.Date).AddDays(-1), CalendarEvent.FromLocal(query.To).AddDays(1)))
        {
            if (!calendars.TryGetValue(calendarEvent.CalendarId, out var calendar) || !accounts.TryGetValue(calendar.AccountId, out var account))
            {
                continue;
            }

            if (query.CalendarId is { } only ? calendar.Id != only : !query.IncludeHidden && (!calendar.IsVisible || !account.IsVisible))
            {
                continue;
            }

            if (!(calendarEvent.StartLocal < query.To && (calendarEvent.EndLocal > query.From || (calendarEvent.EndLocal == calendarEvent.StartLocal && calendarEvent.StartLocal >= query.From))))
            {
                continue;
            }

            if (words.Length > 0 && !words.All(word => Matches(calendarEvent, calendar, word)))
            {
                continue;
            }

            items.Add(new EventItem(calendarEvent, calendar, account));
        }

        return query.Sort switch
        {
            EventSort.StartDescending => items.OrderByDescending(item => item.Event.StartLocal).ThenBy(item => item.Event.Title).ToList(),
            EventSort.Title => items.OrderBy(item => item.Event.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Event.StartLocal).ToList(),
            EventSort.Calendar => items.OrderBy(item => item.Calendar.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Event.StartLocal).ToList(),
            _ => items.OrderBy(item => item.Event.StartLocal).ThenByDescending(item => item.Event.IsAllDay).ThenBy(item => item.Event.Title).ToList(),
        };
    }

    public static EventItem? GetEvent(long id)
    {
        if (CalendarStore.LoadEvent(id) is not { } calendarEvent)
        {
            return null;
        }

        var calendar = Calendars().FirstOrDefault(item => item.Id == calendarEvent.CalendarId);
        var account = calendar is null ? null : Accounts().FirstOrDefault(item => item.Id == calendar.AccountId);
        return calendar is null || account is null ? null : new EventItem(calendarEvent, calendar, account);
    }

    /// <summary>A new event to fill in: one hour from <paramref name="start"/> in the preferred writable calendar.</summary>
    public static CalendarEvent NewEvent(DateTime start, bool isAllDay = false, long? calendarId = null)
    {
        var calendar = calendarId ?? WritableCalendars().Select(pair => (long?)pair.Calendar.Id).FirstOrDefault() ??
            throw new BusinessException("Es gibt keinen Kalender, in den ein Termin eingetragen werden kann. Lege unter „Konten“ einen an.");
        return isAllDay
            ? new CalendarEvent { CalendarId = calendar, IsAllDay = true, Start = CalendarEvent.AllDayDate(start.Date), End = CalendarEvent.AllDayDate(start.Date.AddDays(1)) }
            : new CalendarEvent { CalendarId = calendar, Start = CalendarEvent.FromLocal(start), End = CalendarEvent.FromLocal(start.AddHours(1)) };
    }

    /// <summary>
    /// Creates or changes an event (Id 0 = new). Online calendars get it first; moving it to another calendar creates it
    /// there and removes it from the old one. Returns the stored event.
    /// </summary>
    public static async Task<CalendarEvent> SaveEventAsync(CalendarEvent draft, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SaveEventCoreAsync(draft, cancellationToken);
        }
        catch (ProviderException exception)
        {
            throw AccountService.ToBusinessException(draft.CalendarId, exception);
        }
    }

    private static async Task<CalendarEvent> SaveEventCoreAsync(CalendarEvent draft, CancellationToken cancellationToken)
    {
        var calendarEvent = Normalize(draft);
        Validate(calendarEvent);
        var (calendar, account) = WritableCalendar(calendarEvent.CalendarId);
        var previous = calendarEvent.Id == 0 ? null : CalendarStore.LoadEvent(calendarEvent.Id) ?? throw new BusinessException("Diesen Termin gibt es nicht mehr.");
        if (previous is not null)
        {
            EnsureWritable(previous.CalendarId);
        }

        CalendarEvent stored;
        if (previous is null || previous.CalendarId != calendarEvent.CalendarId)
        {
            var created = await CreateAtProviderAsync(account, calendar, calendarEvent with { Id = 0, RemoteId = "" }, cancellationToken);
            var id = CalendarStore.InsertEvent(created with { Id = 0, CalendarId = calendar.Id });
            stored = created with { Id = id, CalendarId = calendar.Id };
            if (previous is not null)
            {
                await DeleteAtProviderAsync(previous, cancellationToken);
                CalendarStore.DeleteEvent(previous.Id);
            }
        }
        else
        {
            var provider = AccountService.CreateProvider(account);
            var updated = provider is null ? calendarEvent : await provider.UpdateEventAsync(calendar, previous, calendarEvent, cancellationToken)
                .ConfigureAwait(false);
            stored = Merge(calendarEvent, updated) with { Id = previous.Id, CalendarId = calendar.Id, RemoteId = previous.RemoteId };
            CalendarStore.UpdateEvent(stored);
        }

        Log.Info($"Event {stored.Id} saved in calendar {calendar.Id}");
        RaiseChanged();
        return stored;
    }

    public static async Task DeleteEventAsync(long id, CancellationToken cancellationToken = default)
    {
        var calendarEvent = CalendarStore.LoadEvent(id) ?? throw new BusinessException("Diesen Termin gibt es nicht mehr.");
        EnsureWritable(calendarEvent.CalendarId);
        try
        {
            await DeleteAtProviderAsync(calendarEvent, cancellationToken);
        }
        catch (ProviderException exception)
        {
            throw AccountService.ToBusinessException(calendarEvent.CalendarId, exception);
        }

        CalendarStore.DeleteEvent(id);
        Log.Info($"Event {id} deleted");
        RaiseChanged();
    }

    /// <summary>Shows or hides one calendar (an everyday switch: no configuration lock).</summary>
    public static void SetCalendarVisible(long calendarId, bool isVisible)
    {
        var calendar = Calendars().FirstOrDefault(item => item.Id == calendarId) ?? throw new BusinessException("Diesen Kalender gibt es nicht mehr.");
        CalendarStore.UpdateCalendar(calendar with { IsVisible = isVisible });
        RaiseChanged();
    }

    /// <summary>Shows or hides every calendar of an account (the chips above the calendar).</summary>
    public static void SetAccountVisible(long accountId, bool isVisible)
    {
        var account = Accounts().FirstOrDefault(item => item.Id == accountId) ?? throw new BusinessException("Dieses Konto gibt es nicht mehr.");
        CalendarStore.UpdateAccount(account with { IsVisible = isVisible });
        RaiseChanged();
    }

    /// <summary>Name (local calendars only: online ones are named at their provider), colour and reminders of a calendar.</summary>
    public static CalendarInfo UpdateCalendar(long calendarId, string name, string color, bool remindersEnabled)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var calendar = Calendars().FirstOrDefault(item => item.Id == calendarId) ?? throw new BusinessException("Diesen Kalender gibt es nicht mehr.");
        var account = Accounts().First(item => item.Id == calendar.AccountId);
        var normalizedColor = CalendarColors.Normalize(color) ?? throw new BusinessException("Diese Farbe gibt es nicht.", nameof(CalendarInfo.Color));
        var newName = name.Trim();
        if (account.Kind == AccountKind.Local && newName.Length == 0)
        {
            throw new BusinessException("Bitte gib dem Kalender einen Namen.", nameof(CalendarInfo.Name));
        }

        var updated = calendar with
        {
            Name = account.Kind == AccountKind.Local ? newName : calendar.Name,
            Color = normalizedColor,
            IsColorCustom = calendar.IsColorCustom || !string.Equals(calendar.Color, normalizedColor, StringComparison.OrdinalIgnoreCase),
            RemindersEnabled = remindersEnabled,
        };
        CalendarStore.UpdateCalendar(updated);
        RaiseChanged();
        return updated;
    }

    internal static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    private static bool Matches(CalendarEvent calendarEvent, CalendarInfo calendar, string word) =>
        calendarEvent.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
        calendarEvent.Location.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
        calendarEvent.Description.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
        calendarEvent.Organizer.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
        calendar.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase);

    private static CalendarEvent Normalize(CalendarEvent calendarEvent)
    {
        var normalized = calendarEvent with
        {
            Title = calendarEvent.Title.Trim(),
            Location = calendarEvent.Location.Trim(),
            Description = calendarEvent.Description.TrimEnd(),
            MeetingUrl = calendarEvent.MeetingUrl.Trim(),
        };
        if (normalized.IsAllDay)
        {
            normalized = normalized with
            {
                Start = CalendarEvent.AllDayDate(normalized.Start.UtcDateTime.Date),
                End = CalendarEvent.AllDayDate(normalized.End.UtcDateTime.Date),
            };
        }

        return normalized.MeetingUrl.Length > 0 ? normalized : normalized with { MeetingUrl = MeetingLinks.Find(normalized.Location, normalized.Description) };
    }

    private static void Validate(CalendarEvent calendarEvent)
    {
        if (calendarEvent.Title.Length == 0)
        {
            throw new BusinessException("Bitte gib einen Titel ein.", nameof(CalendarEvent.Title));
        }

        if (calendarEvent.Title.Length > 255)
        {
            throw new BusinessException("Der Titel ist zu lang (höchstens 255 Zeichen).", nameof(CalendarEvent.Title));
        }

        if (calendarEvent.IsAllDay ? calendarEvent.End <= calendarEvent.Start : calendarEvent.End < calendarEvent.Start)
        {
            throw new BusinessException(calendarEvent.IsAllDay ? "Der letzte Tag liegt vor dem ersten." : "Das Ende liegt vor dem Beginn.", nameof(CalendarEvent.End));
        }

        if (calendarEvent.End - calendarEvent.Start > TimeSpan.FromDays(366))
        {
            throw new BusinessException("Ein Termin dauert höchstens ein Jahr.", nameof(CalendarEvent.End));
        }

        if (calendarEvent.ReminderMinutes is < 0 or > 40320)
        {
            throw new BusinessException("Die Erinnerung muss zwischen 0 Minuten und 4 Wochen vorher liegen.", nameof(CalendarEvent.ReminderMinutes));
        }

        if (calendarEvent.MeetingUrl.Length > 0 && !(Uri.TryCreate(calendarEvent.MeetingUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
        {
            throw new BusinessException("Der Besprechungslink muss eine Webadresse sein (https://…).", nameof(CalendarEvent.MeetingUrl));
        }
    }

    /// <summary>Permission check: the calendar exists, belongs to a writable account that is signed in.</summary>
    private static (CalendarInfo Calendar, CalendarAccount Account) WritableCalendar(long calendarId)
    {
        var calendar = Calendars().FirstOrDefault(item => item.Id == calendarId) ??
                       throw new BusinessException("Bitte wähle einen Kalender.", nameof(CalendarEvent.CalendarId));
        var account = Accounts().FirstOrDefault(item => item.Id == calendar.AccountId) ??
                      throw new BusinessException("Bitte wähle einen Kalender.", nameof(CalendarEvent.CalendarId));
        if (calendar.IsReadOnly || !account.IsWritable)
        {
            throw new BusinessException($"„{calendar.Name}“ kann nur gelesen werden.", nameof(CalendarEvent.CalendarId));
        }

        if (account.State == AccountState.SignedOut)
        {
            throw new BusinessException($"Das Konto „{account.Name}“ ist abgemeldet – bitte unter „Konten“ neu anmelden.", nameof(CalendarEvent.CalendarId));
        }

        if (account.IsOnline)
        {
            AccessPolicy.EnsureOnlineAccountsAllowed();
        }

        return (calendar, account);
    }

    private static void EnsureWritable(long calendarId) => WritableCalendar(calendarId);

    private static async Task<CalendarEvent> CreateAtProviderAsync(CalendarAccount account, CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var provider = AccountService.CreateProvider(account);
        if (provider is null)
        {
            return calendarEvent with { RemoteId = CalendarStore.NewKey() }; // an own calendar: the event gets its key here
        }

        var created = await provider.CreateEventAsync(calendar, calendarEvent, cancellationToken).ConfigureAwait(false);
        return Merge(calendarEvent, created) with { RemoteId = created.RemoteId };
    }

    private static async Task DeleteAtProviderAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var (calendar, account) = WritableCalendar(calendarEvent.CalendarId);
        if (AccountService.CreateProvider(account) is { } provider && calendarEvent.RemoteId.Length > 0)
        {
            await provider.DeleteEventAsync(calendar, calendarEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What the provider answered, with the user's own values where the answer has none (e.g. the meeting link).</summary>
    private static CalendarEvent Merge(CalendarEvent sent, CalendarEvent answer) => answer with
    {
        MeetingUrl = answer.MeetingUrl.Length > 0 ? answer.MeetingUrl : sent.MeetingUrl,
        Description = answer.Description.Length > 0 ? answer.Description : sent.Description,
        ReminderMinutes = sent.ReminderMinutes,
    };
}
