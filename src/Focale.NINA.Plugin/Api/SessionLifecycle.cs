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
/// Per-night-date resources on Focale, created lazily on first use.
///
/// One night (= local-noon-to-local-noon, see <see cref="NightDate"/>) gets:
///   • one NightLog dated <c>YYYY-MM-DD</c>
///   • one Dataset named "NINA - YYYY-MM-DD - Observations"
///   • one Dataset named "NINA - YYYY-MM-DD - Calibrations"
///
/// Resources roll over when the plugin sees an event/frame whose night-date differs
/// from the cached one. The previous night's UUIDs are forgotten (still exist server-side).
/// On plugin restart, the cache rebuilds from server state via a search-then-create pattern.
/// </summary>
public sealed class SessionLifecycle {
    private readonly FocaleApiClient _api;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateOnly? _cachedNight;
    private string? _nightLogUuid;
    private string? _observationsDatasetUuid;
    private string? _calibrationsDatasetUuid;

    public SessionLifecycle(FocaleApiClient api) {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public DateOnly? CurrentNight       => _cachedNight;
    public string?   NightLogUuid       => _nightLogUuid;
    public string?   ObservationsUuid   => _observationsDatasetUuid;
    public string?   CalibrationsUuid   => _calibrationsDatasetUuid;

    /// <summary>
    /// Ensures the NightLog + the two Datasets exist for the night that contains the
    /// given local timestamp. Returns the chosen Dataset UUID for the given frame class.
    /// Returns null if the user isn't signed in or the back-end rejected creation.
    /// </summary>
    public async Task<string?> EnsureDatasetAsync(DateTime localTimestamp, FrameClass cls, CancellationToken ct) {
        if (!_api.IsAuthenticated) return null;
        var night = NightDate.FromLocal(localTimestamp);
        await EnsureForNightAsync(night, ct).ConfigureAwait(false);
        return cls switch {
            FrameClass.Observation => _observationsDatasetUuid,
            FrameClass.Calibration => _calibrationsDatasetUuid,
            _                      => _observationsDatasetUuid,
        };
    }

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
            if (_cachedNight == night
                && _nightLogUuid is not null
                && _observationsDatasetUuid is not null
                && _calibrationsDatasetUuid is not null) {
                return;
            }
            if (_cachedNight != night) {
                Logger.Info($"Focale: rolling over to night {NightDate.Format(night)}" +
                            (_cachedNight is null ? "" : $" (previous: {NightDate.Format(_cachedNight.Value)})"));
                _nightLogUuid = _observationsDatasetUuid = _calibrationsDatasetUuid = null;
                _cachedNight  = night;
            }

            var nightStr = NightDate.Format(night);

            if (_nightLogUuid is null) {
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
            }

            _observationsDatasetUuid ??= await CreateDatasetAsync(nightStr, FrameClass.Observation, ct).ConfigureAwait(false);
            _calibrationsDatasetUuid ??= await CreateDatasetAsync(nightStr, FrameClass.Calibration, ct).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }
    }

    private async Task<string?> CreateDatasetAsync(string nightStr, FrameClass cls, CancellationToken ct) {
        var suffix = cls == FrameClass.Calibration ? "Calibrations" : "Observations";
        try {
            var ds = await _api.PostJsonAsync<DatasetResponse>(
                FocaleEndpoints.Datasets,
                new DatasetCreate {
                    Name        = $"NINA - {nightStr} - {suffix}",
                    Description = $"NINA {suffix.ToLowerInvariant()} captured on the night of {nightStr}.",
                    IsPublic    = false,
                }, ct).ConfigureAwait(false);
            Logger.Info($"Focale: created Dataset {ds.Uuid} ({suffix}) for {nightStr}");
            return ds.Uuid;
        } catch (Exception ex) {
            Logger.Error($"Focale: Dataset ({suffix}) creation for {nightStr} failed", ex);
            return null;
        }
    }
}
