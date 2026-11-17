namespace ThixCalendar.Core.Infrastructure;

/// <summary>
/// Where ThixCalendar keeps its files. Program files and user data are separate: updates and uninstalling never touch the
/// user data unless the user decides so. The providers (Microsoft 365, Google) are the truth; the database is a copy for
/// offline use and the reminders, plus the local calendars.
///   Windows: %LocalAppData%\ThixCalendar (calendar.db, settings, updates, logs)
///   macOS:   ~/Library/Application Support/ThixCalendar
/// </summary>
public static class AppPaths
{
    /// <summary>Developers can point ThixCalendar to a separate folder (sample data, screenshots) with this variable.</summary>
    public const string DataDirectoryVariable = "THIXCALENDAR_DATA_DIR";

    private static string? _dataDirectoryOverride = NullIfEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    public static string DataDirectory => _dataDirectoryOverride ?? Path.Combine(UserFolder(), AppInfo.ProductName);

    /// <summary>Accounts, calendars, events and reminder answers (SQLite).</summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, "calendar.db");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>What this computer and MatCloud agreed on last (revision, a hash per item, items waiting for their account).</summary>
    public static string CloudIndexFile => Path.Combine(DataDirectory, "cloud-sync.json");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>True when a developer or a test pointed ThixCalendar to its own folder: it is then its own instance.</summary>
    public static bool HasOverride => _dataDirectoryOverride is not null;

    /// <summary>Tests use a temporary folder.</summary>
    internal static void UseDirectoryForTests(string? directory) => _dataDirectoryOverride = directory;

    private static string UserFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
