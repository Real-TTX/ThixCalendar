using System.Text.Json;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Tests;

/// <summary>
/// MatCloud: signing in (browser, password, the hand-over of ThixLauncher), and two computers of one person that meet in the
/// general sync – a cloud in memory (<see cref="FakeCloud"/>), each computer a data folder of its own.
/// </summary>
public sealed class CloudTests : IDisposable
{
    private const string Server = "https://cloud.example.com";

    private readonly TestData _data = new();
    private readonly FakeCloud _cloud = new();
    private readonly string _laptop;
    private readonly string _desktop;

    public CloudTests()
    {
        _laptop = _data.Folder;
        _desktop = Path.Combine(_data.Folder, "desktop");
        Directory.CreateDirectory(_desktop);
        CloudHandoff.Folder = Path.Combine(_data.Folder, "handoff");
        CloudService.HandlerForTests = () => _cloud;
        CloudService.LoopDisabledForTests = true;
        CloudService.ResetForTests();
    }

    public void Dispose()
    {
        CloudService.ResetForTests();
        CloudService.HandlerForTests = null;
        CloudService.LoopDisabledForTests = false;
        CloudHandoff.Folder = CloudHandoff.DefaultFolder();
        UseComputer(_laptop);
        _data.Dispose();
    }

    [Fact]
    public async Task The_browser_sign_in_connects_this_computer_with_the_code_confirmed_there()
    {
        string? opened = null;
        var connecting = CloudService.ConnectInBrowserAsync(Server, "LAPTOP", start => _cloud.Approve(start.Code), address => opened = address);

        Assert.True(await connecting);
        Assert.StartsWith(Server, opened);
        var cloud = SettingsService.Get().Cloud;
        Assert.True(cloud.IsConnected);
        Assert.Equal(FakeCloud.UserName, cloud.UserName);
        Assert.Equal("LAPTOP", cloud.DeviceName);
        Assert.Contains(_cloud.UserAgents, agent => agent.StartsWith("ThixCalendar/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_password_is_said_at_the_password()
    {
        var failure = await Assert.ThrowsAsync<BusinessException>(() => CloudService.ConnectAsync(Server, FakeCloud.UserName, "falsch", "LAPTOP"));

        Assert.Equal("Password", failure.Field);
        Assert.False(CloudService.IsConnected);
    }

    [Fact]
    public async Task The_sign_in_ThixLauncher_left_connects_once_without_the_browser()
    {
        var pollToken = _cloud.LeaveHandoff("LAPTOP");
        CloudHandoff.Write(CloudApps.Calendar, new DeviceHandoff(Server, pollToken, FakeCloud.UserName, DateTimeOffset.UtcNow.AddHours(1)));

        var account = await CloudService.ConnectFromLauncherAsync();

        Assert.Equal(FakeCloud.DisplayName, account);
        Assert.True(CloudService.IsConnected);
        Assert.False(CloudHandoff.Exists(CloudApps.Calendar));
        Assert.Null(await CloudService.ConnectFromLauncherAsync());
    }

    [Fact]
    public async Task The_other_computer_gets_the_accounts_the_own_calendars_their_events_and_the_settings()
    {
        var (own, calendar) = TestData.LocalCalendar("Familie");
        var party = await CalendarService.SaveEventAsync(CalendarService.NewEvent(DateTime.Today.AddDays(3).AddHours(18), calendarId: calendar.Id) with { Title = "Geburtstag Oma", Location = "Köln" });
        CalendarStore.InsertAccount(new CalendarAccount { Kind = AccountKind.Microsoft, Name = "Arbeit", UserName = "anna@firma.de", State = AccountState.Ok });
        SettingsService.Update(settings => settings with { DefaultReminderMinutes = 30, RepeatSoundMinutes = 2 });
        await SignInAsync("LAPTOP");
        await CloudService.SyncOnceAsync();

        UseComputer(_desktop);
        await SignInAsync("DESKTOP");
        await CloudService.SyncOnceAsync();

        var accounts = AccountService.List();
        Assert.Equal(2, accounts.Count);
        var familie = accounts.Single(account => account.Kind == AccountKind.Local);
        Assert.Equal(own.SyncId, familie.SyncId);
        var work = accounts.Single(account => account.Kind == AccountKind.Microsoft);
        Assert.Equal(AccountState.SignedOut, work.State); // the sign-in never travels: "Neu anmelden" on this computer
        Assert.Equal("anna@firma.de", work.UserName);
        var calendars = AccountService.CalendarsOf(familie.Id);
        Assert.Equal(calendar.Name, Assert.Single(calendars).Name);
        var events = CalendarService.Range(DateTime.Today, DateTime.Today.AddDays(7));
        var copy = Assert.Single(events).Event;
        Assert.Equal("Geburtstag Oma", copy.Title);
        Assert.Equal("Köln", copy.Location);
        Assert.Equal(party.Start, copy.Start);
        Assert.Equal(30, SettingsService.Get().DefaultReminderMinutes);
        Assert.Equal(2, SettingsService.Get().RepeatSoundMinutes);
    }

    [Fact]
    public async Task A_reminder_answered_on_one_computer_is_answered_on_the_other()
    {
        var (_, calendar) = TestData.LocalCalendar();
        var soon = DateTimeOffset.Now.AddMinutes(5);
        await CalendarService.SaveEventAsync(CalendarService.NewEvent(soon.LocalDateTime, calendarId: calendar.Id) with { Title = "Zahnarzt" });
        await SignInAsync("LAPTOP");
        await CloudService.SyncOnceAsync();

        UseComputer(_desktop);
        await SignInAsync("DESKTOP");
        await CloudService.SyncOnceAsync();
        Assert.Single(ReminderService.Check(DateTimeOffset.Now));

        UseComputer(_laptop);
        var due = Assert.Single(ReminderService.Check(DateTimeOffset.Now));
        ReminderService.Dismiss(due);
        await CloudService.SyncOnceAsync();

        UseComputer(_desktop);
        Assert.Single(ReminderService.Check(DateTimeOffset.Now));
        await CloudService.SyncOnceAsync();
        ReminderService.Refresh();
        Assert.Empty(ReminderService.Active);
        Assert.Empty(ReminderService.Check(DateTimeOffset.Now));
    }

    [Fact]
    public async Task A_deleted_event_goes_on_every_computer_and_a_later_change_wins()
    {
        var (_, calendar) = TestData.LocalCalendar();
        var first = await CalendarService.SaveEventAsync(CalendarService.NewEvent(DateTime.Today.AddDays(1).AddHours(9), calendarId: calendar.Id) with { Title = "Laufen" });
        var second = await CalendarService.SaveEventAsync(CalendarService.NewEvent(DateTime.Today.AddDays(2).AddHours(9), calendarId: calendar.Id) with { Title = "Lesen" });
        await SignInAsync("LAPTOP");
        await CloudService.SyncOnceAsync();

        UseComputer(_desktop);
        await SignInAsync("DESKTOP");
        await CloudService.SyncOnceAsync();
        var copies = CalendarService.Range(DateTime.Today, DateTime.Today.AddDays(7));
        await CalendarService.DeleteEventAsync(copies.Single(item => item.Event.Title == "Laufen").Event.Id);
        var reading = copies.Single(item => item.Event.Title == "Lesen").Event;
        await CalendarService.SaveEventAsync(reading with { Title = "Lesen im Park" });
        await CloudService.SyncOnceAsync();

        UseComputer(_laptop);
        await CloudService.SyncOnceAsync();
        var left = Assert.Single(CalendarService.Range(DateTime.Today, DateTime.Today.AddDays(7))).Event;
        Assert.Equal(second.Id, left.Id);
        Assert.Equal("Lesen im Park", left.Title);
        Assert.Null(CalendarService.GetEvent(first.Id));
    }

    [Fact]
    public async Task What_was_chosen_for_a_calendar_waits_until_the_account_signed_in_here()
    {
        var accountId = CalendarStore.InsertAccount(new CalendarAccount { Kind = AccountKind.Google, Name = "Privat", UserName = "anna@gmail.com", State = AccountState.Ok });
        var calendarId = CalendarStore.InsertCalendar(new CalendarInfo { AccountId = accountId, RemoteId = "family123@group.calendar.google.com", Name = "Familie", Color = "#16A34A" });
        CalendarService.SetCalendarVisible(calendarId, false);
        await SignInAsync("LAPTOP");
        await CloudService.SyncOnceAsync();

        UseComputer(_desktop);
        await SignInAsync("DESKTOP");
        await CloudService.SyncOnceAsync();
        var google = Assert.Single(AccountService.List());
        Assert.Empty(AccountService.CalendarsOf(google.Id));

        // the person signs in here; the first sync of the account brings its calendars – shown, as Google says
        CalendarStore.InsertCalendar(new CalendarInfo { AccountId = google.Id, RemoteId = "family123@group.calendar.google.com", Name = "Familie", Color = "#16A34A" });
        await CloudService.SyncOnceAsync();

        Assert.False(Assert.Single(AccountService.CalendarsOf(google.Id)).IsVisible);
        var item = Assert.Single(_cloud.Live(CloudItems.Calendars));
        Assert.False(JsonSerializer.Deserialize<CloudItems.CalendarData>(item.Data!, CloudContract.Json)!.IsVisible);
    }

    [Fact]
    public async Task A_device_removed_in_the_cloud_signs_out_and_keeps_its_calendars()
    {
        TestData.LocalCalendar();
        await SignInAsync("LAPTOP");
        _cloud.RemoveAllDevices();

        await CloudService.SyncOnceAsync();

        Assert.False(CloudService.IsConnected);
        Assert.Equal(CloudState.SignedOut, CloudService.Status.State);
        Assert.Single(AccountService.List());
    }

    [Fact]
    public async Task The_policy_DisableCloud_refuses_the_sign_in()
    {
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableCloud");

        await Assert.ThrowsAsync<BusinessException>(() => CloudService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, "LAPTOP"));
    }

    [Fact]
    public async Task A_new_account_goes_to_MatCloud_and_this_computer_forgets_its_sign_in()
    {
        await SignInAsync("LAPTOP");
        SignInAtMicrosoft();

        var account = await AccountService.AddMicrosoftAsync(_ => { });

        Assert.Equal(AccountSyncMode.Cloud, account.SyncMode);
        var handover = _cloud.HandedOver[account.SyncId];
        Assert.Equal((CalendarAccountKind.Microsoft, "refresh-1", "anna@firma.de"), (handover.Kind, handover.RefreshToken, handover.UserName));
        Assert.False(AccountService.HasSignIn(account));
        var item = _cloud.Live(CloudItems.Accounts).Single(candidate => candidate.Key == account.SyncId); // went up before the hand-over
        Assert.Equal(AccountSyncMode.Cloud, JsonSerializer.Deserialize<CloudItems.AccountData>(item.Data!, CloudContract.Json)!.SyncMode);
    }

    [Fact]
    public async Task With_the_default_on_the_computers_a_new_account_keeps_its_sign_in_here()
    {
        await SignInAsync("LAPTOP");
        SettingsService.Update(settings => settings with { DefaultSyncMode = AccountSyncMode.Computers });
        SignInAtMicrosoft();

        var account = await AccountService.AddMicrosoftAsync(_ => { });

        Assert.Equal(AccountSyncMode.Computers, account.SyncMode);
        Assert.Empty(_cloud.HandedOver);
        Assert.True(AccountService.HasSignIn(account));
    }

    [Fact]
    public async Task The_events_MatCloud_keeps_arrive_without_a_sign_in_and_changes_go_through_MatCloud()
    {
        await SignInAsync("LAPTOP");
        var (calendarKey, eventKey) = AccountKeptByCloud("acc-cloud");
        await CloudService.SyncOnceAsync();

        var account = Assert.Single(AccountService.List());
        Assert.Equal((AccountSyncMode.Cloud, AccountState.Ok), (account.SyncMode, account.State)); // no "Neu anmelden" on this computer
        var meeting = Assert.Single(CalendarService.Range(DateTime.Today, DateTime.Today.AddDays(3))).Event;
        Assert.Equal("Team-Meeting", meeting.Title);
        Assert.True(Assert.Single(AccountService.CalendarsOf(account.Id)).IsPrimary);

        // a change goes to MatCloud (and so to Microsoft) first; it stays one event when MatCloud's sync brings it back
        await CalendarService.SaveEventAsync(meeting with { Title = "Team-Meeting (verschoben)" });
        Assert.Equal(eventKey, Assert.Single(_cloud.EventChanges).Event);
        await CloudService.SyncOnceAsync();
        Assert.Equal("Team-Meeting (verschoben)", Assert.Single(CalendarService.Range(DateTime.Today, DateTime.Today.AddDays(3))).Event.Title);
        Assert.DoesNotContain(_cloud.Items.Values, item => SyncContract.IsServerCollection(item.Collection) && item.Source == "LAPTOP");

        // gone at the provider: gone here
        _cloud.PutFromServer<CalendarCloudEvent>(CalendarContract.CloudEvents, eventKey, null);
        await CloudService.SyncOnceAsync();
        Assert.Empty(CalendarService.Range(DateTime.Today, DateTime.Today.AddDays(3)));
        Assert.NotEmpty(calendarKey);
    }

    [Fact]
    public async Task Back_to_the_computers_MatCloud_lets_go_and_this_computer_needs_a_sign_in()
    {
        await SignInAsync("LAPTOP");
        AccountKeptByCloud("acc-cloud");
        await CloudService.SyncOnceAsync();
        var account = Assert.Single(AccountService.List());

        var back = await AccountService.SetSyncModeAsync(account.Id, AccountSyncMode.Computers);

        Assert.Equal("acc-cloud", Assert.Single(_cloud.LetGo));
        Assert.Equal((AccountSyncMode.Computers, AccountState.SignedOut), (back.SyncMode, back.State));
        await CloudService.SyncOnceAsync();
        var item = _cloud.Live(CloudItems.Accounts).Single(candidate => candidate.Key == "acc-cloud");
        Assert.Equal(AccountSyncMode.Computers, JsonSerializer.Deserialize<CloudItems.AccountData>(item.Data!, CloudContract.Json)!.SyncMode);
    }

    /// <summary>A Microsoft account another device (or the browser) let MatCloud keep, with what MatCloud's sync wrote for it.</summary>
    private (string CalendarKey, string EventKey) AccountKeptByCloud(string syncId)
    {
        _cloud.PutFromElsewhere(CloudItems.Accounts, syncId, JsonSerializer.Serialize(
            new CloudItems.AccountData(AccountKind.Microsoft, "Arbeit", "anna@firma.de", "", true, AccountSyncMode.Cloud), CloudContract.Json));
        var calendarKey = CalendarContract.CalendarKey(syncId, "CAL1");
        var eventKey = CalendarContract.EventKey(calendarKey, "E1");
        var start = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9));
        _cloud.PutFromServer(CalendarContract.CloudAccounts, syncId, new CalendarCloudAccount(CalendarAccountKind.Microsoft, "anna@firma.de", CalendarCloudState.Ready, DateTimeOffset.UtcNow));
        _cloud.PutFromServer(CalendarContract.CloudCalendars, calendarKey, new CalendarCloudCalendar(syncId, "CAL1", "Kalender", "#2F6FED", false, true, 15));
        _cloud.PutFromServer(CalendarContract.CloudEvents, eventKey, new CalendarCloudEvent { Calendar = calendarKey, RemoteId = "E1", Title = "Team-Meeting", Start = start, End = start.AddHours(1) });
        return (calendarKey, eventKey);
    }

    /// <summary>Microsoft "signs in" (ThixCalendar's own registration, as MatCloud needs it) and Graph names the account.</summary>
    private void SignInAtMicrosoft()
    {
        AccountService.SignInForTests = (_, _) => Task.FromResult(new ThixCalendar.Core.Providers.OAuthTokens("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), null));
        _data.Http.Json("graph.microsoft.com/v1.0/me?", """{"displayName":"Anna","mail":"anna@firma.de"}""");
    }

    private static Task SignInAsync(string device) => CloudService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, device);

    /// <summary>Switches to the data folder of another computer (the cloud and the token store in memory stay).</summary>
    private static void UseComputer(string folder)
    {
        CalendarStore.Close();
        AppPaths.UseDirectoryForTests(folder);
        SettingsStore.Reset();
        CloudItems.ForgetForTests();
        ReminderService.ResetForTests();
    }
}
