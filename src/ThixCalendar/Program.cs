using Avalonia;
using Avalonia.Controls;

namespace ThixCalendar;

internal static class Program
{
    /// <summary>
    /// One program for everything (App decides): the application and - on Windows - its setup.
    /// The application ends only through "Beenden" (tray / menu bar), not when its window closes: the reminders must keep
    /// coming while the window is closed.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // First of all: no error ends ThixCalendar without a report (and on Windows a message box).
        ThixCalendar.Core.Infrastructure.ProblemReports.Install(showMessageBox: !args.Contains(SelfTest.Argument));

        // The self test works in a fresh folder of its own (before anything reads the data folder).
        if (args.Contains(SelfTest.Argument))
        {
            Environment.SetEnvironmentVariable(ThixCalendar.Core.Infrastructure.AppPaths.DataDirectoryVariable,
                Path.Combine(Path.GetTempPath(), "thixcalendar-self-test", Guid.NewGuid().ToString("N")));
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .LogToTrace();
}
