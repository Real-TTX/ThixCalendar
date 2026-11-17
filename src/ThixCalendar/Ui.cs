using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using MatDesktop;

namespace ThixCalendar;

/// <summary>Small helpers for code-built UI: theme resources by key, the UI thread, colours, German dates.</summary>
internal static class Ui
{
    /// <summary>The app speaks German, whatever the system language is.</summary>
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static WindowIcon? _appIcon;

    /// <summary>The icon of every window (MatThemeOptions.Icon) and of the setup.</summary>
    public static WindowIcon AppIcon => _appIcon ??= new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ThixCalendar/Assets/thixcalendar.ico")));

    private static Avalonia.Media.Imaging.Bitmap? _logo;

    /// <summary>The logo of the title bar, the setup and the dialogs.</summary>
    public static Avalonia.Media.Imaging.Bitmap Logo => _logo ??= new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ThixCalendar/Assets/logo-256.png")));

    public static object Resource(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) && value is not null
            ? value
            : throw new InvalidOperationException($"Resource {key} is missing (MatDesktop or Themes/*.axaml).");

    public static IBrush Brush(string key) => (IBrush)Resource(key);

    /// <summary>An icon of MatDesktop's sprite as a geometry ("calendar"; ThixCalendar's own few in Themes/Icons.axaml).</summary>
    public static Geometry Icon(string name) => MatDesktop.MatIcon.Find(name) ?? throw new InvalidOperationException($"The icon {name} is missing in the sprite.");

    /// <summary>A named look (ControlTheme) such as "MatButton.Primary" or "MatText.Muted".</summary>
    public static ControlTheme Theme(string key) => (ControlTheme)Resource(key);

    /// <summary>Runs the action on the UI thread (events of the business layer come from any thread).</summary>
    public static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>The window that hosts a control (null while it is not shown).</summary>
    public static Window? WindowOf(Control control) => TopLevel.GetTopLevel(control) as Window;

    /// <summary>Ctrl on Windows, Cmd on macOS: the modifier of the app's own shortcuts.</summary>
    public static KeyModifiersName CommandKey => OperatingSystem.IsMacOS() ? new("⌘", Avalonia.Input.KeyModifiers.Meta) : new("Strg", Avalonia.Input.KeyModifiers.Control);

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>A calendar colour ("#RRGGBB").</summary>
    public static Color ColorOf(string hex) => Color.TryParse(hex, out var color) ? color : Color.Parse(CalendarColors.Default);

    public static IBrush SolidBrush(string hex) => new SolidColorBrush(ColorOf(hex));

    /// <summary>The soft fill of an event in its calendar colour (stronger in the dark theme).</summary>
    public static IBrush Tint(string hex, double? opacity = null) =>
        new SolidColorBrush(ColorOf(hex), opacity ?? (Resource("EventTintOpacity") is double value ? value : 0.16));

    /// <summary>Opens a web address or a folder with the program the system uses for it (browser, Explorer, Finder).</summary>
    public static void OpenExternal(string pathOrUrl)
    {
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                : OperatingSystem.IsLinux()
                    ? new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                    : new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not open {pathOrUrl}: {exception.Message}");
        }
    }

    /// <summary>"Heute, 13:47" / "Gestern, 13:47" / "12.11.2026, 13:47".</summary>
    public static string FormatDateTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        if (local.Date == DateTime.Today)
        {
            return $"Heute, {local:HH:mm}";
        }

        return local.Date == DateTime.Today.AddDays(-1) ? $"Gestern, {local:HH:mm}" : local.ToString("dd.MM.yyyy, HH:mm", German);
    }

    /// <summary>"Heute", "Morgen", "Gestern" or "Mo., 20. Jan.".</summary>
    public static string DayName(DateTime day)
    {
        var today = DateTime.Today;
        if (day.Date == today)
        {
            return "Heute";
        }

        if (day.Date == today.AddDays(1))
        {
            return "Morgen";
        }

        if (day.Date == today.AddDays(-1))
        {
            return "Gestern";
        }

        return day.Year == today.Year ? day.ToString("ddd, d. MMM", German) : day.ToString("ddd, d. MMM yyyy", German);
    }

    /// <summary>"Montag, 20. Januar 2025".</summary>
    public static string LongDate(DateTime day) => day.ToString("dddd, d. MMMM yyyy", German);

    /// <summary>The time of an event: "08:00 – 09:00", "Ganztägig", "20.–22. Jan. (ganztägig)", "Mo 22:00 – Di 01:00".</summary>
    public static string TimeRange(CalendarEvent calendarEvent)
    {
        var start = calendarEvent.StartLocal;
        var end = calendarEvent.EndLocal;
        if (calendarEvent.IsAllDay)
        {
            var lastDay = end.AddDays(-1);
            return lastDay <= start ? "Ganztägig" : $"{start.ToString("d. MMM", German)} – {lastDay.ToString("d. MMM", German)} (ganztägig)";
        }

        if (end <= start)
        {
            return start.ToString("HH:mm", German);
        }

        return start.Date == end.Date || (end.TimeOfDay == TimeSpan.Zero && end.Date == start.Date.AddDays(1))
            ? $"{start:HH:mm} – {end:HH:mm}"
            : $"{start.ToString("ddd HH:mm", German)} – {end.ToString("ddd HH:mm", German)}";
    }

    /// <summary>When it happens, said for a reminder: "in 10 Min.", "jetzt", "läuft seit 5 Min.", "heute ganztägig".</summary>
    public static string Relative(CalendarEvent calendarEvent, DateTimeOffset now)
    {
        if (calendarEvent.IsAllDay)
        {
            return calendarEvent.StartLocal.Date == now.LocalDateTime.Date ? "Heute, ganztägig" : $"{DayName(calendarEvent.StartLocal)}, ganztägig";
        }

        var until = calendarEvent.Start - now;
        if (until.TotalSeconds > 59)
        {
            return $"in {Duration(until)}";
        }

        if (until.TotalSeconds > -60)
        {
            return "beginnt jetzt";
        }

        return calendarEvent.End > now ? $"läuft seit {Duration(-until)}" : "vorbei";
    }

    /// <summary>"5 Min.", "1 Std. 20 Min.", "2 Tagen".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1.5)
        {
            return $"{Math.Round(span.TotalDays)} Tagen";
        }

        var minutes = (int)Math.Round(span.TotalMinutes);
        if (minutes < 60)
        {
            return $"{Math.Max(1, minutes)} Min.";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} Std." : $"{hours} Std. {rest} Min.";
    }

    /// <summary>"10 Minuten vorher", "Zum Beginn", "1 Tag vorher".</summary>
    public static string ReminderText(int minutes) => minutes switch
    {
        0 => "Zum Beginn",
        1 => "1 Minute vorher",
        < 60 => $"{minutes} Minuten vorher",
        60 => "1 Stunde vorher",
        < 1440 when minutes % 60 == 0 => $"{minutes / 60} Stunden vorher",
        1440 => "1 Tag vorher",
        _ when minutes % 1440 == 0 => $"{minutes / 1440} Tage vorher",
        _ => $"{minutes} Minuten vorher",
    };

    /// <summary>ISO week number (KW).</summary>
    public static int Week(DateTime day) => ISOWeek.GetWeekOfYear(day);

    /// <summary>The Monday of the week of the day.</summary>
    public static DateTime StartOfWeek(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}

/// <summary>The name and the modifier of the command key of the platform.</summary>
internal sealed record KeyModifiersName(string Name, Avalonia.Input.KeyModifiers Modifier);
