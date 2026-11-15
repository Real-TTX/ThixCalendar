using System.Net;
using System.Text;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Platform;
using ThixCalendar.Core.Providers;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixCalendar.Tests;

/// <summary>
/// Every test gets its own data folder, an empty database, a token store in memory, no machine policies, no network
/// (a fake provider answers) and no sound. Nothing a test does touches the real calendars, settings, Keychain or registry.
/// </summary>
internal sealed class TestData : IDisposable
{
    public TestData()
    {
        Folder = Path.Combine(Path.GetTempPath(), "thixcalendar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        CalendarStore.Close();
        AppPaths.UseDirectoryForTests(Folder);
        SettingsStore.Reset();
        TokenStore.UseMemoryForTests(true);
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        Http = new FakeHttp();
        ProviderHttp.HandlerForTests = Http;
        SyncService.ResetForTests();
        ReminderService.ResetForTests();
        AccountService.ResetForTests();
        AccountService.SignInForTests = null;
        Chime.IsMutedForTests = true;
    }

    public string Folder { get; }

    /// <summary>The "network": answers per URL (Graph, Google, ICS, token endpoints).</summary>
    public FakeHttp Http { get; }

    /// <summary>A local account with one calendar, the usual starting point.</summary>
    public static (CalendarAccount Account, CalendarInfo Calendar) LocalCalendar(string name = "Privat")
    {
        var account = AccountService.AddLocal(name);
        return (account, AccountService.CalendarsOf(account.Id).Single());
    }

    public void Dispose()
    {
        SyncService.Stop();
        CalendarStore.Close();
        ProviderHttp.HandlerForTests = null;
        AccountService.SignInForTests = null;
        AccessPolicy.UsePolicyReaderForTests(null);
        TokenStore.UseMemoryForTests(false);
        ReminderService.ResetForTests();
        SettingsStore.Reset();
        AppPaths.UseDirectoryForTests(null);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A network in memory: the first route whose address part matches answers; everything else is a 404.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(HttpMethod? Method, string UrlPart, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = [];

    public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

    public void Json(string urlPart, string json, HttpMethod? method = null, HttpStatusCode status = HttpStatusCode.OK) =>
        _routes.Insert(0, (method, urlPart, _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

    public void Text(string urlPart, string text) =>
        _routes.Insert(0, (null, urlPart, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/calendar") }));

    public void Answer(string urlPart, Func<HttpRequestMessage, HttpResponseMessage> answer, HttpMethod? method = null) =>
        _routes.Insert(0, (method, urlPart, answer));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var url = request.RequestUri!.ToString();
        lock (Requests)
        {
            Requests.Add((request.Method, url, body));
        }

        foreach (var (method, part, answer) in _routes)
        {
            if ((method is null || method == request.Method) && url.Contains(part, StringComparison.Ordinal))
            {
                return answer(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"message":"not found"}}""") };
    }
}

/// <summary>Events for tests in local wall-clock time.</summary>
internal static class Events
{
    public static CalendarEvent At(long calendarId, DateTime localStart, double hours = 1, string title = "Termin", int? reminder = null) => new()
    {
        CalendarId = calendarId,
        Title = title,
        Start = CalendarEvent.FromLocal(localStart),
        End = CalendarEvent.FromLocal(localStart.AddHours(hours)),
        ReminderMinutes = reminder,
    };

    public static CalendarEvent AllDay(long calendarId, DateTime day, int days = 1, string title = "Urlaub") => new()
    {
        CalendarId = calendarId,
        Title = title,
        IsAllDay = true,
        Start = CalendarEvent.AllDayDate(day),
        End = CalendarEvent.AllDayDate(day.AddDays(days)),
    };
}
