using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;

namespace ThixCalendar.Pages;

/// <summary>Version, channel, build and where the data lives.</summary>
public partial class AboutPage : UserControl, IRefreshable
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        ChannelText.Text = AppInfo.Channel switch
        {
            "release" => "Release",
            "nightly" => "Nightly",
            _ => "Lokaler Build",
        };
        BuildText.Text = AppInfo.BuildNumber > 0 ? $"#{AppInfo.BuildNumber}" : "–";
        SystemText.Text = $"{RuntimeInformation.OSDescription} ({UpdateService.PlatformKey}), .NET {Environment.Version}";
        DataText.Text = AppPaths.DataDirectory;
        AttachedToVisualTree += (_, _) => RefreshUpdate();
        UpdateCoordinator.Checked += (_, _) => Ui.Post(RefreshUpdate);
    }

    public void Refresh() => RefreshUpdate();

    /// <summary>What the last update check found; the button leads to Einstellungen › Updates (install or check there).</summary>
    private void RefreshUpdate()
    {
        var result = UpdateService.LastResult;
        var available = result is { IsUpdateAvailable: true, Manifest: not null };
        UpdateTitle.Text = available ? $"Version {result!.Manifest!.Version} ist verfügbar" : AppInfo.IsLocalBuild ? "Lokaler Build" : "Updates";
        UpdateText.Text = available
            ? "Installieren dauert nur einen Moment, Konten und Termine bleiben erhalten."
            : AppInfo.IsLocalBuild
                ? "Ein lokaler Build bekommt keine Updates."
                : result is null
                    ? "Noch nicht geprüft."
                    : $"{result.Message} Zuletzt geprüft: {(UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–")}.";
        UpdateLabel.Text = available ? "Update installieren …" : "Nach Updates suchen …";
        UpdateButton.Theme = available ? Ui.Theme("PrimaryButton") : null;
        UpdateIcon.Foreground = Ui.Brush(available ? "AccentBrush" : "TextMutedBrush");
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (Ui.WindowOf(this) is MainWindow window)
        {
            window.ShowSettings(SettingsPage.UpdatesTab);
        }
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal($"https://github.com/{AppInfo.Repository}");

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (Ui.WindowOf(this) is MainWindow window)
        {
            window.ShowCalendar();
        }
    }
}
