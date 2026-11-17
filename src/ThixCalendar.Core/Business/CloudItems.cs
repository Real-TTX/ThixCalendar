using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatCloud.Contract;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Business;

/// <summary>
/// What ThixCalendar keeps in the general sync of MatCloud, and how an item of the cloud and the state of this computer meet.
/// Collections (keys):
///   settings   (calendar)                    the reminder settings, the working day, the sync interval, a pause of the reminders
///   accounts   (SyncId of the account)       kind, name, e-mail address, ICS address, shown or hidden. NEVER a sign-in: a
///                                            Microsoft or Google account from another computer arrives as "Anmeldung nötig"
///   calendars  (SyncId/hash of its key)      shown, reminders on/off, an own colour; the whole calendar for the own calendars
///   events     (key of the event)            the events of the own calendars ("Eigene Kalender") – Microsoft, Google and
///                                            ICS keep theirs themselves
///   answers    (hash of the occurrence)      Schließen / Später of a reminder: answered on one computer, gone on all
/// Window sizes, the view, the menu and the app registrations stay on each computer.
///
/// The index (cloud-sync.json) remembers per item the hash both sides agreed on last. A round: the cloud's changes come down and
/// are applied where this computer has no newer change of its own; then every local item whose hash differs from the index goes
/// up (an item missing here goes up as deleted). Last writer wins by time, as MatCloud decides. An item that cannot be applied
/// yet (the calendars of an account that has not signed in here, the policy locks the configuration) is parked: it counts as
/// present, so it is never deleted in the cloud, and it is applied as soon as it can be.
/// </summary>
internal static class CloudItems
{
    public const string Settings = "settings";
    public const string Accounts = "accounts";
    public const string Calendars = "calendars";
    public const string Events = "events";
    public const string Answers = "answers";
    public const string SettingsKey = "calendar";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = CloudContract.Json;
    private static CloudIndex? _index;

    public static long LastRevision(string account)
    {
        lock (Gate)
        {
            return Index(account).LastRevision;
        }
    }

    /// <summary>Applies the cloud's changes; returns true when something changed here.</summary>
    public static bool ApplyRemote(string account, IReadOnlyList<SyncItemDto> items, long latest)
    {
        lock (Gate)
        {
            var index = Index(account);
            var changed = ApplyParked(index);
            var local = Snapshot(index).ToDictionary(item => Key(item.Collection, item.Key));
            foreach (var item in items)
            {
                var key = Key(item.Collection, item.Key);
                index.Items.TryGetValue(key, out var known);
                local.TryGetValue(key, out var mine);
                var mineHash = mine is null ? null : Hash(mine.Data);
                var localChanged = mineHash != known?.Hash;
                var remoteHash = item.IsDeleted ? null : Hash(item.Data ?? "");
                if (localChanged && mine is not null && mine.UpdatedAt > item.UpdatedAt)
                {
                    continue; // this computer changed it later: it goes up and wins
                }

                index.Parked.Remove(key);
                if (remoteHash != mineHash)
                {
                    if (Apply(item))
                    {
                        changed = true;
                    }
                    else if (!item.IsDeleted)
                    {
                        index.Parked[key] = item;
                    }
                }

                index.Items[key] = new IndexEntry(remoteHash, item.UpdatedAt);
            }

            index.LastRevision = Math.Max(index.LastRevision, latest);
            Save(index);
            return changed;
        }
    }

    /// <summary>
    /// The local items whose state the cloud does not have yet (deleted ones as tombstones). Parked items that can be applied
    /// now are applied first (returns true then: something changed here).
    /// </summary>
    public static bool PendingChanges(string account, int max, out List<SyncItemDto> pending)
    {
        var now = DateTimeOffset.UtcNow;
        pending = [];
        lock (Gate)
        {
            var index = Index(account);
            var changed = ApplyParked(index);
            if (changed)
            {
                Save(index);
            }

            var local = Snapshot(index);
            foreach (var item in local)
            {
                var hash = Hash(item.Data);
                if (index.Items.TryGetValue(Key(item.Collection, item.Key), out var known) && known.Hash == hash)
                {
                    continue;
                }

                pending.Add(new SyncItemDto
                {
                    Collection = item.Collection,
                    Key = item.Key,
                    Data = item.Data,
                    ContentHash = hash,
                    UpdatedAt = item.UpdatedAt > (known?.UpdatedAt ?? DateTimeOffset.MinValue) ? item.UpdatedAt : now,
                });
            }

            var present = local.Select(item => Key(item.Collection, item.Key)).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, entry) in index.Items)
            {
                if (entry.Hash is not null && !present.Contains(key))
                {
                    var (collection, itemKey) = Split(key);
                    pending.Add(new SyncItemDto { Collection = collection, Key = itemKey, IsDeleted = true, UpdatedAt = now });
                }
            }

            // Accounts before their calendars, calendars before their events: another computer can apply them in that order.
            pending = pending.OrderBy(item => Order(item.Collection)).Take(max).ToList();
            return changed;
        }
    }

    /// <summary>What the cloud did with a push: applied items are agreed; a newer item of the cloud is taken here.</summary>
    public static bool ConfirmPushed(string account, IReadOnlyList<SyncItemDto> pushed, SyncPushResponse response)
    {
        var changed = false;
        lock (Gate)
        {
            var index = Index(account);
            foreach (var result in response.Results)
            {
                var item = pushed.FirstOrDefault(candidate => candidate.Collection == result.Collection && candidate.Key == result.Key);
                if (item is null)
                {
                    continue;
                }

                var key = Key(item.Collection, item.Key);
                if (result.Applied)
                {
                    index.Items[key] = new IndexEntry(item.IsDeleted ? null : Hash(item.Data ?? ""), item.UpdatedAt);
                }
                else if (result.Current is { } current)
                {
                    if (Apply(current))
                    {
                        changed = true;
                    }
                    else if (!current.IsDeleted)
                    {
                        index.Parked[key] = current;
                    }

                    index.Items[key] = new IndexEntry(current.IsDeleted ? null : Hash(current.Data ?? ""), current.UpdatedAt);
                }
            }

            index.LastRevision = Math.Max(index.LastRevision, response.Latest);
            Save(index);
        }

        return changed;
    }

    /// <summary>A new sign-in starts from nothing: the cloud's state comes down, then this computer's goes up.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _index = null;
            if (File.Exists(AppPaths.CloudIndexFile))
            {
                File.Delete(AppPaths.CloudIndexFile);
            }
        }
    }

    /// <summary>Tests switch between the data folders of two computers: the index is read again from the folder.</summary>
    internal static void ForgetForTests()
    {
        lock (Gate)
        {
            _index = null;
        }
    }

    /// <summary>The key of a calendar every computer agrees on: its account's SyncId and a hash of its key at the provider.</summary>
    internal static string CalendarKey(CalendarAccount account, CalendarInfo calendar) => $"{account.SyncId}/{ShortHash(calendar.RemoteId)}";

    // ===== The shapes in the cloud =====

    /// <summary>The settings that travel with the user.</summary>
    internal sealed record SharedSettings
    {
        public int DayStartHour { get; init; }

        public int DefaultReminderMinutes { get; init; }

        public bool RemindAtStart { get; init; }

        public bool StartAlertCentered { get; init; }

        public bool PlaySound { get; init; }

        public int RepeatSoundMinutes { get; init; }

        public int? AllDayReminderMinuteOfDay { get; init; }

        public bool SkipDeclined { get; init; }

        public int MissedReminderMinutes { get; init; }

        public DateTimeOffset? RemindersPausedUntil { get; init; }

        public int SyncIntervalMinutes { get; init; }

        public static SharedSettings From(AppSettings settings) => new()
        {
            DayStartHour = settings.DayStartHour,
            DefaultReminderMinutes = settings.DefaultReminderMinutes,
            RemindAtStart = settings.RemindAtStart,
            StartAlertCentered = settings.StartAlertCentered,
            PlaySound = settings.PlaySound,
            RepeatSoundMinutes = settings.RepeatSoundMinutes,
            AllDayReminderMinuteOfDay = settings.AllDayReminderMinuteOfDay,
            SkipDeclined = settings.SkipDeclined,
            MissedReminderMinutes = settings.MissedReminderMinutes,
            RemindersPausedUntil = settings.RemindersPausedUntil?.ToUniversalTime(),
            SyncIntervalMinutes = settings.SyncIntervalMinutes,
        };

        public AppSettings ApplyTo(AppSettings settings) => settings with
        {
            DayStartHour = DayStartHour,
            DefaultReminderMinutes = DefaultReminderMinutes,
            RemindAtStart = RemindAtStart,
            StartAlertCentered = StartAlertCentered,
            PlaySound = PlaySound,
            RepeatSoundMinutes = RepeatSoundMinutes,
            AllDayReminderMinuteOfDay = AllDayReminderMinuteOfDay,
            SkipDeclined = SkipDeclined,
            MissedReminderMinutes = MissedReminderMinutes,
            RemindersPausedUntil = RemindersPausedUntil,
            SyncIntervalMinutes = SyncIntervalMinutes,
        };
    }

    internal sealed record AccountData(AccountKind Kind, string Name, string UserName, string Url, bool IsVisible);

    /// <summary>A calendar: for an online one only what the user chose (its name and colour come from the provider), else all of it.</summary>
    internal sealed record CalendarData(
        string RemoteId, string Name, string Color, bool IsVisible, bool RemindersEnabled, int? DefaultReminderMinutes, int SortOrder);

    internal sealed record EventData(
        string Calendar, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string Location, string Description,
        string MeetingUrl, int? ReminderMinutes, EventStatus Status);

    /// <summary>An answer to a reminder: the occurrence as every computer names it (calendar key : event key : start).</summary>
    internal sealed record AnswerData(string Occurrence, DateTimeOffset? DismissedTrigger, DateTimeOffset? SnoozedUntil, DateTimeOffset? SnoozedAt);

    private sealed record LocalItem(string Collection, string Key, string Data, DateTimeOffset UpdatedAt);

    // ===== This computer as items =====

    /// <summary>Everything this computer shares, serialised the same way every time (the hash compares it), and the parked items.</summary>
    private static List<LocalItem> Snapshot(CloudIndex index)
    {
        var items = new List<LocalItem>
        {
            new(Settings, SettingsKey, Serialize(SharedSettings.From(SettingsService.Get())), DateTimeOffset.MinValue),
        };

        var accounts = CalendarStore.LoadAccounts();
        var calendars = CalendarStore.LoadCalendars();
        var calendarKeys = new Dictionary<long, string>();
        if (SharesConfiguration)
        {
            foreach (var account in accounts)
            {
                items.Add(new LocalItem(Accounts, account.SyncId, Serialize(new AccountData(account.Kind, account.Name, account.UserName, account.Url, account.IsVisible)), account.UpdateDate));
            }

            foreach (var calendar in calendars)
            {
                if (accounts.FirstOrDefault(account => account.Id == calendar.AccountId) is not { } account)
                {
                    continue;
                }

                var key = CalendarKey(account, calendar);
                calendarKeys[calendar.Id] = key;
                var own = account.Kind == AccountKind.Local;
                var data = new CalendarData(
                    calendar.RemoteId,
                    own ? calendar.Name : "",
                    own || calendar.IsColorCustom ? calendar.Color : "",
                    calendar.IsVisible,
                    calendar.RemindersEnabled,
                    own ? calendar.DefaultReminderMinutes : null,
                    own ? calendar.SortOrder : 0);
                items.Add(new LocalItem(Calendars, key, Serialize(data), calendar.UpdateDate));
                if (!own)
                {
                    continue;
                }

                foreach (var calendarEvent in CalendarStore.LoadEventsOfCalendar(calendar.Id))
                {
                    var eventData = new EventData(key, calendarEvent.Title, calendarEvent.Start.ToUniversalTime(), calendarEvent.End.ToUniversalTime(), calendarEvent.IsAllDay,
                        calendarEvent.Location, calendarEvent.Description, calendarEvent.MeetingUrl, calendarEvent.ReminderMinutes, calendarEvent.Status);
                    items.Add(new LocalItem(Events, calendarEvent.RemoteId, Serialize(eventData), calendarEvent.UpdateDate));
                }
            }
        }
        else
        {
            foreach (var calendar in calendars)
            {
                if (accounts.FirstOrDefault(account => account.Id == calendar.AccountId) is { } account)
                {
                    calendarKeys[calendar.Id] = CalendarKey(account, calendar);
                }
            }
        }

        foreach (var state in CalendarStore.LoadReminderStates().Values)
        {
            if (SharedOccurrence(state.OccurrenceKey, calendarKeys) is not { } occurrence)
            {
                continue;
            }

            var answer = new AnswerData(occurrence, state.DismissedTrigger?.ToUniversalTime(), state.SnoozedUntil?.ToUniversalTime(), state.SnoozedAt?.ToUniversalTime());
            items.Add(new LocalItem(Answers, AnswerKey(occurrence), Serialize(answer), state.UpdateDate));
        }

        var present = items.Select(item => Key(item.Collection, item.Key)).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, parked) in index.Parked)
        {
            if (!present.Contains(key) && parked.Data is { } data)
            {
                items.Add(new LocalItem(parked.Collection, parked.Key, data, parked.UpdatedAt));
            }
        }

        return items;
    }

    /// <summary>Accounts, calendars and their events travel only while the configuration may be changed (policy LockConfiguration).</summary>
    private static bool SharesConfiguration => !AccessPolicy.IsConfigurationLocked;

    // ===== Items of the cloud here =====

    /// <summary>Tries the parked items again (an account signed in, its calendars arrived). True when one was applied.</summary>
    private static bool ApplyParked(CloudIndex index)
    {
        var applied = false;
        foreach (var (key, item) in index.Parked.OrderBy(pair => Order(pair.Value.Collection)).ToList())
        {
            if (Apply(item))
            {
                index.Parked.Remove(key);
                applied = true;
            }
        }

        return applied;
    }

    /// <summary>Makes this computer what the item says. False when it cannot be applied (yet): the item is parked.</summary>
    private static bool Apply(SyncItemDto item)
    {
        var deleted = item.IsDeleted || item.Data is null;
        try
        {
            return item.Collection switch
            {
                Settings when item.Key == SettingsKey => deleted || ApplySettings(Deserialize<SharedSettings>(item.Data!)),
                Accounts => SharesConfiguration && ApplyAccount(item.Key, deleted ? null : Deserialize<AccountData>(item.Data!)),
                Calendars => SharesConfiguration && ApplyCalendar(item.Key, deleted ? null : Deserialize<CalendarData>(item.Data!)),
                Events => SharesConfiguration && ApplyEvent(item.Key, deleted ? null : Deserialize<EventData>(item.Data!)),
                Answers => ApplyAnswer(item.Key, deleted ? null : Deserialize<AnswerData>(item.Data!)),
                _ => true, // something a newer ThixCalendar keeps: not for this version
            };
        }
        catch (JsonException)
        {
            Log.Warning($"Cloud: an item of {item.Collection} could not be read; left out");
            return true;
        }
    }

    private static bool ApplySettings(SharedSettings? shared)
    {
        if (shared is not null)
        {
            SettingsService.ApplyFromCloud(shared.ApplyTo);
        }

        return true;
    }

    private static bool ApplyAccount(string syncId, AccountData? data)
    {
        var accounts = CalendarStore.LoadAccounts();
        var existing = accounts.FirstOrDefault(account => account.SyncId == syncId);
        if (data is null)
        {
            if (existing is not null)
            {
                TokenStore.Delete(AccountService.TokenKey(existing.Id));
                CalendarStore.DeleteAccount(existing.Id);
                Log.Info($"Account {existing.Id} removed (MatCloud)");
            }

            return true;
        }

        if (data.Kind != AccountKind.Local && AccessPolicy.AreOnlineAccountsDisabled)
        {
            return false;
        }

        // The same account added on two computers before they met in the cloud: this one takes the cloud's key.
        existing ??= accounts.FirstOrDefault(account => account.Kind == data.Kind && SameAccount(account, data) && !IsShared(account.SyncId));
        if (existing is not null)
        {
            var updated = existing with { SyncId = syncId, Name = data.Name, IsVisible = data.IsVisible, Url = data.Kind == AccountKind.Ics ? data.Url : existing.Url };
            if (updated != existing)
            {
                CalendarStore.UpdateAccount(updated);
                if (updated.Url != existing.Url)
                {
                    SyncService.Kick();
                }
            }

            return true;
        }

        var online = data.Kind is AccountKind.Microsoft or AccountKind.Google;
        CalendarStore.InsertAccount(new CalendarAccount
        {
            Kind = data.Kind,
            Name = data.Name,
            UserName = data.UserName,
            Url = data.Url,
            IsVisible = data.IsVisible,
            SyncId = syncId,
            State = online ? AccountState.SignedOut : data.Kind == AccountKind.Local ? AccountState.Ok : AccountState.New,
            LastError = online ? "Auf diesem Computer noch nicht angemeldet – „Neu anmelden“." : "",
        });
        Log.Info($"Account of kind {data.Kind} added (MatCloud)");
        if (data.Kind == AccountKind.Ics)
        {
            SyncService.Kick();
        }

        return true;
    }

    private static bool ApplyCalendar(string key, CalendarData? data)
    {
        var slash = key.IndexOf('/');
        var accountSyncId = slash > 0 ? key[..slash] : key;
        var account = CalendarStore.LoadAccounts().FirstOrDefault(candidate => candidate.SyncId == accountSyncId);
        var existing = account is null ? null : CalendarStore.LoadCalendars().FirstOrDefault(calendar => calendar.AccountId == account.Id && CalendarKey(account, calendar) == key);
        if (data is null)
        {
            // Only an own calendar is removed here; the calendars of Microsoft, Google and ICS are what the provider says.
            if (existing is not null && account!.Kind == AccountKind.Local)
            {
                CalendarStore.DeleteCalendar(existing.Id);
            }

            return true;
        }

        if (account is null)
        {
            return false;
        }

        var own = account.Kind == AccountKind.Local;
        if (existing is null)
        {
            if (!own)
            {
                return false; // comes with the next sync of the account (after it signed in here)
            }

            CalendarStore.InsertCalendar(new CalendarInfo
            {
                AccountId = account.Id,
                RemoteId = data.RemoteId,
                Name = data.Name,
                Color = data.Color.Length > 0 ? data.Color : CalendarColors.Default,
                IsVisible = data.IsVisible,
                RemindersEnabled = data.RemindersEnabled,
                DefaultReminderMinutes = data.DefaultReminderMinutes,
                SortOrder = data.SortOrder,
            });
            return true;
        }

        var updated = existing with
        {
            IsVisible = data.IsVisible,
            RemindersEnabled = data.RemindersEnabled,
            Color = data.Color.Length > 0 ? data.Color : existing.Color,
            IsColorCustom = own ? existing.IsColorCustom : data.Color.Length > 0,
            Name = own && data.Name.Length > 0 ? data.Name : existing.Name,
            DefaultReminderMinutes = own ? data.DefaultReminderMinutes : existing.DefaultReminderMinutes,
            SortOrder = own ? data.SortOrder : existing.SortOrder,
        };
        if (updated != existing)
        {
            CalendarStore.UpdateCalendar(updated);
        }

        return true;
    }

    private static bool ApplyEvent(string key, EventData? data)
    {
        var accounts = CalendarStore.LoadAccounts();
        var ownCalendars = CalendarStore.LoadCalendars()
            .Select(calendar => (Calendar: calendar, Account: accounts.FirstOrDefault(account => account.Id == calendar.AccountId)))
            .Where(pair => pair.Account is { Kind: AccountKind.Local })
            .ToList();
        var existing = ownCalendars.SelectMany(pair => CalendarStore.LoadEventsOfCalendar(pair.Calendar.Id)).FirstOrDefault(calendarEvent => calendarEvent.RemoteId == key);
        if (data is null)
        {
            if (existing is not null)
            {
                CalendarStore.DeleteEvent(existing.Id);
            }

            return true;
        }

        var target = ownCalendars.FirstOrDefault(pair => CalendarKey(pair.Account!, pair.Calendar) == data.Calendar).Calendar;
        if (target is null)
        {
            return false; // its calendar has not arrived yet
        }

        var calendarEvent = new CalendarEvent
        {
            Id = existing?.Id ?? 0,
            CalendarId = target.Id,
            RemoteId = key,
            Title = data.Title,
            Start = data.Start,
            End = data.End,
            IsAllDay = data.IsAllDay,
            Location = data.Location,
            Description = data.Description,
            MeetingUrl = data.MeetingUrl,
            ReminderMinutes = data.ReminderMinutes,
            Status = data.Status,
        };
        if (existing is null)
        {
            CalendarStore.InsertEvent(calendarEvent);
        }
        else
        {
            CalendarStore.UpdateEvent(calendarEvent);
        }

        return true;
    }

    private static bool ApplyAnswer(string key, AnswerData? data)
    {
        var calendarIds = CalendarIdsByKey();
        if (data is null)
        {
            var calendarKeys = calendarIds.ToDictionary(pair => pair.Value, pair => pair.Key);
            var match = CalendarStore.LoadReminderStates().Keys.FirstOrDefault(occurrence =>
                SharedOccurrence(occurrence, calendarKeys) is { } shared && AnswerKey(shared) == key);
            if (match is not null)
            {
                CalendarStore.DeleteReminderState(match);
            }

            return true;
        }

        var colon = data.Occurrence.IndexOf(':');
        if (colon <= 0 || !calendarIds.TryGetValue(data.Occurrence[..colon], out var calendarId))
        {
            return false; // the calendar has not arrived here (yet)
        }

        var local = $"{calendarId}{data.Occurrence[colon..]}";
        CalendarStore.SaveReminderState(new ReminderState(local, data.DismissedTrigger, data.SnoozedUntil, data.SnoozedAt));
        return true;
    }

    // ===== Plumbing =====

    /// <summary>calendar key -> id of the calendar here.</summary>
    private static Dictionary<string, long> CalendarIdsByKey()
    {
        var accounts = CalendarStore.LoadAccounts();
        var keys = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var calendar in CalendarStore.LoadCalendars())
        {
            if (accounts.FirstOrDefault(account => account.Id == calendar.AccountId) is { } account)
            {
                keys[CalendarKey(account, calendar)] = calendar.Id;
            }
        }

        return keys;
    }

    /// <summary>"12:AAMk…:638…" (the calendar's id here) -> "&lt;calendar key&gt;:AAMk…:638…" (the same on every computer).</summary>
    private static string? SharedOccurrence(string occurrenceKey, IReadOnlyDictionary<long, string> calendarKeys)
    {
        var colon = occurrenceKey.IndexOf(':');
        if (colon <= 0 || !long.TryParse(occurrenceKey[..colon], out var calendarId) || !calendarKeys.TryGetValue(calendarId, out var calendarKey))
        {
            return null;
        }

        return calendarKey + occurrenceKey[colon..];
    }

    private static string AnswerKey(string sharedOccurrence) => Hash(sharedOccurrence)[..40];

    private static bool SameAccount(CalendarAccount account, AccountData data) => data.Kind switch
    {
        AccountKind.Ics => string.Equals(account.Url, data.Url, StringComparison.OrdinalIgnoreCase),
        AccountKind.Local => true,
        _ => data.UserName.Length > 0 && string.Equals(account.UserName, data.UserName, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>The cloud knows this account already (it went up or came down): it is not taken over by another one.</summary>
    private static bool IsShared(string syncId) => _index?.Items.ContainsKey(Key(Accounts, syncId)) == true;

    private static int Order(string collection) => collection switch
    {
        Settings => 0,
        Accounts => 1,
        Calendars => 2,
        Events => 3,
        _ => 4,
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static T? Deserialize<T>(string data) => JsonSerializer.Deserialize<T>(data, Json);

    private static string Hash(string data) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));

    private static string ShortHash(string data) => Hash(data)[..24];

    private static string Key(string collection, string key) => $"{collection}/{key}";

    private static (string Collection, string Key) Split(string key)
    {
        var slash = key.IndexOf('/');
        return (key[..slash], key[(slash + 1)..]);
    }

    private static CloudIndex Index(string account)
    {
        if (_index is { } index && index.Account == account)
        {
            return index;
        }

        CloudIndex? stored = null;
        try
        {
            if (File.Exists(AppPaths.CloudIndexFile))
            {
                stored = JsonSerializer.Deserialize<CloudIndex>(File.ReadAllText(AppPaths.CloudIndexFile), Json);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            Log.Warning($"Cloud: the index could not be read ({exception.GetType().Name}); starting over");
        }

        _index = stored is not null && stored.Account == account ? stored : new CloudIndex { Account = account };
        return _index;
    }

    private static void Save(CloudIndex index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.CloudIndexFile)!);
        var temporary = AppPaths.CloudIndexFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(index, Json));
        File.Move(temporary, AppPaths.CloudIndexFile, overwrite: true);
    }

    private sealed class CloudIndex
    {
        public string Account { get; set; } = "";

        public long LastRevision { get; set; }

        public Dictionary<string, IndexEntry> Items { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Items of the cloud that could not be applied yet (collection/key -> item).</summary>
        public Dictionary<string, SyncItemDto> Parked { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record IndexEntry(string? Hash, DateTimeOffset UpdatedAt);
}
