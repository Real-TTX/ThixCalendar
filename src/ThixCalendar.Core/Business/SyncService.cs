using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar.Core.Business;

/// <summary>The state of the synchronisation as the header and the tray show it.</summary>
public sealed record SyncStatus(bool IsSyncing, DateTimeOffset? LastSync, int FailedAccounts, int SignedOutAccounts, string Message);

/// <summary>
/// Keeps the copy of the online calendars current: every few minutes (Einstellungen › Synchronisierung), at once after
/// <see cref="Kick"/> (start, network back, "Jetzt synchronisieren") and for a range the user looks at outside the usual
/// window (<see cref="EnsureRangeAsync"/>). The usual window – two weeks back, four months ahead – is what reminders
/// need. Per account: the calendar list first (new, renamed, removed calendars), then the events of each calendar.
/// </summary>
public static class SyncService
{
    /// <summary>The window every sync refreshes (days relative to today).</summary>
    public const int WindowDaysBack = 14;

    public const int WindowDaysAhead = 120;

    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static readonly object RangeGate = new();
    private static readonly HashSet<(DateTime From, DateTime To)> FetchedRanges = [];
    private static CancellationTokenSource? _loop;
    private static TaskCompletionSource _wakeUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _isSyncing;
    private static DateTimeOffset? _lastSync;

    /// <summary>Raised when a sync starts or ends (any thread).</summary>
    public static event EventHandler<SyncStatus>? StatusChanged;

    public static SyncStatus Status
    {
        get
        {
            var accounts = CalendarStore.LoadAccounts().Where(account => account.Kind != AccountKind.Local).ToList();
            var failed = accounts.Count(account => account.State == AccountState.Error);
            var signedOut = accounts.Count(account => account.State == AccountState.SignedOut);
            var message = _isSyncing ? "Synchronisiere …"
                : signedOut > 0 ? signedOut == 1 ? "Ein Konto ist abgemeldet" : $"{signedOut} Konten sind abgemeldet"
                : failed > 0 ? failed == 1 ? "Ein Konto ist nicht erreichbar" : $"{failed} Konten sind nicht erreichbar"
                : accounts.Count == 0 ? "Keine Online-Konten"
                : _lastSync is { } last ? $"Synchronisiert {last.ToLocalTime():HH:mm}" : "Noch nicht synchronisiert";
            return new SyncStatus(_isSyncing, _lastSync, failed, signedOut, message);
        }
    }

    /// <summary>Starts the background loop (idempotent).</summary>
    public static void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Task.Run(() => LoopAsync(token), token);
    }

    public static void Stop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    /// <summary>Synchronise now (the loop wakes up).</summary>
    public static void Kick() => _wakeUp.TrySetResult();

    /// <summary>Every online and ICS account, one after the other.</summary>
    public static async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var account in CalendarStore.LoadAccounts().Where(account => account.Kind != AccountKind.Local))
        {
            if (account.State == AccountState.SignedOut || account.IsKeptByCloud)
            {
                continue; // waits for "Neu anmelden" / MatCloud keeps it in step
            }

            await SyncAccountAsync(account.Id, cancellationToken);
        }

        _lastSync = DateTimeOffset.Now;
        RaiseStatus();
    }

    /// <summary>One account: calendars, then the events of the usual window. Failures are recorded at the account, not thrown.</summary>
    public static async Task SyncAccountAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await SyncGate.WaitAsync(cancellationToken);
        _isSyncing = true;
        RaiseStatus();
        try
        {
            var account = CalendarStore.LoadAccounts().FirstOrDefault(item => item.Id == accountId);
            if (account is { IsKeptByCloud: true })
            {
                CloudService.SyncAccountNow(account.SyncId); // MatCloud keeps it in step: it is asked to sync now
                return;
            }

            if (account is null || AccountService.CreateProvider(account) is not { } provider)
            {
                return;
            }

            if (AccessPolicy.AreOnlineAccountsDisabled)
            {
                Record(account, AccountState.Error, "Online-Konten sind durch eine Richtlinie deaktiviert.");
                return;
            }

            var (from, to) = Window(DateTime.Today);
            try
            {
                var calendars = await ReconcileCalendarsAsync(account, provider, cancellationToken);
                var changes = 0;
                foreach (var calendar in calendars)
                {
                    var events = await provider.ListEventsAsync(calendar, from, to, cancellationToken);
                    changes += CalendarStore.ReplaceEvents(calendar.Id, from, to, events);
                }

                Record(account, AccountState.Ok, "");
                Log.Info($"Account {account.Id} synchronised: {calendars.Count} calendars, {changes} changes");
            }
            catch (ProviderException exception) when (exception.IsSignInExpired)
            {
                AccountService.MarkSignedOut(account.Id, exception.Message);
            }
            catch (ProviderException exception)
            {
                Record(account, AccountState.Error, exception.Message);
                Log.Warning($"Account {account.Id} not synchronised: {exception.Message}");
            }
        }
        finally
        {
            _isSyncing = false;
            SyncGate.Release();
            CalendarService.RaiseChanged();
            RaiseStatus();
        }
    }

    /// <summary>
    /// Makes sure a range the user looks at (an earlier month, next year) is loaded: outside the usual window it is fetched
    /// once per run of the app. Returns true when something new arrived.
    /// </summary>
    public static async Task<bool> EnsureRangeAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var (windowFrom, windowTo) = Window(DateTime.Today);
        var rangeFrom = CalendarEvent.FromLocal(from.Date);
        var rangeTo = CalendarEvent.FromLocal(to.Date.AddDays(1));
        if (rangeFrom >= windowFrom && rangeTo <= windowTo)
        {
            return false;
        }

        // Months are the unit: browsing week by week does not ask again for every week.
        var monthFrom = new DateTime(from.Year, from.Month, 1);
        var monthTo = new DateTime(to.Year, to.Month, 1).AddMonths(1);
        lock (RangeGate)
        {
            if (!FetchedRanges.Add((monthFrom, monthTo)))
            {
                return false;
            }
        }

        var accounts = CalendarStore.LoadAccounts()
            .Where(account => account.Kind != AccountKind.Local && account.State != AccountState.SignedOut && !account.IsKeptByCloud).ToList();
        if (accounts.Count == 0 || AccessPolicy.AreOnlineAccountsDisabled)
        {
            return false;
        }

        var calendars = CalendarStore.LoadCalendars();
        var fetchFrom = CalendarEvent.FromLocal(monthFrom);
        var fetchTo = CalendarEvent.FromLocal(monthTo);
        var changes = 0;
        foreach (var account in accounts)
        {
            if (AccountService.CreateProvider(account) is not { } provider)
            {
                continue;
            }

            try
            {
                foreach (var calendar in calendars.Where(calendar => calendar.AccountId == account.Id))
                {
                    var events = await provider.ListEventsAsync(calendar, fetchFrom, fetchTo, cancellationToken);
                    changes += CalendarStore.ReplaceEvents(calendar.Id, fetchFrom, fetchTo, events);
                }
            }
            catch (ProviderException exception)
            {
                Log.Warning($"Range {monthFrom:yyyy-MM}..{monthTo:yyyy-MM} of account {account.Id} not loaded: {exception.Message}");
                lock (RangeGate)
                {
                    FetchedRanges.Remove((monthFrom, monthTo));
                }
            }
        }

        if (changes > 0)
        {
            CalendarService.RaiseChanged();
        }

        return changes > 0;
    }

    internal static (DateTimeOffset From, DateTimeOffset To) Window(DateTime today) =>
        (CalendarEvent.FromLocal(today.AddDays(-WindowDaysBack)), CalendarEvent.FromLocal(today.AddDays(WindowDaysAhead)));

    internal static void ResetForTests()
    {
        Stop();
        lock (RangeGate)
        {
            FetchedRanges.Clear();
        }

        _lastSync = null;
        _isSyncing = false;
    }

    /// <summary>The provider's calendar list into the database: new ones get a colour, the user's colour and switches stay.</summary>
    private static async Task<List<CalendarInfo>> ReconcileCalendarsAsync(CalendarAccount account, ICalendarProvider provider, CancellationToken cancellationToken)
    {
        var remote = await provider.ListCalendarsAsync(cancellationToken);
        var known = CalendarStore.LoadCalendars().Where(calendar => calendar.AccountId == account.Id).ToList();
        var total = CalendarStore.LoadCalendars().Count;
        var result = new List<CalendarInfo>();
        for (var index = 0; index < remote.Count; index++)
        {
            var item = remote[index];
            var existing = known.FirstOrDefault(calendar => calendar.RemoteId == item.RemoteId);
            if (existing is null)
            {
                var calendar = new CalendarInfo
                {
                    AccountId = account.Id,
                    RemoteId = item.RemoteId,
                    Name = item.Name,
                    Color = item.Color ?? CalendarColors.ForIndex(total++),
                    IsReadOnly = item.IsReadOnly,
                    IsPrimary = item.IsPrimary,
                    DefaultReminderMinutes = item.DefaultReminderMinutes,
                    SortOrder = item.IsPrimary ? -1 : index,
                    // Read-only extras of an online account (holidays, birthdays …) remind nobody unless the user wants it.
                    RemindersEnabled = item.IsPrimary || !item.IsReadOnly || account.Kind == AccountKind.Ics,
                };
                result.Add(calendar with { Id = CalendarStore.InsertCalendar(calendar) });
                continue;
            }

            var updated = existing with
            {
                Name = item.Name,
                Color = existing.IsColorCustom ? existing.Color : item.Color ?? existing.Color,
                IsReadOnly = item.IsReadOnly,
                IsPrimary = item.IsPrimary,
                DefaultReminderMinutes = item.DefaultReminderMinutes,
                SortOrder = item.IsPrimary ? -1 : index,
            };
            if (updated != existing)
            {
                CalendarStore.UpdateCalendar(updated);
            }

            result.Add(updated);
        }

        foreach (var gone in known.Where(calendar => remote.All(item => item.RemoteId != calendar.RemoteId)))
        {
            CalendarStore.DeleteCalendar(gone.Id);
        }

        return result;
    }

    private static void Record(CalendarAccount account, AccountState state, string error)
    {
        var current = CalendarStore.LoadAccounts().FirstOrDefault(item => item.Id == account.Id);
        if (current is null)
        {
            return;
        }

        CalendarStore.UpdateAccount(current with
        {
            State = state,
            LastError = error,
            LastSync = state == AccountState.Ok ? DateTimeOffset.Now : current.LastSync,
        });
    }

    private static async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // A new signal before the sync: a kick while it runs leads to one more round right after it.
            var wakeUp = _wakeUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await SyncAllAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                Log.Error("Sync loop", exception);
            }

            var interval = TimeSpan.FromMinutes(Math.Max(1, SettingsService.Get().SyncIntervalMinutes));
            try
            {
                await Task.WhenAny(wakeUp.Task, Task.Delay(interval, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void RaiseStatus() => StatusChanged?.Invoke(null, Status);
}
