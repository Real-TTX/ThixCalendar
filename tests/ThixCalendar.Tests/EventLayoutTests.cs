using ThixCalendar.Controls;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Tests;

/// <summary>How overlapping events share a day column.</summary>
public sealed class EventLayoutTests
{
    private static readonly DateTime Day = new(2026, 11, 16);
    private static readonly CalendarInfo Calendar = new() { Id = 1, Name = "Kalender" };
    private static readonly CalendarAccount Account = new() { Id = 1, Name = "Konto" };

    [Fact]
    public void Events_that_do_not_overlap_take_the_full_width()
    {
        var slots = EventLayout.Arrange(Day, [Item(9, 10, "A"), Item(10, 11, "B")]);
        Assert.All(slots, slot => Assert.Equal((0, 1), (slot.Lane, slot.Lanes)));
        Assert.Equal(9 * 60, slots[0].StartMinute);
    }

    [Fact]
    public void Overlapping_events_sit_side_by_side_and_free_lanes_are_reused()
    {
        var slots = EventLayout.Arrange(Day, [Item(9, 11, "A"), Item(9.5, 10, "B"), Item(10, 10.5, "C"), Item(10.25, 12, "D"), Item(13, 14, "E")]);
        var byTitle = slots.ToDictionary(slot => slot.Item.Event.Title);
        Assert.Equal(0, byTitle["A"].Lane);
        Assert.Equal(1, byTitle["B"].Lane);
        Assert.Equal(1, byTitle["C"].Lane); // B has ended: its lane is free again
        Assert.Equal(2, byTitle["D"].Lane);
        Assert.All(new[] { "A", "B", "C", "D" }, title => Assert.Equal(3, byTitle[title].Lanes));
        Assert.Equal((0, 1), (byTitle["E"].Lane, byTitle["E"].Lanes));
    }

    [Fact]
    public void Events_across_midnight_are_cut_to_the_day_and_short_ones_stay_readable()
    {
        var night = new EventItem(new CalendarEvent { Title = "Nacht", Start = CalendarEvent.FromLocal(Day.AddHours(22)), End = CalendarEvent.FromLocal(Day.AddDays(1).AddHours(2)) }, Calendar, Account);
        Assert.Equal((22 * 60, 24 * 60), Pair(EventLayout.Arrange(Day, [night]).Single()));
        Assert.Equal((0, 2 * 60), Pair(EventLayout.Arrange(Day.AddDays(1), [night]).Single()));
        var reminderOnly = new EventItem(new CalendarEvent { Title = "Frist", Start = CalendarEvent.FromLocal(Day.AddHours(17)), End = CalendarEvent.FromLocal(Day.AddHours(17)) }, Calendar, Account);
        Assert.Equal(EventLayout.MinimumMinutes, EventLayout.Arrange(Day, [reminderOnly]).Single() is var slot ? slot.EndMinute - slot.StartMinute : 0);
        Assert.Empty(EventLayout.Arrange(Day, [new EventItem(Events.AllDay(1, Day), Calendar, Account)]));
    }

    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("930", 9, 30)]
    [InlineData("9:5", 9, 5)]
    [InlineData("14.45", 14, 45)]
    [InlineData(" 08:00 Uhr", 8, 0)]
    [InlineData("0", 0, 0)]
    public void Times_are_typed_like_on_paper(string text, int hours, int minutes) =>
        Assert.Equal(new TimeSpan(hours, minutes, 0), TimeField.Parse(text));

    [Theory]
    [InlineData("24:00")]
    [InlineData("9:60")]
    [InlineData("abc")]
    [InlineData("")]
    public void Nonsense_is_no_time(string text) => Assert.Null(TimeField.Parse(text));

    private static (double, double) Pair(EventSlot slot) => (slot.StartMinute, slot.EndMinute);

    private static EventItem Item(double from, double to, string title) =>
        new(new CalendarEvent { Title = title, Start = CalendarEvent.FromLocal(Day.AddHours(from)), End = CalendarEvent.FromLocal(Day.AddHours(to)) }, Calendar, Account);
}
