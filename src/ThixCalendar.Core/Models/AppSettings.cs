namespace ThixCalendar.Core.Models;

public enum ThemeChoice
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>The time range the calendar page shows.</summary>
public enum CalendarViewMode
{
    Day = 0,
    WorkWeek = 1,
    Week = 2,
    Month = 3,
}

/// <summary>The settings of this computer (settings.json). Only <see cref="Business.SettingsService"/> writes them.</summary>
public sealed record AppSettings
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    public CalendarViewMode ViewMode { get; init; } = CalendarViewMode.Week;

    /// <summary>The hour the day and week view scroll to (the working day).</summary>
    public int DayStartHour { get; init; } = 7;

    // ===== Reminders (the heart of ThixCalendar: nothing is missed) =====

    /// <summary>Lead time when an event (and its calendar) names none of its own.</summary>
    public int DefaultReminderMinutes { get; init; } = 10;

    /// <summary>Remind a second time when the event begins.</summary>
    public bool RemindAtStart { get; init; } = true;

    /// <summary>At the start the reminder comes up large in the middle of the screen instead of in the corner.</summary>
    public bool StartAlertCentered { get; init; } = true;

    public bool PlaySound { get; init; } = true;

    /// <summary>Repeat the sound while a reminder waits for an answer (minutes, 0 = once).</summary>
    public int RepeatSoundMinutes { get; init; } = 5;

    /// <summary>All-day events: a reminder at this time of their first day (minutes after midnight, null = none).</summary>
    public int? AllDayReminderMinuteOfDay { get; init; } = 9 * 60;

    /// <summary>No reminders for invitations the user declined.</summary>
    public bool SkipDeclined { get; init; } = true;

    /// <summary>A reminder missed while the computer slept or was off still comes up to this long after the start.</summary>
    public int MissedReminderMinutes { get; init; } = 60;

    /// <summary>Reminders are held back until then ("Erinnerungen pausieren"); what fell due meanwhile appears when it ends.</summary>
    public DateTimeOffset? RemindersPausedUntil { get; init; }

    // ===== Sync =====

    /// <summary>How often the online accounts are asked for changes (minutes).</summary>
    public int SyncIntervalMinutes { get; init; } = 5;

    /// <summary>The user's own app registrations (empty = the ones built into this version).</summary>
    public string MicrosoftClientId { get; init; } = "";

    public string GoogleClientId { get; init; } = "";

    public string GoogleClientSecret { get; init; } = "";

    // ===== Updates =====

    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Full auto update: install new versions without asking (on by default, the setup offers it).</summary>
    public bool InstallUpdatesAutomatically { get; init; } = true;

    // ===== Remembered by the app =====

    public double WindowWidth { get; init; } = 1320;

    public double WindowHeight { get; init; } = 840;

    public bool IsSidebarCollapsed { get; init; }

    /// <summary>The hint "ThixCalendar keeps running in the tray" has been shown.</summary>
    public bool TrayHintShown { get; init; }

    // ===== MatCloud =====

    public CloudSettings Cloud { get; init; } = new();
}

/// <summary>The connection to MatCloud, the cloud of the Thix apps. The device token is stored protected (DPAPI / Keychain), not in here.</summary>
public sealed record CloudSettings
{
    /// <summary>The address of the cloud, e.g. https://cloud.example.com. Empty = never connected.</summary>
    public string ServerUrl { get; init; } = "";

    public string UserName { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string DeviceName { get; init; } = "";

    public long DeviceId { get; init; }

    /// <summary>The device token, protected for this user (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = "";

    public bool IsConnected => ServerUrl.Length > 0 && DeviceId > 0;
}
