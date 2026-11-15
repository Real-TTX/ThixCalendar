using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ThixCalendar.Core.Infrastructure;

namespace ThixCalendar.Core.Providers;

/// <summary>How one identity provider signs in a desktop app (OAuth 2.0 authorization code with PKCE, loopback redirect).</summary>
internal sealed record OAuthProfile(
    string ProviderName,
    string AuthorizeUrl,
    string TokenUrl,
    string ClientId,
    string? ClientSecret,
    string Scope,
    string RedirectHost,
    IReadOnlyDictionary<string, string> ExtraParameters)
{
    /// <summary>Microsoft identity platform ("common": work, school and personal accounts). Redirect: http://localhost (any port).</summary>
    public static OAuthProfile Microsoft(string clientId) => new(
        "Microsoft",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        clientId,
        null,
        "offline_access openid profile User.Read Calendars.ReadWrite",
        "localhost",
        new Dictionary<string, string> { ["prompt"] = "select_account" });

    /// <summary>Google ("Desktop app" client; the secret of such a client is not confidential). Redirect: http://127.0.0.1 (any port).</summary>
    public static OAuthProfile Google(string clientId, string clientSecret) => new(
        "Google",
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        clientId,
        clientSecret,
        "openid email https://www.googleapis.com/auth/calendar",
        "127.0.0.1",
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent select_account" });
}

/// <summary>What a token endpoint handed out.</summary>
internal sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? IdToken);

/// <summary>
/// Signs in in the user's browser: a one-off listener on the loopback address receives the answer (no browser control
/// inside the app, the password never passes through ThixCalendar), the code is exchanged with PKCE. Refresh tokens are
/// stored by the business layer (TokenStore); access tokens live in memory only.
/// </summary>
internal static class OAuthClient
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Opens the browser (through <paramref name="openBrowser"/>) and waits for the answer.</summary>
    public static async Task<OAuthTokens> SignInAsync(OAuthProfile profile, Action<string> openBrowser, CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new LoopbackListener();
        var redirectUri = $"http://{profile.RedirectHost}:{listener.Port}/";
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = profile.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = profile.Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        foreach (var (name, value) in profile.ExtraParameters)
        {
            parameters[name] = value;
        }

        openBrowser(profile.AuthorizeUrl + "?" + string.Join("&", parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        Dictionary<string, string> answer;
        try
        {
            answer = await listener.WaitForRedirectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException("Die Anmeldung wurde nicht innerhalb von 5 Minuten abgeschlossen.");
        }

        if (answer.TryGetValue("error", out var error))
        {
            throw new ProviderException(error == "access_denied"
                ? "Die Anmeldung wurde abgebrochen oder der Zugriff nicht erlaubt."
                : $"{profile.ProviderName} hat die Anmeldung abgelehnt ({answer.GetValueOrDefault("error_description", error)}).");
        }

        if (answer.GetValueOrDefault("state") != state || !answer.TryGetValue("code", out var code))
        {
            throw new ProviderException("Die Antwort der Anmeldung passt nicht zu dieser Anfrage.");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = profile.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        if (profile.ClientSecret is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        return await RequestTokensAsync(profile, form, cancellationToken);
    }

    /// <summary>A new access token for a refresh token (Microsoft also hands out a new refresh token: keep it).</summary>
    public static Task<OAuthTokens> RefreshAsync(OAuthProfile profile, string refreshToken, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = profile.ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        if (profile.ClientSecret is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        if (profile.ProviderName == "Microsoft")
        {
            form["scope"] = profile.Scope;
        }

        return RequestTokensAsync(profile, form, cancellationToken);
    }

    /// <summary>The claims of an ID token (only read for name and e-mail, not verified: it came straight from the token endpoint over TLS).</summary>
    public static JsonObject ReadIdToken(string? idToken)
    {
        var parts = (idToken ?? "").Split('.');
        if (parts.Length < 2)
        {
            return [];
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject ?? [];
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static async Task<OAuthTokens> RequestTokensAsync(OAuthProfile profile, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        var (status, body) = await ProviderHttp.PostFormAsync(profile.TokenUrl, form, cancellationToken);
        var accessToken = body["access_token"]?.GetValue<string>();
        if (status == HttpStatusCode.OK && accessToken is { Length: > 0 })
        {
            var expiresIn = body["expires_in"] is { } expires ? ReadSeconds(expires) : 3600;
            return new OAuthTokens(accessToken, body["refresh_token"]?.GetValue<string>(), DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60),
                body["id_token"]?.GetValue<string>());
        }

        var error = body["error"]?.GetValue<string>() ?? $"HTTP {(int)status}";
        var description = body["error_description"]?.GetValue<string>() ?? "";
        Log.Warning($"{profile.ProviderName} token request failed: {error}");
        if (error is "invalid_grant" or "interaction_required" or "invalid_client" or "unauthorized_client")
        {
            throw new ProviderException(error == "invalid_client" || error == "unauthorized_client"
                ? $"{profile.ProviderName} kennt die App-Registrierung nicht (Einstellungen › Erweitert › Eigene App-Registrierung prüfen)."
                : "Die Anmeldung ist abgelaufen oder wurde widerrufen – bitte neu anmelden.", isSignInExpired: true);
        }

        throw new ProviderException($"{profile.ProviderName} hat die Anmeldung abgelehnt ({error}{(description.Length > 0 ? ": " + FirstLine(description) : "")}).");
    }

    private static int ReadSeconds(JsonNode node) =>
        node.GetValueKind() == System.Text.Json.JsonValueKind.String && int.TryParse(node.GetValue<string>(), out var text) ? text : node.GetValue<int>();

    private static string FirstLine(string text) => text.Split('\n', '\r')[0].Trim();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A one-request HTTP server on the loopback address (IPv4 and, on the same port, IPv6 – the browser may resolve
    /// "localhost" to either). No http.sys, no URL reservation, no administrator rights.
    /// </summary>
    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _ipv4;
        private readonly TcpListener? _ipv6;

        public LoopbackListener()
        {
            _ipv4 = new TcpListener(IPAddress.Loopback, 0);
            _ipv4.Start();
            Port = ((IPEndPoint)_ipv4.LocalEndpoint).Port;
            try
            {
                _ipv6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                _ipv6.Start();
            }
            catch (SocketException)
            {
                _ipv6 = null; // no IPv6 on this computer: the browser then uses IPv4
            }
        }

        public int Port { get; }

        public async Task<Dictionary<string, string>> WaitForRedirectAsync(CancellationToken cancellationToken)
        {
            // One pending accept per listener; the one that answered is replaced, the other keeps waiting.
            var pending = new Dictionary<Task<TcpClient>, TcpListener> { [_ipv4.AcceptTcpClientAsync(cancellationToken).AsTask()] = _ipv4 };
            if (_ipv6 is not null)
            {
                pending[_ipv6.AcceptTcpClientAsync(cancellationToken).AsTask()] = _ipv6;
            }

            while (true)
            {
                var accepted = await Task.WhenAny(pending.Keys);
                var listener = pending[accepted];
                pending.Remove(accepted);
                pending[listener.AcceptTcpClientAsync(cancellationToken).AsTask()] = listener;
                using var client = await accepted;
                var query = await ReadRequestAsync(client, cancellationToken);
                if (query is null)
                {
                    continue; // favicon or a probe of the browser: wait for the real answer
                }

                await RespondAsync(client, query.ContainsKey("error"), cancellationToken);
                return query;
            }
        }

        public void Dispose()
        {
            _ipv4.Stop();
            _ipv6?.Stop();
        }

        private static async Task<Dictionary<string, string>?> ReadRequestAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var requestLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0];
            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || parts[0] != "GET" || !parts[1].Contains('?'))
            {
                var notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(notFound, cancellationToken);
                return null;
            }

            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in parts[1][(parts[1].IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
                var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
                query[name] = value;
            }

            return query.ContainsKey("code") || query.ContainsKey("error") ? query : null;
        }

        private static async Task RespondAsync(TcpClient client, bool failed, CancellationToken cancellationToken)
        {
            var title = failed ? "Anmeldung nicht abgeschlossen" : "Anmeldung abgeschlossen";
            var text = failed ? "ThixCalendar zeigt dir, was nicht geklappt hat." : "Du kannst dieses Fenster schließen und zu ThixCalendar zurückkehren.";
            var html = $$"""
                <!doctype html><html lang="de"><head><meta charset="utf-8"><title>ThixCalendar – {{title}}</title>
                <style>body{font-family:-apple-system,'Segoe UI',sans-serif;background:#F5F6F8;color:#111827;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
                div{background:#fff;border:1px solid #E5E7EB;border-radius:16px;padding:32px 40px;text-align:center;box-shadow:0 8px 30px rgba(0,0,0,.06)}
                h1{font-size:20px;margin:0 0 8px}p{color:#6B7280;margin:0}</style></head>
                <body><div><h1>{{title}}</h1><p>{{text}}</p></div></body></html>
                """;
            var bytes = Encoding.UTF8.GetBytes(html);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            var stream = client.GetStream();
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
}
