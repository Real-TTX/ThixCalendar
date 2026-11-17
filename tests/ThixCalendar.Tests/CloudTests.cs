using System.Text.Json;
using MatCloud.Contract;
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
