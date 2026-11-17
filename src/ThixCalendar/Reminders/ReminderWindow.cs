using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;
using MatDesktop;

namespace ThixCalendar.Reminders;

/// <summary>
/// The reminders on screen – built so they cannot be missed: always on top, never closing by themselves, one card per
/// event with "Beitreten" (online meetings), "Öffnen", "Später …" and "Schließen". It never takes the keyboard from the
/// app the user works in. Before the start it sits in the corner (Windows: bottom right, macOS: top right); when an event
/// begins it comes large into the middle of the screen with a red "Beginnt jetzt" (setting).
/// </summary>
internal sealed class ReminderWindow : MatWindow
{
    private const int MaxVisibleCards = 4;

    private readonly StackPanel _cards = new() { Spacing = 10 };
    private readonly TextBlock _heading = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _dismissAll;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    private IReadOnlyList<DueReminder> _reminders = [];
    private bool _isCentered;

    public ReminderWindow()
    {
        Title = "ThixCalendar – Erinnerung";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        _dismissAll = new Button { Theme = Ui.Theme("MatButton.Link"), Content = "Alle schließen", VerticalAlignment = VerticalAlignment.Center };
        _dismissAll.Click += (_, _) => ReminderCoordinator.DismissAll();
        var bell = new MatIcon { Icon = "bell-ring", Size = 18, Foreground = Ui.Brush("Mat.Accent"), Margin = new Thickness(0, 0, 9, 0) };
        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
        DockPanel.SetDock(bell, Dock.Left);
        DockPanel.SetDock(_dismissAll, Dock.Right);
        header.Children.Add(bell);
        header.Children.Add(_dismissAll);
        header.Children.Add(_heading);

        var scroller = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 };
        Content = new Border
        {
            Background = Ui.Brush("Mat.Bg"),
            BorderBrush = Ui.Brush("Mat.BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
            Margin = new Thickness(10),
            BoxShadow = BoxShadows.Parse("0 10 30 0 #33000000"),
            Child = new DockPanel { Children = { header, scroller } },
        };
        DockPanel.SetDock(header, Dock.Top);

        // Keeps "in 5 Min." current; not while the pointer is on the window (an open "Später" menu would close).
        _clock.Tick += (_, _) =>
        {
            if (!IsPointerOver)
            {
                Show(_reminders);
            }
        };
        Opened += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        Closed += (_, _) => _clock.Stop();
    }

    /// <summary>Shows these reminders (an empty list hides the window).</summary>
    public void Show(IReadOnlyList<DueReminder> reminders)
    {
        _reminders = reminders;
        if (reminders.Count == 0)
        {
            _clock.Stop();
            Hide();
            return;
        }

        var now = DateTimeOffset.Now;
        var settings = SettingsService.Get();
        var centered = settings.StartAlertCentered && reminders.Any(reminder => reminder.Stage == ReminderStage.Start && reminder.HasStarted(now));
        _isCentered = centered;
        Width = centered ? 620 : 520;
        _heading.Text = reminders.Count == 1 ? "Erinnerung" : $"{reminders.Count} Erinnerungen";
        _dismissAll.IsVisible = reminders.Count > 1;
        _cards.Children.Clear();
        foreach (var reminder in reminders.Take(MaxVisibleCards * 3))
        {
            _cards.Children.Add(Card(reminder, now, centered));
        }

        if (!IsVisible)
        {
            base.Show();
        }

        _clock.Start();
        Place();
    }

    private Control Card(DueReminder reminder, DateTimeOffset now, bool large)
    {
        var calendarEvent = reminder.Event;
        var starting = reminder.Stage == ReminderStage.Start || (!calendarEvent.IsAllDay && calendarEvent.Start <= now);
        var color = reminder.Item.Calendar.Color;

        var calendarLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new AccountMark { Kind = reminder.Item.Account.Kind, Width = 13, Height = 13 },
                new TextBlock { Text = reminder.Item.Calendar.Name, FontSize = 12, Foreground = Ui.Brush("Mat.TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 },
            },
        };
        if (starting)
        {
            calendarLine.Children.Add(new Border
            {
                Background = Ui.Brush("Mat.Danger"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 1),
                Margin = new Thickness(4, 0, 0, 0),
                Child = new TextBlock { Text = calendarEvent.Start > now.AddMinutes(-1) ? "Beginnt jetzt" : "Läuft", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
            });
        }

        var title = new TextBlock
        {
            Text = calendarEvent.Title,
            FontSize = large ? 22 : 16.5,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 2),
        };
        var when = new TextBlock
        {
            Text = $"{Ui.TimeRange(calendarEvent)}  ·  {Ui.Relative(calendarEvent, now)}",
            FontSize = large ? 15 : 13.5,
            FontWeight = FontWeight.Medium,
            Foreground = starting ? Ui.Brush("Mat.Danger") : Ui.Brush("Mat.AccentText"),
        };
        var details = new StackPanel { Spacing = 2, Children = { calendarLine, title, when } };
        if (calendarEvent.Location.Length > 0)
        {
            details.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 2, 0, 0),
                Children =
                {
                    new MatIcon { Icon = "map-pin", Size = 14, Foreground = Ui.Brush("Mat.TextMuted") },
                    new TextBlock { Text = calendarEvent.Location, FontSize = 13, Foreground = Ui.Brush("Mat.TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = large ? 440 : 320 },
                },
            });
        }

        var actions = new ActionBar { Margin = new Thickness(0, 12, 0, 0), Spacing = 6, Separation = 14 };
        if (calendarEvent.MeetingUrl.Length > 0)
        {
            var join = new Button { Theme = Ui.Theme("MatButton.Primary"), Content = new IconLabel { Icon = "video", Text = $"{MeetingLinks.ServiceName(calendarEvent.MeetingUrl)} beitreten" } };
            join.Click += (_, _) => ReminderCoordinator.Join(reminder);
            actions.Children.Add(join);
        }

        var open = new Button { Content = new MatIcon { Icon = "calendar", Size = 16 }, Padding = new Thickness(10, 7) };
        ToolTip.SetTip(open, "Im Kalender öffnen");
        AutomationProperties.SetName(open, "Im Kalender öffnen");
        open.Click += (_, _) => ReminderCoordinator.Open(reminder);
        actions.Children.Add(open);

        var snooze = new Button { Content = new IconLabel { Icon = "snooze", Text = "Später" } };
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (var (label, until) in ReminderService.SnoozeChoices(reminder, now))
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => ReminderCoordinator.Snooze(reminder, until);
            menu.Items.Add(item);
        }

        snooze.Flyout = menu;
        ToolTip.SetTip(snooze, "Später noch einmal erinnern");
        actions.Children.Add(snooze);

        var dismiss = new Button { Content = new IconLabel { Icon = "check", Text = "Schließen" } };
        ActionBar.SetSeparated(dismiss, true);
        dismiss.Click += (_, _) => ReminderCoordinator.Dismiss(reminder);
        actions.Children.Add(dismiss);

        var card = new Border
        {
            Background = Ui.Brush("Mat.Surface"),
            BorderBrush = starting ? Ui.Brush("Mat.Danger") : Ui.Brush("Mat.Border"),
            BorderThickness = new Thickness(starting ? 2 : 1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = new DockPanel
            {
                Children =
                {
                    new Border { Width = 5, Background = Ui.SolidBrush(color), [DockPanel.DockProperty] = Dock.Left },
                    new StackPanel { Margin = new Thickness(14, 12, 14, 12), Children = { details, actions } },
                },
            },
        };
        AutomationProperties.SetName(card, $"Erinnerung: {calendarEvent.Title}, {Ui.Relative(calendarEvent, now)}");
        return card;
    }

    /// <summary>Corner of the primary screen, or its middle at the start of an event.</summary>
    private void Place()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null || Bounds.Width <= 0)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = (int)(Bounds.Width * scaling);
        var height = (int)(Bounds.Height * scaling);
        var margin = (int)(6 * scaling);
        Position = _isCentered
            ? new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (int)((area.Height - height) / 2.6))
            : OperatingSystem.IsMacOS()
                ? new PixelPoint(area.Right - width - margin, area.Y + margin)
                : new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }
}
