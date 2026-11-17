using Microsoft.Data.Sqlite;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Infrastructure;

/// <summary>The answer the user gave to a reminder of one occurrence (snooze, dismiss).</summary>
internal sealed record ReminderState(string OccurrenceKey, DateTimeOffset? DismissedTrigger, DateTimeOffset? SnoozedUntil, DateTimeOffset? SnoozedAt)
{
    /// <summary>When the answer was given (read from the store; MatCloud compares it).</summary>
    public DateTimeOffset UpdateDate { get; init; }
}

/// <summary>
/// The accounts, calendars, events and reminder answers on disk (SQLite, calendar.db). Only the business layer uses this
/// class; the UI never sees a query.
///
/// Tables follow the relational rules of the project: PascalCase names, key "Id" (64-bit), the audit columns
/// CreateDate/CreateUserId/UpdateDate/UpdateUserId and UpdateState (1 created, 2 updated; rows are deleted for real:
/// the providers are the truth, there is nothing to hand on). Dates are Unix milliseconds (UTC).
/// </summary>
internal static class CalendarStore
{
    /// <summary>The one local user of a desktop installation (CreateUserId / UpdateUserId).</summary>
    public const long LocalUserId = 1;

    private const int SchemaVersion = 2;

    private const string AccountColumns = "Id, Kind, Name, UserName, Url, ProtectedToken, State, LastError, LastSync, IsVisible, CreateDate, UpdateDate, SyncId";

    private const string CalendarColumns =
        "Id, AccountId, RemoteId, Name, Color, IsColorCustom, IsVisible, RemindersEnabled, IsReadOnly, IsPrimary, DefaultReminderMinutes, SortOrder, CreateDate, UpdateDate";

    private const string EventColumns =
        "Id, CalendarId, RemoteId, Title, StartDate, EndDate, IsAllDay, Location, Description, MeetingUrl, WebLink, Organizer, " +
        "ReminderMinutes, Status, Response, IsRecurring, CreateDate, UpdateDate";

    private static readonly object Gate = new();
    private static SqliteConnection? _connection;
    private static string? _openedPath;

    // ===== Accounts =====

    public static List<CalendarAccount> LoadAccounts()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {AccountColumns} FROM Account ORDER BY Id";
            using var reader = command.ExecuteReader();
            var accounts = new List<CalendarAccount>();
            while (reader.Read())
            {
                accounts.Add(ReadAccount(reader));
            }

            return accounts;
        }
    }

    public static long InsertAccount(CalendarAccount account)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Account (Kind, Name, UserName, Url, ProtectedToken, State, LastError, LastSync, IsVisible, SyncId,
                                     CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
                VALUES ($kind, $name, $user, $url, $token, $state, $error, $lastSync, $visible, $syncId, $now, $localUser, $now, $localUser, 1);
                SELECT last_insert_rowid();
                """;
            AddAccountParameters(command, account);
            return (long)command.ExecuteScalar()!;
        }
    }

    public static void UpdateAccount(CalendarAccount account)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                UPDATE Account SET Kind = $kind, Name = $name, UserName = $user, Url = $url, ProtectedToken = $token, State = $state,
                       LastError = $error, LastSync = $lastSync, IsVisible = $visible, SyncId = $syncId, UpdateDate = $now, UpdateUserId = $localUser, UpdateState = 2
                WHERE Id = $id
                """;
            AddAccountParameters(command, account);
            command.Parameters.AddWithValue("$id", account.Id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The account with its calendars, events and their reminder answers.</summary>
    public static void DeleteAccount(long id)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "DELETE FROM Account WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    // ===== Calendars =====

    public static List<CalendarInfo> LoadCalendars()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {CalendarColumns} FROM Calendar ORDER BY AccountId, SortOrder, Id";
            using var reader = command.ExecuteReader();
            var calendars = new List<CalendarInfo>();
            while (reader.Read())
            {
                calendars.Add(ReadCalendar(reader));
            }

            return calendars;
        }
    }

    public static long InsertCalendar(CalendarInfo calendar)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Calendar (AccountId, RemoteId, Name, Color, IsColorCustom, IsVisible, RemindersEnabled, IsReadOnly, IsPrimary,
                                      DefaultReminderMinutes, SortOrder, CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
                VALUES ($account,
                        CASE WHEN $remote = '' AND (SELECT Kind FROM Account WHERE Id = $account) = 0 THEN lower(hex(randomblob(16))) ELSE $remote END,
                        $name, $color, $custom, $visible, $reminders, $readOnly, $primary, $defaultReminder, $sort,
                        $now, $localUser, $now, $localUser, 1);
                SELECT last_insert_rowid();
                """;
            AddCalendarParameters(command, calendar);
            return (long)command.ExecuteScalar()!;
        }
    }

    public static void UpdateCalendar(CalendarInfo calendar)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                UPDATE Calendar SET AccountId = $account, RemoteId = $remote, Name = $name, Color = $color, IsColorCustom = $custom,
                       IsVisible = $visible, RemindersEnabled = $reminders, IsReadOnly = $readOnly, IsPrimary = $primary,
                       DefaultReminderMinutes = $defaultReminder, SortOrder = $sort, UpdateDate = $now, UpdateUserId = $localUser, UpdateState = 2
                WHERE Id = $id
                """;
            AddCalendarParameters(command, calendar);
            command.Parameters.AddWithValue("$id", calendar.Id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>All events of one calendar (the calendars on this computer, which MatCloud keeps the same everywhere).</summary>
    public static List<CalendarEvent> LoadEventsOfCalendar(long calendarId)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {EventColumns} FROM CalendarEvent WHERE CalendarId = $calendar ORDER BY StartDate, Id";
            command.Parameters.AddWithValue("$calendar", calendarId);
            return ReadEvents(command);
        }
    }

    public static void DeleteCalendar(long id)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "DELETE FROM Calendar WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    // ===== Events =====

    /// <summary>The events that overlap [from, to) – an event of zero length counts when it starts inside.</summary>
    public static List<CalendarEvent> QueryEvents(DateTimeOffset from, DateTimeOffset to)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"""
                SELECT {EventColumns} FROM CalendarEvent
                WHERE StartDate < $to AND (EndDate > $from OR (EndDate = StartDate AND StartDate >= $from))
                ORDER BY StartDate, Id
                """;
            command.Parameters.AddWithValue("$from", ToMilliseconds(from));
            command.Parameters.AddWithValue("$to", ToMilliseconds(to));
            return ReadEvents(command);
        }
    }

    public static CalendarEvent? LoadEvent(long id)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = $"SELECT {EventColumns} FROM CalendarEvent WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            return ReadEvents(command).FirstOrDefault();
        }
    }

    public static long InsertEvent(CalendarEvent calendarEvent)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = InsertEventSql;
            AddEventParameters(command, calendarEvent);
            return (long)command.ExecuteScalar()!;
        }
    }

    public static void UpdateEvent(CalendarEvent calendarEvent)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = UpdateEventSql;
            AddEventParameters(command, calendarEvent);
            command.Parameters.AddWithValue("$id", calendarEvent.Id);
            command.ExecuteNonQuery();
        }
    }

    public static void DeleteEvent(long id)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "DELETE FROM CalendarEvent WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Makes the events of a calendar in [from, to) what the provider sent: known keys keep their row (and id), new ones are
    /// added, the rest of the window goes. Returns how many rows changed.
    /// </summary>
    public static int ReplaceEvents(long calendarId, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<CalendarEvent> events)
    {
        lock (Gate)
        {
            using var transaction = Connection.BeginTransaction();
            var existing = new Dictionary<string, CalendarEvent>(StringComparer.Ordinal);
            using (var query = Connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = $"""
                    SELECT {EventColumns} FROM CalendarEvent
                    WHERE CalendarId = $calendar AND StartDate < $to AND (EndDate > $from OR (EndDate = StartDate AND StartDate >= $from))
                    """;
                query.Parameters.AddWithValue("$calendar", calendarId);
                query.Parameters.AddWithValue("$from", ToMilliseconds(from));
                query.Parameters.AddWithValue("$to", ToMilliseconds(to));
                foreach (var row in ReadEvents(query))
                {
                    existing[row.RemoteId] = row;
                }
            }

            var changes = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var incoming in events)
            {
                if (incoming.RemoteId.Length == 0 || !seen.Add(incoming.RemoteId))
                {
                    continue;
                }

                using var command = Connection.CreateCommand();
                command.Transaction = transaction;
                var row = incoming with { CalendarId = calendarId };
                if (existing.TryGetValue(incoming.RemoteId, out var known))
                {
                    if (SameContent(known, row))
                    {
                        continue;
                    }

                    command.CommandText = UpdateEventSql;
                    AddEventParameters(command, row);
                    command.Parameters.AddWithValue("$id", known.Id);
                }
                else
                {
                    // An event that moved into this window may still sit outside it under the same key.
                    using (var stale = Connection.CreateCommand())
                    {
                        stale.Transaction = transaction;
                        stale.CommandText = "DELETE FROM CalendarEvent WHERE CalendarId = $calendar AND RemoteId = $remote";
                        stale.Parameters.AddWithValue("$calendar", calendarId);
                        stale.Parameters.AddWithValue("$remote", incoming.RemoteId);
                        stale.ExecuteNonQuery();
                    }

                    command.CommandText = InsertEventSql;
                    AddEventParameters(command, row);
                }

                command.ExecuteNonQuery();
                changes++;
            }

            foreach (var gone in existing.Values.Where(row => !seen.Contains(row.RemoteId)))
            {
                using var delete = Connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM CalendarEvent WHERE Id = $id";
                delete.Parameters.AddWithValue("$id", gone.Id);
                delete.ExecuteNonQuery();
                changes++;
            }

            transaction.Commit();
            return changes;
        }
    }

    // ===== Reminder answers =====

    public static Dictionary<string, ReminderState> LoadReminderStates()
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "SELECT OccurrenceKey, DismissedTrigger, SnoozedUntil, SnoozedAt, UpdateDate FROM ReminderState";
            using var reader = command.ExecuteReader();
            var states = new Dictionary<string, ReminderState>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var key = reader.GetString(0);
                states[key] = new ReminderState(key, ReadDate(reader, 1), ReadDate(reader, 2), ReadDate(reader, 3)) { UpdateDate = FromMilliseconds(reader.GetInt64(4)) };
            }

            return states;
        }
    }

    public static void SaveReminderState(ReminderState state)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReminderState (OccurrenceKey, DismissedTrigger, SnoozedUntil, SnoozedAt, CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
                VALUES ($key, $dismissed, $snoozed, $snoozedAt, $now, $localUser, $now, $localUser, 1)
                ON CONFLICT (OccurrenceKey) DO UPDATE SET DismissedTrigger = excluded.DismissedTrigger, SnoozedUntil = excluded.SnoozedUntil,
                    SnoozedAt = excluded.SnoozedAt, UpdateDate = excluded.UpdateDate, UpdateUserId = excluded.UpdateUserId, UpdateState = 2;
                """;
            command.Parameters.AddWithValue("$key", state.OccurrenceKey);
            command.Parameters.AddWithValue("$dismissed", DateOrNull(state.DismissedTrigger));
            command.Parameters.AddWithValue("$snoozed", DateOrNull(state.SnoozedUntil));
            command.Parameters.AddWithValue("$snoozedAt", DateOrNull(state.SnoozedAt));
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$localUser", LocalUserId);
            command.ExecuteNonQuery();
        }
    }

    public static void DeleteReminderState(string occurrenceKey)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "DELETE FROM ReminderState WHERE OccurrenceKey = $key";
            command.Parameters.AddWithValue("$key", occurrenceKey);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Answers to reminders of long-past events are of no use any more.</summary>
    public static void PurgeReminderStates(DateTimeOffset olderThan)
    {
        lock (Gate)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = "DELETE FROM ReminderState WHERE UpdateDate < $before";
            command.Parameters.AddWithValue("$before", ToMilliseconds(olderThan));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Closes the database (exit, tests that switch folders).</summary>
    public static void Close()
    {
        lock (Gate)
        {
            _connection?.Dispose();
            _connection = null;
            _openedPath = null;
            SqliteConnection.ClearAllPools();
        }
    }

    // ===== Plumbing =====

    private const string InsertEventSql = """
        INSERT INTO CalendarEvent (CalendarId, RemoteId, Title, StartDate, EndDate, IsAllDay, Location, Description, MeetingUrl, WebLink,
                                   Organizer, ReminderMinutes, Status, Response, IsRecurring, CreateDate, CreateUserId, UpdateDate, UpdateUserId, UpdateState)
        VALUES ($calendar, $remote, $title, $start, $end, $allDay, $location, $description, $meeting, $web, $organizer, $reminder, $status,
                $response, $recurring, $now, $localUser, $now, $localUser, 1);
        SELECT last_insert_rowid();
        """;

    private const string UpdateEventSql = """
        UPDATE CalendarEvent SET CalendarId = $calendar, RemoteId = $remote, Title = $title, StartDate = $start, EndDate = $end, IsAllDay = $allDay,
               Location = $location, Description = $description, MeetingUrl = $meeting, WebLink = $web, Organizer = $organizer,
               ReminderMinutes = $reminder, Status = $status, Response = $response, IsRecurring = $recurring,
               UpdateDate = $now, UpdateUserId = $localUser, UpdateState = 2
        WHERE Id = $id
        """;

    private static SqliteConnection Connection
    {
        get
        {
            var path = AppPaths.DatabaseFile;
            if (_connection is not null && _openedPath == path)
            {
                return _connection;
            }

            _connection?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            Migrate(connection);
            _connection = connection;
            _openedPath = path;
            return connection;
        }
    }

    private static void Migrate(SqliteConnection connection)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var versionQuery = connection.CreateCommand();
        versionQuery.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionQuery.ExecuteScalar());
        if (version >= SchemaVersion)
        {
            return;
        }

        if (version == 1)
        {
            // 2: keys other computers know a row by (MatCloud): the account's SyncId, a key for every calendar and event on this computer.
            using var upgrade = connection.CreateCommand();
            upgrade.CommandText = $"""
                ALTER TABLE Account ADD COLUMN SyncId TEXT NOT NULL DEFAULT '';
                UPDATE Account SET SyncId = lower(hex(randomblob(16))) WHERE SyncId = '';
                UPDATE Calendar SET RemoteId = lower(hex(randomblob(16)))
                    WHERE RemoteId = '' AND AccountId IN (SELECT Id FROM Account WHERE Kind = 0);
                UPDATE CalendarEvent SET RemoteId = lower(hex(randomblob(16))) WHERE RemoteId = '';
                PRAGMA user_version = {SchemaVersion};
                """;
            upgrade.ExecuteNonQuery();
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS Account (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind INTEGER NOT NULL,
                Name TEXT NOT NULL,
                UserName TEXT NOT NULL DEFAULT '',
                Url TEXT NOT NULL DEFAULT '',
                ProtectedToken TEXT NOT NULL DEFAULT '',
                State INTEGER NOT NULL DEFAULT 0,
                LastError TEXT NOT NULL DEFAULT '',
                LastSync INTEGER NULL,
                IsVisible INTEGER NOT NULL DEFAULT 1,
                SyncId TEXT NOT NULL DEFAULT '',
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS Calendar (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId INTEGER NOT NULL REFERENCES Account (Id) ON DELETE CASCADE,
                RemoteId TEXT NOT NULL DEFAULT '',
                Name TEXT NOT NULL,
                Color TEXT NOT NULL,
                IsColorCustom INTEGER NOT NULL DEFAULT 0,
                IsVisible INTEGER NOT NULL DEFAULT 1,
                RemindersEnabled INTEGER NOT NULL DEFAULT 1,
                IsReadOnly INTEGER NOT NULL DEFAULT 0,
                IsPrimary INTEGER NOT NULL DEFAULT 0,
                DefaultReminderMinutes INTEGER NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS IX_Calendar_AccountId ON Calendar (AccountId);
            CREATE TABLE IF NOT EXISTS CalendarEvent (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CalendarId INTEGER NOT NULL REFERENCES Calendar (Id) ON DELETE CASCADE,
                RemoteId TEXT NOT NULL DEFAULT '',
                Title TEXT NOT NULL,
                StartDate INTEGER NOT NULL,
                EndDate INTEGER NOT NULL,
                IsAllDay INTEGER NOT NULL DEFAULT 0,
                Location TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                MeetingUrl TEXT NOT NULL DEFAULT '',
                WebLink TEXT NOT NULL DEFAULT '',
                Organizer TEXT NOT NULL DEFAULT '',
                ReminderMinutes INTEGER NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                Response INTEGER NOT NULL DEFAULT 0,
                IsRecurring INTEGER NOT NULL DEFAULT 0,
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS IX_CalendarEvent_Range ON CalendarEvent (StartDate, EndDate);
            CREATE INDEX IF NOT EXISTS IX_CalendarEvent_Remote ON CalendarEvent (CalendarId, RemoteId);
            CREATE TABLE IF NOT EXISTS ReminderState (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OccurrenceKey TEXT NOT NULL UNIQUE,
                DismissedTrigger INTEGER NULL,
                SnoozedUntil INTEGER NULL,
                SnoozedAt INTEGER NULL,
                CreateDate INTEGER NOT NULL,
                CreateUserId INTEGER NOT NULL,
                UpdateDate INTEGER NOT NULL,
                UpdateUserId INTEGER NOT NULL,
                UpdateState INTEGER NOT NULL DEFAULT 1
            );
            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
    }

    private static List<CalendarEvent> ReadEvents(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var events = new List<CalendarEvent>();
        while (reader.Read())
        {
            events.Add(new CalendarEvent
            {
                Id = reader.GetInt64(0),
                CalendarId = reader.GetInt64(1),
                RemoteId = reader.GetString(2),
                Title = reader.GetString(3),
                Start = FromMilliseconds(reader.GetInt64(4)),
                End = FromMilliseconds(reader.GetInt64(5)),
                IsAllDay = reader.GetInt32(6) != 0,
                Location = reader.GetString(7),
                Description = reader.GetString(8),
                MeetingUrl = reader.GetString(9),
                WebLink = reader.GetString(10),
                Organizer = reader.GetString(11),
                ReminderMinutes = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                Status = (EventStatus)reader.GetInt32(13),
                Response = (ResponseStatus)reader.GetInt32(14),
                IsRecurring = reader.GetInt32(15) != 0,
                CreateDate = FromMilliseconds(reader.GetInt64(16)),
                UpdateDate = FromMilliseconds(reader.GetInt64(17)),
            });
        }

        return events;
    }

    private static bool SameContent(CalendarEvent known, CalendarEvent incoming) =>
        known with { Id = 0, CreateDate = default, UpdateDate = default } == incoming with { Id = 0, CreateDate = default, UpdateDate = default };

    private static void AddEventParameters(SqliteCommand command, CalendarEvent calendarEvent)
    {
        command.Parameters.AddWithValue("$calendar", calendarEvent.CalendarId);
        // An event of a calendar on this computer gets a key of its own: MatCloud and the reminder answers know it by that.
        command.Parameters.AddWithValue("$remote", calendarEvent.RemoteId.Length > 0 ? calendarEvent.RemoteId : NewKey());
        command.Parameters.AddWithValue("$title", calendarEvent.Title);
        command.Parameters.AddWithValue("$start", ToMilliseconds(calendarEvent.Start));
        command.Parameters.AddWithValue("$end", ToMilliseconds(calendarEvent.End));
        command.Parameters.AddWithValue("$allDay", calendarEvent.IsAllDay ? 1 : 0);
        command.Parameters.AddWithValue("$location", calendarEvent.Location);
        command.Parameters.AddWithValue("$description", calendarEvent.Description);
        command.Parameters.AddWithValue("$meeting", calendarEvent.MeetingUrl);
        command.Parameters.AddWithValue("$web", calendarEvent.WebLink);
        command.Parameters.AddWithValue("$organizer", calendarEvent.Organizer);
        command.Parameters.AddWithValue("$reminder", (object?)calendarEvent.ReminderMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)calendarEvent.Status);
        command.Parameters.AddWithValue("$response", (int)calendarEvent.Response);
        command.Parameters.AddWithValue("$recurring", calendarEvent.IsRecurring ? 1 : 0);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$localUser", LocalUserId);
    }

    private static void AddAccountParameters(SqliteCommand command, CalendarAccount account)
    {
        command.Parameters.AddWithValue("$kind", (int)account.Kind);
        command.Parameters.AddWithValue("$name", account.Name);
        command.Parameters.AddWithValue("$user", account.UserName);
        command.Parameters.AddWithValue("$url", account.Url);
        command.Parameters.AddWithValue("$token", account.ProtectedToken);
        command.Parameters.AddWithValue("$state", (int)account.State);
        command.Parameters.AddWithValue("$error", account.LastError);
        command.Parameters.AddWithValue("$lastSync", DateOrNull(account.LastSync));
        command.Parameters.AddWithValue("$visible", account.IsVisible ? 1 : 0);
        command.Parameters.AddWithValue("$syncId", account.SyncId.Length > 0 ? account.SyncId : NewKey());
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$localUser", LocalUserId);
    }

    private static void AddCalendarParameters(SqliteCommand command, CalendarInfo calendar)
    {
        command.Parameters.AddWithValue("$account", calendar.AccountId);
        command.Parameters.AddWithValue("$remote", calendar.RemoteId);
        command.Parameters.AddWithValue("$name", calendar.Name);
        command.Parameters.AddWithValue("$color", calendar.Color);
        command.Parameters.AddWithValue("$custom", calendar.IsColorCustom ? 1 : 0);
        command.Parameters.AddWithValue("$visible", calendar.IsVisible ? 1 : 0);
        command.Parameters.AddWithValue("$reminders", calendar.RemindersEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$readOnly", calendar.IsReadOnly ? 1 : 0);
        command.Parameters.AddWithValue("$primary", calendar.IsPrimary ? 1 : 0);
        command.Parameters.AddWithValue("$defaultReminder", (object?)calendar.DefaultReminderMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$sort", calendar.SortOrder);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$localUser", LocalUserId);
    }

    private static CalendarAccount ReadAccount(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Kind = (AccountKind)reader.GetInt32(1),
        Name = reader.GetString(2),
        UserName = reader.GetString(3),
        Url = reader.GetString(4),
        ProtectedToken = reader.GetString(5),
        State = (AccountState)reader.GetInt32(6),
        LastError = reader.GetString(7),
        LastSync = ReadDate(reader, 8),
        IsVisible = reader.GetInt32(9) != 0,
        CreateDate = FromMilliseconds(reader.GetInt64(10)),
        UpdateDate = FromMilliseconds(reader.GetInt64(11)),
        SyncId = reader.GetString(12),
    };

    private static CalendarInfo ReadCalendar(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        AccountId = reader.GetInt64(1),
        RemoteId = reader.GetString(2),
        Name = reader.GetString(3),
        Color = reader.GetString(4),
        IsColorCustom = reader.GetInt32(5) != 0,
        IsVisible = reader.GetInt32(6) != 0,
        RemindersEnabled = reader.GetInt32(7) != 0,
        IsReadOnly = reader.GetInt32(8) != 0,
        IsPrimary = reader.GetInt32(9) != 0,
        DefaultReminderMinutes = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        SortOrder = reader.GetInt32(11),
        CreateDate = FromMilliseconds(reader.GetInt64(12)),
        UpdateDate = FromMilliseconds(reader.GetInt64(13)),
    };

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : FromMilliseconds(reader.GetInt64(column));

    private static object DateOrNull(DateTimeOffset? value) => value is { } date ? ToMilliseconds(date) : DBNull.Value;

    /// <summary>A new key of a row that other computers must know it by (32 hex digits).</summary>
    public static string NewKey() => Guid.NewGuid().ToString("N");

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long ToMilliseconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
