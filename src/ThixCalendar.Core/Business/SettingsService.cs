using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Business;

/// <summary>
/// Reading and changing the settings of this computer. Every change is checked here (values, policy) before it is
/// saved; the UI only offers. <see cref="Changed"/> tells the app (theme, reminders, sync) what is new.
/// </summary>
public static class SettingsService
{
    /// <summary>The lead times offered for reminders (minutes).</summary>
    public static readonly IReadOnlyList<int> ReminderChoices = [0, 5, 10, 15, 30, 60, 120, 1440];

    /// <summary>The sound repetitions offered (minutes, 0 = once).</summary>
    public static readonly IReadOnlyList<int> RepeatChoices = [0, 1, 2, 5, 10];

    public static readonly IReadOnlyList<int> SyncIntervalChoices = [1, 2, 5, 10, 15, 30, 60];

    /// <summary>Raised after every saved change (any thread).</summary>
    public static event EventHandler<AppSettings>? Changed;

    public static AppSettings Get() => SettingsStore.Read();

    /// <summary>Changes settings (checked and policy-protected).</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var current = SettingsStore.Read();
        var next = Normalize(change(current));
        Validate(next);
        return Save(_ => next);
    }

    /// <summary>
    /// Holds reminders back until <paramref name="until"/> (null = continue now). No configuration lock: it is an
    /// everyday switch like "Nicht stören".
    /// </summary>
    public static AppSettings PauseReminders(DateTimeOffset? until)
    {
        if (until is { } end && end <= DateTimeOffset.Now)
        {
            throw new BusinessException("Das Ende der Pause liegt in der Vergangenheit.");
        }

        return Save(settings => settings with { RemindersPausedUntil = until });
    }

    /// <summary>Things the app remembers by itself (window size, folded menu, view, hints): no configuration lock.</summary>
    public static AppSettings Remember(Func<AppSettings, AppSettings> change)
    {
        var current = SettingsStore.Read();
        var next = change(current);
        return Save(_ => current with
        {
            WindowWidth = Math.Clamp(next.WindowWidth, 720, 4000),
            WindowHeight = Math.Clamp(next.WindowHeight, 480, 3000),
            IsSidebarCollapsed = next.IsSidebarCollapsed,
            ViewMode = Enum.IsDefined(next.ViewMode) ? next.ViewMode : CalendarViewMode.Week,
            TrayHintShown = next.TrayHintShown,
        });
    }

    /// <summary>The Microsoft app registration in use: the user's own or the built-in one.</summary>
    public static string MicrosoftClientId => Get().MicrosoftClientId is { Length: > 0 } own ? own : AppInfo.BuiltInMicrosoftClientId;

    public static (string ClientId, string ClientSecret) GoogleClient =>
        Get() is { GoogleClientId.Length: > 0 } own ? (own.GoogleClientId, own.GoogleClientSecret) : (AppInfo.BuiltInGoogleClientId, AppInfo.BuiltInGoogleClientSecret);

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        MicrosoftClientId = settings.MicrosoftClientId.Trim(),
        GoogleClientId = settings.GoogleClientId.Trim(),
        GoogleClientSecret = settings.GoogleClientSecret.Trim(),
    };

    private static AppSettings Save(Func<AppSettings, AppSettings> change)
    {
        var saved = SettingsStore.Update(change);
        Changed?.Invoke(null, saved);
        return saved;
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.DefaultReminderMinutes is < 0 or > 7 * 24 * 60)
        {
            throw new BusinessException("Die Vorlaufzeit muss zwischen 0 Minuten und 7 Tagen liegen.", nameof(AppSettings.DefaultReminderMinutes));
        }

        if (settings.RepeatSoundMinutes is < 0 or > 60)
        {
            throw new BusinessException("Der Ton kann höchstens stündlich wiederholt werden.", nameof(AppSettings.RepeatSoundMinutes));
        }

        if (settings.AllDayReminderMinuteOfDay is < 0 or >= 24 * 60)
        {
            throw new BusinessException("Diese Uhrzeit gibt es nicht.", nameof(AppSettings.AllDayReminderMinuteOfDay));
        }

        if (settings.SyncIntervalMinutes is < 1 or > 24 * 60)
        {
            throw new BusinessException("Der Abgleich läuft zwischen jede Minute und einmal am Tag.", nameof(AppSettings.SyncIntervalMinutes));
        }

        if (settings.MissedReminderMinutes is < 0 or > 24 * 60)
        {
            throw new BusinessException("Verpasste Erinnerungen können bis zu einem Tag nachgeholt werden.", nameof(AppSettings.MissedReminderMinutes));
        }

        if (settings.DayStartHour is < 0 or > 20)
        {
            throw new BusinessException("Der Tag beginnt zwischen 0 und 20 Uhr.", nameof(AppSettings.DayStartHour));
        }

        if (settings.MicrosoftClientId.Length > 0 && !Guid.TryParse(settings.MicrosoftClientId, out _))
        {
            throw new BusinessException("Eine Microsoft-Client-ID ist eine GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).", nameof(AppSettings.MicrosoftClientId));
        }

        if (settings.GoogleClientId.Length > 0 && !settings.GoogleClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
        {
            throw new BusinessException("Eine Google-Client-ID endet auf „.apps.googleusercontent.com“.", nameof(AppSettings.GoogleClientId));
        }

        if (settings.GoogleClientId.Length > 0 && settings.GoogleClientSecret.Length == 0)
        {
            throw new BusinessException("Zur eigenen Google-Client-ID gehört ihr Client-Secret.", nameof(AppSettings.GoogleClientSecret));
        }
    }
}
