using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Tests;

/// <summary>Events and calendars through the business layer: validation, permissions, queries.</summary>
public sealed class CalendarServiceTests
{
    [Fact]
    public async Task A_local_event_is_created_changed_and_deleted()
    {
        using var data = new TestData();
        var (_, calendar) = TestData.LocalCalendar();
        var start = DateTime.Today.AddDays(1).AddHours(9);
        var saved = await CalendarService.SaveEventAsync(Events.At(calendar.Id, start, title: "  Zahnarzt  ") with { Location = "Praxis" });
        Assert.True(saved.Id > 0);
        Assert.Equal("Zahnarzt", saved.Title);

        var changed = await CalendarService.SaveEventAsync(saved with { Title = "Zahnarzt (Kontrolle)", End = saved.End.AddMinutes(30) });
        Assert.Equal(saved.Id, changed.Id);
        var item = CalendarService.GetEvent(saved.Id)!;
        Assert.Equal("Zahnarzt (Kontrolle)", item.Event.Title);
        Assert.Equal(TimeSpan.FromMinutes(90), item.Event.End - item.Event.Start);

        await CalendarService.DeleteEventAsync(saved.Id);
        Assert.Null(CalendarService.GetEvent(saved.Id));
    }

    [Fact]
    public async Task Wrong_input_is_named_at_its_field()
    {
        using var data = new TestData();
        var (_, calendar) = TestData.LocalCalendar();
        var start = DateTime.Today.AddDays(1).AddHours(9);
        var noTitle = await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(Events.At(calendar.Id, start, title: " ")));
        Assert.Equal("Title", noTitle.Field);
        var backwards = await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(Events.At(calendar.Id, start) with { End = CalendarEvent.FromLocal(start.AddHours(-1)) }));
        Assert.Equal("End", backwards.Field);
        var badLink = await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(Events.At(calendar.Id, start) with { MeetingUrl = "teams" }));
        Assert.Equal("MeetingUrl", badLink.Field);
        var noCalendar = await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(Events.At(999, start)));
        Assert.Equal("CalendarId", noCalendar.Field);
    }

    [Fact]
    public async Task The_meeting_link_is_found_in_location_and_notes()
    {
        using var data = new TestData();
        var (_, calendar) = TestData.LocalCalendar();
        var saved = await CalendarService.SaveEventAsync(Events.At(calendar.Id, DateTime.Today.AddHours(30)) with
        {
            Description = "Einwahl: https://us02web.zoom.us/j/123456789?pwd=abc. Bis dann!",
        });
        Assert.Equal("https://us02web.zoom.us/j/123456789?pwd=abc", saved.MeetingUrl);
    }

    [Fact]
    public async Task A_subscribed_calendar_cannot_be_changed()
    {
        using var data = new TestData();
        data.Http.Text("example.org/feiertage.ics", Ics.Holidays);
        var account = await AccountService.AddIcsAsync("", "webcal://example.org/feiertage.ics");
        Assert.Equal("Feiertage", account.Name);
        Assert.Equal("https://example.org/feiertage.ics", account.Url);
        var calendar = Assert.Single(AccountService.CalendarsOf(account.Id));
        Assert.True(calendar.IsReadOnly);
        var holiday = CalendarService.Query(new EventQuery { From = Ics.HolidayDate, To = Ics.HolidayDate.AddDays(1), IncludeHidden = true }).Single();
        Assert.True(holiday.Event.IsAllDay);

        var change = await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(holiday.Event with { Title = "Anders" }));
        Assert.Contains("nur gelesen", change.Message);
        await Assert.ThrowsAsync<BusinessException>(() => CalendarService.DeleteEventAsync(holiday.Event.Id));
        await Assert.ThrowsAsync<BusinessException>(() => CalendarService.SaveEventAsync(Events.At(calendar.Id, DateTime.Today.AddHours(30))));
        Assert.DoesNotContain(CalendarService.WritableCalendars(), pair => pair.Calendar.Id == calendar.Id);
    }

    [Fact]
    public async Task Moving_an_event_to_another_calendar_keeps_it_once()
    {
        using var data = new TestData();
        var (account, first) = TestData.LocalCalendar();
        var second = AccountService.AddLocalCalendar(account.Id, "Sport");
        var saved = await CalendarService.SaveEventAsync(Events.At(first.Id, DateTime.Today.AddHours(33), title: "Laufen"));
        var moved = await CalendarService.SaveEventAsync(saved with { CalendarId = second.Id });
        var all = CalendarService.Query(new EventQuery { From = DateTime.Today, To = DateTime.Today.AddDays(3), Search = "laufen" });
        var only = Assert.Single(all);
        Assert.Equal(second.Id, only.Calendar.Id);
        Assert.Equal(moved.Id, only.Event.Id);
    }

    [Fact]
    public async Task Queries_filter_by_range_visibility_and_words()
    {
        using var data = new TestData();
        var (account, privat) = TestData.LocalCalendar();
        var work = AccountService.AddLocalCalendar(account.Id, "Arbeit");
        var tomorrow = DateTime.Today.AddDays(1);
        await CalendarService.SaveEventAsync(Events.At(privat.Id, tomorrow.AddHours(18), title: "Kino mit Lisa") with { Location = "Cinedom" });
        await CalendarService.SaveEventAsync(Events.At(work.Id, tomorrow.AddHours(9), title: "Sprint Planning"));
        await CalendarService.SaveEventAsync(Events.AllDay(privat.Id, tomorrow, title: "Urlaub"));
        await CalendarService.SaveEventAsync(Events.At(work.Id, tomorrow.AddDays(10).AddHours(9), title: "Später"));

        Assert.Equal(3, CalendarService.Range(tomorrow, tomorrow.AddDays(1)).Count);
        Assert.Equal("Urlaub", CalendarService.Range(tomorrow, tomorrow.AddDays(1))[0].Event.Title); // all-day first
        Assert.Single(CalendarService.Query(new EventQuery { From = tomorrow, To = tomorrow.AddDays(1), Search = "lisa cinedom" }));
        Assert.Single(CalendarService.Query(new EventQuery { From = tomorrow, To = tomorrow.AddDays(1), Search = "arbeit" })); // calendar name

        CalendarService.SetCalendarVisible(work.Id, false);
        Assert.Equal(2, CalendarService.Range(tomorrow, tomorrow.AddDays(1)).Count);
        Assert.Single(CalendarService.Query(new EventQuery { From = tomorrow, To = tomorrow.AddDays(1), CalendarId = work.Id }));
        CalendarService.SetAccountVisible(account.Id, false);
        Assert.Empty(CalendarService.Range(tomorrow, tomorrow.AddDays(1)));
    }

    [Fact]
    public void All_day_events_stay_on_their_dates()
    {
        var calendarEvent = Events.AllDay(1, new DateTime(2026, 7, 20), days: 3);
        Assert.Equal(new DateTime(2026, 7, 20), calendarEvent.StartLocal);
        Assert.Equal(new DateTime(2026, 7, 23), calendarEvent.EndLocal);
        Assert.True(calendarEvent.Touches(new DateTime(2026, 7, 22)));
        Assert.False(calendarEvent.Touches(new DateTime(2026, 7, 23)));
    }

    [Fact]
    public void A_locked_configuration_blocks_changes_of_accounts_and_settings()
    {
        using var data = new TestData();
        AccessPolicy.UsePolicyReaderForTests(name => name == "LockConfiguration");
        Assert.Throws<BusinessException>(() => AccountService.AddLocal("Privat"));
        Assert.Throws<BusinessException>(() => SettingsService.Update(settings => settings with { DefaultReminderMinutes = 5 }));
        // Everyday switches stay: pausing reminders, the view, the window size.
        SettingsService.PauseReminders(DateTimeOffset.Now.AddMinutes(10));
        SettingsService.Remember(settings => settings with { ViewMode = CalendarViewMode.Month });
        Assert.Equal(CalendarViewMode.Month, SettingsService.Get().ViewMode);
    }

    [Fact]
    public async Task Online_accounts_can_be_forbidden_by_policy()
    {
        using var data = new TestData();
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableOnlineAccounts");
        await Assert.ThrowsAsync<BusinessException>(() => AccountService.AddIcsAsync("x", "https://example.org/a.ics"));
        await Assert.ThrowsAsync<BusinessException>(() => AccountService.AddMicrosoftAsync(_ => { }));
        Assert.NotNull(AccountService.AddLocal("Privat"));
    }

    [Fact]
    public void Settings_are_checked()
    {
        using var data = new TestData();
        Assert.Equal("MicrosoftClientId", Assert.Throws<BusinessException>(() => SettingsService.Update(settings => settings with { MicrosoftClientId = "abc" })).Field);
        Assert.Equal("GoogleClientId", Assert.Throws<BusinessException>(() => SettingsService.Update(settings => settings with { GoogleClientId = "abc" })).Field);
        Assert.Equal("GoogleClientSecret", Assert.Throws<BusinessException>(() =>
            SettingsService.Update(settings => settings with { GoogleClientId = "1-x.apps.googleusercontent.com" })).Field);
        SettingsService.Update(settings => settings with { MicrosoftClientId = " 11111111-2222-3333-4444-555555555555 " });
        Assert.Equal("11111111-2222-3333-4444-555555555555", SettingsService.MicrosoftClientId);
        Assert.True(AccountService.IsConfigured(AccountKind.Microsoft));
    }

    [Fact]
    public void A_local_account_keeps_at_least_one_calendar()
    {
        using var data = new TestData();
        var (account, calendar) = TestData.LocalCalendar();
        Assert.Throws<BusinessException>(() => AccountService.DeleteLocalCalendar(calendar.Id));
        var second = AccountService.AddLocalCalendar(account.Id, "Sport");
        AccountService.DeleteLocalCalendar(second.Id);
        Assert.Single(AccountService.CalendarsOf(account.Id));
        var renamed = CalendarService.UpdateCalendar(calendar.Id, "Familie", "#DC2626", remindersEnabled: false);
        Assert.Equal(("Familie", "#DC2626", false, true), (renamed.Name, renamed.Color, renamed.RemindersEnabled, renamed.IsColorCustom));
    }
}
