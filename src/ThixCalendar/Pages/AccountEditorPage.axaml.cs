using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Dialogs;
using MatDesktop;

namespace ThixCalendar.Pages;

/// <summary>
/// Creates (ICS subscription, local calendar) or changes an account and its calendars. Everything goes through
/// AccountService / CalendarService, which check values and policies; errors appear at their field.
/// </summary>
public partial class AccountEditorPage : UserControl, IRefreshable
{
    private readonly AccountKind _kind;
    private long? _accountId;
    private bool _isBusy;
    private bool _showingMode;

    /// <summary>The XAML previewer (a new local calendar).</summary>
    public AccountEditorPage()
        : this(null, AccountKind.Local)
    {
    }

    public AccountEditorPage(long? accountId, AccountKind? newKind = null)
    {
        InitializeComponent();
        _accountId = accountId;
        _kind = accountId is { } id ? AccountService.Get(id)?.Kind ?? AccountKind.Local : newKind ?? AccountKind.Local;
        Mark.Kind = _kind;
        if (accountId is null)
        {
            NameBox.Text = _kind == AccountKind.Local ? "Privat" : "";
            NameBox.Watermark = _kind == AccountKind.Ics ? "leer = Name aus dem Kalender" : "";
        }

        Refresh();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (_kind == AccountKind.Ics && accountId is null ? UrlBox : NameBox).Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private MainWindow? Window => Ui.WindowOf(this) as MainWindow;

    public void Refresh()
    {
        var account = _accountId is { } id ? AccountService.Get(id) : null;
        var isNew = account is null;
        PageTitle.Text = isNew ? _kind == AccountKind.Ics ? "ICS-Abo hinzufügen" : "Kalender auf diesem Computer" : account!.Name;
        PageSubtitle.Text = CalendarAccount.KindDisplayName(_kind);
        KindText.Text = _kind switch
        {
            AccountKind.Microsoft => "Microsoft 365 / Outlook.com (Microsoft Graph)",
            AccountKind.Google => "Google Kalender",
            AccountKind.Ics => "ICS-Abo – nur lesen",
            _ => "Nur auf diesem Computer (keine Synchronisierung)",
        };
        if (account is not null && !NameBox.IsFocused)
        {
            NameBox.Text = account.Name;
        }

        UrlField.IsVisible = UrlHelp.IsVisible = _kind == AccountKind.Ics;
        if (account is not null && _kind == AccountKind.Ics && !UrlBox.IsFocused)
        {
            UrlBox.Text = account.Url;
        }

        SyncModeField.IsVisible = account is { Kind: not AccountKind.Local } && CloudService.IsConnected && !AccessPolicy.IsCloudDisabled;
        _showingMode = true;
        SyncModeBox.SelectedIndex = account?.SyncMode == AccountSyncMode.Cloud ? 1 : 0;
        _showingMode = false;
        UserLabel.IsVisible = UserText.IsVisible = account?.IsOnline == true;
        UserText.Text = account?.UserName ?? "";
        Facts.IsVisible = !isNew;
        SyncText.Text = account?.Kind == AccountKind.Local ? "–" : account?.LastSync is { } last ? Ui.FormatDateTime(last) : "noch nie";
        if (account?.IsKeptByCloud == true)
        {
            SyncText.Text += " · durch MatCloud";
        }

        StateNote.Hide();
        if (account?.State == AccountState.SignedOut)
        {
            StateNote.Show($"Die Anmeldung ist abgelaufen ({account.LastError}). Ohne neue Anmeldung kommen von hier keine Termine und keine Erinnerungen.", InfoSeverity.Warning);
        }
        else if (account?.State == AccountState.Error)
        {
            StateNote.Show($"Der letzte Abgleich ist fehlgeschlagen: {account.LastError}", InfoSeverity.Warning);
        }

        SaveLabel.Text = isNew ? _kind == AccountKind.Ics ? "Abonnieren" : "Anlegen" : "Speichern";
        SaveLabel.Icon = (isNew ? "plus" : "check");
        ReconnectButton.IsVisible = account?.IsOnline == true;
        ReconnectButton.Theme = account?.State == AccountState.SignedOut ? Ui.Theme("MatButton.Warning") : null;
        SyncButton.IsVisible = account is { Kind: not AccountKind.Local };
        RemoveButton.IsVisible = !isNew;
        CalendarsTab.IsVisible = !isNew;
        AddCalendarCard.IsVisible = account?.Kind == AccountKind.Local;
        CalendarsIntro.Text = _kind == AccountKind.Local
            ? "Farbe, Anzeige und Erinnerungen je Kalender. Die Termine bleiben auf diesem Computer."
            : "Farbe, Anzeige und Erinnerungen je Kalender. Neue Kalender und Namen kommen vom Anbieter – beim nächsten Abgleich.";
        if (account is not null)
        {
            BuildCalendarCards(account);
        }
    }

    private void BuildCalendarCards(CalendarAccount account)
    {
        CalendarCards.Children.Clear();
        var calendars = AccountService.CalendarsOf(account.Id);
        foreach (var calendar in calendars)
        {
            CalendarCards.Children.Add(CalendarCard(account, calendar, canDelete: account.Kind == AccountKind.Local && calendars.Count > 1));
        }
    }

    /// <summary>One calendar: name (editable for local ones), colour, switches for showing and reminding.</summary>
    private Border CalendarCard(CalendarAccount account, CalendarInfo calendar, bool canDelete)
    {
        var calendarId = calendar.Id;
        var current = calendar;
        var nameBox = new TextBox { Text = calendar.Name, Width = 260, IsVisible = account.Kind == AccountKind.Local };
        AutomationProperties.SetName(nameBox, "Name des Kalenders");
        var nameText = new TextBlock { Text = calendar.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, IsVisible = account.Kind != AccountKind.Local, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(5), Background = Ui.SolidBrush(calendar.Color), VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { dot, nameText, nameBox } };
        if (calendar.IsReadOnly)
        {
            header.Children.Add(new Border { Theme = Ui.Theme("MatBadge"), Child = new TextBlock { Text = "Nur lesen" } });
        }

        if (calendar.IsPrimary && account.Kind != AccountKind.Ics)
        {
            header.Children.Add(new Border { Theme = Ui.Theme("MatBadge"), Child = new TextBlock { Text = "Hauptkalender" } });
        }

        var swatches = new ColorSwatches { SelectedColor = calendar.Color };
        var visible = new ToggleSwitch { IsChecked = calendar.IsVisible, OnContent = "", OffContent = "" };
        var reminders = new ToggleSwitch { IsChecked = calendar.RemindersEnabled, OnContent = "", OffContent = "" };
        AutomationProperties.SetName(visible, $"{calendar.Name} anzeigen");
        AutomationProperties.SetName(reminders, $"Erinnerungen für {calendar.Name}");

        void Save()
        {
            try
            {
                current = CalendarService.UpdateCalendar(calendarId, nameBox.Text ?? "", swatches.SelectedColor, reminders.IsChecked == true);
                FormErrors.ShowError(this, null, "");
            }
            catch (BusinessException exception)
            {
                Message.Show(exception.Message, InfoSeverity.Error);
            }
        }

        swatches.ColorChosen += (_, color) =>
        {
            dot.Background = Ui.SolidBrush(color);
            Save();
        };
        reminders.IsCheckedChanged += (_, _) => Save();
        nameBox.LostFocus += (_, _) =>
        {
            if (nameBox.Text != current.Name)
            {
                Save();
            }
        };
        visible.IsCheckedChanged += (_, _) => CalendarService.SetCalendarVisible(calendarId, visible.IsChecked == true);

        var content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                header,
                new MatSettingRow { Title = "Farbe", Content = swatches, Padding = new Thickness(0, 8), BorderThickness = new Thickness(0) },
                new MatSettingRow { Title = "Im Kalender anzeigen", Content = visible, Padding = new Thickness(0, 6) },
                new MatSettingRow
                {
                    Title = "Erinnerungen",
                    Description = calendar.DefaultReminderMinutes is { } minutes ? $"Standard dieses Kalenders: {Ui.ReminderText(minutes)}" : "Für Feiertage oder Geburtstage meist aus.",
                    Content = reminders,
                    Padding = new Thickness(0, 6),
                    BorderThickness = new Thickness(0),
                },
            },
        };
        if (canDelete)
        {
            var delete = new Button { Theme = Ui.Theme("MatButton.Danger"), Content = new IconLabel { Icon = "trash", Text = "Kalender löschen" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            delete.Click += async (_, _) =>
            {
                if (Window is { } window && await ConfirmDialog.AskAsync(window, "Kalender löschen?", $"„{current.Name}“ und alle seine Termine werden gelöscht.", "Löschen", isDestructive: true))
                {
                    try
                    {
                        AccountService.DeleteLocalCalendar(calendarId);
                        Refresh();
                    }
                    catch (BusinessException exception)
                    {
                        Message.Show(exception.Message, InfoSeverity.Error);
                    }
                }
            };
            content.Children.Add(delete);
        }

        return new Border
        {
            Theme = Ui.Theme("MatCard"),
            Child = content,
        };
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        SaveButton.IsEnabled = false;
        Message.Hide();
        try
        {
            if (_accountId is null)
            {
                if (_kind == AccountKind.Ics)
                {
                    Message.Show("Lade den Kalender …", InfoSeverity.Info);
                    var account = await AccountService.AddIcsAsync(NameBox.Text ?? "", UrlBox.Text ?? "");
                    _accountId = account.Id;
                    Message.Show($"„{account.Name}“ ist abonniert.", InfoSeverity.Success);
                }
                else
                {
                    var account = AccountService.AddLocal(NameBox.Text ?? "");
                    _accountId = account.Id;
                    Message.Show($"„{account.Name}“ ist angelegt.", InfoSeverity.Success);
                }

                Refresh();
                Tabs.SelectedItem = CalendarsTab;
                return;
            }

            AccountService.Rename(_accountId.Value, NameBox.Text ?? "");
            if (_kind == AccountKind.Ics)
            {
                await AccountService.ChangeIcsUrlAsync(_accountId.Value, UrlBox.Text ?? "");
            }

            Message.Show("Gespeichert.", InfoSeverity.Success);
            Refresh();
        }
        catch (BusinessException exception)
        {
            Message.Hide();
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                Message.Show(exception.Message, InfoSeverity.Error);
            }
        }
        finally
        {
            _isBusy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void OnAddCalendarClick(object? sender, RoutedEventArgs e)
    {
        if (_accountId is not { } id)
        {
            return;
        }

        try
        {
            AccountService.AddLocalCalendar(id, NewCalendarBox.Text ?? "");
            NewCalendarBox.Text = "";
            Refresh();
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, "NewCalendar", exception.Message))
            {
                Message.Show(exception.Message, InfoSeverity.Error);
            }
        }
    }

    private async void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        if (_accountId is { } id && Window is { } window && await SignInDialog.RunAsync(window, _kind, id) is not null)
        {
            Message.Show("Wieder angemeldet und synchronisiert.", InfoSeverity.Success);
            Refresh();
        }
    }

    /// <summary>Who keeps the account in step: handed to MatCloud or back to the computers, at once.</summary>
    private async void OnSyncModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_showingMode || _accountId is not { } id || _isBusy)
        {
            return;
        }

        var mode = SyncModeBox.SelectedIndex == 1 ? AccountSyncMode.Cloud : AccountSyncMode.Computers;
        _isBusy = true;
        SyncModeBox.IsEnabled = false;
        try
        {
            var account = await AccountService.SetSyncModeAsync(id, mode);
            Message.Show(mode == AccountSyncMode.Cloud
                ? account.IsOnline && account.State == AccountState.Ok && !AccountService.List().Any(a => a.Id == id && a.State == AccountState.SignedOut)
                    ? "MatCloud gleicht das Konto jetzt ab – auch wenn dieser Computer aus ist."
                    : "Das Konto läuft über MatCloud."
                : account.State == AccountState.SignedOut
                    ? "Das Konto läuft wieder über die Computer. Melde es hier neu an („Neu anmelden“)."
                    : "Das Konto läuft wieder über die Computer.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            _isBusy = false;
            SyncModeBox.IsEnabled = true;
            Refresh();
        }
    }

    private async void OnSyncClick(object? sender, RoutedEventArgs e)
    {
        if (_accountId is not { } id)
        {
            return;
        }

        SyncButton.IsEnabled = false;
        try
        {
            await SyncService.SyncAccountAsync(id);
            var account = AccountService.Get(id);
            if (account?.State == AccountState.Ok)
            {
                Message.Show("Synchronisiert.", InfoSeverity.Success);
            }
        }
        finally
        {
            SyncButton.IsEnabled = true;
            Refresh();
        }
    }

    private async void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_accountId is not { } id || AccountService.Get(id) is not { } account || Window is not { } window)
        {
            return;
        }

        var text = account.Kind == AccountKind.Local
            ? $"„{account.Name}“ und alle seine Termine werden gelöscht. Das lässt sich nicht rückgängig machen."
            : $"„{account.Name}“ wird von diesem Computer entfernt (Kalender, Termine, Anmeldung). Bei {account.KindName} selbst bleibt alles, wie es ist.";
        if (!await ConfirmDialog.AskAsync(window, "Konto entfernen?", text, "Entfernen", isDestructive: true))
        {
            return;
        }

        try
        {
            AccountService.Remove(id);
            window.GoBack();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => Window?.GoBack();
}
