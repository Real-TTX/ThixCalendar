using System.Net;
using System.Text.Json.Nodes;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar.Tests;

/// <summary>
/// Accounts against fake providers: adding a Microsoft 365 / Google account (sign-in without a browser), the sync of
/// calendars and events, writes going to the provider first, expired sign-ins, and the real OAuth loopback.
/// </summary>
public sealed class SyncTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task A_microsoft_account_is_added_and_synchronised()
    {
        using var data = new TestData();
        var graph = new FakeGraph(data.Http);
        SignInAs(data);

        var account = await AccountService.AddMicrosoftAsync(_ => { });
        Assert.Equal(("matthias@example.com", AccountState.Ok), (account.UserName, account.State));
        var calendars = AccountService.CalendarsOf(account.Id);
        Assert.Equal(["Kalender", "Feiertage in Deutschland"], calendars.OrderBy(calendar => calendar.SortOrder).Select(calendar => calendar.Name));
        Assert.True(calendars.Single(calendar => calendar.Name == "Kalender").IsPrimary);
        Assert.False(calendars.Single(calendar => calendar.Name == "Feiertage in Deutschland").RemindersEnabled); // read-only extra
        var events = CalendarService.Query(new EventQuery { From = DateTime.Today, To = DateTime.Today.AddDays(3) });
        Assert.Equal(["Team-Meeting"], events.Select(item => item.Event.Title));

        // The provider changes: one event gone, one new; a calendar removed. Ids of kept events stay.
        var keptId = events[0].Event.Id;
        graph.Events = [graph.Event("E2", "Neu", DateTime.Today.AddDays(1).AddHours(14))];
        graph.Events.Add(graph.Event("E1", "Team-Meeting (verschoben)", DateTime.Today.AddDays(1).AddHours(10)));
        graph.Calendars.RemoveAt(1);
        await SyncService.SyncAccountAsync(account.Id);
        var after = CalendarService.Query(new EventQuery { From = DateTime.Today, To = DateTime.Today.AddDays(3) });
        Assert.Equal(["Team-Meeting (verschoben)", "Neu"], after.Select(item => item.Event.Title));
        Assert.Equal(keptId, after[0].Event.Id);
        Assert.Single(AccountService.CalendarsOf(account.Id));

        // A second sign-in of the same person is refused.
        await Assert.ThrowsAsync<BusinessException>(() => AccountService.AddMicrosoftAsync(_ => { }));
    }

    [Fact]
    public async Task Changes_go_to_the_provider_first()
    {
        using var data = new TestData();
        var graph = new FakeGraph(data.Http);
        SignInAs(data);
        var account = await AccountService.AddMicrosoftAsync(_ => { });
        var calendar = AccountService.CalendarsOf(account.Id).Single(item => item.IsPrimary);

        var created = await CalendarService.SaveEventAsync(Events.At(calendar.Id, DateTime.Today.AddDays(2).AddHours(15), title: "Review", reminder: 15));
        Assert.Equal("NEW1", created.RemoteId);
        var post = data.Http.Requests.Last(request => request.Method == HttpMethod.Post);
        Assert.Contains("/me/calendars/CAL1/events", post.Url);
        Assert.Equal("Review", JsonNode.Parse(post.Body)!["subject"]!.GetValue<string>());

        await CalendarService.SaveEventAsync(created with { Title = "Review (neu)" });
        var patch = data.Http.Requests.Last(request => request.Method == HttpMethod.Patch);
        Assert.EndsWith("/me/events/NEW1", patch.Url);
        Assert.Equal(["subject"], JsonNode.Parse(patch.Body)!.AsObject().Select(pair => pair.Key));

        await CalendarService.DeleteEventAsync(created.Id);
        Assert.Contains(data.Http.Requests, request => request.Method == HttpMethod.Delete && request.Url.EndsWith("/me/events/NEW1", StringComparison.Ordinal));

        // A refusal of the provider stores nothing.
        data.Http.Json("/me/calendars/CAL1/events", """{"error":{"message":"Mailbox is full"}}""", HttpMethod.Post, HttpStatusCode.BadRequest);
        var refused = await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(Events.At(calendar.Id, DateTime.Today.AddDays(2).AddHours(9), title: "Nein")));
        Assert.Contains("Mailbox is full", refused.Message);
        Assert.Empty(CalendarService.Query(new EventQuery { From = DateTime.Today, To = DateTime.Today.AddDays(5), Search = "Nein" }));
    }

    [Fact]
    public async Task An_expired_sign_in_marks_the_account_and_reconnecting_repairs_it()
    {
        using var data = new TestData();
        new FakeGraph(data.Http);
        SignInAs(data);
        var account = await AccountService.AddMicrosoftAsync(_ => { });

        // The access token runs out, the refresh token was revoked.
        AccountService.ResetForTests();
        data.Http.Json("oauth2/v2.0/token", """{"error":"invalid_grant","error_description":"AADSTS700082: The refresh token has expired"}""", status: HttpStatusCode.BadRequest);
        await SyncService.SyncAccountAsync(account.Id);
        var signedOut = AccountService.Get(account.Id)!;
        Assert.Equal(AccountState.SignedOut, signedOut.State);
        Assert.Equal(1, SyncService.Status.SignedOutAccounts);
        var calendar = AccountService.CalendarsOf(account.Id).Single(item => item.IsPrimary);
        await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(Events.At(calendar.Id, DateTime.Today.AddDays(1).AddHours(9))));

        // Signing in as somebody else is refused, as the same person repairs the account.
        SignInAs(data, "jemand@example.com");
        await Assert.ThrowsAsync<BusinessException>(() => AccountService.ReconnectAsync(account.Id, _ => { }));
        SignInAs(data);
        var repaired = await AccountService.ReconnectAsync(account.Id, _ => { });
        Assert.Equal(AccountState.Ok, repaired.State);
    }

    [Fact]
    public async Task A_rotated_refresh_token_is_kept()
    {
        using var data = new TestData();
        new FakeGraph(data.Http);
        SignInAs(data);
        var account = await AccountService.AddMicrosoftAsync(_ => { });
        AccountService.ResetForTests();
        data.Http.Json("oauth2/v2.0/token", """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""");
        Assert.Equal("access-2", await AccountService.AccessTokenAsync(account.Id, CancellationToken.None));
        var refresh = data.Http.Requests.Last(request => request.Url.Contains("oauth2/v2.0/token", StringComparison.Ordinal));
        Assert.Contains("refresh_token=refresh-1", refresh.Body);
        AccountService.ResetForTests();
        await AccountService.AccessTokenAsync(account.Id, CancellationToken.None);
        Assert.Contains("refresh_token=refresh-2", data.Http.Requests.Last(request => request.Url.Contains("oauth2/v2.0/token", StringComparison.Ordinal)).Body);
    }

    [Fact]
    public async Task A_google_account_reads_the_address_from_the_id_token()
    {
        using var data = new TestData();
        SettingsService.Update(settings => settings with { GoogleClientId = "1-test.apps.googleusercontent.com", GoogleClientSecret = "secret" });
        var claims = Convert.ToBase64String("""{"email":"privat@gmail.com"}"""u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        AccountService.SignInForTests = (profile, _) =>
        {
            Assert.Equal("Google", profile.ProviderName);
            Assert.Contains("auth/calendar", profile.Scope);
            return Task.FromResult(new OAuthTokens("g-access", "g-refresh", DateTimeOffset.UtcNow.AddHours(1), $"x.{claims}.y"));
        };
        data.Http.Json("calendar/v3/users/me/calendarList", """
            {"items":[{"id":"privat@gmail.com","summary":"Privat","backgroundColor":"#16a765","accessRole":"owner","primary":true,
                       "defaultReminders":[{"method":"popup","minutes":30}]},
                      {"id":"geburtstage","summary":"Geburtstage","accessRole":"reader"},
                      {"id":"busy","summary":"Kollege","accessRole":"freeBusyReader"}]}
            """);
        var start = DateTimeOffset.Now.Date.AddDays(1).AddHours(18);
        data.Http.Json("calendars/privat%40gmail.com/events", $$$"""
            {"items":[{"id":"k1","summary":"Kino","start":{"dateTime":"{{{start:yyyy-MM-dd'T'HH:mm:ssK}}}"},"end":{"dateTime":"{{{start.AddHours(2):yyyy-MM-dd'T'HH:mm:ssK}}}"},
                       "reminders":{"useDefault":true}}]}
            """);
        data.Http.Json("calendars/geburtstage/events", """{"items":[]}""");

        var account = await AccountService.AddGoogleAsync(_ => { });
        Assert.Equal("privat@gmail.com", account.UserName);
        var calendars = AccountService.CalendarsOf(account.Id);
        Assert.Equal(2, calendars.Count); // free/busy only: left out
        var privat = calendars.Single(calendar => calendar.IsPrimary);
        Assert.Equal((30, "#16A765"), (privat.DefaultReminderMinutes, privat.Color));
        var kino = Assert.Single(CalendarService.Query(new EventQuery { From = DateTime.Today, To = DateTime.Today.AddDays(3) }));
        Assert.Equal(30, ReminderService.LeadMinutes(kino.Event, kino.Calendar, SettingsService.Get()));
    }

    [Fact]
    public async Task Without_an_app_registration_the_user_is_told_where_to_enter_it()
    {
        using var data = new TestData();
        if (AccountService.IsConfigured(AccountKind.Microsoft))
        {
            return; // a build with a built-in registration
        }

        var error = await Assert.ThrowsAsync<BusinessException>(() => AccountService.AddMicrosoftAsync(_ => { }));
        Assert.Contains("Konten-Zugang", error.Message);
    }

    [Fact]
    public async Task The_loopback_sign_in_receives_the_code_and_exchanges_it_with_pkce()
    {
        using var data = new TestData();
        data.Http.Json("oauth2/v2.0/token", """{"access_token":"a","refresh_token":"r","expires_in":"3599","id_token":"x"}""");
        using var browser = new HttpClient(); // the "browser": a real HTTP request to the loopback address
        string? page = null;
        var tokens = await OAuthClient.SignInAsync(OAuthProfile.Microsoft(ClientId), url =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            Assert.Equal("S256", query["code_challenge_method"]);
            var redirect = query["redirect_uri"]!.Replace("localhost", "127.0.0.1");
            _ = Task.Run(async () =>
            {
                await browser.GetAsync(redirect + "favicon.ico"); // browsers ask for it: ignored
                page = await browser.GetStringAsync($"{redirect}?code=the-code&state={Uri.EscapeDataString(query["state"]!)}");
            });
        }, CancellationToken.None);

        Assert.Equal(("a", "r"), (tokens.AccessToken, tokens.RefreshToken));
        var exchange = data.Http.Requests.Single(request => request.Url.Contains("token", StringComparison.Ordinal));
        Assert.Contains("code=the-code", exchange.Body);
        Assert.Contains("code_verifier=", exchange.Body);
        for (var wait = 0; wait < 50 && page is null; wait++)
        {
            await Task.Delay(20);
        }

        Assert.Contains("Anmeldung abgeschlossen", page);
    }

    [Fact]
    public async Task A_cancelled_sign_in_in_the_browser_is_reported()
    {
        using var data = new TestData();
        using var browser = new HttpClient();
        var failure = await Assert.ThrowsAsync<ProviderException>(() => OAuthClient.SignInAsync(OAuthProfile.Microsoft(ClientId), url =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            _ = browser.GetStringAsync(query["redirect_uri"]!.Replace("localhost", "127.0.0.1") + "?error=access_denied&state=x");
        }, CancellationToken.None));
        Assert.Contains("abgebrochen", failure.Message);
    }

    [Fact]
    public async Task Ranges_outside_the_window_are_loaded_once()
    {
        using var data = new TestData();
        var graph = new FakeGraph(data.Http);
        SignInAs(data);
        await AccountService.AddMicrosoftAsync(_ => { });
        var far = DateTime.Today.AddMonths(8);
        graph.Events = [graph.Event("F1", "Jahrestreffen", far.AddHours(10))];
        var before = data.Http.Requests.Count;
        Assert.True(await SyncService.EnsureRangeAsync(far, far.AddDays(6)));
        Assert.Single(CalendarService.Range(far, far.AddDays(1)));
        Assert.False(await SyncService.EnsureRangeAsync(far.AddDays(1), far.AddDays(2))); // the same month: not again
        Assert.False(await SyncService.EnsureRangeAsync(DateTime.Today, DateTime.Today.AddDays(6))); // inside the window
        Assert.True(data.Http.Requests.Count > before);
    }

    private static void SignInAs(TestData data, string mail = "matthias@example.com")
    {
        SettingsService.Update(settings => settings with { MicrosoftClientId = ClientId });
        AccountService.SignInForTests = (_, _) => Task.FromResult(new OAuthTokens("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), null));
        data.Http.Json("graph.microsoft.com/v1.0/me?", $$"""{"displayName":"Matthias","mail":"{{mail}}"}""");
    }

    /// <summary>Microsoft Graph in memory: two calendars, events that the test changes.</summary>
    private sealed class FakeGraph
    {
        public FakeGraph(FakeHttp http)
        {
            Events = [Event("E1", "Team-Meeting", DateTime.Today.AddDays(1).AddHours(8))];
            http.Answer("/me/calendars?", _ => Json(new JsonObject { ["value"] = new JsonArray(Calendars.Select(calendar => (JsonNode)calendar.DeepClone()).ToArray()) }));
            http.Answer("/me/calendars/CAL1/calendarView", request =>
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                var from = DateTimeOffset.Parse(query["startDateTime"]!);
                var to = DateTimeOffset.Parse(query["endDateTime"]!);
                var inRange = Events.Where(item => Start(item) < to && Start(item) >= from.AddDays(-1)).Select(item => (JsonNode)item.DeepClone());
                return Json(new JsonObject { ["value"] = new JsonArray(inRange.ToArray()) });
            });
            http.Answer("/me/calendars/CAL2/calendarView", _ => Json(new JsonObject { ["value"] = new JsonArray() }));
            http.Answer("/me/calendars/CAL1/events", _ => Json(Event("NEW1", "Review", DateTime.Today.AddDays(2).AddHours(15))), HttpMethod.Post);
            http.Answer("/me/events/", request => request.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json(Event("NEW1", "Review (neu)", DateTime.Today.AddDays(2).AddHours(15))));
        }

        public List<JsonObject> Calendars { get; } =
        [
            new() { ["id"] = "CAL1", ["name"] = "Kalender", ["hexColor"] = "#2F6FED", ["canEdit"] = true, ["isDefaultCalendar"] = true },
            new() { ["id"] = "CAL2", ["name"] = "Feiertage in Deutschland", ["color"] = "lightGreen", ["canEdit"] = false, ["isDefaultCalendar"] = false },
        ];

        public List<JsonObject> Events { get; set; }

        public JsonObject Event(string id, string title, DateTime localStart) => new()
        {
            ["id"] = id,
            ["subject"] = title,
            ["isAllDay"] = false,
            ["start"] = new JsonObject { ["dateTime"] = localStart.ToString("yyyy-MM-dd'T'HH:mm:ss.0000000"), ["timeZone"] = "local" },
            ["end"] = new JsonObject { ["dateTime"] = localStart.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss.0000000"), ["timeZone"] = "local" },
            ["isReminderOn"] = true,
            ["reminderMinutesBeforeStart"] = 15,
            ["responseStatus"] = new JsonObject { ["response"] = "organizer" },
        };

        private static DateTimeOffset Start(JsonObject item) => CalendarEvent.FromLocal(DateTime.Parse(item["start"]!["dateTime"]!.GetValue<string>()));

        private static HttpResponseMessage Json(JsonNode node) =>
            new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}
