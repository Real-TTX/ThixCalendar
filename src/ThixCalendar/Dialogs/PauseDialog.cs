using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ThixCalendar.Controls;

namespace ThixCalendar.Dialogs;

/// <summary>
/// "Erinnerungen pausieren" (Nicht stören): how long – 30 minutes, 1 or 2 hours, until tomorrow morning. ↑ ↓ choose,
/// Enter pauses, Esc cancels. Answers the end of the pause or null; the caller pauses (SettingsService.PauseReminders).
/// Nothing is lost: what falls due meanwhile appears when the pause ends.
/// </summary>
public sealed class PauseDialog : ThemedWindow
{
    private readonly ListBox _lengths = new()
    {
        SelectionMode = SelectionMode.Single | SelectionMode.AlwaysSelected,
        Margin = new Thickness(-4, 0, -4, 0),
    };

    private DateTimeOffset? _chosen;

    private PauseDialog()
    {
        Title = "Erinnerungen pausieren";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        this[!BackgroundProperty] = this.GetResourceObservable("CardBrush").ToBinding();

        _lengths.Theme = Ui.Theme("PlainList");
        AutomationProperties.SetName(_lengths, "Dauer");
        foreach (var (label, until) in Choices(DateTimeOffset.Now))
        {
            var item = new ListBoxItem { Theme = Ui.Theme("ChoiceRow"), Tag = until, Content = new TextBlock { Text = label } };
            AutomationProperties.SetName(item, label);
            item.DoubleTapped += (_, _) => Choose();
            _lengths.Items.Add(item);
        }

        _lengths.SelectedIndex = 1;
        var pause = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.BellOff"), Text = "Pausieren" }, IsDefault = true, Theme = Ui.Theme("PrimaryButton") };
        pause.Click += (_, _) => Choose();
        var cancel = new Button { Content = new IconLabel { Icon = Ui.Icon("Icon.Close"), Text = "Abbrechen" }, IsCancel = true };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Children =
            {
                new TextBlock { Text = Title, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                new TextBlock
                {
                    Text = "Nicht stören, z. B. während einer Präsentation. Es geht nichts verloren: was in der Zeit fällig wird, erscheint, sobald die Pause endet.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Ui.Brush("TextMutedBrush"),
                    Margin = new Thickness(0, 0, 0, 14),
                },
                new TextBlock { Text = "Dauer", Theme = Ui.Theme("FieldLabel"), Margin = new Thickness(0, 0, 0, 6) },
                _lengths,
                new ActionBar { Margin = new Thickness(0, 20, 0, 0), Children = { pause, cancel } },
            },
        };

        Opened += (_, _) => Dispatcher.UIThread.Post(() => (_lengths.SelectedItem as ListBoxItem)?.Focus(NavigationMethod.Directional));
    }

    /// <summary>The lengths offered.</summary>
    public static IReadOnlyList<(string Label, DateTimeOffset Until)> Choices(DateTimeOffset now)
    {
        var tomorrow = new DateTimeOffset(now.LocalDateTime.Date.AddDays(1).AddHours(8));
        return
        [
            ("30 Minuten", now.AddMinutes(30)),
            ("1 Stunde", now.AddHours(1)),
            ("2 Stunden", now.AddHours(2)),
            ($"Bis morgen früh ({tomorrow.LocalDateTime.ToString("ddd", Ui.German)} 08:00)", tomorrow),
        ];
    }

    /// <summary>The end of the pause; null when the user cancelled. Without an owner (tray) it opens in the middle of the screen.</summary>
    public static async Task<DateTimeOffset?> AskAsync(Window? owner)
    {
        var dialog = new PauseDialog();
        if (owner is { IsVisible: true })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            dialog.Activate();
            await closed.Task;
        }

        return dialog._chosen;
    }

    private void Choose()
    {
        _chosen = (DateTimeOffset)((ListBoxItem)_lengths.SelectedItem!).Tag!;
        Close();
    }
}
