using System;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Common;
using NINA.Core.Utility;

namespace Focale.NINA.Plugin.Api;

public enum FrameClass {
    Observation,
    Calibration,
}

/// <summary>
/// Lazy NightLog creation, one per local-noon-to-noon night (see <see cref="NightDate"/>).
///
/// Used by `SequenceSyncService` so the sequence-push side-effect creates a
/// NightLog before any SequenceStep / SequenceTask rows reference its date.
/// Frame upload no longer goes through this class — the auto-ingest endpoint
/// resolves the NightLog server-side from the FITS header's DATE-OBS.
///
/// On plugin restart, the cache is empty and a single POST recreates (or
/// gets, via the backend's unique-together on date+telescope+source+owner)
/// the night's NightLog idempotently.
/// </summary>
public sealed class SessionLifecycle {
    private readonly FocaleApiClient _api;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateOnly? _cachedNight;
    private string? _nightLogUuid;

    public SessionLifecycle(FocaleApiClient api) {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public DateOnly? CurrentNight => _cachedNight;
    public string?   NightLogUuid => _nightLogUuid;

    /// <summary>Returns the NightLog UUID for the night containing the given local time.</summary>
    public async Task<string?> EnsureNightLogAsync(DateTime localTimestamp, CancellationToken ct) {
        if (!_api.IsAuthenticated) return null;
        var night = NightDate.FromLocal(localTimestamp);
        await EnsureForNightAsync(night, ct).ConfigureAwait(false);
        return _nightLogUuid;
    }

    private async Task EnsureForNightAsync(DateOnly night, CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_cachedNight == night && _nightLogUuid is not null) return;
            if (_cachedNight != night) {
                Logger.Info($"Focale: rolling over to night {NightDate.Format(night)}" +
                            (_cachedNight is null ? "" : $" (previous: {NightDate.Format(_cachedNight.Value)})"));
                _nightLogUuid = null;
                _cachedNight  = night;
            }

            var nightStr = NightDate.Format(night);
            try {
                var nl = await _api.PostJsonAsync<NightLogResponse>(
                    FocaleEndpoints.NightLogs,
                    new NightLogCreate {
                        Name   = $"NINA — {nightStr}",
                        Date   = nightStr,
                        Source = "observer_console",
                    }, ct).ConfigureAwait(false);
                _nightLogUuid = nl.Uuid;
                Logger.Info($"Focale: created NightLog {_nightLogUuid} for {nightStr}");
            } catch (Exception ex) {
                Logger.Error($"Focale: NightLog creation for {nightStr} failed", ex);
            }
        } finally {
            _gate.Release();
        }
    }
}
