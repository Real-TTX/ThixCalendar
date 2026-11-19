using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Avalonia.Threading;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar;

/// <summary>
/// "ThixCalendar.exe --self-test &lt;report file&gt;": the published program checks itself – everything trimming or
/// single-file packaging could break silently: JSON of settings, update manifest and install options, SQLite, Ical.Net, the provider JSON,
/// SkiaSharp (tray badge), the protected token store, the sound, Windows shortcuts (COM) and the windows of the app with
/// their bindings. It runs in a fresh temporary data folder (Program sets it), never touches the user's data, writes a
/// report and ends with exit code 0 (all passed) or 1. CI runs it on every Windows build.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--self-test";

    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder($"ThixCalendar {AppInfo.Version} self test, data folder {AppPaths.DataDirectory}{Environment.NewLine}");
        var failures = 0;

        async Task Check(string name, Func<Task<string>> check)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var note = await check();
                report.AppendLine($"ok    {name} ({watch.ElapsedMilliseconds} ms){(note.Length > 0 ? " – " + note : "")}");
            }
            catch (Exception exception)
            {
                failures++;
                report.AppendLine($"FAIL  {name}: {exception}");
            }
        }

        await Check("settings (JSON)", () =>
        {
            SettingsService.Update(settings => settings with { DefaultReminderMinutes = 15, Theme = ThemeChoice.Dark });
            SettingsStore.Reset();
            var read = SettingsService.Get();
            Ensure(read is { DefaultReminderMinutes: 15, Theme: ThemeChoice.Dark }, "settings did not survive a save");
            return Task.FromResult("");
        });

        await Check("update manifest (JSON)", () =>
        {
            const string json = """{"version":"0.1.9-20261115","channel":"release","buildNumber":9,"setup":"ThixCalendarSetup.exe","sha256":"AB","packages":{"win-x64":{"file":"ThixCalendarSetup.exe","sha256":"AB"}}}""";
            var manifest = new HttpResponseMessage { Content = new StringContent(json, Encoding.UTF8, "application/json") }.Content
                .ReadFromJsonAsync<UpdateManifest>().GetAwaiter().GetResult();
            Ensure(manifest is { BuildNumber: 9 } && UpdateService.PackageFor(manifest, "win-x64")?.File == "ThixCalendarSetup.exe", "manifest not read");
            return Task.FromResult("");
        });

        // Only whether they are there, never the values: shows that the build got the repository secrets.
        await Check("app registrations", () => Task.FromResult(
            $"Microsoft {(AccountService.IsConfigured(AccountKind.Microsoft) ? "built in" : "missing")}, " +
            $"Google {(AccountService.IsConfigured(AccountKind.Google) ? "built in" : "missing")}"));

        await Check("calendar database (SQLite)", () =>
        {
            SampleData.Create(DateTime.Today);
            var items = CalendarService.Range(Ui.StartOfWeek(DateTime.Today), Ui.StartOfWeek(DateTime.Today).AddDays(7));
            Ensure(items.Count >= 10, $"only {items.Count} sample events");
            Ensure(CalendarService.Query(new EventQuery { From = DateTime.Today.AddDays(-7), To = DateTime.Today.AddDays(14), Search = "sprint" }).Count == 1, "search failed");
            return Task.FromResult($"{items.Count} events");
        });

        await Check("providers (Graph / Google JSON, Ical.Net)", () =>
        {
            var graph = GraphProvider.ReadEvent(System.Text.Json.Nodes.JsonNode.Parse("""
                {"id":"A1","subject":"Test","isAllDay":false,"start":{"dateTime":"2026-11-16T09:00:00.0000000","timeZone":"UTC"},
                 "end":{"dateTime":"2026-11-16T10:00:00.0000000","timeZone":"UTC"},"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/x"},
                 "isReminderOn":true,"reminderMinutesBeforeStart":15,"responseStatus":{"response":"accepted"}}
                """)!);
            Ensure(graph is { ReminderMinutes: 15, MeetingUrl.Length: > 0 }, "Graph event not read");
            var google = GoogleProvider.ReadEvent(System.Text.Json.Nodes.JsonNode.Parse("""
                {"id":"g1","status":"confirmed","summary":"Test","start":{"date":"2026-11-16"},"end":{"date":"2026-11-17"}}
                """)!);
            Ensure(google is { IsAllDay: true }, "Google event not read");
            var ics = IcsProvider.Expand(IcsProvider.Parse("""
                BEGIN:VCALENDAR
                VERSION:2.0
                PRODID:-//ThixCalendar//SelfTest//DE
                BEGIN:VEVENT
                UID:weekly@test
                DTSTAMP:20261101T000000Z
                DTSTART:20261102T080000Z
                DTEND:20261102T090000Z
                RRULE:FREQ=WEEKLY;COUNT=4
                SUMMARY:Jour fixe
                END:VEVENT
                END:VCALENDAR
                """), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero));
            Ensure(ics.Count == 4, $"{ics.Count} occurrences instead of 4");
            return Task.FromResult("");
        });

        await Check("reminders", () =>
        {
            var due = ReminderService.Check(DateTimeOffset.Now);
            return Task.FromResult($"{ReminderService.Active.Count} due");
        });

        await Check("install options (installer.json, arguments)", () =>
        {
            Ensure(InstallerOptions.Config is { Autostart: not null, DesktopShortcut: not null, AutomaticUpdates: not null }, "installer.json not read");
            var request = InstallerOptions.Parse([InstallerOptions.QuietArgument, "--autostart=no", "--desktop-shortcut=yes"]);
            Ensure(request is { Action: SetupAction.Install, Choices: { Autostart: false, DesktopShortcut: true } }, "arguments not read");
            return Task.FromResult("");
        });

        await Check("token store", () =>
        {
            var stored = TokenStore.Save("self-test", "geheim");
            Ensure(TokenStore.Read("self-test", stored) == "geheim", "token not read back");
            TokenStore.Delete("self-test");
            return Task.FromResult("");
        });

        await Check("tray badge (SkiaSharp)", () =>
        {
            using var asset = Avalonia.Platform.AssetLoader.Open(new Uri("avares://ThixCalendar/Assets/logo-64.png"));
            Ensure(Tray.TrayIconImages.BadgePng(asset, isTemplate: false, Tray.TrayBadge.Alert)?.Length > 0, "badge not drawn");
            return Task.FromResult("");
        });

        await Check("sound", () =>
        {
            Ensure(Core.Platform.Chime.Wave().Length > 40000, "chime not generated");
            return Task.FromResult("");
        });

        if (OperatingSystem.IsWindows())
        {
            await Check("shortcut (COM)", () =>
            {
                var link = Path.Combine(AppPaths.DataDirectory, "ThixCalendar.lnk");
                Core.Platform.Windows.ShellLink.Write(link, App.ExecutablePath, AppPaths.DataDirectory, "Selbsttest", App.ExecutablePath);
                Ensure(File.Exists(link) && new FileInfo(link).Length > 0, "no .lnk written");
                return Task.FromResult("");
            });
        }

        await Check("windows and bindings", async () =>
        {
            var window = new MainWindow();
            window.Show();
            await Settle();
            Ensure(window.CurrentPage is Pages.CalendarPage, "calendar not shown");
            window.ShowEvents("Team");
            await Settle();
            window.ShowAccounts();
            await Settle();
            window.ShowSettings(Pages.SettingsPage.RemindersTab);
            await Settle();
            window.ShowAbout();
            await Settle();
            var first = CalendarService.Range(Ui.StartOfWeek(DateTime.Today), Ui.StartOfWeek(DateTime.Today).AddDays(7)).First();
            window.OpenEvent(first.Event.Id);
            await Settle();
            Ensure(window.CurrentPage is Pages.EventEditorPage, "event editor not shown");
            window.OpenAccount(first.Account.Id);
            await Settle();
            window.ShowCalendar(DateTime.Today, CalendarViewMode.Month);
            await Settle();

            var reminders = new Reminders.ReminderWindow();
            reminders.Show(ReminderService.Active.Count > 0 ? ReminderService.Active : []);
            await Settle();
            reminders.Close();

            var pause = Dialogs.PauseDialog.AskAsync(window);
            await Settle();
            foreach (var owned in window.OwnedWindows.ToList())
            {
                owned.Close();
            }

            Ensure(await pause is null, "pause dialog answered without a choice");
            if (OperatingSystem.IsWindows())
            {
                var setup = new Setup.SetupWindow();
                setup.Show();
                await Settle();
                setup.Close();
            }

            ProblemReports.WriteHang(TimeSpan.FromSeconds(9), withDump: false);
            Dialogs.ProblemReportDialog.ShowUnseen()?.Close();
            window.IsExiting = true;
            window.Close();
            return "";
        });

        report.AppendLine(failures == 0 ? "PASSED" : $"FAILED ({failures})");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);

        // The temporary data folder goes again (unless the report was written into it).
        CalendarStore.Close();
        if (!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task Settle()
    {
        for (var round = 0; round < 10; round++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(30);
        }
    }
}
