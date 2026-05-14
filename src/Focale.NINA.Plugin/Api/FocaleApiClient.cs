using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Settings;
using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api;

public sealed class FocaleApiException : Exception {
    public HttpStatusCode? StatusCode { get; }
    public bool IsRetryable { get; }
    public FocaleApiException(string message, HttpStatusCode? code, bool retryable, Exception? inner = null)
        : base(message, inner) {
        StatusCode  = code;
        IsRetryable = retryable;
    }
}

/// <summary>
/// Long-lived HttpClient (avoids socket-handle exhaustion). Uses the system default
/// SocketsHttpHandler — TLS 1.2+ on modern Windows, default certificate validation
/// against the OS trust store. TLS verification is never disabled.
///
/// Auth: JWT Bearer with refresh-on-401 via <see cref="AuthHandler"/>. The plugin's
/// dedicated client_type ("nina-plugin") gives it an independent server-side session
/// so revoking the plugin does not log the user out of web/desktop.
/// </summary>
public sealed class FocaleApiClient : IDisposable {
    private readonly FocaleSettings _settings;
    private readonly TokenStore _tokens;
    private readonly HttpClient _http;

    public FocaleApiClient(FocaleSettings settings) {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tokens   = new TokenStore();

        var inner = new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression   = DecompressionMethods.All,
            // SslOptions left at defaults: validates server cert against the OS trust store.
        };
        var auth = new AuthHandler(_tokens, RefreshAccessAsync) { InnerHandler = inner };
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

        var body = JsonConvert.SerializeObject(new LoginRequest { Username = username, Password = password });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        // Login goes through the same handler so the X-Focale-Client header is set, but
        // there's no Authorization header yet — that's fine.
        using var resp = await _http.PostAsync(BuildUri(FocaleEndpoints.Login), content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
        var pair = JsonConvert.DeserializeObject<TokenPairResponse>(
            await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            ?? throw new FocaleApiException("Empty login response", resp.StatusCode, retryable: false);

        ApplyTokenPair(pair, fallbackUsername: username);
        return pair;
    }

    /// <summary>
    /// Posts the stored refresh token to /auth/token/refresh/. Updates the in-memory access
    /// token and persists any rotated refresh token. Returns false if there is no refresh
    /// token to use or the refresh failed permanently (in which case the caller should
    /// prompt for credentials again).
    /// </summary>
    public async Task<bool> RefreshAccessAsync(CancellationToken ct) {
        var refresh = _settings.GetRefreshToken();
        if (string.IsNullOrEmpty(refresh)) return false;

        var body = JsonConvert.SerializeObject(new RefreshRequest { Refresh = refresh });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(BuildUri(FocaleEndpoints.RefreshToken), content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) {
            // Refresh was rejected — clear local state so the worker stops hammering 401s.
            _tokens.Clear();
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
                _settings.SetRefreshToken(null);
            }
            return false;
        }

        var pair = JsonConvert.DeserializeObject<TokenPairResponse>(
            await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (pair is null) return false;

        ApplyTokenPair(pair, fallbackUsername: _settings.Username);
        return true;
    }

    /// <summary>Clears stored credentials and tells the back-end to invalidate the session.</summary>
    public async Task LogoutAsync(CancellationToken ct) {
        try {
            using var resp = await _http.PostAsync(BuildUri(FocaleEndpoints.Logout),
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            // Whether the server accepts or not, we drop local credentials.
        } catch { /* swallow — local cleanup happens unconditionally */ }
        _tokens.Clear();
        _settings.SetRefreshToken(null);
        _settings.Username = null;
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

    /// <summary>POST a JSON body to the given path. Generic helper used by snapshot + observation creation.</summary>
    public Task<TResp> PostJsonAsync<TResp>(string path, object body, CancellationToken ct)
        where TResp : class
        => SendJsonAsync<TResp>(HttpMethod.Post, path, body, ct);

    public Task<TResp> PatchJsonAsync<TResp>(string path, object body, CancellationToken ct)
        where TResp : class
        => SendJsonAsync<TResp>(HttpMethod.Patch, path, body, ct);

    public async Task<TResp> GetJsonAsync<TResp>(string path, CancellationToken ct) where TResp : class {
        using var resp = await _http.GetAsync(BuildUri(path), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<TResp>(s)
               ?? throw new FocaleApiException($"Empty response from GET {path}", resp.StatusCode, false);
    }

    private async Task<TResp> SendJsonAsync<TResp>(HttpMethod method, string path, object body, CancellationToken ct)
        where TResp : class {
        using var req = new HttpRequestMessage(method, BuildUri(path)) {
            Content = new StringContent(JsonConvert.SerializeObject(body), System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<TResp>(s)
               ?? throw new FocaleApiException($"Empty response from {method.Method} {path}", resp.StatusCode, false);
    }

    /// <summary>
    /// Multipart upload of a single FITS file plus an idempotency key.
    ///
    /// We deliberately do NOT send a separate <c>metadata</c> JSON part: NINA writes ~70 FITS
    /// header keywords (NINA.Image/FileFormat/FITS/CFitsioFITS.cs:98-392) and the arcsecond-back
    /// Celery pipeline already extracts them into <c>DataFile.pixel_metrics</c>. Sending a
    /// duplicate JSON would just be a second source of truth.
    ///
    /// The sha256 doubles as the idempotency key (server-side dedup of replays).
    /// </summary>
    public async Task UploadFileMultipartAsync(
        string path,
        FileInfo file,
        string sha256Hex,
        CancellationToken ct) {
        if (!file.Exists) throw new FileNotFoundException("FITS file missing at upload time", file.FullName);

        await using var stream = new FileStream(
            file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType        = new MediaTypeHeaderValue("image/fits");
        streamContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") {
            Name     = "\"file\"",
            FileName = "\"" + file.Name + "\""
        };
        form.Add(streamContent, "file", file.Name);
        form.Add(new StringContent(sha256Hex), "sha256");
        form.Add(new StringContent(sha256Hex), "idempotency_key");

        using var req = new HttpRequestMessage(HttpMethod.Post, BuildUri(path)) { Content = form };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", sha256Hex);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp) {
        if (resp.IsSuccessStatusCode) return;
        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
        var statusInt = (int)resp.StatusCode;
        // 401/403/404/409/422 = client error, NOT retryable
        // 408/425/429/5xx = transient, retryable
        var retryable = statusInt == 408 || statusInt == 425 || statusInt == 429 || statusInt >= 500;
        throw new FocaleApiException(
            $"Focale API responded {statusInt} {resp.ReasonPhrase}: {Truncate(body, 500)}",
            resp.StatusCode, retryable);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose() => _http.Dispose();
}
