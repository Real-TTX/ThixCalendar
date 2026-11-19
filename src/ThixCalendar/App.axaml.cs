using System.IO.Pipes;
using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Reminders;
using TrayIcon = ThixCalendar.Tray.TrayIcon;
using MatDesktop;

namespace ThixCalendar;

/// <summary>
/// Start-up and end of ThixCalendar.
///  - Windows: the published ThixCalendar.exe is also its setup (RunSetupIfRequested).
///  - One instance per user and data folder: a second start opens the window of the first one.
///  - "--autostart" (sign-in) and "--tray" (after an update) start hidden in the tray / menu bar: the reminders run
///    without the window.
/// </summary>
public partial class App : Application
{
    public const string TrayArgument = "--tray";

    // One instance per data folder: a development run with its own THIXCALENDAR_DATA_DIR does not meet the installed one.
    private static readonly string InstanceSuffix = AppPaths.HasOverride
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDirectory.ToUpperInvariant())))[..16]
        : "";

    private static readonly string InstanceName = "ThixCalendar.SingleInstance" + InstanceSuffix;
    private static readonly string ShowWindowPipeName = "ThixCalendar.ShowWindow" + InstanceSuffix + "." + Environment.UserName;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Mutex? _instanceMutex;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _isExiting;
    private bool _isAskingPause;

    /// <summary>The program file; the autostart entry points to it.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ThixCalendar");

    public static App Instance => (App)Current!;

    /// <summary>ThixCalendar's accent: the blue of its logo (the same in MatCloud's pages of ThixCalendar).</summary>
    public const string Accent = "#2F6FED";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ThixCalendar speaks German, the texts of MatDesktop (title bar, menu, tray, setup) too.
        MatText.Language = "de";
        MatTheme.Install(this, new MatThemeOptions(Accent) { Icon = Ui.AppIcon });
        Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://ThixCalendar/Themes/Icons.axaml")));
        Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://ThixCalendar/Themes/Theme.axaml")));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _desktop = desktop;
        var args = desktop.Args ?? [];

        var selfTest = Array.IndexOf(args, SelfTest.Argument);
        if (selfTest >= 0)
        {
            var report = selfTest + 1 < args.Length ? args[selfTest + 1] : Path.Combine(AppPaths.DataDirectory, "self-test.txt");
            Dispatcher.UIThread.Post(async () => EndProcess(await SelfTest.RunAsync(report)));
            return;
        }

        if (RunSetupIfRequested(args))
        {
            return;
        }

        if (!TakeSingleInstance())
        {
            EndProcess(0);
            return;
        }

        Start(args);
    }

    /// <summary>The main window (null during setup).</summary>
    public MainWindow? MainWindow => _mainWindow;

    /// <summary>
    /// Ends the process through the main loop. Shutting down straight from OnFrameworkInitializationCompleted (update,
    /// quiet uninstall, second instance) throws "Dispatcher shut down" because the loop has not started yet.
    /// </summary>
    private void EndProcess(int exitCode) => Dispatcher.UIThread.Post(() => _desktop?.Shutdown(exitCode));

    private void Start(string[] args)
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        UiWatchdog.Start();
        Log.Info($"ThixCalendar {AppInfo.Version} starting on {UpdateService.PlatformKey} ({string.Join(' ', args)})");

        // Development: a sample week in a separate data folder (never in the real one).
        if (AppPaths.HasOverride && Environment.GetEnvironmentVariable(SampleData.Variable) == "1")
        {
            SampleData.Create(DateTime.Today);
        }

        ApplyTheme(SettingsService.Get().Theme);
        SettingsService.Changed += (_, settings) => Ui.Post(() => ApplyTheme(settings.Theme));

        _mainWindow = new MainWindow();
        _trayIcon = new TrayIcon(this);

        var startHidden = args.Contains(TrayArgument) || args.Contains(AutostartService.StartupArgument);
        if (!startHidden)
        {
            _mainWindow.Show();
        }

        SyncService.Start();
        CloudService.Start();
        _ = ConnectFromLauncherAsync();
        ReminderCoordinator.Start();

        // Crashed or hung last time: say so, with the report.
        Dispatcher.UIThread.Post(() => Dialogs.ProblemReportDialog.ShowUnseen(), DispatcherPriority.Background);

        // macOS: opening the app again (Finder, Dock, Spotlight) shows the window.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, activation) =>
            {
                if (activation.Kind == ActivationKind.Reopen)
                {
                    ShowMainWindow();
                }
            };
        }

        UpdateCoordinator.Checked += (_, result) =>
        {
            _trayIcon?.SetUpdateAvailable(result.IsUpdateAvailable);
            if (result is { IsUpdateAvailable: true, Manifest: { } manifest } && !SettingsService.Get().InstallUpdatesAutomatically)
            {
                _trayIcon?.ShowNotification("Update verfügbar", $"ThixCalendar {manifest.Version} kann installiert werden (Einstellungen › Updates).");
            }
        };
        UpdateCoordinator.Start();

        // After sleep, a network change or a new Wi-Fi: synchronise at once and catch up on missed reminders.
        NetworkChange.NetworkAvailabilityChanged += (_, availability) =>
        {
            if (availability.IsAvailable)
            {
                SyncService.Kick();
            }
        };
        NetworkChange.NetworkAddressChanged += (_, _) => SyncService.Kick();
        CalendarService.Changed += (_, _) => Ui.Post(() => _ = ReminderCoordinator.CheckAsync());
    }

    /// <summary>Opens the main window (tray, second start, Dock, a reminder) and optionally navigates.</summary>
    public void ShowMainWindow(Action<MainWindow>? navigate = null)
    {
        if (_mainWindow is null || _isExiting)
        {
            return;
        }

        _mainWindow.ShowFromTray();
        navigate?.Invoke(_mainWindow);
    }

    /// <summary>The waiting reminders on screen again (tray, sidebar).</summary>
    public void ShowReminders() => ReminderCoordinator.ShowAll();

    public void ShowTestReminder() => ReminderCoordinator.ShowTest();

    /// <summary>"Erinnerungen pausieren": a running pause ends at once, otherwise the dialog asks how long.</summary>
    public async Task ToggleReminderPauseAsync(Window? owner = null)
    {
        if (ReminderService.IsPaused(DateTimeOffset.Now))
        {
            SettingsService.PauseReminders(null);
            await ReminderCoordinator.CheckAsync();
            return;
        }

        if (_isAskingPause)
        {
            return;
        }

        _isAskingPause = true;
        try
        {
            owner ??= _mainWindow is { IsVisible: true } ? _mainWindow : null;
            if (await Dialogs.PauseDialog.AskAsync(owner) is { } until)
            {
                SettingsService.PauseReminders(until);
                await ReminderCoordinator.CheckAsync();
            }
        }
        catch (BusinessException exception)
        {
            _trayIcon?.ShowNotification("ThixCalendar", exception.Message, isWarning: true);
        }
        finally
        {
            _isAskingPause = false;
        }
    }

    /// <summary>The setting "Erscheinungsbild": Wie das System, Hell, Dunkel (DESK-02).</summary>
    public void ApplyTheme(ThemeChoice theme) => MatTheme.SetAppearance(theme switch
    {
        ThemeChoice.Light => MatAppearance.Light,
        ThemeChoice.Dark => MatAppearance.Dark,
        _ => MatAppearance.System,
    });

    /// <summary>ThixLauncher installed ThixCalendar and left its MatCloud sign-in: connected without the browser.</summary>
    private async Task ConnectFromLauncherAsync()
    {
        if (await Task.Run(() => CloudService.ConnectFromLauncherAsync()) is { } account)
        {
            Ui.Post(() => _trayIcon?.ShowNotification("Mit der MatCloud verbunden", $"ThixCalendar gleicht deine Kalender jetzt mit der MatCloud ab (Konto {account}, angemeldet von ThixLauncher)."));
        }
    }

    /// <summary>"Beenden": ends the application.</summary>
    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        UiWatchdog.Stop();
        Log.Info("ThixCalendar exiting");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ReminderCoordinator.Stop();
        SyncService.Stop();
        CloudService.Stop();
        _trayIcon?.Dispose();
        if (_mainWindow is not null)
        {
            _mainWindow.IsExiting = true;
            _mainWindow.Close();
        }

        CalendarStore.Close();
        _instanceMutex?.Dispose();
        Log.Info($"ThixCalendar ended after {watch.ElapsedMilliseconds} ms");
        _desktop?.Shutdown(0);
    }

    /// <summary>
    /// The command line of SETUP-05 with the install options of SETUP-08 (InstallerOptions; a wrong value: exit code 2).
    /// Windows: the published ThixCalendar.exe is its own setup (InstallService):
    ///  - "--uninstall" (from "Installierte Apps"): the uninstall assistant, or silently with "--quiet".
    ///  - "--quiet [options]" (ThixLauncher, ThixInstall, an administrator): installs without a window; "--start" starts it in the tray.
    ///  - "--update [options]" (downloaded by the update check): installs itself silently and starts the installed ThixCalendar in the tray.
    ///  - started anywhere else than the program folder (e.g. from Downloads): the setup assistant.
    /// Both systems: "--configure [options]" applies the options to the installed ThixCalendar and ends (macOS: ThixLauncher
    /// calls it after it copied the bundle). A development build (not single-file) runs as the application otherwise.
    /// </summary>
    private bool RunSetupIfRequested(string[] args)
    {
        SetupRequest request;
        try
        {
            request = InstallerOptions.Parse(args);
        }
        catch (BusinessException exception)
        {
            Log.Warning($"Setup arguments refused: {exception.Message}");
            Console.Error.WriteLine(exception.Message);
            EndProcess(2);
            return true;
        }

        if (request.Action == SetupAction.Configure)
        {
            InstallerOptions.Apply(request.Choices, OperatingSystem.IsWindows() && File.Exists(InstallService.InstalledExecutable) ? InstallService.InstalledExecutable : ExecutablePath, Log.Info);
            EndProcess(0);
            return true;
        }

        return OperatingSystem.IsWindows() && RunWindowsSetup(request);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool RunWindowsSetup(SetupRequest request)
    {
        if (request.Action == SetupAction.Uninstall)
        {
            if (request.Quiet)
            {
                if (InstallService.Uninstall(deleteUserData: false, Log.Info))
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
                return true;
            }

            Setup.SetupWindow.Run(isUninstall: true, removeProgramFolder =>
            {
                if (removeProgramFolder)
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
            });
            return true;
        }

        if (request.Action is SetupAction.Install or SetupAction.Update)
        {
            var exitCode = 0;
            try
            {
                InstallService.Install(ExecutablePath, request.Choices, Log.Info);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            {
                Log.Error(request.Action == SetupAction.Update ? "Update failed" : "Quiet install failed", exception);
                Console.Error.WriteLine(exception.Message);
                exitCode = 1;
            }

            // an update starts ThixCalendar again; a quiet install only with "--start" (in the tray, the reminders run)
            if ((request.Action == SetupAction.Update || request.Start) && File.Exists(InstallService.InstalledExecutable))
            {
                InstallService.StartInstalled(TrayArgument);
            }

            EndProcess(exitCode);
            return true;
        }

#if SINGLE_FILE
        const bool isPublishedBuild = true;
#else
        const bool isPublishedBuild = false;
#endif
        if (isPublishedBuild && !InstallService.IsInstalledExecutable(ExecutablePath))
        {
            Setup.SetupWindow.Run(isUninstall: false, _ => EndProcess(0));
            return true;
        }

        return false;
    }

    /// <summary>
    /// The first instance owns the mutex and listens on a pipe; a second start knocks on the pipe (the first one shows
    /// its window) and ends. On Windows the setup may also ask the running instance to end (ListenForExitSignal).
    /// </summary>
    private bool TakeSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            KnockOnRunningInstance();
            return false;
        }

        var listener = new Thread(ListenForSecondStarts) { IsBackground = true, Name = "ThixCalendar second start listener" };
        listener.Start();
        if (OperatingSystem.IsWindows())
        {
            ListenForExitSignal();
        }

        return true;
    }

    private static void KnockOnRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ShowWindowPipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(1);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not reach the running instance: {exception.Message}");
        }
    }

    private void ListenForSecondStarts()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(ShowWindowPipeName, PipeDirection.In, 1);
                server.WaitForConnection();
                server.ReadByte();
                Ui.Post(() => ShowMainWindow());
            }
            catch (IOException exception)
            {
                Log.Warning($"Second start listener: {exception.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>The setup asks a running ThixCalendar to end (update, uninstall).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ListenForExitSignal()
    {
        if (InstanceSuffix.Length > 0)
        {
            return; // a development run with its own data folder is none of the setup's business
        }

        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstallService.ExitSignalName);
        var listener = new Thread(() =>
        {
            exitSignal.WaitOne();
            Ui.Post(() =>
            {
                Log.Info("Setup asked ThixCalendar to end");
                ExitApplication();
            });
        })
        {
            IsBackground = true,
            Name = "ThixCalendar exit signal listener",
        };
        listener.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        e.Handled = true;
        if (e.Exception is BusinessException business)
        {
            _trayIcon?.ShowNotification("ThixCalendar", business.Message, isWarning: true);
            return;
        }

        _trayIcon?.ShowNotification("Unerwarteter Fehler", $"{e.Exception.Message} – Details im Protokoll ({AppPaths.LogDirectory}).", isWarning: true);
    }
}
