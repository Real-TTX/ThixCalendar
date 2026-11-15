using Avalonia.Controls;
using Avalonia.Interactivity;
using ThixCalendar.Controls;
using ThixCalendar.Core.Business;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;
using ThixCalendar.Dialogs;

namespace ThixCalendar.Pages;

/// <summary>
/// Creates or changes one event. The page only collects the values; CalendarService checks them (and the permission:
/// read-only calendars, signed-out accounts, policies) and writes them to the provider. Its errors are shown at the field.
/// </summary>
public partial class EventEditorPage : UserControl, IRefreshable
{
    private readonly CalendarEvent _original;
    private readonly List<(CalendarInfo Calendar, CalendarAccount Account)> _calendars;
    private readonly List<int?> _reminderValues;
    private bool _isBusy;

    /// <summary>The XAML previewer (a blank new event).</summary>
    public EventEditorPage()
        : this(new CalendarEvent { Start = DateTimeOffset.Now, End = DateTimeOffset.Now.AddHours(1) })
    {
    }

    public EventEditorPage(CalendarEvent calendarEvent)
    {
        InitializeComponent();
        _original = calendarEvent;
        var isNew = calendarEvent.Id == 0;

        // The event's own calendar is always offered (also when it is read-only), the writable ones for a move.
        var item = isNew ? null : CalendarService.GetEvent(calendarEvent.Id);
        _calendars = CalendarService.WritableCalendars().ToList();
        if (item is not null && _calendars.All(pair => pair.Calendar.Id != item.Calendar.Id))
        {
            _calendars.Insert(0, (item.Calendar, item.Account));
        }

        CalendarChoice.ItemsSource = _calendars.Select(pair => CalendarLabel(pair.Calendar, pair.Account)).ToList();
        CalendarChoice.SelectedIndex = Math.Max(0, _calendars.FindIndex(pair => pair.Calendar.Id == calendarEvent.CalendarId));

        var settings = SettingsService.Get();
        _reminderValues = [null, .. SettingsService.ReminderChoices.Select(minutes => (int?)minutes)];
        if (calendarEvent.ReminderMinutes is { } own && !_reminderValues.Contains(own))
        {
            _reminderValues.Add(own);
        }

        CalendarChoice.SelectionChanged += (_, _) => RefreshHeader();
        ReminderChoice.ItemsSource = _reminderValues.Select(value => value is { } minutes ? Ui.ReminderText(minutes) : DefaultReminderLabel()).ToList();
        ReminderChoice.SelectedIndex = Math.Max(0, _reminderValues.IndexOf(calendarEvent.ReminderMinutes));
        ReminderExplanation.Text =
            $"Zur gewählten Zeit erscheint eine Erinnerung, die bleibt, bis du sie schließt oder auf später verschiebst" +
            (settings.RemindAtStart ? ", und zum Beginn noch einmal" : "") +
            (settings.StartAlertCentered ? " – dann groß in der Bildschirmmitte" : "") +
            (settings.PlaySound ? $". Dazu ein Ton{(settings.RepeatSoundMinutes > 0 ? $", alle {settings.RepeatSoundMinutes} Min. wiederholt, solange niemand reagiert" : "")}." : ".") +
            " Ändern unter Einstellungen › Erinnerungen.";

        TitleBox.Text = calendarEvent.Title;
        AllDaySwitch.IsChecked = calendarEvent.IsAllDay;
        var start = calendarEvent.StartLocal;
        var end = calendarEvent.IsAllDay ? calendarEvent.EndLocal.AddDays(-1) : calendarEvent.EndLocal;
        StartDate.SelectedDate = start.Date;
        StartTime.SelectedTime = start.TimeOfDay;
        EndDate.SelectedDate = end.Date;
        EndTime.SelectedTime = calendarEvent.IsAllDay ? start.TimeOfDay.Add(TimeSpan.FromHours(1)) : end.TimeOfDay;
        LocationBox.Text = calendarEvent.Location;
        MeetingBox.Text = calendarEvent.MeetingUrl;
        DescriptionBox.Text = calendarEvent.Description;

        AllDaySwitch.IsCheckedChanged += (_, _) => ApplyAllDay();
        // Moving the start moves the end along (the duration stays), as in every calendar app.
        var lastStart = CombinedStart();
        StartDate.SelectedDateChanged += (_, _) => KeepDuration(ref lastStart);
        StartTime.SelectedTimeChanged += (_, _) => KeepDuration(ref lastStart);
        EndTime.SelectedTimeChanged += (_, _) => lastStart = CombinedStart();
        MeetingBox.TextChanged += (_, _) => RefreshActions();

        ApplyAllDay();
        RefreshHeader();
        RefreshActions();

        var readOnly = item?.IsReadOnly == true;
        if (readOnly)
        {
            ReadOnlyNote.Show($"„{item!.Calendar.Name}“ ({item.Account.KindName}) kann nur gelesen werden – ThixCalendar zeigt den Termin und erinnert daran.", InfoSeverity.Info);
            foreach (var control in new Control[] { TitleBox, CalendarChoice, AllDaySwitch, StartDate, StartTime, EndDate, EndTime, LocationBox, MeetingBox, DescriptionBox, ReminderChoice })
            {
                control.IsEnabled = false;
            }
        }

        SaveButton.IsVisible = !readOnly;
        DeleteButton.IsVisible = !isNew && !readOnly;
        WebButton.IsVisible = calendarEvent.WebLink.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (item is not null)
        {
            var details = new List<string>();
            if (item.Event.Organizer.Length > 0)
            {
                details.Add($"Organisiert von {item.Event.Organizer}");
            }

            details.Add(item.Event.Response switch
            {
                ResponseStatus.Accepted => "Zugesagt",
                ResponseStatus.Tentative => "Mit Vorbehalt zugesagt",
                ResponseStatus.Declined => "Abgelehnt – es kommt keine Erinnerung",
                ResponseStatus.NotResponded => "Noch nicht beantwortet",
                _ => "",
            });
            if (item.Event.IsRecurring)
            {
                details.Add("Teil einer Serie – Änderungen gelten nur für diesen Termin");
            }

            DetailsText.Text = string.Join(" · ", details.Where(text => text.Length > 0));
        }

        DetailsText.IsVisible = DetailsText.Text?.Length > 0;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => TitleBox.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private MainWindow? Window => Ui.WindowOf(this) as MainWindow;

    public void Refresh()
    {
    }

    private static string CalendarLabel(CalendarInfo calendar, CalendarAccount account) => $"{calendar.Name}  ·  {account.Name}";

    private string DefaultReminderLabel()
    {
        var calendar = _calendars.ElementAtOrDefault(Math.Max(0, CalendarChoice.SelectedIndex)).Calendar;
        var minutes = calendar?.DefaultReminderMinutes ?? SettingsService.Get().DefaultReminderMinutes;
        return $"Standard ({Ui.ReminderText(minutes)})";
    }

    private void RefreshHeader()
    {
        var calendar = _calendars.ElementAtOrDefault(Math.Max(0, CalendarChoice.SelectedIndex));
        ColorDot.Background = Ui.SolidBrush(calendar.Calendar?.Color ?? CalendarColors.Default);
        PageTitle.Text = _original.Id == 0 ? "Neuer Termin" : _original.Title;
        PageSubtitle.Text = _original.Id == 0
            ? calendar.Calendar is null ? "" : $"in {calendar.Calendar.Name} ({calendar.Account.KindName})"
            : $"{Ui.LongDate(_original.StartLocal)} · {Ui.TimeRange(_original)}";
    }

    private void RefreshActions()
    {
        var url = (MeetingBox.Text ?? "").Trim();
        JoinButton.IsVisible = url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        JoinLabel.Text = JoinButton.IsVisible ? $"{MeetingLinks.ServiceName(url)} beitreten" : "Beitreten";
    }

    private void ApplyAllDay()
    {
        var allDay = AllDaySwitch.IsChecked == true;
        StartTime.IsVisible = EndTime.IsVisible = !allDay;
    }

    private DateTime CombinedStart() => (StartDate.SelectedDate ?? DateTime.Today).Date + (StartTime.SelectedTime ?? TimeSpan.Zero);

    private DateTime CombinedEnd() => (EndDate.SelectedDate ?? DateTime.Today).Date + (EndTime.SelectedTime ?? TimeSpan.Zero);

    private void KeepDuration(ref DateTime lastStart)
    {
        var start = CombinedStart();
        var shift = start - lastStart;
        lastStart = start;
        if (shift == TimeSpan.Zero)
        {
            return;
        }

        var end = CombinedEnd() + shift;
        EndDate.SelectedDate = end.Date;
        EndTime.SelectedTime = end.TimeOfDay;
    }

    /// <summary>The values of the form as an event (not checked: that is the business layer's job).</summary>
    private CalendarEvent Collect()
    {
        var allDay = AllDaySwitch.IsChecked == true;
        var calendarId = _calendars.ElementAtOrDefault(Math.Max(0, CalendarChoice.SelectedIndex)).Calendar?.Id ?? 0;
        var start = allDay ? CalendarEvent.AllDayDate((StartDate.SelectedDate ?? DateTime.Today).Date) : CalendarEvent.FromLocal(CombinedStart());
        var end = allDay ? CalendarEvent.AllDayDate((EndDate.SelectedDate ?? DateTime.Today).Date.AddDays(1)) : CalendarEvent.FromLocal(CombinedEnd());
        return _original with
        {
            CalendarId = calendarId,
            Title = TitleBox.Text ?? "",
            IsAllDay = allDay,
            Start = start,
            End = end,
            Location = LocationBox.Text ?? "",
            MeetingUrl = MeetingBox.Text ?? "",
            Description = DescriptionBox.Text ?? "",
            ReminderMinutes = _reminderValues.ElementAtOrDefault(Math.Max(0, ReminderChoice.SelectedIndex)),
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
        Message.Show("Speichere …", InfoSeverity.Info);
        try
        {
            await CalendarService.SaveEventAsync(Collect());
            Message.Hide();
            Window?.GoBack();
        }
        catch (BusinessException exception)
        {
            Message.Hide();
            if (!FormField.ShowError(this, exception.Field, exception.Message))
            {
                Message.Show(exception.Message, InfoSeverity.Error);
            }
            else
            {
                Tabs.SelectedIndex = exception.Field is "Description" ? 1 : exception.Field is "ReminderMinutes" ? 2 : 0;
            }
        }
        finally
        {
            _isBusy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (Window is not { } window || _isBusy)
        {
            return;
        }

        var item = CalendarService.GetEvent(_original.Id);
        var text = item?.Account.IsOnline == true
            ? $"„{_original.Title}“ wird auch in {item.Account.KindName} gelöscht{(item.Event.IsRecurring ? " (nur dieser Termin der Serie)" : "")}."
            : $"„{_original.Title}“ wird gelöscht.";
        if (!await ConfirmDialog.AskAsync(window, "Termin löschen?", text, "Löschen", isDestructive: true))
        {
            return;
        }

        _isBusy = true;
        try
        {
            await CalendarService.DeleteEventAsync(_original.Id);
            window.GoBack();
        }
        catch (BusinessException exception)
        {
            Message.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void OnJoinClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal((MeetingBox.Text ?? "").Trim());

    private void OnWebClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(_original.WebLink);

    private void OnBackClick(object? sender, RoutedEventArgs e) => Window?.GoBack();
}
