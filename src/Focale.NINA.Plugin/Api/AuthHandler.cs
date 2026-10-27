using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Settings;

namespace Focale.NINA.Plugin.Api;

/// <summary>
/// DelegatingHandler that:
///   1. Always adds the <c>X-Focale-Client: nina-plugin</c> header so the back-end
///      assigns this client to its own session bucket (separate from web/desktop).
///   2. Refreshes the access token <em>before</em> sending when it is missing or about to
///      expire — the access token lives in memory only, so the first request after a NINA
///      restart would otherwise always go out unauthenticated.
///   3. Adds <c>Authorization: Bearer &lt;access&gt;</c>.
///   4. On a 401, refreshes once and replays the request — unless its body is a stream
///      (FITS upload), which has been consumed and is too large to buffer. Those 401s are
///      returned to the caller, which re-queues the frame.
///
/// Requests flagged with <see cref="SkipAuth"/> (login, refresh, logout) bypass 2–4: they
/// carry their own credentials, and a 401 on the refresh call must not trigger a refresh.
///
/// The refresh delegate is provided by <see cref="FocaleApiClient"/> at construction time;
/// keeping that indirection avoids a circular reference between the handler and the client.
/// It takes the access token the caller knows to be stale, so concurrent callers collapse
/// into a single refresh.
/// </summary>
internal sealed class AuthHandler : DelegatingHandler {
    public static readonly HttpRequestOptionsKey<bool> SkipAuth = new("Focale.SkipAuth");

    private readonly TokenStore _tokens;
    private readonly Func<string?, CancellationToken, Task<bool>> _refreshAsync;

    public AuthHandler(TokenStore tokens, Func<string?, CancellationToken, Task<bool>> refreshAsync) {
        _tokens       = tokens       ?? throw new ArgumentNullException(nameof(tokens));
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) {
        ApplyClientHeader(request);
        if (request.Options.TryGetValue(SkipAuth, out var skip) && skip) {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!_tokens.HasFreshAccess()) {
            await _refreshAsync(_tokens.GetAccess(), cancellationToken).ConfigureAwait(false);
        }
        var sentAccess = ApplyBearer(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        if (!CanReplay(request.Content)) return response;
        if (!await _refreshAsync(sentAccess, cancellationToken).ConfigureAwait(false)) return response;

        response.Dispose();
        var retry = await CloneAsync(request).ConfigureAwait(false);
        ApplyClientHeader(retry);
        ApplyBearer(retry);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyClientHeader(HttpRequestMessage request) {
        request.Headers.Remove("X-Focale-Client");
        request.Headers.TryAddWithoutValidation("X-Focale-Client", FocaleSettings.ClientType);
    }

    private string? ApplyBearer(HttpRequestMessage request) {
        var access = _tokens.GetAccess();
        request.Headers.Authorization = string.IsNullOrEmpty(access)
            ? null
            : new AuthenticationHeaderValue("Bearer", access);
        return access;
    }

    /// <summary>Small in-memory bodies (JSON) can be replayed; streamed ones cannot.</summary>
    private static bool CanReplay(HttpContent? content) =>
        content is null or ByteArrayContent;

    /// <summary>
    /// Re-issues the request after a 401 → refresh round-trip. HttpRequestMessage cannot be
    /// re-sent as-is once dispatched, so we construct a new one with the same content.
    /// Only called for replayable (in-memory) bodies, see <see cref="CanReplay"/>.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source) {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri) {
            Version = source.Version
        };
        foreach (var (key, value) in source.Headers) {
            clone.Headers.TryAddWithoutValidation(key, value);
        }
        if (source.Content is not null) {
            var bytes = await source.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var (key, value) in source.Content.Headers) {
                clone.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }
        return clone;
    }
}
