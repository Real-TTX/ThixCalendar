namespace ThixCalendar.Core.Models;

/// <summary>Where the calendars of an account come from.</summary>
public enum AccountKind
{
    /// <summary>Calendars that only live on this computer.</summary>
    Local = 0,

    /// <summary>Microsoft 365 / Exchange Online / Outlook.com through Microsoft Graph.</summary>
    Microsoft = 1,

    /// <summary>Google Calendar (private Gmail or Google Workspace).</summary>
    Google = 2,

    /// <summary>A subscribed iCalendar address (read-only): Google's secret address, a published Outlook calendar, holidays …</summary>
    Ics = 3,
}

/// <summary>How the last synchronisation of an account went.</summary>
public enum AccountState
{
    /// <summary>Never synchronised yet.</summary>
    New = 0,
    Ok = 1,

    /// <summary>The last attempt failed (network, server); the next one tries again.</summary>
    Error = 2,

    /// <summary>The sign-in is no longer valid: the user has to sign in again (AccountService.ReconnectAsync).</summary>
    SignedOut = 3,
}

/// <summary>An account with one or more calendars. Its refresh token is stored protected (TokenStore), never in here.</summary>
public sealed record CalendarAccount
{
    public long Id { get; init; }

    public AccountKind Kind { get; init; }

    /// <summary>The name the user gave it, e.g. "Arbeit".</summary>
    public string Name { get; init; } = "";

    /// <summary>The e-mail address of the account (online accounts) – shown, never used to sign in.</summary>
    public string UserName { get; init; } = "";

    /// <summary>The subscribed address (ICS accounts only).</summary>
    public string Url { get; init; } = "";

    /// <summary>The refresh token, protected for this user (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = "";

    public AccountState State { get; init; }

    public string LastError { get; init; } = "";

    public DateTimeOffset? LastSync { get; init; }

    /// <summary>Hides every calendar of the account at once (the filter above the calendar, the sidebar).</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>The key every computer of the user knows this account by (MatCloud); made when the account is added.</summary>
    public string SyncId { get; init; } = "";

    public DateTimeOffset CreateDate { get; init; }

    public DateTimeOffset UpdateDate { get; init; }

    public bool IsOnline => Kind is AccountKind.Microsoft or AccountKind.Google;

    /// <summary>Events can be created and changed (not in subscribed ICS calendars).</summary>
    public bool IsWritable => Kind is not AccountKind.Ics;

    public string KindName => KindDisplayName(Kind);

    public static string KindDisplayName(AccountKind kind) => kind switch
    {
        AccountKind.Microsoft => "Microsoft 365",
        AccountKind.Google => "Google Kalender",
        AccountKind.Ics => "ICS-Abo",
        _ => "Auf diesem Computer",
    };
}

/// <summary>One calendar of an account ("Kalender", "Privat", "Feiertage" …).</summary>
public sealed record CalendarInfo
{
    public long Id { get; init; }

    public long AccountId { get; init; }

    /// <summary>The key of the calendar at its provider (empty for the one calendar of an ICS address or a local one).</summary>
    public string RemoteId { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>"#RRGGBB".</summary>
    public string Color { get; init; } = CalendarColors.Default;

    /// <summary>The user chose the colour: it wins over the provider's on the next sync.</summary>
    public bool IsColorCustom { get; init; }

    public bool IsVisible { get; init; } = true;

    /// <summary>Reminders for the events of this calendar (off for e.g. holidays or birthdays).</summary>
    public bool RemindersEnabled { get; init; } = true;

    public bool IsReadOnly { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>The provider's default reminder (Google: the calendar's default) for events that name none.</summary>
    public int? DefaultReminderMinutes { get; init; }

    public int SortOrder { get; init; }

    public DateTimeOffset CreateDate { get; init; }

    public DateTimeOffset UpdateDate { get; init; }
}

public enum EventStatus
{
    Confirmed = 0,
    Tentative = 1,
    Cancelled = 2,
}

/// <summary>The user's own answer to an invitation.</summary>
public enum ResponseStatus
{
    None = 0,
    Organizer = 1,
    Accepted = 2,
    Tentative = 3,
    Declined = 4,
    NotResponded = 5,
}

/// <summary>
/// One occurrence of an event (recurring events arrive expanded: every occurrence is its own row with its own key).
/// Times are absolute (<see cref="Start"/>/<see cref="End"/>); an all-day event keeps its dates at midnight UTC and is
/// shown on those dates wherever the computer is (<see cref="StartLocal"/>).
/// </summary>
public sealed record CalendarEvent
{
    public long Id { get; init; }

    public long CalendarId { get; init; }

    /// <summary>The key at the provider (an occurrence of a series has its own key); events on this computer get one of their own.</summary>
    public string RemoteId { get; init; } = "";

    public string Title { get; init; } = "";

    public DateTimeOffset Start { get; init; }

    /// <summary>Exclusive end; all-day: midnight after the last day.</summary>
    public DateTimeOffset End { get; init; }

    public bool IsAllDay { get; init; }

    public string Location { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>Teams / Meet / Zoom / Webex address to join (from the provider or found in location and notes).</summary>
    public string MeetingUrl { get; init; } = "";

    /// <summary>The event in the provider's web app.</summary>
    public string WebLink { get; init; } = "";

    public string Organizer { get; init; } = "";

    /// <summary>Minutes before the start as the event asks; null = its calendar's or ThixCalendar's default lead time.</summary>
    public int? ReminderMinutes { get; init; }

    public EventStatus Status { get; init; }

    public ResponseStatus Response { get; init; }

    public bool IsRecurring { get; init; }

    public DateTimeOffset CreateDate { get; init; }

    public DateTimeOffset UpdateDate { get; init; }

    /// <summary>Start in the local wall clock (all-day: the date itself).</summary>
    public DateTime StartLocal => IsAllDay ? DateTime.SpecifyKind(Start.UtcDateTime.Date, DateTimeKind.Local) : Start.ToLocalTime().DateTime;

    /// <summary>End in the local wall clock (all-day: the day after the last day).</summary>
    public DateTime EndLocal => IsAllDay ? DateTime.SpecifyKind(End.UtcDateTime.Date, DateTimeKind.Local) : End.ToLocalTime().DateTime;

    /// <summary>Identifies this occurrence for the reminder state: survives a full re-sync (new row ids).</summary>
    public string OccurrenceKey => $"{CalendarId}:{(RemoteId.Length > 0 ? RemoteId : "local-" + Id)}:{Start.UtcTicks}";

    public bool IsDeclined => Response == ResponseStatus.Declined;

    public bool IsCancelled => Status == EventStatus.Cancelled;

    /// <summary>A date of an all-day event, stored as the providers send it: midnight UTC.</summary>
    public static DateTimeOffset AllDayDate(DateTime date) => new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A local wall-clock time as an absolute time (daylight saving time included).</summary>
    public static DateTimeOffset FromLocal(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    /// <summary>True when the event touches the local day.</summary>
    public bool Touches(DateTime day) => StartLocal < day.Date.AddDays(1) && (EndLocal > day.Date || (EndLocal == StartLocal && StartLocal.Date == day.Date));
}

/// <summary>An event with its calendar and account – what lists, the calendar view and reminders show.</summary>
public sealed record EventItem(CalendarEvent Event, CalendarInfo Calendar, CalendarAccount Account)
{
    public bool IsReadOnly => Calendar.IsReadOnly || !Account.IsWritable;
}

/// <summary>What the events list and the calendar view ask for.</summary>
public sealed record EventQuery
{
    public DateTime From { get; init; } = DateTime.Today;

    public DateTime To { get; init; } = DateTime.Today.AddDays(30);

    /// <summary>Words that must all appear in title, location, notes, organizer or calendar name.</summary>
    public string Search { get; init; } = "";

    /// <summary>Only this calendar (null = every visible one).</summary>
    public long? CalendarId { get; init; }

    /// <summary>Hidden calendars too (the events list filtered on one calendar).</summary>
    public bool IncludeHidden { get; init; }

    public EventSort Sort { get; init; } = EventSort.StartAscending;
}

public enum EventSort
{
    StartAscending = 0,
    StartDescending = 1,
    Title = 2,
    Calendar = 3,
}

/// <summary>The colours offered for calendars (Apple-like, readable in light and dark).</summary>
public static class CalendarColors
{
    public const string Default = "#2F6FED";

    public static readonly IReadOnlyList<string> Palette =
    [
        "#2F6FED", "#F97316", "#16A34A", "#9333EA", "#DC2626", "#0EA5E9", "#EAB308", "#DB2777", "#14B8A6", "#64748B",
    ];

    /// <summary>The n-th colour of the palette (new calendars without a colour of their own).</summary>
    public static string ForIndex(int index) => Palette[Math.Abs(index) % Palette.Count];

    /// <summary>"#RRGGBB" in upper case, or null when the text is no colour.</summary>
    public static string? Normalize(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length == 8)
        {
            text = text[2..]; // AARRGGBB
        }

        return text.Length == 6 && text.All(Uri.IsHexDigit) ? "#" + text.ToUpperInvariant() : null;
    }
}
