using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Dialogs;

/// <summary>
/// Waits while the user signs in in the browser (Microsoft 365 or Google): explains what happens, can open the browser
/// again and cancel. The business layer does the sign-in and the first sync; errors are shown here, with the way to the
/// settings when the app registration is missing.
/// </summary>
public sealed class SignInDialog : ThemedWindow
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly AccountKind _kind;
    private readonly long? _accountId;
    private readonly TextBlock _text;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 4, 0, 18) };
    private readonly InfoBar _error = new();
    private readonly Button _reopen;
    private readonly Button _settings;
    private string? _url;

    private SignInDialog(AccountKind kind, long? accountId)
    {
        _kind = kind;
        _accountId = accountId;
        var name = CalendarAccount.KindDisplayName(kind);
        Title = $"Bei {name} anmelden";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this[!BackgroundProperty] = this.GetResourceObservable("CardBrush").ToBinding();

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, 10),
            Children =
            {
                new AccountMark { Kind = kind, Width = 24, Height = 24 },
                new TextBlock { Text = Title, FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _text = new TextBlock
        {
            Text = $"Dein Browser öffnet die Anmeldung von {(kind == AccountKind.Microsoft ? "Microsoft" : "Google")}. Melde dich dort an und erlaube ThixCalendar den Zugriff auf deine Kalender – danach geht es hier von selbst weiter. Dein Passwort sieht ThixCalendar nie.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Brush("TextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 14),
        };

        _reopen = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.External"), Text = "Browser erneut öffnen" }, IsEnabled = false };
        _reopen.Click += (_, _) =>
        {
            if (_url is not null)
            {
                Ui.OpenExternal(_url);
            }
        };
        _settings = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.Settings"), Text = "Eigene Registrierung eintragen" }, IsVisible = false };
        _settings.Click += (_, _) =>
        {
            Close();
            App.Instance.ShowMainWindow(window => window.ShowSettings(Pages.SettingsPage.AccessTab));
        };
        var cancel = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.Close"), Text = "Abbrechen" }, IsCancel = true };
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => _cancel.Cancel();

        Content = new StackPanel { Margin = new Thickness(24, 22), Children = { heading, _text, _progress, _error, new ActionBar { Children = { _settings, _reopen, cancel } } } };
    }

    public CalendarAccount? Result { get; private set; }

    /// <summary>Adds (<paramref name="accountId"/> null) or signs in again; returns the account or null (cancelled, failed).</summary>
    public static async Task<CalendarAccount?> RunAsync(Window owner, AccountKind kind, long? accountId)
    {
        var dialog = new SignInDialog(kind, accountId);
        dialog.Opened += async (_, _) => await dialog.SignInAsync();
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private async Task SignInAsync()
    {
        void OpenBrowser(string url)
        {
            _url = url;
            Ui.Post(() => _reopen.IsEnabled = true);
            Ui.OpenExternal(url);
        }

        try
        {
            Result = _accountId is { } id
                ? await AccountService.ReconnectAsync(id, OpenBrowser, _cancel.Token)
                : _kind == AccountKind.Microsoft
                    ? await AccountService.AddMicrosoftAsync(OpenBrowser, _cancel.Token)
                    : await AccountService.AddGoogleAsync(OpenBrowser, _cancel.Token);
            Close();
        }
        catch (OperationCanceledException)
        {
            // closed by the user
        }
        catch (BusinessException exception)
        {
            if (!IsVisible)
            {
                return;
            }

            _progress.IsVisible = false;
            _reopen.IsVisible = false;
            _error.Show(exception.Message, InfoSeverity.Error);
            _settings.IsVisible = !AccountService.IsConfigured(_kind);
            _text.Text = AccountService.IsConfigured(_kind) ? "Die Anmeldung hat nicht geklappt." : "Diese Version kann sich bei diesem Anbieter noch nicht anmelden.";
        }
    }
}
