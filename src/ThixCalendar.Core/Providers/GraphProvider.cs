using System.Globalization;
using System.Text.Json.Nodes;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Providers;

/// <summary>
/// Microsoft 365 / Exchange Online / Outlook.com through Microsoft Graph (v1.0). Times are asked for in the local time
/// zone of this computer (Prefer: outlook.timezone): timed events are converted to absolute times, all-day events keep
/// their dates. Recurring events come expanded (calendarView); every occurrence has its own id and is changed alone.
/// </summary>
internal sealed class GraphProvider(Func<CancellationToken, Task<string>> accessToken) : ICalendarProvider
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    private const string EventFields =
        "id,subject,start,end,isAllDay,location,body,onlineMeeting,onlineMeetingUrl,webLink,organizer,isReminderOn," +
        "reminderMinutesBeforeStart,isCancelled,responseStatus,type,seriesMasterId";

    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken cancellationToken)
    {
        var calendars = new List<RemoteCalendar>();
        await foreach (var item in PagesAsync($"{Graph}/me/calendars?$select=id,name,color,hexColor,canEdit,isDefaultCalendar&$top=100", cancellationToken))
        {
            calendars.Add(new RemoteCalendar(
                Text(item, "id"),
                Text(item, "name"),
                CalendarColors.Normalize(Text(item, "hexColor")) ?? NamedColor(Text(item, "color")),
                item["canEdit"]?.GetValue<bool>() == false,
                item["isDefaultCalendar"]?.GetValue<bool>() == true,
                null));
        }

        return calendars;
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(CalendarInfo calendar, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var url = $"{Graph}/me/calendars/{Uri.EscapeDataString(calendar.RemoteId)}/calendarView" +
                  $"?startDateTime={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&endDateTime={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}" +
                  $"&$select={EventFields}&$top=250";
        var events = new List<CalendarEvent>();
        await foreach (var item in PagesAsync(url, cancellationToken))
        {
            if (ReadEvent(item) is { } calendarEvent)
            {
                events.Add(calendarEvent);
            }
        }

        return events;
    }

    public async Task<CalendarEvent> CreateEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var body = Body(null, calendarEvent);
        var created = await ProviderHttp.SendJsonAsync(HttpMethod.Post, $"{Graph}/me/calendars/{Uri.EscapeDataString(calendar.RemoteId)}/events",
            await accessToken(cancellationToken), body, cancellationToken, Headers);
        return ReadEvent(created) ?? calendarEvent;
    }

    public async Task<CalendarEvent> UpdateEventAsync(CalendarInfo calendar, CalendarEvent previous, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var updated = await ProviderHttp.SendJsonAsync(HttpMethod.Patch, $"{Graph}/me/events/{Uri.EscapeDataString(calendarEvent.RemoteId)}",
            await accessToken(cancellationToken), Body(previous, calendarEvent), cancellationToken, Headers);
        return ReadEvent(updated) ?? calendarEvent;
    }

    public async Task DeleteEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken) =>
        await ProviderHttp.SendJsonAsync(HttpMethod.Delete, $"{Graph}/me/events/{Uri.EscapeDataString(calendarEvent.RemoteId)}",
            await accessToken(cancellationToken), null, cancellationToken, Headers, ignoreNotFound: true);

    /// <summary>Who signed in: e-mail address and name (Graph /me).</summary>
    public async Task<(string Mail, string Name)> ReadProfileAsync(CancellationToken cancellationToken)
    {
        var me = await ProviderHttp.SendJsonAsync(HttpMethod.Get, $"{Graph}/me?$select=displayName,mail,userPrincipalName",
            await accessToken(cancellationToken), null, cancellationToken);
        var mail = Text(me, "mail");
        return (mail.Length > 0 ? mail : Text(me, "userPrincipalName"), Text(me, "displayName"));
    }

    /// <summary>The time zone Graph speaks with us: the computer's, as a Windows id (Graph's own naming).</summary>
    internal static string TimeZoneId
    {
        get
        {
            var id = TimeZoneInfo.Local.Id;
            if (!OperatingSystem.IsWindows() && TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                return windowsId;
            }

            return id;
        }
    }

    private static Dictionary<string, string> Headers => new()
    {
        ["Prefer"] = $"outlook.timezone=\"{TimeZoneId}\", outlook.body-content-type=\"text\"",
    };

    /// <summary>The fields of a new event, or only the changed ones of an existing one.</summary>
    internal static JsonObject Body(CalendarEvent? previous, CalendarEvent calendarEvent)
    {
        var body = new JsonObject();
        if (previous is null || previous.Title != calendarEvent.Title)
        {
            body["subject"] = calendarEvent.Title;
        }

        if (previous is null || previous.Start != calendarEvent.Start || previous.End != calendarEvent.End || previous.IsAllDay != calendarEvent.IsAllDay)
        {
            body["isAllDay"] = calendarEvent.IsAllDay;
            body["start"] = Time(calendarEvent.StartLocal);
            body["end"] = Time(calendarEvent.EndLocal);
        }

        if (previous is null || previous.Location != calendarEvent.Location)
        {
            body["location"] = new JsonObject { ["displayName"] = calendarEvent.Location };
        }

        // The notes go only when they changed: Graph keeps formatted (HTML) notes, ThixCalendar edits plain text.
        if (previous is null ? calendarEvent.Description.Length > 0 : previous.Description != calendarEvent.Description)
        {
            body["body"] = new JsonObject { ["contentType"] = "text", ["content"] = calendarEvent.Description };
        }

        if (previous is null || previous.ReminderMinutes != calendarEvent.ReminderMinutes)
        {
            body["isReminderOn"] = calendarEvent.ReminderMinutes is not null;
            if (calendarEvent.ReminderMinutes is { } minutes)
            {
                body["reminderMinutesBeforeStart"] = minutes;
            }
        }

        return body;
    }

    private static JsonObject Time(DateTime local) => new()
    {
        ["dateTime"] = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        ["timeZone"] = TimeZoneId,
    };

    /// <summary>One event of Graph as a row (null for cancelled occurrences).</summary>
    internal static CalendarEvent? ReadEvent(JsonNode item)
    {
        if (item["isCancelled"]?.GetValue<bool>() == true || Text(item, "id").Length == 0)
        {
            return null;
        }

        var isAllDay = item["isAllDay"]?.GetValue<bool>() == true;
        var start = ReadTime(item["start"], isAllDay);
        var end = ReadTime(item["end"], isAllDay);
        if (start is null || end is null)
        {
            return null;
        }

        var location = Text(item["location"], "displayName");
        var description = Text(item["body"], "content").Trim();
        var meeting = Text(item["onlineMeeting"], "joinUrl");
        if (meeting.Length == 0)
        {
            meeting = Text(item, "onlineMeetingUrl");
        }

        return new CalendarEvent
        {
            RemoteId = Text(item, "id"),
            Title = Text(item, "subject") is { Length: > 0 } subject ? subject : "(Ohne Titel)",
            Start = start.Value,
            End = end.Value < start.Value ? start.Value : end.Value,
            IsAllDay = isAllDay,
            Location = location,
            Description = description,
            MeetingUrl = meeting.Length > 0 ? meeting : MeetingLinks.Find(location, description),
            WebLink = Text(item, "webLink"),
            Organizer = Text(item["organizer"]?["emailAddress"], "name"),
            ReminderMinutes = item["isReminderOn"]?.GetValue<bool>() == true ? item["reminderMinutesBeforeStart"]?.GetValue<int>() : null,
            Status = EventStatus.Confirmed,
            Response = Text(item["responseStatus"], "response") switch
            {
                "organizer" => ResponseStatus.Organizer,
                "accepted" => ResponseStatus.Accepted,
                "tentativelyAccepted" => ResponseStatus.Tentative,
                "declined" => ResponseStatus.Declined,
                "notResponded" => ResponseStatus.NotResponded,
                _ => ResponseStatus.None,
            },
            IsRecurring = Text(item, "type") is "occurrence" or "exception" || Text(item, "seriesMasterId").Length > 0,
        };
    }

    /// <summary>A Graph time in the asked-for (local) zone; all-day events keep only their date.</summary>
    private static DateTimeOffset? ReadTime(JsonNode? node, bool isAllDay)
    {
        if (!DateTime.TryParse(Text(node, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var wallClock))
        {
            return null;
        }

        if (isAllDay)
        {
            return CalendarEvent.AllDayDate(wallClock.Date);
        }

        var zone = Text(node, "timeZone");
        if (string.Equals(zone, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return CalendarEvent.FromLocal(wallClock);
    }

    private async IAsyncEnumerable<JsonNode> PagesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = url;
        var pages = 0;
        while (next is not null && pages++ < 50)
        {
            var page = await ProviderHttp.SendJsonAsync(HttpMethod.Get, next, await accessToken(cancellationToken), null, cancellationToken, Headers);
            foreach (var item in page["value"]?.AsArray() ?? [])
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            next = page["@odata.nextLink"]?.GetValue<string>();
        }
    }

    /// <summary>Graph's calendar colour names (when hexColor is empty).</summary>
    private static string? NamedColor(string name) => name switch
    {
        "lightBlue" => "#2F6FED",
        "lightGreen" => "#16A34A",
        "lightOrange" => "#F97316",
        "lightGray" => "#64748B",
        "lightYellow" => "#EAB308",
        "lightTeal" => "#14B8A6",
        "lightPink" => "#DB2777",
        "lightBrown" => "#A16207",
        "lightRed" => "#DC2626",
        _ => null,
    };

    private static string Text(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
