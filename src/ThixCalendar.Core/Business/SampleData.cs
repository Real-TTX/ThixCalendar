using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Business;

/// <summary>
/// A week like the mockup's, in local calendars ("Arbeit", "Privat", "Sport") – for the UI tests, the screenshots and a
/// development run (THIXCALENDAR_SAMPLE_DATA=1 with its own THIXCALENDAR_DATA_DIR). Never used on real data.
/// </summary>
internal static class SampleData
{
    public const string Variable = "THIXCALENDAR_SAMPLE_DATA";

    /// <summary>Fills an empty database with the sample week around <paramref name="today"/>.</summary>
    public static void Create(DateTime today)
    {
        if (CalendarStore.LoadAccounts().Count > 0)
        {
            return;
        }

        var accountId = CalendarStore.InsertAccount(new CalendarAccount { Kind = AccountKind.Local, Name = "Eigene Kalender", State = AccountState.Ok, LastSync = DateTimeOffset.Now });
        var work = CalendarStore.InsertCalendar(new CalendarInfo { AccountId = accountId, Name = "Arbeit", Color = "#2F6FED", IsPrimary = true, SortOrder = 0 });
        var home = CalendarStore.InsertCalendar(new CalendarInfo { AccountId = accountId, Name = "Privat", Color = "#16A34A", SortOrder = 1 });
        var sport = CalendarStore.InsertCalendar(new CalendarInfo { AccountId = accountId, Name = "Projekte", Color = "#9333EA", SortOrder = 2 });
        var family = CalendarStore.InsertCalendar(new CalendarInfo { AccountId = accountId, Name = "Familie", Color = "#F97316", SortOrder = 3 });

        var monday = today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        void Add(long calendar, int day, double fromHour, double toHour, string title, string location = "", string meeting = "")
        {
            var start = monday.AddDays(day).AddHours(fromHour);
            CalendarStore.InsertEvent(new CalendarEvent
            {
                CalendarId = calendar,
                Title = title,
                Start = CalendarEvent.FromLocal(start),
                End = CalendarEvent.FromLocal(monday.AddDays(day).AddHours(toHour)),
                Location = location,
                MeetingUrl = meeting,
            });
        }

        Add(work, 0, 8, 9, "Team-Meeting", "", "https://teams.microsoft.com/l/meetup-join/sample");
        Add(work, 0, 14, 16, "Projekt-Workshop", "Raum Rhein");
        Add(home, 0, 12.5, 13.5, "Mittagessen mit Lisa", "Café am Markt");
        Add(family, 1, 10.5, 11.5, "Kunden-Call", "", "https://meet.google.com/abc-defg-hij");
        Add(family, 2, 17, 18, "Geburtstag Anna 🎉");
        Add(home, 2, 9.5, 10.5, "Arzttermin", "Praxis Dr. Weber");
        Add(home, 2, 12, 13, "Fitness");
        Add(work, 3, 11, 12, "Sprint Planning", "", "https://teams.microsoft.com/l/meetup-join/sample2");
        Add(work, 3, 15, 15.5, "1:1 Ich & TL");
        Add(sport, 4, 9, 10, "Projekt-Review");
        Add(family, 4, 12.5, 13.5, "Team-Lunch", "Kantine");
        Add(home, 5, 16, 18, "Wanderung", "Siebengebirge");
        CalendarStore.InsertEvent(new CalendarEvent
        {
            CalendarId = home,
            Title = "Urlaub planen",
            IsAllDay = true,
            Start = CalendarEvent.AllDayDate(monday.AddDays(4)),
            End = CalendarEvent.AllDayDate(monday.AddDays(5)),
        });
        Log.Info("Sample data created");
    }
}
