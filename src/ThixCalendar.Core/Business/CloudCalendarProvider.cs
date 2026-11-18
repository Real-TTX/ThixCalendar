using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar.Core.Business;

/// <summary>
/// The "provider" of an account MatCloud keeps in step: events are created, changed and deleted through MatCloud, which writes to Microsoft / Google
/// first and answers what they stored (the same rule as everywhere: the provider is the truth). Calendars and events come down with the sync of MatCloud
/// (CloudItems), never through this – reading here answers nothing.
/// </summary>
internal sealed class CloudCalendarProvider(CalendarAccount account) : ICalendarProvider
{
    public Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteCalendar>>([]);

    public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(CalendarInfo calendar, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CalendarEvent>>([]);

    public async Task<CalendarEvent> CreateEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var calendarKey = CloudItems.CalendarKey(account, calendar);
        return await ChangeAsync(new CalendarEventChange(null, CloudItems.ToCloud(calendarKey, calendarEvent with { RemoteId = "" })), cancellationToken);
    }

    public async Task<CalendarEvent> UpdateEventAsync(CalendarInfo calendar, CalendarEvent previous, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var calendarKey = CloudItems.CalendarKey(account, calendar);
        var change = new CalendarEventChange(CalendarContract.EventKey(calendarKey, previous.RemoteId),
            CloudItems.ToCloud(calendarKey, calendarEvent with { RemoteId = previous.RemoteId }));
        return await ChangeAsync(change, cancellationToken);
    }

    public async Task DeleteEventAsync(CalendarInfo calendar, CalendarEvent calendarEvent, CancellationToken cancellationToken)
    {
        var calendarKey = CloudItems.CalendarKey(account, calendar);
        await ChangeAsync(new CalendarEventChange(CalendarContract.EventKey(calendarKey, calendarEvent.RemoteId), null, Delete: true), cancellationToken);
    }

    private static async Task<CalendarEvent> ChangeAsync(CalendarEventChange change, CancellationToken cancellationToken)
    {
        try
        {
            var result = await CloudService.ChangeEventAsync(change, cancellationToken);
            return result.Data is { } data ? CloudItems.FromCloud(data) : new CalendarEvent();
        }
        catch (CloudException exception)
        {
            throw new ProviderException(exception.Message, inner: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException("MatCloud ist nicht erreichbar – das Konto läuft über MatCloud, Änderungen gehen nur mit Verbindung.", inner: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException("MatCloud antwortet nicht (Zeitüberschreitung).", inner: exception);
        }
    }
}
