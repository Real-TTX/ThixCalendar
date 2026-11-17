using System.Collections.Concurrent;
using ThixCalendar.Core.Infrastructure;
using ThixCalendar.Core.Models;
using ThixCalendar.Core.Providers;

namespace ThixCalendar.Core.Business;

/// <summary>
/// The accounts: calendars on this computer, Microsoft 365, Google and subscribed ICS addresses. Signing in happens in
/// the user's browser (OAuthClient); the refresh token is kept protected (TokenStore), access tokens only in memory.
/// Every change checks the machine policies (LockConfiguration, DisableOnlineAccounts) here.
/// </summary>
public static class AccountService
{
    private static readonly ConcurrentDictionary<long, (string Token, DateTimeOffset ExpiresAt)> AccessTokens = new();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    /// <summary>Tests sign in without a browser: they return the tokens the "provider" would hand out.</summary>
    internal static Func<OAuthProfile, CancellationToken, Task<OAuthTokens>>? SignInForTests { get; set; }

    public static IReadOnlyList<CalendarAccount> List() => CalendarStore.LoadAccounts();

    public static CalendarAccount? Get(long id) => List().FirstOrDefault(account => account.Id == id);

    /// <summary>The calendars of one account.</summary>
    public static IReadOnlyList<CalendarInfo> CalendarsOf(long accountId) =>
        CalendarStore.LoadCalendars().Where(calendar => calendar.AccountId == accountId).ToList();

    /// <summary>Is the app registration for this kind of account there (built in or the user's own)?</summary>
    public static bool IsConfigured(AccountKind kind) => kind switch
    {
        AccountKind.Microsoft => SettingsService.MicrosoftClientId.Length > 0,
        // Google exchanges the code only with the secret of the registration (also for desktop apps).
        AccountKind.Google => SettingsService.GoogleClient is { ClientId.Length: > 0, ClientSecret.Length: > 0 },
        _ => true,
    };

    /// <summary>A calendar that lives only on this computer (no account anywhere needed).</summary>
    public static CalendarAccount AddLocal(string name, string calendarName = "Kalender")
    {
        AccessPolicy.EnsureConfigurationEditable();
        var accountName = RequireName(name);
        var account = Insert(new CalendarAccount { Kind = AccountKind.Local, Name = accountName, State = AccountState.Ok, LastSync = DateTimeOffset.Now });
        AddLocalCalendar(account.Id, calendarName);
        return account;
    }

    /// <summary>Another calendar in a local account ("Privat", "Sport" …).</summary>
    public static CalendarInfo AddLocalCalendar(long accountId, string name)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var account = Get(accountId) ?? throw new BusinessException("Dieses Konto gibt es nicht mehr.");
        if (account.Kind != AccountKind.Local)
        {
            throw new BusinessException("Kalender eines Online-Kontos legst du bei seinem Anbieter an; sie erscheinen beim nächsten Abgleich.");
        }

        var calendarName = name.Trim();
        if (calendarName.Length == 0)
        {
            throw new BusinessException("Bitte gib dem Kalender einen Namen.", nameof(CalendarInfo.Name));
        }

        var count = CalendarStore.LoadCalendars().Count;
        var calendar = new CalendarInfo
        {
            AccountId = accountId,
            RemoteId = CalendarStore.NewKey(),
            Name = calendarName,
            Color = CalendarColors.ForIndex(count),
            IsPrimary = CalendarsOf(accountId).Count == 0,
            SortOrder = CalendarsOf(accountId).Count,
        };
        var id = CalendarStore.InsertCalendar(calendar);
        CalendarService.RaiseChanged();
        return calendar with { Id = id };
    }

    /// <summary>Removes a calendar of a local account with its events (at least one stays).</summary>
    public static void DeleteLocalCalendar(long calendarId)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var calendar = CalendarStore.LoadCalendars().FirstOrDefault(item => item.Id == calendarId) ?? throw new BusinessException("Diesen Kalender gibt es nicht mehr.");
        var account = Get(calendar.AccountId)!;
        if (account.Kind != AccountKind.Local)
        {
            throw new BusinessException("Kalender eines Online-Kontos entfernst du bei seinem Anbieter.");
        }

        if (CalendarsOf(account.Id).Count <= 1)
        {
            throw new BusinessException("Ein Konto auf diesem Computer behält mindestens einen Kalender – entferne sonst das Konto.");
        }

        CalendarStore.DeleteCalendar(calendarId);
        CalendarService.RaiseChanged();
    }

    /// <summary>Subscribes to an iCalendar address (checked by loading it once).</summary>
    public static async Task<CalendarAccount> AddIcsAsync(string name, string url, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureConfigurationEditable();
        AccessPolicy.EnsureOnlineAccountsAllowed();
        var address = ValidateIcsUrl(url);
        IReadOnlyList<RemoteCalendar> calendars;
        try
        {
            calendars = await new IcsProvider(address).ListCalendarsAsync(cancellationToken);
        }
        catch (ProviderException exception)
        {
            throw new BusinessException(exception.Message, nameof(CalendarAccount.Url));
        }

        var accountName = name.Trim().Length > 0 ? name.Trim() : calendars[0].Name;
        var account = Insert(new CalendarAccount { Kind = AccountKind.Ics, Name = accountName, Url = address });
        await SyncService.SyncAccountAsync(account.Id, cancellationToken);
        return Get(account.Id) ?? account;
    }

    /// <summary>Microsoft 365 / Outlook.com: sign in in the browser, then the first sync.</summary>
    public static Task<CalendarAccount> AddMicrosoftAsync(Action<string> openBrowser, CancellationToken cancellationToken = default) =>
        AddOnlineAsync(AccountKind.Microsoft, openBrowser, cancellationToken);

    /// <summary>Google Calendar: sign in in the browser, then the first sync.</summary>
    public static Task<CalendarAccount> AddGoogleAsync(Action<string> openBrowser, CancellationToken cancellationToken = default) =>
        AddOnlineAsync(AccountKind.Google, openBrowser, cancellationToken);

    /// <summary>Signs an account in again (after "Anmeldung abgelaufen") and synchronises it.</summary>
    public static async Task<CalendarAccount> ReconnectAsync(long accountId, Action<string> openBrowser, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureOnlineAccountsAllowed();
        var account = Get(accountId) ?? throw new BusinessException("Dieses Konto gibt es nicht mehr.");
        if (!account.IsOnline)
        {
            throw new BusinessException("Nur Microsoft- und Google-Konten melden sich an.");
        }

        var (tokens, userName) = await SignInAsync(account.Kind, openBrowser, cancellationToken);
        if (account.UserName.Length > 0 && userName.Length > 0 && !string.Equals(account.UserName, userName, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException($"Du hast dich als {userName} angemeldet – dieses Konto gehört zu {account.UserName}. Für ein anderes Konto: „Konto hinzufügen“.");
        }

        StoreTokens(account, tokens);
        CalendarStore.UpdateAccount(Get(accountId)! with { State = AccountState.Ok, LastError = "", UserName = userName.Length > 0 ? userName : account.UserName });
        await SyncService.SyncAccountAsync(accountId, cancellationToken);
        return Get(accountId)!;
    }

    public static CalendarAccount Rename(long accountId, string name)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var account = Get(accountId) ?? throw new BusinessException("Dieses Konto gibt es nicht mehr.");
        var renamed = account with { Name = RequireName(name) };
        CalendarStore.UpdateAccount(renamed);
        CalendarService.RaiseChanged();
        return renamed;
    }

    /// <summary>Changes the address of an ICS subscription (checked by loading it).</summary>
    public static async Task<CalendarAccount> ChangeIcsUrlAsync(long accountId, string url, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureConfigurationEditable();
        AccessPolicy.EnsureOnlineAccountsAllowed();
        var account = Get(accountId) ?? throw new BusinessException("Dieses Konto gibt es nicht mehr.");
        var address = ValidateIcsUrl(url);
        if (address == account.Url)
        {
            return account;
        }

        try
        {
            await new IcsProvider(address).ListCalendarsAsync(cancellationToken);
        }
        catch (ProviderException exception)
        {
            throw new BusinessException(exception.Message, nameof(CalendarAccount.Url));
        }

        CalendarStore.UpdateAccount(account with { Url = address });
        await SyncService.SyncAccountAsync(accountId, cancellationToken);
        return Get(accountId)!;
    }

    /// <summary>Removes the account from this computer: its calendars, events and stored sign-in. Nothing at the provider changes.</summary>
    public static void Remove(long accountId)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var account = Get(accountId) ?? throw new BusinessException("Dieses Konto gibt es nicht mehr.");
        TokenStore.Delete(TokenKey(account.Id));
        AccessTokens.TryRemove(account.Id, out _);
        CalendarStore.DeleteAccount(account.Id);
        Log.Info($"Account {account.Id} ({account.Kind}) removed");
        CalendarService.RaiseChanged();
    }

    /// <summary>The provider of an account (null for a local one: its events live only in the database).</summary>
    internal static ICalendarProvider? CreateProvider(CalendarAccount account) => account.Kind switch
    {
        AccountKind.Microsoft => new GraphProvider(cancellationToken => AccessTokenAsync(account.Id, cancellationToken)),
        AccountKind.Google => new GoogleProvider(cancellationToken => AccessTokenAsync(account.Id, cancellationToken)),
        AccountKind.Ics => new IcsProvider(account.Url),
        _ => null,
    };

    /// <summary>A provider error as a message for the user; an expired sign-in marks the account (the UI offers "Neu anmelden").</summary>
    internal static BusinessException ToBusinessException(long calendarId, ProviderException exception)
    {
        if (exception.IsSignInExpired && CalendarStore.LoadCalendars().FirstOrDefault(calendar => calendar.Id == calendarId) is { } calendar)
        {
            MarkSignedOut(calendar.AccountId, exception.Message);
        }

        return new BusinessException(exception.Message);
    }

    internal static void MarkSignedOut(long accountId, string message)
    {
        AccessTokens.TryRemove(accountId, out _);
        if (Get(accountId) is { } account && account.State != AccountState.SignedOut)
        {
            CalendarStore.UpdateAccount(account with { State = AccountState.SignedOut, LastError = message });
            Log.Warning($"Account {accountId} signed out: {message}");
            CalendarService.RaiseChanged();
        }
    }

    internal static string TokenKey(long accountId) => $"account-{accountId}";

    internal static void ResetForTests() => AccessTokens.Clear();

    /// <summary>A valid access token (refreshed when it ran out; Microsoft's new refresh token is kept).</summary>
    internal static async Task<string> AccessTokenAsync(long accountId, CancellationToken cancellationToken)
    {
        if (AccessTokens.TryGetValue(accountId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Token;
        }

        await RefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (AccessTokens.TryGetValue(accountId, out cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Token;
            }

            var account = Get(accountId) ?? throw new ProviderException("Dieses Konto gibt es nicht mehr.");
            var refreshToken = TokenStore.Read(TokenKey(account.Id), account.ProtectedToken)
                               ?? throw new ProviderException("Die Anmeldung fehlt – bitte neu anmelden.", isSignInExpired: true);
            var tokens = await OAuthClient.RefreshAsync(Profile(account.Kind), refreshToken, cancellationToken);
            if (tokens.RefreshToken is { Length: > 0 } rotated && rotated != refreshToken)
            {
                StoreTokens(account, tokens);
            }

            AccessTokens[accountId] = (tokens.AccessToken, tokens.ExpiresAt);
            return tokens.AccessToken;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<CalendarAccount> AddOnlineAsync(AccountKind kind, Action<string> openBrowser, CancellationToken cancellationToken)
    {
        AccessPolicy.EnsureConfigurationEditable();
        AccessPolicy.EnsureOnlineAccountsAllowed();
        var (tokens, userName) = await SignInAsync(kind, openBrowser, cancellationToken);
        if (userName.Length > 0 && List().Any(existing => existing.Kind == kind && string.Equals(existing.UserName, userName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessException($"{userName} ist bereits verbunden.");
        }

        var account = Insert(new CalendarAccount
        {
            Kind = kind,
            Name = userName.Length > 0 ? userName : CalendarAccount.KindDisplayName(kind),
            UserName = userName,
        });
        StoreTokens(account, tokens);
        Log.Info($"Account {account.Id} ({kind}) added");
        await SyncService.SyncAccountAsync(account.Id, cancellationToken);
        return Get(account.Id) ?? account;
    }

    /// <summary>Signs in and finds out who: Microsoft asks Graph /me, Google's ID token carries the e-mail address.</summary>
    private static async Task<(OAuthTokens Tokens, string UserName)> SignInAsync(AccountKind kind, Action<string> openBrowser, CancellationToken cancellationToken)
    {
        if (!IsConfigured(kind))
        {
            throw new BusinessException(kind == AccountKind.Microsoft
                ? "In dieser ThixCalendar-Version ist die Anmeldung bei Microsoft noch nicht hinterlegt (App-Registrierung des Herausgebers, siehe docs/APP-REGISTRIERUNG.md). Administratoren können eine eigene unter Einstellungen › Erweitert eintragen."
                : "In dieser ThixCalendar-Version ist die Anmeldung bei Google noch nicht hinterlegt (App-Registrierung des Herausgebers, siehe docs/APP-REGISTRIERUNG.md). Administratoren können eine eigene unter Einstellungen › Erweitert eintragen.");
        }

        var profile = Profile(kind);
        try
        {
            var tokens = SignInForTests is { } fake
                ? await fake(profile, cancellationToken)
                : await OAuthClient.SignInAsync(profile, openBrowser, cancellationToken);
            if (tokens.RefreshToken is not { Length: > 0 })
            {
                throw new BusinessException($"{profile.ProviderName} hat keinen dauerhaften Zugang erteilt (kein Refresh-Token). Bitte erneut versuchen.");
            }

            var claims = OAuthClient.ReadIdToken(tokens.IdToken);
            var userName = claims["email"]?.GetValue<string>() ?? claims["preferred_username"]?.GetValue<string>() ?? "";
            if (kind == AccountKind.Microsoft)
            {
                var (mail, _) = await new GraphProvider(_ => Task.FromResult(tokens.AccessToken)).ReadProfileAsync(cancellationToken);
                userName = mail.Length > 0 ? mail : userName;
            }

            return (tokens, userName);
        }
        catch (ProviderException exception)
        {
            throw new BusinessException(exception.Message);
        }
    }

    private static OAuthProfile Profile(AccountKind kind)
    {
        if (kind == AccountKind.Microsoft)
        {
            return OAuthProfile.Microsoft(SettingsService.MicrosoftClientId);
        }

        var (clientId, secret) = SettingsService.GoogleClient;
        return OAuthProfile.Google(clientId, secret);
    }

    private static void StoreTokens(CalendarAccount account, OAuthTokens tokens)
    {
        var protectedToken = TokenStore.Save(TokenKey(account.Id), tokens.RefreshToken!);
        var current = Get(account.Id) ?? account;
        CalendarStore.UpdateAccount(current with { ProtectedToken = protectedToken });
        AccessTokens[account.Id] = (tokens.AccessToken, tokens.ExpiresAt);
    }

    private static CalendarAccount Insert(CalendarAccount account)
    {
        var keyed = account.SyncId.Length > 0 ? account : account with { SyncId = CalendarStore.NewKey() };
        var id = CalendarStore.InsertAccount(keyed);
        CalendarService.RaiseChanged();
        return keyed with { Id = id };
    }

    private static string RequireName(string name)
    {
        var text = name.Trim();
        if (text.Length == 0)
        {
            throw new BusinessException("Bitte gib dem Konto einen Namen.", nameof(CalendarAccount.Name));
        }

        return text.Length > 80 ? throw new BusinessException("Der Name ist zu lang (höchstens 80 Zeichen).", nameof(CalendarAccount.Name)) : text;
    }

    private static string ValidateIcsUrl(string url)
    {
        var address = IcsProvider.NormalizeUrl(url);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new BusinessException("Bitte gib die Adresse des Kalenders ein (https://… oder webcal://…).", nameof(CalendarAccount.Url));
        }

        return address;
    }
}
