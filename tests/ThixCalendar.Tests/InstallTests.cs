using Microsoft.Win32;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Tests;

/// <summary>
/// The setup and its install options (SETUP-08). Nothing here touches the real program folder, registry values, file types or
/// LaunchAgents: the program goes into the test folder, the registry under HKCU\Software\ThixCalendarTests, the autostart into memory.
/// </summary>
public sealed class InstallTests : IDisposable
{
    private readonly TestData _data = new();

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            InstallService.UseLocationsForTests(null);
            ThixCalendar.Core.Platform.Windows.FileAssociations.UseClassesRootForTests(null);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ThixCalendarTests", throwOnMissingSubKey: false);
        }

        AutostartService.UseInMemoryEntry(null);
        InstallerOptions.UseConfigForTests(null);
        _data.Dispose();
    }

    [Fact]
    public void The_setup_installs_with_the_chosen_options_keeps_them_on_update_and_uninstalls_everything()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var program = Path.Combine(_data.Folder, "Programs", "ThixCalendar");
        var shortcuts = Path.Combine(_data.Folder, "StartMenu");
        InstallService.UseLocationsForTests(program, shortcuts, @"Software\ThixCalendarTests\Uninstall");
        var source = Path.Combine(_data.Folder, "Downloads", "ThixCalendarSetup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, new byte[1024 * 8]);

        AutostartService.UseInMemoryEntry(false);
        ThixCalendar.Core.Platform.Windows.FileAssociations.UseClassesRootForTests(@"Software\ThixCalendarTests\Classes");
        // ThixCalendar offers no file types; a test type proves the contract's file type path all the same
        InstallerOptions.UseConfigForTests(InstallerOptions.Config with { FileTypes = [new InstallerFileType { Extension = ".thixcal", Name = "ThixCalendar-Test", Default = true }] });
        var steps = new List<string>();
        InstallService.Install(source, new InstallChoices(DesktopShortcut: true, Autostart: false, AutomaticUpdates: false), steps.Add);

        Assert.True(File.Exists(InstallService.InstalledExecutable));
        Assert.True(File.Exists(InstallService.StartMenuShortcut));
        Assert.True(File.Exists(InstallService.DesktopShortcut));
        Assert.True(InstallService.IsInstalled);
        Assert.Equal(AppInfo.Version, InstallService.InstalledVersion);
        Assert.Contains("Installation abgeschlossen", steps);
        Assert.False(AutostartService.IsEnabled());
        Assert.False(SettingsService.Get().InstallUpdatesAutomatically);
        Assert.Equal([".thixcal"], InstallerOptions.Current().FileTypes);
        using (var command = Registry.CurrentUser.OpenSubKey(@"Software\ThixCalendarTests\Classes\ThixCalendar.thixcal\shell\open\command"))
        {
            Assert.Equal($"\"{InstallService.InstalledExecutable}\" \"%1\"", command?.GetValue(""));
        }

        // An update keeps what is installed; a given option changes only itself.
        InstallService.Install(source, null, _ => { });
        Assert.True(File.Exists(InstallService.DesktopShortcut));
        Assert.False(AutostartService.IsEnabled());
        Assert.False(SettingsService.Get().InstallUpdatesAutomatically);
        InstallService.Install(source, new InstallChoices(Autostart: true, FileTypes: []), _ => { });
        Assert.True(AutostartService.IsEnabled());
        Assert.Empty(InstallerOptions.Current().FileTypes!);
        Assert.True(File.Exists(InstallService.DesktopShortcut));

        var removeLater = InstallService.Uninstall(deleteUserData: false, _ => { });
        Assert.False(removeLater);
        Assert.False(Directory.Exists(program));
        Assert.False(File.Exists(InstallService.StartMenuShortcut));
        Assert.False(File.Exists(InstallService.DesktopShortcut));
        Assert.False(InstallService.IsInstalled);
        Assert.False(AutostartService.IsEnabled());
        Assert.True(Directory.Exists(AppPaths.DataDirectory), "the data stays unless the user deletes it");
    }

    [Fact]
    public void A_first_install_told_nothing_takes_the_defaults_of_installer_json()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        InstallService.UseLocationsForTests(Path.Combine(_data.Folder, "Programs", "ThixCalendar"), Path.Combine(_data.Folder, "StartMenu"), @"Software\ThixCalendarTests\Uninstall");
        var source = Path.Combine(_data.Folder, "ThixCalendarSetup.exe");
        File.WriteAllBytes(source, new byte[1024]);
        AutostartService.UseInMemoryEntry(false);

        InstallService.Install(source, InstallerOptions.Parse([InstallerOptions.QuietArgument]).Choices, _ => { });

        Assert.True(AutostartService.IsEnabled());
        Assert.False(File.Exists(InstallService.DesktopShortcut));
        Assert.True(SettingsService.Get().InstallUpdatesAutomatically);
        InstallService.Uninstall(deleteUserData: false, _ => { });
    }

    [Fact]
    public void The_command_line_carries_the_install_options_and_refuses_what_it_does_not_know()
    {
        InstallerOptions.UseConfigForTests(InstallerOptions.Config with { FileTypes = [new InstallerFileType { Extension = ".ics", Name = "iCalendar-Datei", Default = true }, new InstallerFileType { Extension = ".vcs", Name = "vCalendar-Datei" }] });
        var request = InstallerOptions.Parse(["--quiet", "--desktop-shortcut=yes", "--autostart=no", "--automatic-updates=ja", "--file-types=ics,.VCS", "--start"]);
        Assert.Equal(SetupAction.Install, request.Action);
        Assert.True(request.Start);
        Assert.Equal((true, false, true), (request.Choices.DesktopShortcut, request.Choices.Autostart, request.Choices.AutomaticUpdates));
        Assert.Equal([".ics", ".vcs"], request.Choices.FileTypes);
        Assert.Equal(["--desktop-shortcut=yes", "--autostart=no", "--automatic-updates=yes", "--file-types=.ics,.vcs"], InstallerOptions.ToArguments(request.Choices));
        Assert.Equal([".ics"], InstallerOptions.Parse(["--update", "--file-types=default"]).Choices.FileTypes);
        Assert.Empty(InstallerOptions.Parse(["--configure", "--file-types=none"]).Choices.FileTypes!);
        Assert.Equal(SetupAction.Configure, InstallerOptions.Parse(["--configure", "--autostart=yes"]).Action);
        Assert.True(InstallerOptions.Parse(["--uninstall", "--quiet"]) is { Action: SetupAction.Uninstall, Quiet: true });

        // a bare "--autostart" is the start at sign-in, "--tray" the start after an update: no setup
        Assert.Equal(SetupAction.None, InstallerOptions.Parse(["--autostart"]).Action);
        Assert.Equal(SetupAction.None, InstallerOptions.Parse(["--tray"]).Action);

        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--autostart=vielleicht"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--file-types=.docx"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--color=blue"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--tray", "--autostart=yes"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--uninstall", "--autostart=no"]));

        InstallerOptions.UseConfigForTests(InstallerOptions.Config with { DesktopShortcut = null });
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--desktop-shortcut=yes"]));
    }

    [Fact]
    public void The_embedded_installer_json_is_what_the_manifest_publishes()
    {
        var config = InstallerOptions.Config;
        Assert.NotNull(config.Autostart);
        Assert.NotNull(config.DesktopShortcut);
        Assert.NotNull(config.AutomaticUpdates);
        Assert.Empty(config.FileTypes); // ThixCalendar opens no files: an ICS calendar is subscribed by its address
        Assert.Equal((false, true, true), (InstallerOptions.Defaults.DesktopShortcut, InstallerOptions.Defaults.Autostart, InstallerOptions.Defaults.AutomaticUpdates));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--file-types=.ics"]));
        Assert.Empty(InstallerOptions.Parse(["--quiet", "--file-types=none"]).Choices.FileTypes!);
    }
}
