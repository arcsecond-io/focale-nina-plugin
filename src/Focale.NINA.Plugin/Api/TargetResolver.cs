using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api.Models;
using NINA.Core.Utility;

namespace Focale.NINA.Plugin.Api;

/// <summary>
/// Resolves a NINA target name to a Focale XTarget pk. Caches in-process so repeated frames
/// of the same target don't trigger redundant GET/POSTs. The resolver assumes a name-based
/// lookup is sufficient for v1 — for catalogue objects (M51, NGC 5194, …) arcsecond-back's
/// <c>XTargetManager.get_or_create</c> resolves the <c>AstronomicalObject</c> automatically
/// when <c>target_class = "AstronomicalObject"</c> and <c>mode = "auto"</c>.
/// </summary>
public sealed class TargetResolver {
    private readonly FocaleApiClient _api;
    private readonly ConcurrentDictionary<string, long> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TargetResolver(FocaleApiClient api) {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>
    /// Returns the XTarget pk for the given target name, looking it up server-side and
    /// creating one if absent. Returns null if the name is empty, the user isn't signed in,
    /// or the server rejects both lookup and creation.
    /// </summary>
    public async Task<long?> ResolveAsync(string? name, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!_api.IsAuthenticated) return null;
        var key = name.Trim();
        if (_cache.TryGetValue(key, out var pk)) return pk;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_cache.TryGetValue(key, out pk)) return pk;

            // 1) Find — the listing endpoint accepts a name filter.
            try {
                var list = await _api.GetJsonAsync<XTargetListResponse>(
                    FocaleEndpoints.Targets + "?name=" + Uri.EscapeDataString(key), ct).ConfigureAwait(false);
                var hit = list.Results;
                if (hit is { Count: > 0 } && hit[0].Pk is long existingPk) {
                    _cache[key] = existingPk;
                    return existingPk;
                }
            } catch (FocaleApiException ex) {
                // Lookup is best-effort. If the server returned a non-paginated array we won't
                // see it under .Results — fall through to create.
                Logger.Trace($"Focale: target lookup for '{key}' returned {ex.StatusCode}; will try create");
            }

            // 2) Create.
            try {
                var created = await _api.PostJsonAsync<XTargetResponse>(
                    FocaleEndpoints.Targets,
                    new XTargetCreate { Name = key }, ct).ConfigureAwait(false);
                if (created.Pk is long newPk) {
                    _cache[key] = newPk;
                    Logger.Info($"Focale: created XTarget '{key}' (pk={newPk})");
                    return newPk;
                }
            } catch (FocaleApiException ex) {
                Logger.Warning($"Focale: failed to create XTarget '{key}': {ex.StatusCode} {ex.Message}");
            }
            return null;
        } finally {
            _gate.Release();
        }
    }
}
