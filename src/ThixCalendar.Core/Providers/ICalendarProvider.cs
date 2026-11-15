using System.Text.RegularExpressions;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Providers;

/// <summary>A calendar as its provider lists it.</summary>
internal sealed record RemoteCalendar(string RemoteId, string Name, string? Color, bool IsReadOnly, bool IsPrimary, int? DefaultReminderMinutes);

/// <summary>
/// One calendar service (Microsoft Graph, Google Calendar, an ICS address). Events come back as
/// <see cref="CalendarEvent"/> rows without Id/CalendarId; recurring events are expanded into their occurrences.
/// </summary>
internal interface ICalendarProvider
{
    Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(CalendarInfo calendar, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Creates the event; returns it as the provider stored it (with its key).</summary>
    Task<CalendarEvent> CreateEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken);

    /// <summary>Changes the event; <paramref name="previous"/> tells what really changed (only that is sent).</summary>
    Task<CalendarEvent> UpdateEventAsync(CalendarInfo calendar, CalendarEvent previous, CalendarEvent calendarEvent, CancellationToken cancellationToken);

    Task DeleteEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken);
}

/// <summary>Finds the address to join an online meeting in location or notes (Teams, Meet, Zoom, Webex, …).</summary>
public static partial class MeetingLinks
{
    [GeneratedRegex(
        @"https://(?:teams\.microsoft\.com/l/meetup-join/|teams\.live\.com/meet/|meet\.google\.com/[a-z]{3}-[a-z]{4}-[a-z]{3}|(?:[\w-]+\.)?zoom\.us/(?:j|my|w)/|[\w-]+\.webex\.com/|whereby\.com/|meet\.jit\.si/|[\w-]+\.gotomeeting\.com/|app\.chime\.aws/)[^\s""'<>)\]]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex MeetingPattern();

    /// <summary>The first meeting address in the texts, or "".</summary>
    public static string Find(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var match = MeetingPattern().Match(text);
            if (match.Success)
            {
                return match.Value.TrimEnd('.', ',', ';');
            }
        }

        return "";
    }

    /// <summary>"Teams", "Google Meet", "Zoom" … for the join button.</summary>
    public static string ServiceName(string url) => url switch
    {
        _ when url.Contains("teams.", StringComparison.OrdinalIgnoreCase) => "Teams",
        _ when url.Contains("meet.google.", StringComparison.OrdinalIgnoreCase) => "Google Meet",
        _ when url.Contains("zoom.us", StringComparison.OrdinalIgnoreCase) => "Zoom",
        _ when url.Contains("webex.com", StringComparison.OrdinalIgnoreCase) => "Webex",
        _ => "Besprechung",
    };
}
