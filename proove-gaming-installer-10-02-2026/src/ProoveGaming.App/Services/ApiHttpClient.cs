using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

// error_code values ApiHttpClient sets itself when a request got no usable answer; the server never
// sends them (its catalog is AuthErrorCodes). They exist so the UI can show a localized message
// (ApiErrorMessages.Describe) instead of exception or transport text
// (docs/plans/fix-repository-audit-findings, S10/A8). The CLIENT_ prefix keeps them from ever
// colliding with a server code.
public static class ClientErrorCodes
{
    public const string NetworkError = "CLIENT_NETWORK_ERROR";
    public const string Timeout = "CLIENT_TIMEOUT";
    public const string InvalidResponse = "CLIENT_INVALID_RESPONSE";
}

// The one HTTP envelope helper both Launcher API clients (AuthApiService, SyncApiService)
// compose: owns the single HttpClient (no per-call clients / socket exhaustion), the base
// URL, and the transport-failure → IsNetworkError mapping, so the POST/read-response logic
// exists exactly once (extracted from AuthApiService for docs/plans/add-api-v2-sync).
public sealed class ApiHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private int _tokenRejectionSignaled;

    // Raised when a request that carried a Bearer token is answered (non-network) with
    // TOKEN_REQUIRED or INVALID_OR_EXPIRED_TOKEN: the server has invalidated the session
    // (30-day expiry or a password change, contract §11) and only a re-login can recover.
    // Carries the rejected token so subscribers can drop stale signals — a call that stayed
    // in flight across sign-out + re-sign-in reports the OLD session's token, which must not
    // sign the new session out. Latched to fire at most once per signed-in session — one
    // expiry bursts across every concurrent call (profile probe + sync pass) — see
    // ResetTokenRejectionLatch. Fires on a pool thread; subscribers marshal to the UI
    // thread themselves.
    public event Action<string>? TokenRejected;

    public ApiHttpClient(ConfigurationManager config)
    {
        _baseUrl = config.ApiSettings.UsersApiBaseUrl;
        _http = new HttpClient();
    }

    // Test seam (InternalsVisibleTo: ProoveGaming.App.Tests): drives the real envelope
    // pipeline against a stub HttpMessageHandler — the transport is the only faked boundary.
    internal ApiHttpClient(HttpMessageHandler handler, string baseUrl)
    {
        _baseUrl = baseUrl;
        _http = new HttpClient(handler);
    }

    public Task<T> PostAsync<T>(string endpoint, object? body, string? token = null, CancellationToken ct = default) where T : ApiResponse, new() =>
        SendAsync<T>(endpoint, body is null ? null : JsonContent.Create(body), token, ct);

    // Non-JSON bodies (the avatar's multipart form) go through the same envelope pipeline.
    public Task<T> PostAsync<T>(string endpoint, HttpContent content, string? token = null, CancellationToken ct = default) where T : ApiResponse, new() =>
        SendAsync<T>(endpoint, content, token, ct);

    private async Task<T> SendAsync<T>(string endpoint, HttpContent? content, string? token, CancellationToken ct) where T : ApiResponse, new()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + endpoint);
            if (content != null)
                request.Content = content;
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _http.SendAsync(request, ct);
            var result = await ReadResponseAsync<T>(response, ct);
            if (!string.IsNullOrEmpty(token) && !result.IsNetworkError && IsTokenRejection(result))
                SignalTokenRejected(token);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER cancelled (navigation, sign-out, shutdown). That is not a network failure
            // and must not be reported as one: it propagates like every other cancellation.
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient's own timeout (the caller's token is not cancelled).
            return new T { Success = false, IsNetworkError = true, ErrorCode = ClientErrorCodes.Timeout };
        }
        catch (Exception)
        {
            // No Message on purpose: exception text is English, and HttpRequestException's names
            // hosts and sockets. The UI localizes from ErrorCode (ApiErrorMessages.Describe).
            return new T { Success = false, IsNetworkError = true, ErrorCode = ClientErrorCodes.NetworkError };
        }
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct = default) where T : ApiResponse, new()
    {
        T? result = null;
        var malformed = false;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            result = JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web);

            // Valid JSON is not necessarily the API envelope: the PHP query-router backend can
            // answer an unknown route with 200 and its own JSON object, which deserializes into
            // T with every field defaulted (Success=false) and would read as a real refusal.
            // The envelope always carries "success", so its absence on a 2xx marks the payload
            // malformed — the caller decides what a malformed 2xx means for its flow.
            if (result is not null && response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                malformed = doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("success", out _);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout/cancellation — let SendAsync map it to IsNetworkError.
            throw;
        }
        catch
        {
            // ignore deserialization errors; fall back to generic error below
            malformed = true;
        }

        malformed |= result == null;
        result ??= new T { Success = false, ErrorCode = ClientErrorCodes.InvalidResponse };
        result.StatusCode = (int)response.StatusCode;
        result.MalformedPayload = malformed;
        result.RetryAfter = ReadRetryAfter(response);

        if (!response.IsSuccessStatusCode)
        {
            result.Success = false;
            result.IsNetworkError = false;
            if (string.IsNullOrEmpty(result.Message) && (result.Errors == null || result.Errors.Count == 0))
                result.Message = $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase})";
        }

        return result;
    }

    // RFC 9110 allows Retry-After in two forms: delta-seconds and an HTTP-date. HttpClient
    // parses both into RetryConditionHeaderValue, so the only work here is turning a date into
    // a wait and refusing a negative one — a date already in the past (or a client clock ahead
    // of the server's) would otherwise yield a negative TimeSpan that callers would pass
    // straight to Task.Delay. Absent or unparsable header stays null; the caller decides its
    // own default cooldown.
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;
        if (header.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    // Re-arms TokenRejected for the next session. AuthSessionCoordinator calls this whenever a session
    // starts (sign-in, restore, registration) so a later rejection of the NEW token can
    // signal again.
    public void ResetTokenRejectionLatch() => Interlocked.Exchange(ref _tokenRejectionSignaled, 0);

    private static bool IsTokenRejection(ApiResponse result)
    {
        if (result.ErrorCode is AuthErrorCodes.TokenRequired or AuthErrorCodes.InvalidOrExpiredToken)
            return true;
        if (result.Errors is not { } errors)
            return false;
        foreach (var error in errors)
        {
            if (error?.ErrorCode is AuthErrorCodes.TokenRequired or AuthErrorCodes.InvalidOrExpiredToken)
                return true;
        }
        return false;
    }

    private void SignalTokenRejected(string token)
    {
        // Interlocked: concurrent responses race the latch; exactly one may raise the event.
        if (Interlocked.Exchange(ref _tokenRejectionSignaled, 1) != 0)
            return;
        try
        {
            TokenRejected?.Invoke(token);
        }
        catch
        {
            // A faulting subscriber must not turn a successfully-read response into an API error.
        }
    }

    public void Dispose() => _http.Dispose();
}
