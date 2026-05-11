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
///   1. Always adds the <c>X-Arcsecond-Client: nina-plugin</c> header so the back-end
///      assigns this client to its own session bucket (separate from web/desktop).
///   2. Adds <c>Authorization: Bearer &lt;access&gt;</c> when an access token is cached.
///   3. On a 401 with a non-empty refresh token, runs the refresh delegate exactly once
///      and retries the request with the new access token.
///
/// The refresh delegate is provided by <see cref="FocaleApiClient"/> at construction time;
/// keeping that indirection avoids a circular reference between the handler and the client.
/// </summary>
internal sealed class AuthHandler : DelegatingHandler {
    private readonly TokenStore _tokens;
    private readonly Func<CancellationToken, Task<bool>> _refreshAsync;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public AuthHandler(TokenStore tokens, Func<CancellationToken, Task<bool>> refreshAsync) {
        _tokens       = tokens       ?? throw new ArgumentNullException(nameof(tokens));
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) {
        ApplyHeaders(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        // One concurrent refresh at a time — collapse the thundering herd of 401s into a single refresh call.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool refreshed;
        try {
            refreshed = await _refreshAsync(cancellationToken).ConfigureAwait(false);
        } finally {
            _refreshGate.Release();
        }
        if (!refreshed) return response;

        response.Dispose();
        var retry = await CloneAsync(request).ConfigureAwait(false);
        ApplyHeaders(retry);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyHeaders(HttpRequestMessage request) {
        request.Headers.Remove("X-Arcsecond-Client");
        request.Headers.TryAddWithoutValidation("X-Arcsecond-Client", FocaleSettings.ClientType);

        var access = _tokens.GetAccess();
        if (!string.IsNullOrEmpty(access)) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        }
    }

    /// <summary>
    /// Re-issues the request after a 401 → refresh round-trip. HttpRequestMessage cannot be
    /// re-sent as-is once dispatched, so we construct a new one with the same content.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source) {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri) {
            Version = source.Version
        };
        foreach (var (key, value) in source.Headers) {
            clone.Headers.TryAddWithoutValidation(key, value);
        }
        if (source.Content is not null) {
            // Buffer the original content into bytes so the clone can re-stream.
            // Acceptable for our typical bodies (JSON, small multipart envelopes).
            // Note: for very large multipart uploads (FITS files), the upload path bypasses
            // the auth handler's retry by ensuring its access token is fresh up-front.
            var bytes = await source.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var (key, value) in source.Content.Headers) {
                clone.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }
        return clone;
    }
}
