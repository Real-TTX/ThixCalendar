using System.Text.Json.Nodes;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar.Tests;

/// <summary>How Microsoft Graph, Google and iCalendar data become events, and what goes back to the providers.</summary>
public sealed class ProviderTests
{
    [Fact]
    public void Graph_events_are_read_in_local_time_with_reminder_answer_and_meeting()
    {
        var item = JsonNode.Parse("""
            {"id":"AAMk1","subject":"Sprint Planning","isAllDay":false,
             "start":{"dateTime":"2026-11-16T11:00:00.0000000","timeZone":"W. Europe Standard Time"},
             "end":{"dateTime":"2026-11-16T12:00:00.0000000","timeZone":"W. Europe Standard Time"},
             "location":{"displayName":"Raum 3"},"body":{"content":"Agenda"},
             "onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/19%3ameeting"},
             "isReminderOn":true,"reminderMinutesBeforeStart":15,"isCancelled":false,
             "responseStatus":{"response":"tentativelyAccepted"},"organizer":{"emailAddress":{"name":"Tim Lead"}},
             "type":"occurrence","webLink":"https://outlook.office365.com/owa/?itemid=1"}
            """)!;
        var calendarEvent = GraphProvider.ReadEvent(item)!;
        Assert.Equal(new DateTime(2026, 11, 16, 11, 0, 0), calendarEvent.StartLocal);
        Assert.Equal(TimeSpan.FromHours(1), calendarEvent.End - calendarEvent.Start);
        Assert.Equal(15, calendarEvent.ReminderMinutes);
        Assert.Equal(ResponseStatus.Tentative, calendarEvent.Response);
        Assert.StartsWith("https://teams.microsoft.com/", calendarEvent.MeetingUrl);
        Assert.Equal(("Raum 3", "Agenda", "Tim Lead", true), (calendarEvent.Location, calendarEvent.Description, calendarEvent.Organizer, calendarEvent.IsRecurring));
    }

    [Fact]
    public void Graph_all_day_cancelled_and_reminderless_events()
    {
        var allDay = GraphProvider.ReadEvent(JsonNode.Parse("""
            {"id":"A2","subject":"Urlaub","isAllDay":true,"start":{"dateTime":"2026-12-24T00:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-12-27T00:00:00.0000000","timeZone":"UTC"},"isReminderOn":false,"location":{"displayName":"https://meet.google.com/abc-defg-hij"}}
            """)!)!;
        Assert.True(allDay.IsAllDay);
        Assert.Equal(new DateTime(2026, 12, 24), allDay.StartLocal);
        Assert.Equal(new DateTime(2026, 12, 27), allDay.EndLocal);
        Assert.Null(allDay.ReminderMinutes);
        Assert.Equal("https://meet.google.com/abc-defg-hij", allDay.MeetingUrl);
        Assert.Null(GraphProvider.ReadEvent(JsonNode.Parse("""{"id":"A3","subject":"x","isCancelled":true}""")!));
    }

    [Fact]
    public void Graph_gets_only_what_changed()
    {
        var before = Events.At(1, new DateTime(2026, 11, 16, 9, 0, 0), title: "Alt") with { Description = "<b>formatiert</b>" };
        var body = GraphProvider.Body(before, before with { Title = "Neu" });
        Assert.Equal(["subject"], body.Select(pair => pair.Key));
        var full = GraphProvider.Body(null, before with { ReminderMinutes = 5 });
        Assert.Equal("2026-11-16T09:00:00", full["start"]!["dateTime"]!.GetValue<string>());
        Assert.True(full["isReminderOn"]!.GetValue<bool>());
        Assert.Equal(5, full["reminderMinutesBeforeStart"]!.GetValue<int>());
    }

    [Fact]
    public void Google_events_with_offset_dates_reminders_and_answers()
    {
        var timed = GoogleProvider.ReadEvent(JsonNode.Parse("""
            {"id":"g1","status":"confirmed","summary":"Kunden-Call","start":{"dateTime":"2026-11-17T10:30:00+01:00"},
             "end":{"dateTime":"2026-11-17T11:30:00+01:00"},"hangoutLink":"https://meet.google.com/abc-defg-hij",
             "reminders":{"useDefault":false,"overrides":[{"method":"email","minutes":1440},{"method":"popup","minutes":10},{"method":"popup","minutes":30}]},
             "attendees":[{"email":"me@example.com","self":true,"responseStatus":"declined"}],"description":"Zeile 1<br>Zeile &amp; 2",
             "recurringEventId":"series"}
            """)!)!;
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero), timed.Start.ToUniversalTime());
        Assert.Equal(30, timed.ReminderMinutes);
        Assert.Equal(ResponseStatus.Declined, timed.Response);
        Assert.Equal("https://meet.google.com/abc-defg-hij", timed.MeetingUrl);
        Assert.Equal("Zeile 1\nZeile & 2", timed.Description);
        Assert.True(timed.IsRecurring);

        var allDay = GoogleProvider.ReadEvent(JsonNode.Parse("""
            {"id":"g2","summary":"Geburtstag","start":{"date":"2026-11-19"},"end":{"date":"2026-11-20"},"reminders":{"useDefault":true},
             "organizer":{"self":true,"email":"me@example.com"}}
            """)!)!;
        Assert.True(allDay.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 19), allDay.StartLocal);
        Assert.Null(allDay.ReminderMinutes); // the calendar's default applies
        Assert.Equal(ResponseStatus.Organizer, allDay.Response);
        Assert.Null(GoogleProvider.ReadEvent(JsonNode.Parse("""{"id":"g3","status":"cancelled"}""")!));
    }

    [Fact]
    public void Google_gets_dates_for_all_day_events_and_clears_the_other_field()
    {
        var calendarEvent = Events.AllDay(1, new DateTime(2026, 12, 24), days: 2);
        var body = GoogleProvider.Body(null, calendarEvent);
        Assert.Equal("2026-12-24", body["start"]!["date"]!.GetValue<string>());
        Assert.Equal("2026-12-26", body["end"]!["date"]!.GetValue<string>());
        Assert.True(body["start"]!.AsObject().ContainsKey("dateTime"));
        Assert.Null(body["start"]!["dateTime"]);
        Assert.True(body["reminders"]!["useDefault"]!.GetValue<bool>());
    }

    [Fact]
    public void Ics_recurrences_exceptions_and_all_day_events_are_expanded()
    {
        var calendar = IcsProvider.Parse(Ics.Weekly);
        var events = IcsProvider.Expand(calendar, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var weekly = events.Where(item => item.Title == "Jour fixe").ToList();
        Assert.Equal(3, weekly.Count); // 4 occurrences, one excluded (EXDATE)
        Assert.All(weekly, item => Assert.True(item.IsRecurring));
        Assert.Equal(weekly.Count, weekly.Select(item => item.RemoteId).Distinct().Count());
        Assert.Equal(15, weekly[0].ReminderMinutes);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), weekly[0].Start);

        var holiday = Assert.Single(events, item => item.Title == "Buß- und Bettag");
        Assert.True(holiday.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 18), holiday.StartLocal);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/abc", Assert.Single(events, item => item.Title == "Online").MeetingUrl);
    }

    [Fact]
    public void Garbage_is_no_calendar()
    {
        Assert.Throws<ProviderException>(() => IcsProvider.Parse("<html>nope</html>"));
        Assert.Equal("https://example.org/a.ics", IcsProvider.NormalizeUrl(" webcal://example.org/a.ics "));
    }

    [Theory]
    [InlineData("Einwahl https://teams.microsoft.com/l/meetup-join/19%3a123/0?context=x bitte", "https://teams.microsoft.com/l/meetup-join/19%3a123/0?context=x")]
    [InlineData("Zoom: https://us02web.zoom.us/j/123456789?pwd=abc.", "https://us02web.zoom.us/j/123456789?pwd=abc")]
    [InlineData("(https://meet.google.com/abc-defg-hij)", "https://meet.google.com/abc-defg-hij")]
    [InlineData("Raum 4, 2. OG", "")]
    public void Meeting_links_are_found(string text, string expected) => Assert.Equal(expected, MeetingLinks.Find(text));

    [Fact]
    public void Calendar_colours_are_normalised()
    {
        Assert.Equal("#9FC6E7", CalendarColors.Normalize("#9fc6e7"));
        Assert.Equal("#112233", CalendarColors.Normalize("FF112233"));
        Assert.Null(CalendarColors.Normalize("blue"));
    }
}

/// <summary>iCalendar samples.</summary>
internal static class Ics
{
    /// <summary>One holiday in ten days (inside the synchronised window whenever the tests run).</summary>
    public static DateTime HolidayDate => DateTime.Today.AddDays(10);

    public static string Holidays => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//Feiertage//DE
        X-WR-CALNAME:Feiertage
        BEGIN:VEVENT
        UID:feiertag@test
        DTSTAMP:20260101T000000Z
        DTSTART;VALUE=DATE:{HolidayDate:yyyyMMdd}
        DTEND;VALUE=DATE:{HolidayDate.AddDays(1):yyyyMMdd}
        SUMMARY:Feiertag
        END:VEVENT
        END:VCALENDAR
        """;

    public const string Weekly = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//Weekly//DE
        BEGIN:VEVENT
        UID:weekly@test
        DTSTAMP:20261101T000000Z
        DTSTART:20261102T080000Z
        DTEND:20261102T090000Z
        RRULE:FREQ=WEEKLY;COUNT=4
        EXDATE:20261109T080000Z
        SUMMARY:Jour fixe
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER:-PT15M
        DESCRIPTION:Reminder
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:bettag@test
        DTSTAMP:20261101T000000Z
        DTSTART;VALUE=DATE:20261118
        DTEND;VALUE=DATE:20261119
        SUMMARY:Buß- und Bettag
        END:VEVENT
        BEGIN:VEVENT
        UID:online@test
        DTSTAMP:20261101T000000Z
        DTSTART:20261120T130000Z
        DTEND:20261120T133000Z
        SUMMARY:Online
        DESCRIPTION:Teams: https://teams.microsoft.com/l/meetup-join/abc
        END:VEVENT
        END:VCALENDAR
        """;
}
