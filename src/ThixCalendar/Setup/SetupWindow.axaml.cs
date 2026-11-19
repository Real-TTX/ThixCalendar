using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using MatDesktop;

namespace ThixCalendar.Setup;

/// <summary>
/// The Windows setup assistant. The work (InstallService, business layer) runs on a background task and reports its
/// steps into the progress page; the window only steers. It cannot be closed halfway through.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class SetupWindow : MatWindow
{
    private readonly bool _isUninstall;
    private readonly bool _isUpdate;
    private readonly ObservableCollection<string> _steps = [];
    private readonly Dictionary<string, ToggleSwitch> _fileTypeToggles = new(StringComparer.OrdinalIgnoreCase);
    private bool _isWorking;
    private bool _isFinished;
    private bool _succeeded;

    /// <summary>For the XAML previewer only.</summary>
    public SetupWindow()
        : this(isUninstall: false)
    {
    }

    private SetupWindow(bool isUninstall)
    {
        InitializeComponent();
        var workArea = WorkArea();
        Width = Math.Min(Width, workArea.Width - 16);
        Height = Math.Min(Height, workArea.Height - 16);

        _isUninstall = isUninstall;
        _isUpdate = !isUninstall && InstallService.IsInstalled;
        StepList.ItemsSource = _steps;
        VersionText.Text = $"Version {(isUninstall ? InstallService.InstalledVersion ?? AppInfo.Version : AppInfo.Version)}";

        if (isUninstall)
        {
            ShowConfirmPage();
        }
        else
        {
            ShowWelcomePage();
        }
    }

    /// <summary>True when the program folder has to be removed after this process ended (uninstall from the program folder).</summary>
    public bool RemoveProgramFolderOnExit { get; private set; }

    /// <summary>Shows the assistant; <paramref name="finished"/> gets <see cref="RemoveProgramFolderOnExit"/> when it was closed.</summary>
    public static void Run(bool isUninstall, Action<bool> finished)
    {
        var window = new SetupWindow(isUninstall);
        window.Closed += (_, _) => finished(window.RemoveProgramFolderOnExit);
        window.Show();
        window.Activate();
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        // Half an installation is worse than a slow one.
        e.Cancel = _isWorking;
        base.OnClosing(e);
    }

    private void ShowWelcomePage()
    {
        Title = _isUpdate ? "ThixCalendar aktualisieren" : $"ThixCalendar {AppInfo.Version} Setup";
        ModeText.Text = _isUpdate ? "Update" : "Setup";
        if (_isUpdate)
        {
            // An update keeps autostart and shortcuts as they are: no options to ask for.
            WelcomeTitle.Text = "Update";
            WelcomeText.Text = $"Installiert ist {InstallService.InstalledVersion ?? "eine ältere Version"}, dieses Setup bringt {AppInfo.Version}. " +
                               "Ein laufendes ThixCalendar wird beendet und danach wieder gestartet. Konten und Termine bleiben erhalten.";
            OptionsCard.IsVisible = false;
            PrimaryLabel.Text = "Aktualisieren";
        }
        else
        {
            ShowOptions(InstallerOptions.Config);
            PrimaryLabel.Text = "Installieren";
        }

        PlaceInfo.Show($"Wird für dein Benutzerkonto installiert – ohne Administratorrechte – nach {InstallService.ProgramFolder}.", InfoSeverity.Info);
    }

    /// <summary>
    /// The options of a first install from installer.json (SETUP-08): only what it offers, each on as its default – the same
    /// questions ThixLauncher asks before it installs quietly. A file type gets a row of its own.
    /// </summary>
    private void ShowOptions(InstallerConfig config)
    {
        AutostartRow.IsVisible = config.Autostart is not null;
        AutostartToggle.IsChecked = config.Autostart?.Default == true;
        DesktopRow.IsVisible = config.DesktopShortcut is not null;
        DesktopToggle.IsChecked = config.DesktopShortcut?.Default == true || InstallService.HasDesktopShortcut;
        AutoUpdateRow.IsVisible = config.AutomaticUpdates is not null;
        AutoUpdateToggle.IsChecked = config.AutomaticUpdates?.Default == true;

        foreach (var type in config.FileTypes)
        {
            var title = $"{type.Extension}-Dateien mit ThixCalendar öffnen";
            var toggle = new ToggleSwitch { IsChecked = type.Default, OnContent = "", OffContent = "" };
            AutomationProperties.SetName(toggle, title);
            _fileTypeToggles[type.Extension] = toggle;
            OptionsPanel.Children.Add(new MatSettingRow { Title = title, Description = type.Name, Content = toggle });
        }

        var rows = OptionsPanel.Children.OfType<MatSettingRow>().Where(row => row.IsVisible).ToList();
        OptionsCard.IsVisible = rows.Count > 0;
        if (rows.Count > 0)
        {
            rows[^1].BorderThickness = new Thickness(0);
        }
    }

    /// <summary>The answers of the options as install choices (an option not offered stays null).</summary>
    private InstallChoices Choices()
    {
        var config = InstallerOptions.Config;
        return new InstallChoices(
            config.DesktopShortcut is null ? null : DesktopToggle.IsChecked == true,
            config.Autostart is null ? null : AutostartToggle.IsChecked == true,
            config.AutomaticUpdates is null ? null : AutoUpdateToggle.IsChecked == true,
            config.FileTypes.Where(type => _fileTypeToggles.GetValueOrDefault(type.Extension)?.IsChecked == true).Select(type => type.Extension).ToList());
    }

    private void ShowConfirmPage()
    {
        Title = "ThixCalendar deinstallieren";
        ModeText.Text = "Deinstallation";
        WelcomePage.IsVisible = false;
        ConfirmPage.IsVisible = true;
        PrimaryButton.Theme = Ui.Theme("MatButton.Danger");
        PrimaryButton.IsDefault = false;
        PrimaryLabel.Icon = "trash";
        PrimaryLabel.Text = "Deinstallieren";
    }

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (_isFinished)
        {
            if (_succeeded && !_isUninstall && StartToggle.IsChecked == true)
            {
                InstallService.StartInstalled();
            }

            Close();
            return;
        }

        await RunAsync();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private async Task RunAsync()
    {
        _isWorking = true;
        WelcomePage.IsVisible = ConfirmPage.IsVisible = false;
        ProgressPage.IsVisible = true;
        ProgressTitle.Text = _isUninstall ? "Entferne …" : _isUpdate ? "Aktualisiere …" : "Installiere …";
        PrimaryButton.IsEnabled = CancelButton.IsEnabled = false;

        var source = App.ExecutablePath;
        var choices = _isUpdate ? null : Choices();
        var deleteData = DeleteDataToggle.IsChecked == true;
        void Report(string step) => Ui.Post(() => _steps.Add(step));

        string? error = null;
        try
        {
            await Task.Run(() =>
            {
                if (_isUninstall)
                {
                    RemoveProgramFolderOnExit = InstallService.Uninstall(deleteData, Report);
                }
                else
                {
                    InstallService.Install(source, choices, Report);
                }
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            Log.Error(_isUninstall ? "Uninstall failed" : "Setup failed", exception);
            error = exception.Message;
        }

        _isWorking = false;
        ShowFinishPage(error);
    }

    private void ShowFinishPage(string? error)
    {
        _isFinished = true;
        _succeeded = error is null;
        ProgressPage.IsVisible = false;
        FinishPage.IsVisible = true;
        CancelButton.IsVisible = false;
        PrimaryButton.IsEnabled = true;
        PrimaryButton.IsDefault = true;
        PrimaryButton.Theme = Ui.Theme("MatButton.Primary");
        PrimaryLabel.Icon = "check";
        PrimaryLabel.Text = "Fertig";
        StartCard.IsVisible = _succeeded && !_isUninstall;

        if (error is not null)
        {
            FinishTitle.Text = _isUninstall ? "Deinstallation fehlgeschlagen" : "Installation fehlgeschlagen";
            ResultBar.Show($"{error}\n\nDetails stehen im Protokoll ({AppPaths.LogDirectory}).", InfoSeverity.Error);
            return;
        }

        FinishTitle.Text = _isUninstall ? "ThixCalendar wurde entfernt" : "ThixCalendar ist bereit";
        ResultBar.Show(_isUninstall
            ? DeleteDataToggle.IsChecked == true ? "Programm und Daten wurden entfernt." : "Das Programm wurde entfernt. Deine Daten bleiben erhalten."
            : $"ThixCalendar {AppInfo.Version} ist installiert. Es erinnert dich ab jetzt aus dem Infobereich an deine Termine.",
            InfoSeverity.Success);
    }
}
