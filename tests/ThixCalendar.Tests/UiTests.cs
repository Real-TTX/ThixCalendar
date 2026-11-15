using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Pages;
using ThixCalendar.Reminders;

[assembly: AvaloniaTestApplication(typeof(ThixCalendar.Tests.UiTestApp))]

namespace ThixCalendar.Tests;

/// <summary>The real app (App.axaml, themes, pages) rendered headless with Skia: real pixels, no screen needed.</summary>
public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The main window driven like a user would, and screenshots of every page in light and dark (set
/// THIXCALENDAR_SCREENSHOTS to a folder to keep them; docs/images has the ones of the README).
/// </summary>
public sealed class UiTests
{
    private static readonly string? ShotFolder = Environment.GetEnvironmentVariable("THIXCALENDAR_SCREENSHOTS");

    [AvaloniaFact]
    public void The_week_shows_the_events_and_the_keys_move_through_time()
    {
        using var data = new TestData();
        SampleData.Create(DateTime.Today);
        var window = Open();
        var page = Assert.IsType<CalendarPage>(window.CurrentPage);
        Assert.Equal(CalendarViewMode.Week, page.Mode);
        Assert.Equal(Ui.StartOfWeek(DateTime.Today), page.Range.From);

        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(Ui.StartOfWeek(DateTime.Today).AddDays(7), page.Range.From);
        Press(window, PhysicalKey.T);
        Assert.Equal(DateTime.Today, page.Anchor);
        Press(window, PhysicalKey.M);
        Assert.Equal(CalendarViewMode.Month, page.Mode);
        Press(window, PhysicalKey.D);
        Assert.Equal((DateTime.Today, DateTime.Today), page.Range);
        Press(window, PhysicalKey.W);
        Assert.Equal(CalendarViewMode.Week, SettingsService.Get().ViewMode); // remembered
        window.Close();
    }

    [AvaloniaFact]
    public void A_new_event_is_entered_saved_and_shown()
    {
        using var data = new TestData();
        TestData.LocalCalendar();
        var window = Open();
        Press(window, PhysicalKey.N, CommandModifier);
        var editor = Assert.IsType<EventEditorPage>(window.CurrentPage);
        editor.TitleBox.Text = "Steuerberater";
        editor.LocationBox.Text = "Kanzlei";
        editor.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        WaitUntil(window, () => window.CurrentPage is CalendarPage);
        Assert.IsType<CalendarPage>(window.CurrentPage);
        var saved = Assert.Single(CalendarService.Query(new EventQuery { From = DateTime.Today, To = DateTime.Today.AddDays(2), Search = "Steuerberater" }));
        Assert.Equal("Kanzlei", saved.Event.Location);

        // A wrong value is shown at its field, nothing is stored.
        window.OpenEvent(saved.Event.Id);
        Settle(window);
        editor = Assert.IsType<EventEditorPage>(window.CurrentPage);
        editor.TitleBox.Text = "";
        editor.SaveButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        Assert.IsType<EventEditorPage>(window.CurrentPage);
        Press(window, PhysicalKey.Escape);
        Assert.IsType<CalendarPage>(window.CurrentPage);
        Assert.Equal("Steuerberater", CalendarService.GetEvent(saved.Event.Id)!.Event.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void Reminders_appear_on_top_and_answers_remove_them()
    {
        using var data = new TestData();
        var (_, calendar) = TestData.LocalCalendar();
        CalendarService.SaveEventAsync(Events.At(calendar.Id, DateTime.Now.AddMinutes(8), title: "Kunden-Call") with { MeetingUrl = "https://teams.microsoft.com/l/meetup-join/x", Location = "Teams" })
            .GetAwaiter().GetResult();
        ReminderService.Check(DateTimeOffset.Now);
        var reminder = Assert.Single(ReminderService.Active);
        var window = new ReminderWindow();
        window.Show(ReminderService.Active);
        Settle(window);
        Assert.True(window.Topmost);
        Assert.False(window.ShowActivated);
        Save(window, "reminder-corner");

        ReminderCoordinator.Snooze(reminder, DateTimeOffset.Now.AddMinutes(5));
        Assert.Empty(ReminderService.Active);
        Assert.Empty(ReminderService.Check(DateTimeOffset.Now));
        window.Close();
        ReminderCoordinator.ResetForTests();
    }

    [AvaloniaFact]
    public void Screenshots_of_every_page_in_light_and_dark()
    {
        using var data = new TestData();
        SampleData.Create(DateTime.Today);
        foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var window = Open();
            Save(window, $"week-{suffix}");
            window.ShowCalendar(DateTime.Today, CalendarViewMode.Month);
            Settle(window);
            Save(window, $"month-{suffix}");
            window.ShowCalendar(DateTime.Today, CalendarViewMode.Day);
            Settle(window);
            Save(window, $"day-{suffix}");
            window.ShowCalendar(DateTime.Today, CalendarViewMode.Week);
            window.ShowEvents();
            ((EventsPage)window.CurrentPage!).PeriodFilter.SelectedIndex = 5; // every loaded event: the sample week may lie behind today
            Settle(window);
            Save(window, $"events-{suffix}");
            window.ShowAccounts();
            Settle(window);
            Save(window, $"accounts-{suffix}");
            var first = CalendarService.Range(Ui.StartOfWeek(DateTime.Today), Ui.StartOfWeek(DateTime.Today).AddDays(7)).First(item => item.Event.MeetingUrl.Length > 0);
            window.OpenEvent(first.Event.Id);
            Settle(window);
            Save(window, $"editor-{suffix}");
            window.OpenAccount(first.Account.Id);
            Settle(window);
            ((AccountEditorPage)window.CurrentPage!).Tabs.SelectedIndex = 1;
            Settle(window);
            Save(window, $"account-{suffix}");
            window.ShowSettings(SettingsPage.RemindersTab);
            Settle(window);
            Save(window, $"settings-reminders-{suffix}");
            window.ShowSettings(SettingsPage.AccessTab);
            Settle(window);
            Save(window, $"settings-access-{suffix}");
            window.ShowAbout();
            Settle(window);
            Save(window, $"about-{suffix}");
            window.ToggleSidebar();
            window.ShowCalendar();
            Settle(window);
            Save(window, $"folded-{suffix}");
            window.ToggleSidebar();
            window.Close();

            // A reminder in the corner and the start alert in the middle.
            var item = CalendarService.Range(Ui.StartOfWeek(DateTime.Today), Ui.StartOfWeek(DateTime.Today).AddDays(7)).First(entry => entry.Event.MeetingUrl.Length > 0);
            var now = DateTimeOffset.Now;
            var soon = item with { Event = item.Event with { Start = now.AddMinutes(10), End = now.AddMinutes(70) } };
            var starting = item with { Event = item.Event with { Title = "Sprint Planning", Start = now.AddSeconds(-20), End = now.AddMinutes(60), Location = "Raum Rhein" } };
            var reminders = new ReminderWindow();
            reminders.Show([new DueReminder(soon, ReminderStage.Before, now)]);
            Settle(reminders);
            Save(reminders, $"reminder-{suffix}");
            reminders.Show([new DueReminder(starting, ReminderStage.Start, now), new DueReminder(soon, ReminderStage.Before, now)]);
            Settle(reminders);
            Save(reminders, $"reminder-start-{suffix}");
            reminders.Close();
        }

        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }

    [AvaloniaFact]
    public void Accounts_page_offers_the_ways_in_when_empty_and_lists_accounts()
    {
        using var data = new TestData();
        var window = Open();
        window.ShowAccounts();
        Settle(window);
        var page = Assert.IsType<AccountsPage>(window.CurrentPage);
        Assert.True(page.EmptyCard.IsVisible);
        Assert.True(window.CalendarPage.Hint.IsVisible);
        TestData.LocalCalendar("Familie");
        page.Refresh();
        Assert.False(page.EmptyCard.IsVisible);
        Assert.Equal(1, page.AccountList.ItemCount);
        window.Close();
    }

    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static MainWindow Open(double width = 1440, double height = 900)
    {
        SettingsService.Remember(settings => settings with { WindowWidth = width, WindowHeight = height });
        var window = new MainWindow { Width = width, Height = height };
        window.Show();
        window.Width = width;
        window.Height = height;
        Settle(window);
        return window;
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Settle(window);
    }

    /// <summary>Runs the dispatcher until the condition holds (async saves), at most two seconds.</summary>
    private static void WaitUntil(Window window, Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Settle(window);
        }
    }

    private static void Settle(Window window)
    {
        for (var round = 0; round < 8; round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Save(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (ShotFolder is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotFolder);
        frame.Save(Path.Combine(ShotFolder, $"{name}.png"));
    }
}
