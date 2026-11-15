using Ical.Net.DataTypes;
using ThixCalendar.Core.Models;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace ThixCalendar.Core.Providers;

/// <summary>
/// A subscribed iCalendar address (read-only): Google's "secret address in iCal format", a published Outlook / Exchange
/// calendar, holidays, school dates … One address is one calendar. Recurrences, exceptions and time zones are expanded
/// by Ical.Net.
/// </summary>
internal sealed class IcsProvider(string url) : ICalendarProvider
{
    /// <summary>webcal:// is http(s) for us.</summary>
    public static string NormalizeUrl(string url)
    {
        var text = url.Trim();
        if (text.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + text["webcals://".Length..];
        }

        return text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + text["webcal://".Length..] : text;
    }

    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken cancellationToken)
    {
        var text = await ProviderHttp.GetTextAsync(NormalizeUrl(url), cancellationToken);
        var calendar = Parse(text);
        var name = calendar.Properties.FirstOrDefault(property => property.Name == "X-WR-CALNAME")?.Value?.ToString();
        var color = calendar.Properties.FirstOrDefault(property => property.Name == "X-APPLE-CALENDAR-COLOR")?.Value?.ToString();
        return [new RemoteCalendar("", string.IsNullOrWhiteSpace(name) ? "Abonnierter Kalender" : name.Trim(), CalendarColors.Normalize(color), true, true, null)];
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(CalendarInfo calendar, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var text = await ProviderHttp.GetTextAsync(NormalizeUrl(url), cancellationToken);
        return await Task.Run(() => Expand(Parse(text), from, to), cancellationToken);
    }

    public Task<CalendarEvent> CreateEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken) =>
        throw new ProviderException("Ein abonnierter Kalender (ICS) kann nur gelesen werden.");

    public Task<CalendarEvent> UpdateEventAsync(CalendarInfo calendar, CalendarEvent previous, CalendarEvent calendarEvent, CancellationToken cancellationToken) =>
        throw new ProviderException("Ein abonnierter Kalender (ICS) kann nur gelesen werden.");

    public Task DeleteEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken) =>
        throw new ProviderException("Ein abonnierter Kalender (ICS) kann nur gelesen werden.");

    internal static IcalCalendar Parse(string text)
    {
        try
        {
            return IcalCalendar.Load(text) ?? throw new ProviderException("Die Adresse liefert keinen Kalender (iCalendar).");
        }
        catch (Exception exception) when (exception is not ProviderException and not OutOfMemoryException)
        {
            throw new ProviderException("Die Adresse liefert keinen lesbaren Kalender (iCalendar).", inner: exception);
        }
    }

    /// <summary>The occurrences that overlap [from, to) as rows; the key is the UID plus the start of the occurrence.</summary>
    internal static List<CalendarEvent> Expand(IcalCalendar calendar, DateTimeOffset from, DateTimeOffset to)
    {
        var events = new List<CalendarEvent>();
        // Start a day early: an occurrence that began before the window and still runs belongs in it.
        var start = new CalDateTime(from.UtcDateTime.AddDays(-1), "UTC");
        foreach (var occurrence in calendar.GetOccurrences<IcalEvent>(start, null).TakeWhile(occurrence => Absolute(occurrence.Period.StartTime) < to))
        {
            if (occurrence.Source is not IcalEvent source || string.Equals(source.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isAllDay = !occurrence.Period.StartTime.HasTime;
            var begin = isAllDay ? CalendarEvent.AllDayDate(occurrence.Period.StartTime.Date.ToDateTime(TimeOnly.MinValue)) : Absolute(occurrence.Period.StartTime);
            var endTime = occurrence.Period.EffectiveEndTime;
            DateTimeOffset end;
            if (endTime is null)
            {
                end = isAllDay ? begin.AddDays(1) : begin;
            }
            else
            {
                end = isAllDay ? CalendarEvent.AllDayDate(endTime.Date.ToDateTime(TimeOnly.MinValue)) : Absolute(endTime);
            }

            if (end <= from && !(end == begin && begin >= from))
            {
                continue;
            }

            var location = source.Location ?? "";
            var description = source.Description ?? "";
            var alarm = source.Alarms.Select(item => item.Trigger?.Duration).Where(duration => duration is not null).Select(duration => duration!.Value.ToTimeSpanUnspecified()).FirstOrDefault();
            events.Add(new CalendarEvent
            {
                RemoteId = $"{source.Uid}|{begin.UtcTicks}",
                Title = string.IsNullOrWhiteSpace(source.Summary) ? "(Ohne Titel)" : source.Summary.Trim(),
                Start = begin,
                End = end < begin ? begin : end,
                IsAllDay = isAllDay,
                Location = location,
                Description = description.Trim(),
                MeetingUrl = MeetingLinks.Find(source.Url?.ToString(), location, description),
                WebLink = source.Url?.ToString() ?? "",
                Organizer = source.Organizer?.CommonName ?? "",
                ReminderMinutes = alarm < TimeSpan.Zero ? (int)Math.Round(-alarm.TotalMinutes) : null,
                Status = string.Equals(source.Status, "TENTATIVE", StringComparison.OrdinalIgnoreCase) ? EventStatus.Tentative : EventStatus.Confirmed,
                IsRecurring = source.RecurrenceRule is not null || source.RecurrenceIdentifier is not null,
            });
        }

        return events;
    }

    private static DateTimeOffset Absolute(CalDateTime value) =>
        value.HasTime ? new DateTimeOffset(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc)) : CalendarEvent.FromLocal(value.Date.ToDateTime(TimeOnly.MinValue));
}
