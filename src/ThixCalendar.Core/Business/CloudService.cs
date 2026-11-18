using System.Net;
using System.Text.Json;
using MatCloud.Contract;
using MatCloud.Contract.Calendar;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;

namespace ThixCalendar.Core.Business;

public enum CloudState
{
    Off,
    Connecting,
    Online,
    Syncing,
    Offline,
    SignedOut,
}

/// <summary>What the connection to MatCloud does right now (settings page, the foot of the menu, the tray).</summary>
public sealed record CloudStatus(CloudState State, string Message, DateTimeOffset? LastSync = null);

/// <summary>
/// The connection to MatCloud, the cloud of the Thix apps (sibling repository MatCloud, see <see cref="SyncContract"/>).
/// ThixCalendar has no module there: it keeps its things in the general sync as app <see cref="CloudApps.Calendar"/>
/// (<see cref="CloudItems"/>: settings, accounts, calendars, the events of the own calendars, the answers to reminders).
/// Signing this computer in – in the browser with a code, with user name and password, or with the sign-in ThixLauncher left
/// when it installed ThixCalendar – and out, and one background loop:
///   changes of the cloud come down -> local changes go up -> long poll.
/// A local change wakes the loop (<see cref="NotifyLocalChange"/>), a change elsewhere ends the long poll. Without network the
/// loop waits and tries again (2 s … 60 s); a removed device (401) signs out, an app the administrator switched off (403) waits.
/// The device token is kept protected (<see cref="TokenStore"/>); a password is used once and never stored. Nothing here logs
/// an event, a password or a token.
/// </summary>
public static class CloudService
{
    public const string CloudAppId = CloudApps.Calendar;

    private const int LongPollSeconds = 25;
    private const string NotConnected = "Nicht verbunden";

    private static readonly TimeSpan KickSettle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SwitchedOffWait = TimeSpan.FromMinutes(5);
    private static readonly object Gate = new();

    private static CancellationTokenSource? _stop;
    private static CancellationTokenSource _kick = new();
    private static Task? _loop;
    private static CloudStatus _status = new(CloudState.Off, NotConnected);
    private static bool _watching;
    private static volatile bool _applyingRemote;

    /// <summary>Raised when <see cref="Status"/> changes (any thread).</summary>
    public static event EventHandler<CloudStatus>? StatusChanged;

    public static CloudStatus Status => _status;

    public static bool IsConnected => SettingsService.Get().Cloud.IsConnected;

    /// <summary>How ThixCalendar introduces itself to the cloud ("ThixCalendar/0.1.42-20261117 (Windows)").</summary>
    public static string UserAgent => $"ThixCalendar/{AppInfo.Version} ({AppInfo.PlatformName})";

    /// <summary>Tests talk to a cloud in memory.</summary>
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    /// <summary>Tests run the rounds themselves (<see cref="SyncOnceAsync"/>): no background loop after a sign-in.</summary>
    internal static bool LoopDisabledForTests { get; set; }

    // ---- signing in and out ----

    /// <summary>
    /// The sign-in in the browser (every account of the cloud, also one of MatIdentity): MatCloud gives a code, the UI shows it
    /// (<paramref name="onCode"/>) and opens the page (<paramref name="openAddress"/>); ThixCalendar waits until the person
    /// confirms it there. False when it was cancelled.
    /// </summary>
    public static async Task<bool> ConnectInBrowserAsync(string serverUrl, string deviceName, Action<DeviceLinkStart> onCode, Action<string> openAddress,
        CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        var url = NormalizeServerUrl(serverUrl);
        var device = CheckDeviceName(deviceName);
        SetStatus(new CloudStatus(CloudState.Connecting, "Warte auf die Bestätigung im Browser …"));
        DeviceLoginResponse login;
        try
        {
            using var client = CreateClient(url, null);
            var start = await client.StartLinkAsync(new DeviceLinkRequest(device, AppInfo.PlatformName, AppInfo.Version, CloudAppId), cancellationToken);
            onCode(start);
            openAddress(start.VerificationUrl);
            login = await client.WaitForLinkAsync(start, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(new CloudStatus(CloudState.Off, NotConnected));
            return false;
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            SetStatus(new CloudStatus(CloudState.Off, NotConnected));
            throw Explain(exception, url);
        }

        await DisconnectIfConnectedAsync();
        SignedIn(url, login);
        return true;
    }

    /// <summary>Signs this computer in with the user name and password of a local account of the cloud (the password is used once).</summary>
    public static async Task ConnectAsync(string serverUrl, string userName, string password, string deviceName, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        var url = NormalizeServerUrl(serverUrl);
        var device = CheckDeviceName(deviceName);
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new BusinessException("Bitte den Benutzernamen angeben.", nameof(CloudSettings.UserName));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new BusinessException("Bitte das Passwort angeben.", "Password");
        }

        SetStatus(new CloudStatus(CloudState.Connecting, "Verbinde …"));
        DeviceLoginResponse login;
        try
        {
            using var client = CreateClient(url, null);
            login = await client.LoginAsync(new DeviceLoginRequest(userName.Trim(), password, device, AppInfo.PlatformName, AppInfo.Version, CloudAppId), cancellationToken);
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            SetStatus(new CloudStatus(CloudState.Off, NotConnected));
            throw exception is CloudException { IsUnauthorized: true }
                ? new BusinessException("Benutzername oder Passwort stimmen nicht.", "Password")
                : Explain(exception, url);
        }
        catch (OperationCanceledException)
        {
            SetStatus(new CloudStatus(CloudState.Off, NotConnected));
            throw;
        }

        await DisconnectIfConnectedAsync();
        SignedIn(url, login);
    }

    /// <summary>
    /// At start: the sign-in ThixLauncher left when it installed ThixCalendar (<see cref="CloudHandoff"/>). While this computer is
    /// not connected, ThixCalendar fetches its own token with it – no browser. The hand-over is removed when it was used or had
    /// expired, and kept when the cloud did not answer (the next start tries again). Returns the account it signed in with, or
    /// null. Never throws.
    /// </summary>
    public static async Task<string?> ConnectFromLauncherAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected || AccessPolicy.IsCloudDisabled)
        {
            CloudHandoff.Remove(CloudAppId);
            return null;
        }

        if (CloudHandoff.Find(CloudAppId) is not { } handoff)
        {
            return null;
        }

        string url;
        DeviceLoginResponse? login;
        try
        {
            url = NormalizeServerUrl(handoff.ServerUrl);
            using var client = CreateClient(url, null);
            login = await client.RedeemHandoffAsync(handoff, cancellationToken);
        }
        catch (BusinessException)
        {
            CloudHandoff.Remove(CloudAppId);
            return null;
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warning($"The sign-in of ThixLauncher could not be fetched ({exception.GetType().Name}); trying again at the next start");
            return null;
        }

        CloudHandoff.Remove(CloudAppId);
        if (login is null || IsConnected)
        {
            Log.Info("The sign-in of ThixLauncher was used or had expired");
            return null;
        }

        SignedIn(url, login);
        Log.Info("Connected with the sign-in of ThixLauncher");
        return login.Account.DisplayName.Length > 0 ? login.Account.DisplayName : login.Account.UserName;
    }

    /// <summary>Signs this computer out (the cloud forgets the device); the calendars stay here.</summary>
    public static async Task DisconnectAsync()
    {
        var cloud = SettingsService.Get().Cloud;
        Stop();
        if (cloud.IsConnected && ReadToken(cloud) is { } token)
        {
            try
            {
                using var client = CreateClient(cloud.ServerUrl, token);
                await client.LogoutAsync(CancellationToken.None);
            }
            catch (Exception exception) when (IsCloudFailure(exception, CancellationToken.None))
            {
                Log.Warning($"Logout at the cloud failed ({exception.GetType().Name}); signing out locally");
            }
        }

        Forget(cloud);
        SetStatus(new CloudStatus(CloudState.Off, NotConnected));
    }

    /// <summary>The address as it is stored: http(s), "https://" added when missing, no trailing slash.</summary>
    public static string NormalizeServerUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new BusinessException("Bitte die Adresse der MatCloud angeben.", nameof(CloudSettings.ServerUrl));
        }

        try
        {
            return MatCloudClient.NormalizeServerUrl(serverUrl);
        }
        catch (UriFormatException)
        {
            throw new BusinessException("Das ist keine gültige Adresse (z. B. https://cloud.example.com).", nameof(CloudSettings.ServerUrl));
        }
    }

    // ---- the loop ----

    /// <summary>Starts the background loop when this computer is signed in (and the cloud is allowed).</summary>
    public static void Start()
    {
        WatchLocalChanges();
        var cloud = SettingsService.Get().Cloud;
        if (!cloud.IsConnected || AccessPolicy.IsCloudDisabled)
        {
            SetStatus(new CloudStatus(CloudState.Off, AccessPolicy.IsCloudDisabled ? "Durch Richtlinie deaktiviert" : NotConnected));
            return;
        }

        lock (Gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token), token);
        }
    }

    public static void Stop()
    {
        Task? loop;
        lock (Gate)
        {
            _stop?.Cancel();
            _stop = null;
            _kick.Cancel();
            loop = _loop;
            _loop = null;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
    }

    /// <summary>"Jetzt synchronisieren", or the network is back: sync now instead of waiting.</summary>
    public static void Kick()
    {
        lock (Gate)
        {
            _kick.Cancel();
        }
    }

    /// <summary>Something shared changed here (a setting, an account, an event, an answer): sync soon (nothing happens when not connected).</summary>
    internal static void NotifyLocalChange()
    {
        if (!_applyingRemote && IsConnected)
        {
            Kick();
        }
    }

    // ===== The accounts MatCloud keeps in step =====

    /// <summary>Hands the sign-in of an account to MatCloud; the item of the account goes up first (MatCloud looks for it).</summary>
    internal static async Task<CalendarCloudAccount> HandOverAsync(CalendarHandover handover, CancellationToken cancellationToken)
    {
        var (cloud, token) = RequireConnection();
        using var client = CreateClient(cloud.ServerUrl, token);
        await RoundAsync(client, AccountKey(cloud), cancellationToken);
        return await client.HandOverCalendarAsync(handover, cancellationToken);
    }

    /// <summary>MatCloud forgets the sign-in of the account and what it wrote for it.</summary>
    internal static async Task LetGoAsync(string accountSyncId, CancellationToken cancellationToken)
    {
        var (cloud, token) = RequireConnection();
        using var client = CreateClient(cloud.ServerUrl, token);
        await client.DisconnectCalendarAsync(accountSyncId, cancellationToken);
    }

    /// <summary>Creates, changes or deletes an event of an account MatCloud keeps (at the provider first).</summary>
    internal static async Task<CalendarEventChangeResult> ChangeEventAsync(CalendarEventChange change, CancellationToken cancellationToken)
    {
        var (cloud, token) = RequireConnection();
        using var client = CreateClient(cloud.ServerUrl, token);
        return await client.ChangeCalendarEventAsync(change, cancellationToken);
    }

    /// <summary>Asks MatCloud to sync an account it keeps now ("Jetzt abgleichen"); the result comes with the sync. Nothing when not connected.</summary>
    internal static void SyncAccountNow(string accountSyncId)
    {
        var cloud = SettingsService.Get().Cloud;
        if (ReadToken(cloud) is not { } token)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = CreateClient(cloud.ServerUrl, token);
                await client.SyncCalendarNowAsync(accountSyncId, CancellationToken.None);
            }
            catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException)
            {
                Log.Warning($"Cloud: sync now not asked ({exception.GetType().Name})");
            }
        });
        Kick();
    }

    private static (CloudSettings Cloud, string Token) RequireConnection()
    {
        var cloud = SettingsService.Get().Cloud;
        return ReadToken(cloud) is { } token
            ? (cloud, token)
            : throw new BusinessException("ThixCalendar ist nicht mit MatCloud verbunden (Einstellungen › Cloud).");
    }

    /// <summary>One round without the long poll (tests): what the loop does, with its handling of 401 and 403.</summary>
    internal static async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var cloud = SettingsService.Get().Cloud;
        var token = ReadToken(cloud) ?? throw new InvalidOperationException("This computer is not connected.");
        try
        {
            using var client = CreateClient(cloud.ServerUrl, token);
            await RoundAsync(client, AccountKey(cloud), cancellationToken);
            SetStatus(new CloudStatus(CloudState.Online, "Synchronisiert", DateTimeOffset.Now));
        }
        catch (CloudException exception) when (exception.IsUnauthorized)
        {
            SignedOut(cloud, "Dieses Gerät wurde in der MatCloud entfernt – bitte neu anmelden.");
        }
        catch (CloudException exception) when (exception.IsForbidden)
        {
            SetStatus(new CloudStatus(CloudState.Offline, exception.Message, _status.LastSync));
        }
    }

    /// <summary>Tests start disconnected and with nothing in memory.</summary>
    internal static void ResetForTests()
    {
        Stop();
        CloudItems.Reset();
        _status = new CloudStatus(CloudState.Off, NotConnected);
    }

    /// <summary>The snapshots show a state of the cloud without one (null: not connected again).</summary>
    internal static void ShowStatusForSnapshot(CloudStatus? status) => SetStatus(status ?? new CloudStatus(CloudState.Off, NotConnected));

    /// <summary>Every shared thing says when it changed: the loop wakes up.</summary>
    private static void WatchLocalChanges()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        SettingsService.Changed += (_, _) => NotifyLocalChange();
        CalendarService.Changed += (_, _) => NotifyLocalChange();
        ReminderService.Answered += (_, _) => NotifyLocalChange();
    }

    private static async Task RunAsync(CancellationToken stopToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!stopToken.IsCancellationRequested)
        {
            var kickToken = KickToken();
            var cloud = SettingsService.Get().Cloud;
            var token = ReadToken(cloud);
            if (!cloud.IsConnected || token is null)
            {
                SignedOut(cloud, "Der Zugang zur MatCloud fehlt – bitte neu anmelden.");
                return;
            }

            var wait = delay;
            var settleOnly = false;
            try
            {
                using var client = CreateClient(cloud.ServerUrl, token);
                SetStatus(_status with { State = CloudState.Syncing, Message = "Synchronisiere …" });
                var account = AccountKey(cloud);
                await RoundAsync(client, account, stopToken);
                SetStatus(new CloudStatus(CloudState.Online, "Synchronisiert", DateTimeOffset.Now));
                delay = TimeSpan.FromSeconds(2);

                using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(stopToken, kickToken);
                await client.WaitAsync(CloudItems.LastRevision(account), LongPollSeconds, waitToken.Token);
                continue;
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (kickToken.IsCancellationRequested)
            {
                (wait, settleOnly) = (KickSettle, true); // a local change: a moment for the next ones, then the next round
            }
            catch (CloudException exception) when (exception.IsUnauthorized)
            {
                SignedOut(cloud, "Dieses Gerät wurde in der MatCloud entfernt – bitte neu anmelden.");
                return;
            }
            catch (CloudException exception) when (exception.IsForbidden)
            {
                Log.Warning("Cloud: MatCloud refused ThixCalendar (403)");
                SetStatus(new CloudStatus(CloudState.Offline, exception.Message, _status.LastSync));
                wait = SwitchedOffWait;
            }
            catch (Exception exception) when (IsCloudFailure(exception, stopToken))
            {
                Log.Warning($"Cloud: {exception.GetType().Name}");
                SetStatus(new CloudStatus(CloudState.Offline, exception is CloudException ? exception.Message : "MatCloud nicht erreichbar – neuer Versuch folgt", _status.LastSync));
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                Log.Warning($"Cloud: {exception.GetType().Name} {exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                SetStatus(new CloudStatus(CloudState.Offline, "Die Daten ließen sich nicht abgleichen – neuer Versuch folgt", _status.LastSync));
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }

            try
            {
                // A kick ends the waiting after an error at once; after a kick it only lets the next changes gather.
                using var waitToken = settleOnly ? CancellationTokenSource.CreateLinkedTokenSource(stopToken) : CancellationTokenSource.CreateLinkedTokenSource(stopToken, KickToken());
                await Task.Delay(wait, waitToken.Token);
            }
            catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The token a kick cancels from now on (a fresh one when the last kick has been taken).</summary>
    private static CancellationToken KickToken()
    {
        lock (Gate)
        {
            if (_kick.IsCancellationRequested)
            {
                _kick.Dispose();
                _kick = new CancellationTokenSource();
            }

            return _kick.Token;
        }
    }

    /// <summary>changes of the cloud come down -> local changes go up.</summary>
    private static async Task RoundAsync(MatCloudClient client, string account, CancellationToken cancellationToken)
    {
        await PullAsync(client, account, cancellationToken);
        await PushAsync(client, account, cancellationToken);
    }

    private static async Task PullAsync(MatCloudClient client, string account, CancellationToken cancellationToken)
    {
        while (true)
        {
            var since = CloudItems.LastRevision(account);
            var changes = await client.GetChangesAsync(since, SyncContract.MaxBatchSize, cancellationToken);
            Publish(ApplyingRemote(() => CloudItems.ApplyRemote(account, changes.Items, changes.Latest)));
            if (!changes.HasMore || changes.Items.Count == 0)
            {
                return;
            }
        }
    }

    private static async Task PushAsync(MatCloudClient client, string account, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 50; round++)
        {
            List<SyncItemDto> pending = [];
            Publish(ApplyingRemote(() => CloudItems.PendingChanges(account, SyncContract.MaxBatchSize, out pending)));
            if (pending.Count == 0)
            {
                return;
            }

            try
            {
                var response = await client.PushAsync(new SyncPushRequest(pending), cancellationToken);
                Publish(ApplyingRemote(() => CloudItems.ConfirmPushed(account, pending, response)));
            }
            catch (CloudException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
            {
                // the cloud refuses a whole push for one item it does not take: one by one, so one item never holds up the others
                Log.Warning($"Cloud: a push was refused ({exception.Field ?? "no field"}); pushing one by one");
                foreach (var item in pending)
                {
                    try
                    {
                        var response = await client.PushAsync(new SyncPushRequest([item]), cancellationToken);
                        Publish(ApplyingRemote(() => CloudItems.ConfirmPushed(account, [item], response)));
                    }
                    catch (CloudException single) when (single.StatusCode == HttpStatusCode.BadRequest)
                    {
                        Log.Warning($"Cloud: {item.Collection} item refused ({single.Field ?? "no field"}); it stays on this computer");
                    }
                }

                return;
            }
        }
    }

    /// <summary>Changes the cloud makes here must not wake the loop as if they were local ones.</summary>
    private static bool ApplyingRemote(Func<bool> apply)
    {
        _applyingRemote = true;
        try
        {
            return apply();
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    /// <summary>The pages and the reminders read again (raised outside of <see cref="ApplyingRemote"/> would wake the loop).</summary>
    private static void Publish(bool changed)
    {
        if (!changed)
        {
            return;
        }

        _applyingRemote = true;
        try
        {
            CalendarService.RaiseChanged();
            ReminderService.Refresh();
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private static void SignedIn(string url, DeviceLoginResponse login)
    {
        var cloud = new CloudSettings
        {
            ServerUrl = url,
            UserName = login.Account.UserName,
            DisplayName = login.Account.DisplayName,
            DeviceName = login.Account.DeviceName,
            DeviceId = login.DeviceId,
        };
        var protectedToken = TokenStore.Save(AccountKey(cloud), login.Token);
        CloudItems.Reset();
        _applyingRemote = true;
        try
        {
            SettingsService.UpdateCloud(_ => cloud with { ProtectedToken = protectedToken });
        }
        finally
        {
            _applyingRemote = false;
        }

        Log.Info($"Connected to MatCloud as device {login.DeviceId}");
        SetStatus(new CloudStatus(CloudState.Connecting, "Verbunden – gleiche die Kalender ab …"));
        if (!LoopDisabledForTests)
        {
            Start();
        }
    }

    private static async Task DisconnectIfConnectedAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }
    }

    private static string CheckDeviceName(string? deviceName)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? AppInfo.MachineName : deviceName.Trim();
        if (name.Length > CloudContract.MaxDeviceNameLength)
        {
            throw new BusinessException($"Der Gerätename ist zu lang (höchstens {CloudContract.MaxDeviceNameLength} Zeichen).", nameof(CloudSettings.DeviceName));
        }

        return name;
    }

    /// <summary>The message of the cloud at its field (user name, password, device name), or "not reachable" at the address.</summary>
    private static BusinessException Explain(Exception exception, string url)
    {
        if (exception is not CloudException cloud)
        {
            return new BusinessException($"Die MatCloud ist unter {url} nicht erreichbar.", nameof(CloudSettings.ServerUrl));
        }

        var field = cloud.Field?.ToLowerInvariant() switch
        {
            "username" => nameof(CloudSettings.UserName),
            "password" => "Password",
            "devicename" => nameof(CloudSettings.DeviceName),
            _ => cloud.StatusCode == HttpStatusCode.NotFound ? nameof(CloudSettings.ServerUrl) : null,
        };
        return new BusinessException(cloud.Message, field);
    }

    /// <summary>The cloud answered with an error, could not be reached or took too long (and nobody cancelled on purpose).</summary>
    private static bool IsCloudFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is CloudException or HttpRequestException or JsonException or UriFormatException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static MatCloudClient CreateClient(string serverUrl, string? token) => new(serverUrl, token, UserAgent, HandlerForTests?.Invoke());

    private static void SignedOut(CloudSettings cloud, string message)
    {
        Log.Warning("Cloud: signed out");
        Forget(cloud);
        SetStatus(new CloudStatus(CloudState.SignedOut, message));
    }

    private static void Forget(CloudSettings cloud)
    {
        if (cloud.ServerUrl.Length > 0)
        {
            TokenStore.Delete(AccountKey(cloud));
        }

        _applyingRemote = true;
        try
        {
            SettingsService.UpdateCloud(current => new CloudSettings { ServerUrl = current.ServerUrl, UserName = current.UserName, DeviceName = current.DeviceName });
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private static string? ReadToken(CloudSettings cloud) =>
        cloud.IsConnected ? TokenStore.Read(AccountKey(cloud), cloud.ProtectedToken) : null;

    /// <summary>cloud-user@server: the Keychain entry and the account the sync index belongs to.</summary>
    private static string AccountKey(CloudSettings cloud) => $"cloud-{cloud.UserName}@{cloud.ServerUrl}";

    private static void SetStatus(CloudStatus status)
    {
        if (status == _status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(null, status);
    }
}
