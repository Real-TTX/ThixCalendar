using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThixCalendar.Core.Infrastructure;

namespace ThixCalendar.Core.Providers;

/// <summary>A provider refused or failed. <see cref="IsSignInExpired"/>: the account has to sign in again.</summary>
public sealed class ProviderException(string message, bool isSignInExpired = false, Exception? inner = null) : Exception(message, inner)
{
    public bool IsSignInExpired { get; } = isSignInExpired;
}

/// <summary>
/// The one HTTP client of the calendar providers (Graph, Google, ICS, the OAuth token endpoints). JSON goes through
/// JsonNode: no reflection, nothing a trimmed build could lose. Tests replace the network with <see cref="HandlerForTests"/>.
/// </summary>
internal static class ProviderHttp
{
    private static readonly object Gate = new();
    private static HttpClient? _client;
    private static HttpMessageHandler? _clientHandler;

    /// <summary>Tests answer every request themselves (null = the network).</summary>
    public static HttpMessageHandler? HandlerForTests { get; set; }

    public static HttpClient Client
    {
        get
        {
            lock (Gate)
            {
                if (_client is not null && _clientHandler == HandlerForTests)
                {
                    return _client;
                }

                _client = HandlerForTests is null
                    ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
                    : new HttpClient(HandlerForTests, disposeHandler: false);
                _client.Timeout = TimeSpan.FromSeconds(60);
                _client.DefaultRequestHeaders.UserAgent.ParseAdd($"ThixCalendar/{AppInfo.BuildNumber}");
                _clientHandler = HandlerForTests;
                return _client;
            }
        }
    }

    /// <summary>Sends a JSON request with a bearer token; returns the JSON answer (an empty object for 204).</summary>
    public static async Task<JsonNode> SendJsonAsync(HttpMethod method, string url, string accessToken, JsonNode? body, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null, bool ignoreNotFound = false)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException("Keine Verbindung zum Kalenderdienst.", inner: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException("Der Kalenderdienst antwortet nicht (Zeitüberschreitung).", inner: exception);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
            }

            if (ignoreNotFound && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new JsonObject();
            }

            throw Failure(response.StatusCode, text);
        }
    }

    /// <summary>A plain GET without authentication (ICS addresses).</summary>
    public static async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(response.StatusCode == HttpStatusCode.NotFound
                    ? "Die Kalenderadresse gibt es nicht (mehr)."
                    : $"Die Kalenderadresse antwortet mit {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException("Die Kalenderadresse ist nicht erreichbar.", inner: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException("Die Kalenderadresse antwortet nicht (Zeitüberschreitung).", inner: exception);
        }
    }

    /// <summary>A form POST (OAuth token endpoints); returns the JSON answer, also for errors (they carry "error").</summary>
    public static async Task<(HttpStatusCode Status, JsonNode Body)> PostFormAsync(string url, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode body;
            try
            {
                body = JsonNode.Parse(text) ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                body = new JsonObject { ["error"] = "invalid_response", ["error_description"] = text.Length > 200 ? text[..200] : text };
            }

            return (response.StatusCode, body);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException("Der Anmeldedienst ist nicht erreichbar.", inner: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException("Der Anmeldedienst antwortet nicht (Zeitüberschreitung).", inner: exception);
        }
    }

    private static ProviderException Failure(HttpStatusCode status, string body)
    {
        var detail = ErrorDetail(body);
        return status switch
        {
            HttpStatusCode.Unauthorized => new ProviderException("Die Anmeldung ist abgelaufen – bitte neu anmelden.", isSignInExpired: true),
            HttpStatusCode.Forbidden => new ProviderException($"Keine Berechtigung für diesen Kalender.{detail}"),
            HttpStatusCode.NotFound => new ProviderException($"Der Termin oder Kalender wurde nicht gefunden (inzwischen gelöscht?).{detail}"),
            HttpStatusCode.TooManyRequests => new ProviderException("Der Kalenderdienst bittet um eine Pause – der nächste Abgleich versucht es wieder."),
            >= HttpStatusCode.InternalServerError => new ProviderException($"Der Kalenderdienst hat einen Fehler ({(int)status}).{detail}"),
            _ => new ProviderException($"Der Kalenderdienst lehnt die Anfrage ab ({(int)status}).{detail}"),
        };
    }

    /// <summary>The message of a Graph / Google error body, if there is one.</summary>
    private static string ErrorDetail(string body)
    {
        try
        {
            var message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(message) ? "" : $" ({message})";
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            return "";
        }
    }
}
