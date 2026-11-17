using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using MatDesktop;

namespace ThixCalendar.Pages;

/// <summary>
/// The settings. Every change goes through the business layer (SettingsService, AutostartService, UpdateService), which
/// checks values and policies; an error is shown at its field or above the tabs.
/// </summary>
public partial class SettingsPage : UserControl, IRefreshable
{
    public const string GeneralTab = "general";
    public const string RemindersTab = "reminders";
    public const string SyncTab = "sync";
    /// <summary>The own app registrations (Erweitert, folded section for administrators).</summary>
    public const string AccessTab = "access";
    public const string UpdatesTab = "updates";

    public const string CloudTab = "cloud";

    private static readonly int?[] AllDayChoices = [null, 7 * 60, 8 * 60, 9 * 60, 10 * 60, 12 * 60];
    private static readonly int[] MissedChoices = [0, 15, 30, 60, 120, 240];

    private bool _isLoading;
    private UpdateManifest? _availableUpdate;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeChoice.ItemsSource = new[] { "Wie das System", "Hell", "Dunkel" };
        DayStartChoice.ItemsSource = Enumerable.Range(0, 13).Select(hour => $"{hour:00}:00").ToList();
        ViewChoice.ItemsSource = new[] { "Tag", "Arbeitswoche", "Woche", "Monat" };
        LeadChoice.ItemsSource = SettingsService.ReminderChoices.Select(Ui.ReminderText).ToList();
        RepeatChoice.ItemsSource = SettingsService.RepeatChoices.Select(minutes => minutes == 0 ? "Nicht wiederholen" : minutes == 1 ? "Jede Minute" : $"Alle {minutes} Minuten").ToList();
        AllDayChoice.ItemsSource = AllDayChoices.Select(minute => minute is { } value ? $"Am Tag um {value / 60:00}:{value % 60:00}" : "Keine Erinnerung").ToList();
        MissedChoice.ItemsSource = MissedChoices.Select(minutes => minutes == 0 ? "Nicht nachholen" : minutes < 60 ? $"Bis {minutes} Min. nach Beginn" : $"Bis {minutes / 60} Std. nach Beginn").ToList();
        IntervalChoice.ItemsSource = SettingsService.SyncIntervalChoices.Select(minutes => minutes < 60 ? $"{minutes} Minute{(minutes == 1 ? "" : "n")}" : "1 Stunde").ToList();

        AutostartSwitch.IsCheckedChanged += (_, _) => Apply(() =>
        {
            if (AutostartSwitch.IsChecked == true)
            {
                AutostartService.Enable(App.ExecutablePath);
            }
            else
            {
                AutostartService.Disable();
            }
        });
        ThemeChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { Theme = (ThemeChoice)Math.Max(0, ThemeChoice.SelectedIndex) }));
        DayStartChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { DayStartHour = Math.Max(0, DayStartChoice.SelectedIndex) }));
        ViewChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Remember(settings => settings with { ViewMode = (CalendarViewMode)Math.Max(0, ViewChoice.SelectedIndex) }));
        LeadChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { DefaultReminderMinutes = SettingsService.ReminderChoices[Math.Max(0, LeadChoice.SelectedIndex)] }));
        AtStartSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { RemindAtStart = AtStartSwitch.IsChecked == true }));
        CenteredSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { StartAlertCentered = CenteredSwitch.IsChecked == true }));
        SoundSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { PlaySound = SoundSwitch.IsChecked == true }));
        RepeatChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { RepeatSoundMinutes = SettingsService.RepeatChoices[Math.Max(0, RepeatChoice.SelectedIndex)] }));
        AllDayChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { AllDayReminderMinuteOfDay = AllDayChoices[Math.Max(0, AllDayChoice.SelectedIndex)] }));
        MissedChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { MissedReminderMinutes = MissedChoices[Math.Max(0, MissedChoice.SelectedIndex)] }));
        DeclinedSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { SkipDeclined = DeclinedSwitch.IsChecked == true }));
        IntervalChoice.SelectionChanged += (_, _) => Apply(() =>
        {
            SettingsService.Update(settings => settings with { SyncIntervalMinutes = SettingsService.SyncIntervalChoices[Math.Max(0, IntervalChoice.SelectedIndex)] });
            SyncService.Kick();
        });
        CheckUpdatesSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { CheckForUpdates = CheckUpdatesSwitch.IsChecked == true }));
        AutoInstallSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = AutoInstallSwitch.IsChecked == true }));
        UpdateCoordinator.Checked += (_, result) => Ui.Post(() => ShowUpdateResult(result));
        SyncService.StatusChanged += (_, _) => Ui.Post(LoadSyncState);
        SettingsService.Changed += (_, _) => Ui.Post(LoadPause);
        CloudService.StatusChanged += (_, _) => Ui.Post(LoadCloud);
        Refresh();
    }

    public void SelectTab(string tab)
    {
        AccessExpander.IsExpanded = tab == AccessTab;
        Tabs.SelectedItem = TabFor(tab);
    }

    private TabItem TabFor(string tab) => tab switch
    {
        RemindersTab => RemindersTabItem,
        SyncTab => SyncTabItem,
        AccessTab => AdvancedTabItem,
        UpdatesTab => UpdatesTabItem,
        CloudTab => CloudTabItem,
        _ => GeneralTabItem,
    };

    public void Refresh()
    {
        _isLoading = true;
        try
        {
            var settings = SettingsService.Get();
            var locked = AccessPolicy.IsConfigurationLocked;
            PolicyNote.IsVisible = false;
            if (locked || AccessPolicy.AreOnlineAccountsDisabled || AccessPolicy.AreUpdatesDisabled)
            {
                PolicyNote.Show("Einige Einstellungen wurden von einem Administrator festgelegt (Richtlinie) und können hier nicht geändert werden.", InfoSeverity.Info);
            }

            AutostartSwitch.IsChecked = AutostartService.IsEnabled();
            AutostartSwitch.IsEnabled = AutostartService.IsSupported && !locked;
            ThemeChoice.SelectedIndex = (int)settings.Theme;
            DayStartChoice.SelectedIndex = Math.Clamp(settings.DayStartHour, 0, 12);
            ViewChoice.SelectedIndex = (int)settings.ViewMode;
            LeadChoice.SelectedIndex = Math.Max(0, SettingsService.ReminderChoices.ToList().IndexOf(settings.DefaultReminderMinutes));
            AtStartSwitch.IsChecked = settings.RemindAtStart;
            CenteredSwitch.IsChecked = settings.StartAlertCentered;
            SoundSwitch.IsChecked = settings.PlaySound;
            RepeatChoice.SelectedIndex = Math.Max(0, SettingsService.RepeatChoices.ToList().IndexOf(settings.RepeatSoundMinutes));
            AllDayChoice.SelectedIndex = Math.Max(0, Array.IndexOf(AllDayChoices, settings.AllDayReminderMinuteOfDay));
            MissedChoice.SelectedIndex = Math.Max(0, Array.IndexOf(MissedChoices, settings.MissedReminderMinutes));
            DeclinedSwitch.IsChecked = settings.SkipDeclined;
            IntervalChoice.SelectedIndex = Math.Max(0, SettingsService.SyncIntervalChoices.ToList().IndexOf(settings.SyncIntervalMinutes));
            foreach (var control in new Control[] { ThemeChoice, DayStartChoice, LeadChoice, AtStartSwitch, CenteredSwitch, SoundSwitch, RepeatChoice, AllDayChoice, MissedChoice, DeclinedSwitch, IntervalChoice, MicrosoftClientBox, GoogleClientBox, GoogleSecretBox })
            {
                control.IsEnabled = !locked;
            }

            MicrosoftClientBox.Text = settings.MicrosoftClientId;
            GoogleClientBox.Text = settings.GoogleClientId;
            GoogleSecretBox.Text = settings.GoogleClientSecret;
            MicrosoftState.Text = settings.MicrosoftClientId.Length > 0 ? "· eigene Registrierung" : AppInfo.BuiltInMicrosoftClientId.Length > 0 ? "· eingebaut" : "· noch nicht eingerichtet";
            GoogleState.Text = settings.GoogleClientId.Length > 0 ? "· eigene Registrierung" : AppInfo.BuiltInGoogleClientId.Length > 0 && AppInfo.BuiltInGoogleClientSecret.Length > 0 ? "· eingebaut" : "· noch nicht eingerichtet";

            LoadCloud();
            CheckUpdatesSwitch.IsChecked = settings.CheckForUpdates;
            AutoInstallSwitch.IsChecked = settings.InstallUpdatesAutomatically;
            CheckUpdatesSwitch.IsEnabled = AutoInstallSwitch.IsEnabled = UpdateService.CanUpdate && !locked;
            InstalledText.Text = AppInfo.Version;
            ChannelText.Text = AppInfo.Channel switch
            {
                "release" => "Release (Branch main)",
                "nightly" => "Nightly (Branch dev)",
                _ => "Lokaler Build – keine Updates",
            };
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–";
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            if (UpdateService.LastResult is { } result)
            {
                ShowUpdateResult(result);
            }

            ((MatSettingRow)DataFolderRow).Description = AppPaths.DataDirectory;
            LoadSyncState();
            LoadPause();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void LoadSyncState() => SyncStateRow.Description = SyncService.Status.Message;

    private void LoadPause()
    {
        var until = SettingsService.Get().RemindersPausedUntil;
        var paused = until is { } end && end > DateTimeOffset.Now;
        PauseRow.Description = paused ? $"Pausiert bis {until!.Value.ToLocalTime():HH:mm} – was in der Zeit fällig wird, kommt danach." : "Nicht stören, z. B. während einer Präsentation. Nichts geht verloren: Fälliges erscheint danach.";
        PauseLabel.Text = paused ? "Fortsetzen" : "Pausieren …";
        PauseLabel.Icon = (paused ? "bell" : "bell-off");
        PauseButton.Theme = paused ? Ui.Theme("MatButton.Warning") : null;
    }

    private void Apply(Action change)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            change();
            Message.Hide();
            FormErrors.ShowError(this, null, "");
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                Message.Show(exception.Message, InfoSeverity.Error);
            }

            Refresh();
        }
    }

    private void OnSaveAccessClick(object? sender, RoutedEventArgs e) => Apply(() =>
    {
        SettingsService.Update(settings => settings with
        {
            MicrosoftClientId = MicrosoftClientBox.Text ?? "",
            GoogleClientId = GoogleClientBox.Text ?? "",
            GoogleClientSecret = GoogleSecretBox.Text ?? "",
        });
        Refresh();
        Message.Show("Gespeichert. Unter „Konten“ meldest du dich jetzt an.", InfoSeverity.Success);
    });

    private void OnTestReminderClick(object? sender, RoutedEventArgs e) => App.Instance.ShowTestReminder();

    private async void OnPauseClick(object? sender, RoutedEventArgs e) => await App.Instance.ToggleReminderPauseAsync(Ui.WindowOf(this));

    private void OnSyncNowClick(object? sender, RoutedEventArgs e) => SyncService.Kick();

    private void OnOpenEntraClick(object? sender, RoutedEventArgs e) =>
        Ui.OpenExternal("https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade");

    private void OnOpenGoogleConsoleClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal("https://console.cloud.google.com/apis/credentials");

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            ShowUpdateResult(await UpdateCoordinator.CheckAsync());
        }
        catch (BusinessException exception)
        {
            UpdateMessage.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–";
        }
    }

    private void ShowUpdateResult(UpdateCheckResult result)
    {
        _availableUpdate = result.Manifest;
        InstallUpdateButton.IsVisible = result.IsUpdateAvailable;
        UpdateMessage.Show(result.Message, result.IsUpdateAvailable ? InfoSeverity.Success : InfoSeverity.Info);
    }

    private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        try
        {
            await UpdateCoordinator.InstallAsync(_availableUpdate, new Progress<double>(value => UpdateProgress.Value = value));
        }
        catch (Exception exception) when (exception is BusinessException or HttpRequestException or IOException)
        {
            UpdateMessage.Show($"Das Update ist fehlgeschlagen: {exception.Message}", InfoSeverity.Error);
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
            UpdateProgress.IsVisible = false;
        }
    }

    private void OnReleasePageClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(UpdateService.ReleasePageUrl(AppInfo.IsLocalBuild ? "release" : AppInfo.Channel));

    private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(AppPaths.DataDirectory);

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Ui.OpenExternal(AppPaths.LogDirectory);
    }
}
