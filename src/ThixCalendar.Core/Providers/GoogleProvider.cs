using System.Globalization;
using System.Text.Json.Nodes;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Providers;

/// <summary>
/// Google Calendar (API v3). Recurring events come expanded (singleEvents=true); each occurrence has its own id and is
/// changed alone. Timed events carry their offset, all-day events a date ("date") with an exclusive end.
/// </summary>
internal sealed class GoogleProvider(Func<CancellationToken, Task<string>> accessToken) : ICalendarProvider
{
    private const string Api = "https://www.googleapis.com/calendar/v3";

    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken cancellationToken)
    {
        var calendars = new List<RemoteCalendar>();
        await foreach (var item in PagesAsync($"{Api}/users/me/calendarList?maxResults=250", cancellationToken))
        {
            var role = Text(item, "accessRole");
            if (role == "freeBusyReader")
            {
                continue; // nothing but busy blocks: no use for reminders
            }

            var name = Text(item, "summaryOverride") is { Length: > 0 } custom ? custom : Text(item, "summary");
            calendars.Add(new RemoteCalendar(
                Text(item, "id"),
                name,
                CalendarColors.Normalize(Text(item, "backgroundColor")),
                role is not ("owner" or "writer"),
                item["primary"]?.GetValue<bool>() == true,
                PopupMinutes(item["defaultReminders"])));
        }

        return calendars;
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(CalendarInfo calendar, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var url = $"{Api}/calendars/{Uri.EscapeDataString(calendar.RemoteId)}/events?singleEvents=true&orderBy=startTime&maxResults=2500" +
                  $"&timeMin={Uri.EscapeDataString(from.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture))}" +
                  $"&timeMax={Uri.EscapeDataString(to.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture))}";
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
        var created = await ProviderHttp.SendJsonAsync(HttpMethod.Post, $"{Api}/calendars/{Uri.EscapeDataString(calendar.RemoteId)}/events",
            await accessToken(cancellationToken), Body(null, calendarEvent), cancellationToken);
        return ReadEvent(created) ?? calendarEvent;
    }

    public async Task<CalendarEvent> UpdateEventAsync(CalendarInfo calendar, CalendarEvent previous, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var updated = await ProviderHttp.SendJsonAsync(HttpMethod.Patch,
            $"{Api}/calendars/{Uri.EscapeDataString(calendar.RemoteId)}/events/{Uri.EscapeDataString(calendarEvent.RemoteId)}",
            await accessToken(cancellationToken), Body(previous, calendarEvent), cancellationToken);
        return ReadEvent(updated) ?? calendarEvent;
    }

    public async Task DeleteEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken) =>
        await ProviderHttp.SendJsonAsync(HttpMethod.Delete,
            $"{Api}/calendars/{Uri.EscapeDataString(calendar.RemoteId)}/events/{Uri.EscapeDataString(calendarEvent.RemoteId)}",
            await accessToken(cancellationToken), null, cancellationToken, ignoreNotFound: true);

    internal static JsonObject Body(CalendarEvent? previous, CalendarEvent calendarEvent)
    {
        var body = new JsonObject();
        if (previous is null || previous.Title != calendarEvent.Title)
        {
            body["summary"] = calendarEvent.Title;
        }

        if (previous is null || previous.Start != calendarEvent.Start || previous.End != calendarEvent.End || previous.IsAllDay != calendarEvent.IsAllDay)
        {
            body["start"] = Time(calendarEvent.Start, calendarEvent.StartLocal, calendarEvent.IsAllDay);
            body["end"] = Time(calendarEvent.End, calendarEvent.EndLocal, calendarEvent.IsAllDay);
        }

        if (previous is null || previous.Location != calendarEvent.Location)
        {
            body["location"] = calendarEvent.Location;
        }

        if (previous is null || previous.Description != calendarEvent.Description)
        {
            body["description"] = calendarEvent.Description;
        }

        if (previous is null || previous.ReminderMinutes != calendarEvent.ReminderMinutes)
        {
            body["reminders"] = calendarEvent.ReminderMinutes is { } minutes
                ? new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = minutes }) }
                : new JsonObject { ["useDefault"] = true };
        }

        return body;
    }

    /// <summary>A Google time: all-day events a date, the other field explicitly cleared (switching between both).</summary>
    private static JsonObject Time(DateTimeOffset absolute, DateTime local, bool isAllDay) => isAllDay
        ? new JsonObject { ["date"] = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["dateTime"] = null }
        : new JsonObject { ["dateTime"] = absolute.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture), ["date"] = null };

    internal static CalendarEvent? ReadEvent(JsonNode item)
    {
        if (Text(item, "status") == "cancelled" || Text(item, "id").Length == 0)
        {
            return null;
        }

        var (start, startIsDate) = ReadTime(item["start"]);
        var (end, _) = ReadTime(item["end"]);
        if (start is null || end is null)
        {
            return null;
        }

        var self = item["attendees"]?.AsArray().FirstOrDefault(attendee => attendee?["self"]?.GetValue<bool>() == true);
        var location = Text(item, "location");
        var description = Text(item, "description");
        var meeting = Text(item, "hangoutLink");
        if (meeting.Length == 0)
        {
            meeting = item["conferenceData"]?["entryPoints"]?.AsArray()
                .FirstOrDefault(entry => Text(entry, "entryPointType") == "video") is { } video ? Text(video, "uri") : "";
        }

        var reminders = item["reminders"];
        int? reminderMinutes = reminders?["useDefault"]?.GetValue<bool>() == false ? PopupMinutes(reminders["overrides"]) : null;
        var organizer = item["organizer"];

        return new CalendarEvent
        {
            RemoteId = Text(item, "id"),
            Title = Text(item, "summary") is { Length: > 0 } summary ? summary : "(Ohne Titel)",
            Start = start.Value,
            End = end.Value < start.Value ? start.Value : end.Value,
            IsAllDay = startIsDate,
            Location = location,
            Description = StripHtml(description),
            MeetingUrl = meeting.Length > 0 ? meeting : MeetingLinks.Find(location, description),
            WebLink = Text(item, "htmlLink"),
            Organizer = Text(organizer, "displayName") is { Length: > 0 } name ? name : Text(organizer, "email"),
            ReminderMinutes = reminderMinutes,
            Status = Text(item, "status") == "tentative" ? EventStatus.Tentative : EventStatus.Confirmed,
            Response = organizer?["self"]?.GetValue<bool>() == true
                ? ResponseStatus.Organizer
                : Text(self, "responseStatus") switch
                {
                    "accepted" => ResponseStatus.Accepted,
                    "tentative" => ResponseStatus.Tentative,
                    "declined" => ResponseStatus.Declined,
                    "needsAction" => ResponseStatus.NotResponded,
                    _ => ResponseStatus.None,
                },
            IsRecurring = Text(item, "recurringEventId").Length > 0,
        };
    }

    private static (DateTimeOffset? Value, bool IsDate) ReadTime(JsonNode? node)
    {
        if (DateTimeOffset.TryParse(Text(node, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return (time, false);
        }

        return DateTime.TryParseExact(Text(node, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? (CalendarEvent.AllDayDate(date), true)
            : (null, false);
    }

    /// <summary>The earliest pop-up reminder of a list (Google: defaultReminders / reminders.overrides); null when there is none.</summary>
    private static int? PopupMinutes(JsonNode? list)
    {
        var minutes = (list as JsonArray ?? [])
            .Where(entry => Text(entry, "method") is "popup" or "")
            .Select(entry => entry?["minutes"]?.GetValue<int>())
            .Where(value => value is not null)
            .ToList();
        return minutes.Count == 0 ? null : minutes.Max();
    }

    /// <summary>Google keeps notes as light HTML; the app shows plain text.</summary>
    private static string StripHtml(string text)
    {
        if (!text.Contains('<'))
        {
            return text.Trim();
        }

        var withBreaks = System.Text.RegularExpressions.Regex.Replace(text, @"<\s*(br|/p|/div|/li)\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var plain = System.Text.RegularExpressions.Regex.Replace(withBreaks, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(plain).Trim();
    }

    private async IAsyncEnumerable<JsonNode> PagesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        var pages = 0;
        do
        {
            var pageUrl = pageToken is null ? url : $"{url}&pageToken={Uri.EscapeDataString(pageToken)}";
            var page = await ProviderHttp.SendJsonAsync(HttpMethod.Get, pageUrl, await accessToken(cancellationToken), null, cancellationToken);
            foreach (var item in page["items"]?.AsArray() ?? [])
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            pageToken = page["nextPageToken"]?.GetValue<string>();
        }
        while (pageToken is not null && pages++ < 50);
    }

    private static string Text(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
