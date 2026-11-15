using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Dialogs;

namespace ThixCalendar.Pages;

/// <summary>
/// The accounts with their state. Adding an online account opens the provider's sign-in in the browser
/// (<see cref="SignInDialog"/> waits for it); an ICS subscription or a local calendar opens the account editor.
/// </summary>
public partial class AccountsPage : UserControl, IRefreshable
{
    private static readonly string[] Kinds = ["Alle Arten", "Microsoft 365", "Google Kalender", "ICS-Abo", "Auf diesem Computer"];
    private static readonly string[] Sorts = ["Name", "Art", "Letzte Synchronisierung"];

    public AccountsPage()
    {
        InitializeComponent();
        KindFilter.ItemsSource = Kinds;
        KindFilter.SelectedIndex = 0;
        SortChoice.ItemsSource = Sorts;
        SortChoice.SelectedIndex = 0;
        SearchBox.TextChanged += (_, _) => Refresh();
        KindFilter.SelectionChanged += (_, _) => Refresh();
        SortChoice.SelectionChanged += (_, _) => Refresh();
        AccountList.SelectionChanged += (_, _) => RefreshActions();
        AccountList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<ListBoxItem>() is not null)
            {
                EditSelected();
            }
        };
        AccountList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                EditSelected();
            }
        };
        Refresh();
    }

    private MainWindow? Window => Ui.WindowOf(this) as MainWindow;

    private CalendarAccount? Selected => (AccountList.SelectedItem as Control)?.Tag as CalendarAccount;

    public void Refresh()
    {
        if (KindFilter.SelectedIndex < 0 || SortChoice.SelectedIndex < 0)
        {
            return;
        }

        var all = AccountService.List();
        var calendars = CalendarService.Calendars();
        var words = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = KindFilter.SelectedIndex switch
        {
            1 => AccountKind.Microsoft,
            2 => AccountKind.Google,
            3 => AccountKind.Ics,
            4 => AccountKind.Local,
            _ => (AccountKind?)null,
        };
        var accounts = all
            .Where(account => kind is null || account.Kind == kind)
            .Where(account => words.All(word => account.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
                                                account.UserName.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
                                                account.Url.Contains(word, StringComparison.OrdinalIgnoreCase)));
        accounts = SortChoice.SelectedIndex switch
        {
            1 => accounts.OrderBy(account => account.Kind).ThenBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => accounts.OrderByDescending(account => account.LastSync),
            _ => accounts.OrderBy(account => account.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var keep = Selected?.Id;
        var rows = accounts.Select(account => Row(account, calendars.Count(calendar => calendar.AccountId == account.Id))).ToList();
        AccountList.ItemsSource = rows;
        AccountList.SelectedItem = rows.FirstOrDefault(row => (row.Tag as CalendarAccount)?.Id == keep) ?? rows.FirstOrDefault();
        EmptyCard.IsVisible = all.Count == 0;
        AccountList.IsVisible = all.Count > 0;
        CountText.Text = all.Count == 1 ? "1 Konto" : $"{all.Count} Konten";
        RefreshActions();
    }

    private static Control Row(CalendarAccount account, int calendarCount)
    {
        var mark = new AccountMark { Kind = account.Kind, Width = 26, Height = 26, Margin = new Thickness(4, 0, 16, 0) };
        var subtitle = account.Kind switch
        {
            AccountKind.Ics => account.Url,
            AccountKind.Local => "Nur auf diesem Computer",
            _ => account.UserName,
        };
        var names = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = account.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{account.KindName} · {subtitle}", FontSize = 12.5, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        var (stateText, stateBrush, stateIcon) = account.State switch
        {
            AccountState.SignedOut => ("Abgemeldet – neu anmelden", "DangerBrush", "Icon.Warning"),
            AccountState.Error => ("Nicht erreichbar", "WarningBrush", "Icon.Warning"),
            AccountState.New => ("Wird eingerichtet …", "AccentBrush", "Icon.Sync"),
            _ => (account.Kind == AccountKind.Local ? "Bereit" : account.LastSync is { } last ? $"Synchronisiert {Ui.FormatDateTime(last)}" : "Bereit", "SuccessBrush", "Icon.Success"),
        };
        var state = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 8, 0),
            Children =
            {
                new IconView { Data = Ui.Icon(stateIcon), Width = 15, Height = 15, Foreground = Ui.Brush(stateBrush) },
                new TextBlock { Text = stateText, FontSize = 13, Foreground = Ui.Brush(stateBrush), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        if (account.LastError.Length > 0)
        {
            ToolTip.SetTip(state, account.LastError);
        }

        var count = new TextBlock
        {
            Text = calendarCount == 1 ? "1 Kalender" : $"{calendarCount} Kalender",
            FontSize = 13,
            Foreground = Ui.Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 90,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(count, Dock.Right);
        DockPanel.SetDock(state, Dock.Right);
        return new Border
        {
            Classes = { "row" },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 12),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new DockPanel { Children = { mark, count, state, names } },
            Tag = account,
        };
    }

    private void RefreshActions()
    {
        var account = Selected;
        EditButton.IsEnabled = account is not null;
        RemoveButton.IsEnabled = account is not null;
        SyncButton.IsVisible = account is { Kind: not AccountKind.Local };
        ReconnectButton.IsVisible = account is { IsOnline: true };
        ReconnectButton.Theme = account?.State == AccountState.SignedOut ? Ui.Theme("WarningButton") : null;
    }

    private void EditSelected()
    {
        if (Selected is { } account)
        {
            Window?.OpenAccount(account.Id);
        }
    }

    private async Task AddOnlineAsync(AccountKind kind)
    {
        if (Window is not { } window)
        {
            return;
        }

        Message.Hide();
        var account = await SignInDialog.RunAsync(window, kind, null);
        if (account is not null)
        {
            Message.Show($"„{account.Name}“ ist verbunden ({CalendarService.Calendars().Count(calendar => calendar.AccountId == account.Id)} Kalender).", InfoSeverity.Success);
            Refresh();
        }
    }

    private async void OnAddMicrosoftClick(object? sender, RoutedEventArgs e) => await AddOnlineAsync(AccountKind.Microsoft);

    private async void OnAddGoogleClick(object? sender, RoutedEventArgs e) => await AddOnlineAsync(AccountKind.Google);

    private void OnAddIcsClick(object? sender, RoutedEventArgs e) => Window?.OpenAccount(null, AccountKind.Ics);

    private void OnAddLocalClick(object? sender, RoutedEventArgs e) => Window?.OpenAccount(null, AccountKind.Local);

    private void OnEditClick(object? sender, RoutedEventArgs e) => EditSelected();

    private async void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { IsOnline: true } account && Window is { } window && await SignInDialog.RunAsync(window, account.Kind, account.Id) is { } done)
        {
            Message.Show($"„{done.Name}“ ist wieder angemeldet.", InfoSeverity.Success);
            Refresh();
        }
    }

    private async void OnSyncClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } account)
        {
            return;
        }

        SyncButton.IsEnabled = false;
        try
        {
            await SyncService.SyncAccountAsync(account.Id);
            var after = AccountService.Get(account.Id);
            Message.Show(after?.State == AccountState.Ok ? $"„{account.Name}“ ist synchronisiert." : after?.LastError ?? "", after?.State == AccountState.Ok ? InfoSeverity.Success : InfoSeverity.Warning);
        }
        finally
        {
            SyncButton.IsEnabled = true;
            Refresh();
        }
    }

    private async void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } account || Window is not { } window)
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
            AccountService.Remove(account.Id);
            Message.Show($"„{account.Name}“ wurde entfernt.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }

        Refresh();
    }
}
