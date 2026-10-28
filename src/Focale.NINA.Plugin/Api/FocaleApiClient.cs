using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Common;
using Focale.NINA.Plugin.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Focale.NINA.Plugin.Api;

public sealed class FocaleApiException : Exception {
    public HttpStatusCode? StatusCode { get; }
    public bool IsRetryable { get; }

    /// <summary>
    /// The <c>error</c> field of a JSON error body, when the server sent one — e.g.
    /// <c>subscription_required</c> from a plan gate (entitlements.exceptions.SubscriptionRequired).
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>The human-readable <c>message</c> (or <c>detail</c>) of a JSON error body.</summary>
    public string? ServerMessage { get; }

    /// <summary>
    /// The request fields a DRF validation error names (the top-level keys of a 400 body such as
    /// <c>{"destination": ["Unknown storage."]}</c>), so a caller can tell a refused setting from
    /// a refused file.
    /// </summary>
    public IReadOnlyCollection<string> FieldErrors { get; }

    public FocaleApiException(string message, HttpStatusCode? code, bool retryable, Exception? inner = null,
                              string? errorCode = null, string? serverMessage = null,
                              IReadOnlyCollection<string>? fieldErrors = null)
        : base(message, inner) {
        StatusCode    = code;
        IsRetryable   = retryable;
        ErrorCode     = errorCode;
        ServerMessage = serverMessage;
        FieldErrors   = fieldErrors ?? Array.Empty<string>();
    }
}

/// <summary>
/// Long-lived HttpClient (avoids socket-handle exhaustion). Uses the system default
/// SocketsHttpHandler — TLS 1.2+ on modern Windows, default certificate validation
/// against the OS trust store. TLS verification is never disabled.
///
/// Auth: JWT Bearer, refreshed before it expires and on 401 via <see cref="AuthHandler"/>.
/// The plugin's dedicated client_type ("nina-plugin") gives it an independent server-side
/// session so revoking the plugin does not log the user out of web/desktop.
/// </summary>
public sealed class FocaleApiClient : IDisposable {
    private readonly FocaleSettings _settings;
    private readonly TokenStore _tokens;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public FocaleApiClient(FocaleSettings settings) {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tokens   = new TokenStore();

        var inner = new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression   = DecompressionMethods.All,
            // SslOptions left at defaults: validates server cert against the OS trust store.
        };
        var auth = new AuthHandler(_tokens, RefreshIfStaleAsync) { InnerHandler = inner };
        _http = new HttpClient(auth) {
            // FITS uploads can be hundreds of MB on slow uplinks.
            Timeout = TimeSpan.FromMinutes(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Focale.NINA.Plugin/0.1 (+https://focale.space)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private Uri BuildUri(string path) => new Uri(new Uri(_settings.ServerUrl), path);

    // ---- Auth ----

    /// <summary>Exchanges username/password for an access+refresh pair. Persists the refresh token.</summary>
    public async Task<TokenPairResponse> LoginAsync(string username, string password, CancellationToken ct) {
        if (string.IsNullOrEmpty(username)) throw new ArgumentException("username required", nameof(username));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("password required", nameof(password));

        using var req = NewAuthRequest(FocaleEndpoints.Login, new LoginRequest { Username = username, Password = password });
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
        var pair = JsonConvert.DeserializeObject<TokenPairResponse>(
            await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            ?? throw new FocaleApiException("Empty login response", resp.StatusCode, retryable: false);

        ApplyTokenPair(pair, fallbackUsername: username);
        return pair;
    }

    /// <summary>
    /// Makes sure a fresh access token is in memory, refreshing it if needed. Returns false
    /// when the user is not signed in or the server rejected the refresh token (which is then
    /// forgotten: the user has to sign in again). Throws on transient failures (network, 5xx),
    /// leaving the refresh token in place.
    /// </summary>
    public Task<bool> EnsureFreshAccessAsync(CancellationToken ct) =>
        _tokens.HasFreshAccess() ? Task.FromResult(true) : RefreshIfStaleAsync(_tokens.GetAccess(), ct);

    /// <summary>Refreshes even if the access token looks fresh — for when the server has just rejected it.</summary>
    public Task<bool> ForceRefreshAsync(CancellationToken ct) => RefreshIfStaleAsync(_tokens.GetAccess(), ct);

    /// <summary>
    /// Refreshes the access token unless another caller already replaced
    /// <paramref name="staleAccess"/> while this one waited for the gate. Same return/throw
    /// contract as <see cref="EnsureFreshAccessAsync"/>.
    /// </summary>
    public async Task<bool> RefreshIfStaleAsync(string? staleAccess, CancellationToken ct) {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_tokens.HasFreshAccess() && _tokens.GetAccess() != staleAccess) return true;

            var refresh = _settings.GetRefreshToken();
            if (string.IsNullOrEmpty(refresh)) return false;

            using var req = NewAuthRequest(FocaleEndpoints.RefreshToken, new RefreshRequest { Refresh = refresh });
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
                // Refresh token expired or revoked — forget it so nothing keeps hammering 401s.
                _tokens.Clear();
                _settings.SetRefreshToken(null);
                return false;
            }
            await EnsureSuccessAsync(resp).ConfigureAwait(false);

            var pair = JsonConvert.DeserializeObject<TokenPairResponse>(
                await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (pair is null || string.IsNullOrEmpty(pair.Access)) return false;

            ApplyTokenPair(pair, fallbackUsername: _settings.Username);
            return true;
        } finally {
            _refreshGate.Release();
        }
    }

    /// <summary>Clears stored credentials and tells the back-end to invalidate the session.</summary>
    public async Task LogoutAsync(CancellationToken ct) {
        try {
            // The back-end identifies the session by the refresh token in the body when the
            // access token is absent or expired (accounts.views.auth.LogoutView).
            using var req = NewAuthRequest(FocaleEndpoints.Logout,
                new RefreshRequest { Refresh = _settings.GetRefreshToken() ?? "" });
            var access = _tokens.GetAccess();
            if (!string.IsNullOrEmpty(access)) {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            }
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            // Whether the server accepts or not, we drop local credentials.
        } catch { /* swallow — local cleanup happens unconditionally */ }
        _tokens.Clear();
        _settings.SetRefreshToken(null);
        _settings.Username = null;
    }

    /// <summary>A JSON POST to an auth endpoint: carries its own credentials, so the auth handler leaves it alone.</summary>
    private HttpRequestMessage NewAuthRequest(string path, object body) {
        var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(path)) {
            Content = new StringContent(JsonConvert.SerializeObject(body), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Options.Set(AuthHandler.SkipAuth, true);
        return req;
    }

    private void ApplyTokenPair(TokenPairResponse pair, string? fallbackUsername) {
        if (!string.IsNullOrEmpty(pair.Refresh)) {
            _settings.SetRefreshToken(pair.Refresh);
        }
        if (!string.IsNullOrEmpty(pair.Access)) {
            // Prefer access_exp (absolute unix-seconds) over expires_in (relative) when present.
            var expUtc = pair.AccessExp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(pair.AccessExp).UtcDateTime
                : DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(60, pair.ExpiresIn));
            _tokens.SetAccess(pair.Access, expUtc);
        }
        var u = pair.Username ?? fallbackUsername;
        if (!string.IsNullOrEmpty(u)) _settings.Username = u;
    }

    public bool IsAuthenticated => _tokens.HasFreshAccess() || _settings.HasRefreshToken();

    // ---- App calls ----

    /// <summary>
    /// Multipart upload of a single FITS/XISF file plus an idempotency key, with
    /// optional extra string form fields (used by the auto-ingest endpoint to
    /// carry `auto_ingest`, `persist_file`, `source`, …).
    ///
    /// We deliberately do NOT send a separate <c>metadata</c> JSON part: NINA writes ~70 FITS
    /// header keywords (NINA.Image/FileFormat/FITS/CFitsioFITS.cs:98-392) and arcsecond-back's
    /// auto-ingest pipeline extracts them server-side (DataFile.pixel_metrics +
    /// Observation/Calibration creation + site/telescope/camera resolution).
    /// Sending a duplicate JSON would just be a second source of truth.
    ///
    /// The sha256 doubles as the idempotency key — when the server sees a
    /// repeat, it returns the existing DataFile and skips re-ingestion.
    ///
    /// A 401 here is not retried by the auth handler (the file stream has been consumed);
    /// it surfaces as a <see cref="FocaleApiException"/> and the worker re-queues the frame.
    ///
    /// Returns what the server says it did with the frame — notably the night log it was filed
    /// under — or null when the body could not be read (the upload itself succeeded).
    /// </summary>
    public async Task<UploadResponse?> UploadFileMultipartAsync(
        string path,
        FileInfo file,
        string sha256Hex,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraFields = null) {
        if (!file.Exists) throw new FileNotFoundException("Image file missing at upload time", file.FullName);

        await using var stream = new FileStream(
            file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType        = new MediaTypeHeaderValue(ImageFileTypes.ContentTypeFor(file.Name));
        streamContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
            Name     = "\"file\"",
            FileName = "\"" + file.Name + "\""
        };
        form.Add(streamContent, "file", file.Name);
        form.Add(new StringContent(sha256Hex), "sha256");
        form.Add(new StringContent(sha256Hex), "idempotency_key");
        if (extraFields is not null) {
            foreach (var (k, v) in extraFields) {
                form.Add(new StringContent(v), k);
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(path)) { Content = form };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", sha256Hex);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
        try {
            return JsonConvert.DeserializeObject<UploadResponse>(
                await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>The user's storages that can receive uploads (writable, of a type the back-end writes to).</summary>
    public async Task<IReadOnlyList<DataStorageResponse>> ListUploadDestinationsAsync(CancellationToken ct) {
        using var resp = await _http.GetAsync(BuildUri(FocaleEndpoints.DataStorages), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
        var token = JToken.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        // Plain list or DRF page — accept either.
        var rows = token is JObject page && page["results"] is JArray results ? results : token as JArray;
        var storages = rows?.ToObject<List<DataStorageResponse>>() ?? new List<DataStorageResponse>();
        return storages.FindAll(s => s.CanReceiveUploads);
    }

    /// <summary>
    /// The exposure plan of the user's night plan for <paramref name="nightDate"/> (yyyy-MM-dd), or
    /// null when that night has no plan. Focale's Tonight keeps one plan per night.
    /// </summary>
    public async Task<ExposurePlanResponse?> GetExposurePlanAsync(string nightDate, CancellationToken ct) {
        using var list = await _http.GetAsync(BuildUri(FocaleEndpoints.NightPlans + "?date=" + Uri.EscapeDataString(nightDate)), ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(list).ConfigureAwait(false);
        var token = JToken.Parse(await list.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var rows = token is JObject page && page["results"] is JArray results ? results : token as JArray;
        var uuid = rows?.FirstOrDefault()?["uuid"]?.ToString();
        if (string.IsNullOrEmpty(uuid)) return null;

        using var resp = await _http.GetAsync(BuildUri(string.Format(FocaleEndpoints.NightPlanExposures, uuid)), ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<ExposurePlanResponse>(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Files a batch of events under a night log. Idempotent: the server keys them on their client ids.</summary>
    public async Task PostNightEventsAsync(string nightLogUuid, IReadOnlyList<NightEventPayload> events, CancellationToken ct) {
        var path = string.Format(FocaleEndpoints.NightLogEvents, nightLogUuid);
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(path)) {
            Content = new StringContent(SerializeEvents(events), System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
    }

    internal static string SerializeEvents(IReadOnlyList<NightEventPayload> events) =>
        JsonConvert.SerializeObject(events, EventSerializerSettings);

    private static readonly JsonSerializerSettings EventSerializerSettings = new() {
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        DateFormatString     = "yyyy-MM-ddTHH:mm:ss.fffZ",
    };

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp) {
        if (resp.IsSuccessStatusCode) return;
        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
        var statusInt = (int)resp.StatusCode;
        // 401/403/404/409/422 = client error, NOT retryable
        // 408/425/429/5xx = transient, retryable
        var retryable = statusInt == 408 || statusInt == 425 || statusInt == 429 || statusInt >= 500;
        var (errorCode, serverMessage) = ParseErrorBody(body);
        throw new FocaleApiException(
            $"Focale API responded {statusInt} {resp.ReasonPhrase}: {Truncate(body, 500)}",
            resp.StatusCode, retryable, errorCode: errorCode, serverMessage: serverMessage,
            fieldErrors: ParseFieldErrors(body));
    }

    /// <summary>The top-level keys of a JSON object error body, other than the generic ones.</summary>
    internal static IReadOnlyCollection<string> ParseFieldErrors(string? body) {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
        try {
            if (JToken.Parse(body) is not JObject o) return Array.Empty<string>();
            var fields = new List<string>();
            foreach (var p in o.Properties()) {
                if (p.Name is "error" or "message" or "detail" or "gate" or "non_field_errors") continue;
                fields.Add(p.Name);
            }
            return fields;
        } catch (JsonException) {
            return Array.Empty<string>();
        }
    }

    /// <summary>Reads <c>error</c> and <c>message</c>/<c>detail</c> from a JSON error body, if it is one.</summary>
    internal static (string? ErrorCode, string? Message) ParseErrorBody(string? body) {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try {
            if (JToken.Parse(body) is not JObject o) return (null, null);
            var error   = o["error"]?.Type == JTokenType.String ? (string?)o["error"] : null;
            var message = o["message"]?.Type == JTokenType.String ? (string?)o["message"]
                        : o["detail"]?.Type  == JTokenType.String ? (string?)o["detail"]
                        : null;
            return (error, message);
        } catch (JsonException) {
            return (null, null);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose() => _http.Dispose();
}
