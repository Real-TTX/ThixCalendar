using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MatCloud.Contract;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Dialogs;

namespace ThixCalendar.Pages;

/// <summary>
/// The tab "Cloud": the connection to MatCloud, as in ThixClip. Not connected: the address and the device name, then
/// "Im Browser anmelden" (first: the code big in a card, "Bestätige den Code im Browser", Seite erneut öffnen, Abbrechen) or
/// "Mit Benutzername und Passwort" (second). Connected: the state, "Jetzt synchronisieren", "Im Browser öffnen", Abmelden.
/// </summary>
public partial class SettingsPage
{
    private CancellationTokenSource? _browserSignIn;
    private string? _browserAddress;

    /// <summary>A sign-in in the browser is waiting for its confirmation (tests).</summary>
    public bool IsSigningInInBrowser => _browserSignIn is not null;

    private void LoadCloud()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = CloudService.Status;
        CloudLogin.IsVisible = !cloud.IsConnected;
        CloudConnected.IsVisible = cloud.IsConnected;
        CloudLogin.IsEnabled = !AccessPolicy.IsCloudDisabled;
        if (AccessPolicy.IsCloudDisabled)
        {
            CloudMessage.Show("Von deiner Organisation festgelegt: die Verbindung zur MatCloud ist ausgeschaltet (Richtlinie DisableCloud).", InfoSeverity.Warning);
        }
        else if (status.State == CloudState.SignedOut)
        {
            CloudMessage.Show(status.Message, InfoSeverity.Warning);
        }

        if (!cloud.IsConnected)
        {
            if (string.IsNullOrEmpty(ServerUrlBox.Text))
            {
                ServerUrlBox.Text = cloud.ServerUrl;
            }

            if (string.IsNullOrEmpty(UserNameBox.Text))
            {
                UserNameBox.Text = cloud.UserName;
            }

            if (string.IsNullOrEmpty(DeviceNameBox.Text))
            {
                DeviceNameBox.Text = cloud.DeviceName.Length > 0 ? cloud.DeviceName : AppInfo.MachineName;
            }

            return;
        }

        CloudServerText.Text = cloud.ServerUrl;
        CloudAccountText.Text = cloud.DisplayName.Length > 0 && cloud.DisplayName != cloud.UserName ? $"{cloud.DisplayName} ({cloud.UserName})" : cloud.UserName;
        CloudDeviceText.Text = cloud.DeviceName;
        CloudStateText.Text = status.Message;
        CloudLastSyncText.Text = status.LastSync is { } last ? Ui.FormatDateTime(last) : "–";
    }

    /// <summary>The first way: MatCloud shows a code, the browser opens, the person confirms it there; ThixCalendar waits (Abbrechen ends it).</summary>
    private async void OnBrowserConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_browserSignIn is not null)
        {
            return;
        }

        CloudMessage.Hide();
        FormErrors.ShowError(this, null, "");
        _browserSignIn = new CancellationTokenSource();
        ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = false;
        try
        {
            if (await CloudService.ConnectInBrowserAsync(ServerUrlBox.Text ?? "", DeviceNameBox.Text ?? "", start => Ui.Post(() => ShowBrowserCode(start)),
                    Ui.OpenExternal, _browserSignIn.Token))
            {
                CloudMessage.Show("Verbunden. Deine Kalender werden jetzt mit der MatCloud abgeglichen.", InfoSeverity.Success);
            }
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            _browserSignIn.Dispose();
            _browserSignIn = null;
            ServerUrlBox.IsEnabled = DeviceNameBox.IsEnabled = BrowserConnectButton.IsEnabled = ShowPasswordSignInButton.IsEnabled = true;
            ShowSignInWay(password: false);
            LoadCloud();
        }
    }

    /// <summary>The code of a started sign-in in the browser, big, with the hint to confirm it there.</summary>
    internal void ShowBrowserCode(DeviceLinkStart start)
    {
        _browserAddress = start.VerificationUrl;
        BrowserCodeText.Text = start.Code;
        BrowserSignIn.IsVisible = PasswordSignIn.IsVisible = false;
        BrowserCode.IsVisible = true;
        FocusAfterLayout(CancelLinkButton);
    }

    /// <summary>The way to sign in: in the browser (first) or with user name and password (second).</summary>
    internal void ShowSignInWay(bool password)
    {
        BrowserCode.IsVisible = false;
        BrowserSignIn.IsVisible = !password;
        PasswordSignIn.IsVisible = password;
    }

    private void OnShowPasswordSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: true);
        FocusAfterLayout(string.IsNullOrEmpty(UserNameBox.Text) ? UserNameBox : PasswordBox);
    }

    private void OnShowBrowserSignInClick(object? sender, RoutedEventArgs e)
    {
        ShowSignInWay(password: false);
        FocusAfterLayout(BrowserConnectButton);
    }

    /// <summary>A control that has just become visible takes the focus once it is laid out (before that it cannot).</summary>
    private static void FocusAfterLayout(Control control) =>
        Dispatcher.UIThread.Post(() => control.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);

    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (_browserAddress is not null)
        {
            Ui.OpenExternal(_browserAddress);
        }
    }

    private void OnCancelLinkClick(object? sender, RoutedEventArgs e) => _browserSignIn?.Cancel();

    /// <summary>The second way: user name and password of a local account of MatCloud.</summary>
    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        CloudMessage.Hide();
        try
        {
            FormErrors.ShowError(this, null, "");
            await CloudService.ConnectAsync(ServerUrlBox.Text ?? "", UserNameBox.Text ?? "", PasswordBox.Text ?? "", DeviceNameBox.Text ?? "");
            PasswordBox.Text = "";
            CloudMessage.Show("Verbunden. Deine Kalender werden jetzt mit der MatCloud abgeglichen.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                CloudMessage.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            LoadCloud();
        }
    }

    private void OnCloudSyncNowClick(object? sender, RoutedEventArgs e) => CloudService.Kick();

    private void OnOpenCloudClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(SettingsService.Get().Cloud.ServerUrl);

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Von der MatCloud abmelden?",
                "Dieser Computer wird aus deinem MatCloud-Konto entfernt und gleicht nicht mehr ab. Konten, Kalender und Termine auf diesem Computer bleiben erhalten.",
                "Abmelden", isDestructive: true))
        {
            return;
        }

        await CloudService.DisconnectAsync();
        CloudMessage.Show("Abgemeldet.", InfoSeverity.Info);
        LoadCloud();
    }
}
