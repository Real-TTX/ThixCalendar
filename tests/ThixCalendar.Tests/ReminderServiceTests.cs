using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Tests;

/// <summary>
/// The reminder rules – the reason ThixCalendar exists. Pure rules (ComputeDue) with explicit times, and the whole way
/// through the database (answers survive a restart).
/// </summary>
public sealed class ReminderServiceTests
{
    private static readonly CalendarAccount Account = new() { Id = 1, Kind = AccountKind.Microsoft, Name = "Arbeit" };
    private static readonly CalendarInfo Calendar = new() { Id = 1, AccountId = 1, Name = "Kalender" };
    private static readonly AppSettings Settings = new() { DefaultReminderMinutes = 10, RemindAtStart = true, MissedReminderMinutes = 60 };
    private static readonly DateTime Ten = DateTime.Today.AddDays(3).AddHours(10);

    [Fact]
    public void The_lead_time_reminder_comes_at_its_time_and_not_before()
    {
        var item = Item(Ten);
        Assert.Empty(Due(Ten.AddMinutes(-11), item));
        var due = Assert.Single(Due(Ten.AddMinutes(-10), item));
        Assert.Equal(ReminderStage.Before, due.Stage);
        Assert.Equal(CalendarEvent.FromLocal(Ten.AddMinutes(-10)), due.Trigger);
    }

    [Fact]
    public void The_start_reminder_comes_after_the_lead_time_one_was_closed()
    {
        var item = Item(Ten);
        var before = Assert.Single(Due(Ten.AddMinutes(-9), item));
        var states = Answered(before, dismissed: true);
        Assert.Empty(Due(Ten.AddMinutes(-5), item, states));
        var start = Assert.Single(Due(Ten, item, states));
        Assert.Equal(ReminderStage.Start, start.Stage);
    }

    [Fact]
    public void Without_the_start_reminder_a_closed_reminder_stays_closed()
    {
        var item = Item(Ten);
        var settings = Settings with { RemindAtStart = false };
        var before = Assert.Single(ReminderService.ComputeDue(Local(Ten.AddMinutes(-9)), [item], settings, Empty, _ => false));
        Assert.Empty(ReminderService.ComputeDue(Local(Ten.AddMinutes(5)), [item], settings, Answered(before, dismissed: true), _ => true));
    }

    [Fact]
    public void An_unanswered_reminder_stays_until_the_event_is_over()
    {
        var item = Item(Ten, hours: 1);
        Assert.Single(Due(Ten.AddMinutes(-10), item));
        Assert.Single(Due(Ten.AddMinutes(45), item, shown: true));
        Assert.Empty(Due(Ten.AddMinutes(61), item, shown: true));
    }

    [Fact]
    public void Snoozing_holds_it_back_but_the_start_breaks_through()
    {
        var item = Item(Ten);
        var before = Assert.Single(Due(Ten.AddMinutes(-10), item));
        var snoozed = new Dictionary<string, ReminderState>
        {
            [before.Key] = new(before.Key, null, Local(Ten.AddMinutes(5)), Local(Ten.AddMinutes(-9))),
        };
        Assert.Empty(Due(Ten.AddMinutes(-2), item, snoozed));
        Assert.Equal(ReminderStage.Start, Assert.Single(Due(Ten, item, snoozed)).Stage);
    }

    [Fact]
    public void A_snooze_ends_at_its_time()
    {
        var item = Item(Ten, hours: 2);
        var settings = Settings with { RemindAtStart = false };
        var before = Assert.Single(ReminderService.ComputeDue(Local(Ten.AddMinutes(-10)), [item], settings, Empty, _ => false));
        var snoozed = new Dictionary<string, ReminderState> { [before.Key] = new(before.Key, null, Local(Ten.AddMinutes(-3)), Local(Ten.AddMinutes(-8))) };
        Assert.Empty(ReminderService.ComputeDue(Local(Ten.AddMinutes(-4)), [item], settings, snoozed, _ => true));
        Assert.Single(ReminderService.ComputeDue(Local(Ten.AddMinutes(-3)), [item], settings, snoozed, _ => true));
    }

    [Fact]
    public void Missed_while_the_computer_was_off_comes_up_to_the_missed_window()
    {
        var item = Item(Ten, hours: 3);
        Assert.Equal(ReminderStage.Start, Assert.Single(Due(Ten.AddMinutes(20), item)).Stage);
        Assert.Empty(Due(Ten.AddMinutes(90), item)); // long ago and never shown: not dug out
        Assert.Single(Due(Ten.AddMinutes(90), item, shown: true)); // shown and not answered: stays
    }

    [Fact]
    public void Declined_cancelled_and_silenced_calendars_remind_nobody()
    {
        Assert.Empty(Due(Ten, Item(Ten) with { Event = Item(Ten).Event with { Response = ResponseStatus.Declined } }));
        Assert.Empty(Due(Ten, Item(Ten) with { Event = Item(Ten).Event with { Status = EventStatus.Cancelled } }));
        Assert.Empty(Due(Ten, Item(Ten) with { Calendar = Calendar with { RemindersEnabled = false } }));
        var declined = Item(Ten) with { Event = Item(Ten).Event with { Response = ResponseStatus.Declined } };
        Assert.Single(ReminderService.ComputeDue(Local(Ten), [declined], Settings with { SkipDeclined = false }, Empty, _ => false));
    }

    [Fact]
    public void The_events_own_lead_time_wins_over_the_calendar_and_the_setting()
    {
        var own = Item(Ten, reminder: 30);
        Assert.Single(Due(Ten.AddMinutes(-30), own));
        var calendarDefault = Item(Ten) with { Calendar = Calendar with { DefaultReminderMinutes = 20 } };
        Assert.Empty(Due(Ten.AddMinutes(-21), calendarDefault));
        Assert.Single(Due(Ten.AddMinutes(-20), calendarDefault));
        Assert.Equal(10, ReminderService.LeadMinutes(Item(Ten).Event, Calendar, Settings));
    }

    [Fact]
    public void A_reminder_at_the_start_only_comes_once()
    {
        var item = Item(Ten, reminder: 0);
        Assert.Empty(Due(Ten.AddMinutes(-1), item));
        var due = Assert.Single(Due(Ten, item));
        Assert.Equal(ReminderStage.Start, due.Stage);
        Assert.Empty(Due(Ten.AddMinutes(5), item, Answered(due, dismissed: true), shown: true));
    }

    [Fact]
    public void All_day_events_remind_on_their_first_day_at_the_chosen_time()
    {
        var day = DateTime.Today.AddDays(2);
        var item = new EventItem(Events.AllDay(1, day, days: 2) with { Id = 5, RemoteId = "u" }, Calendar, Account);
        Assert.Empty(Due(day.AddHours(8.9), item));
        Assert.Equal(ReminderStage.AllDay, Assert.Single(Due(day.AddHours(9), item)).Stage);
        Assert.Single(Due(day.AddDays(1).AddHours(12), item, shown: true));
        Assert.Empty(Due(day.AddDays(2), item, shown: true));
        Assert.Empty(ReminderService.ComputeDue(Local(day.AddHours(10)), [item], Settings with { AllDayReminderMinuteOfDay = null }, Empty, _ => false));
    }

    [Fact]
    public async Task Answers_survive_a_restart_and_a_resync()
    {
        using var data = new TestData();
        var (_, calendar) = TestData.LocalCalendar();
        var start = DateTime.Now.AddMinutes(5);
        var saved = await CalendarService.SaveEventAsync(Events.At(calendar.Id, start, title: "Gleich"));

        var appeared = ReminderService.Check(DateTimeOffset.Now);
        var reminder = Assert.Single(appeared);
        Assert.Equal(saved.Id, reminder.Event.Id);
        Assert.Empty(ReminderService.Check(DateTimeOffset.Now)); // already shown: not "new" again
        Assert.Single(ReminderService.Active);

        ReminderService.Dismiss(reminder);
        Assert.Empty(ReminderService.Active);
        ReminderService.ResetForTests(); // a restart
        ReminderService.Check(DateTimeOffset.Now);
        Assert.Empty(ReminderService.Active);

        // At the start it comes again (the second stage).
        ReminderService.Check(CalendarEvent.FromLocal(start).AddSeconds(1));
        Assert.Equal(ReminderStage.Start, Assert.Single(ReminderService.Active).Stage);
    }

    [Fact]
    public async Task A_pause_holds_reminders_back_and_they_come_when_it_ends()
    {
        using var data = new TestData();
        var (_, calendar) = TestData.LocalCalendar();
        await CalendarService.SaveEventAsync(Events.At(calendar.Id, DateTime.Now.AddMinutes(3)));
        SettingsService.PauseReminders(DateTimeOffset.Now.AddMinutes(30));
        Assert.Empty(ReminderService.Check(DateTimeOffset.Now));
        Assert.True(ReminderService.IsPaused(DateTimeOffset.Now));
        SettingsService.PauseReminders(null);
        Assert.Single(ReminderService.Check(DateTimeOffset.Now));
    }

    [Fact]
    public void Snooze_choices_offer_the_start_while_it_is_ahead()
    {
        var item = Item(Ten);
        var due = new DueReminder(item, ReminderStage.Before, Local(Ten.AddMinutes(-10)));
        var choices = ReminderService.SnoozeChoices(due, Local(Ten.AddMinutes(-10)));
        Assert.Equal("Bis zum Beginn", choices[0].Label);
        Assert.Equal(Local(Ten), choices[0].Until);
        Assert.All(choices, choice => Assert.True(choice.Until > Local(Ten.AddMinutes(-10))));
        Assert.DoesNotContain(ReminderService.SnoozeChoices(due, Local(Ten.AddMinutes(1))), choice => choice.Label == "Bis zum Beginn");
    }

    private static readonly Dictionary<string, ReminderState> Empty = [];

    private static EventItem Item(DateTime start, double hours = 1, int? reminder = null) =>
        new(Events.At(1, start, hours, reminder: reminder) with { Id = 7, RemoteId = "remote-7" }, Calendar, Account);

    private static DateTimeOffset Local(DateTime value) => CalendarEvent.FromLocal(value);

    private static List<DueReminder> Due(DateTime now, EventItem item, IReadOnlyDictionary<string, ReminderState>? states = null, bool shown = false) =>
        ReminderService.ComputeDue(Local(now), [item], Settings, states ?? Empty, _ => shown);

    private static Dictionary<string, ReminderState> Answered(DueReminder reminder, bool dismissed) =>
        new() { [reminder.Key] = new ReminderState(reminder.Key, dismissed ? reminder.Trigger : null, null, null) };
}
