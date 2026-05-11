using System;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api.Models;
using NINA.Core.Utility;

namespace Focale.NINA.Plugin.Api;

/// <summary>
/// After a DataFile has been uploaded, creates the matching <c>Observation</c> (for lights)
/// or <c>Calibration</c> (for darks/biases/flats) row under the night's NightLog, so each
/// frame is reachable via typed FKs:
///   NightLog → Observation → XTarget
///   NightLog → Calibration
///
/// Failures are non-fatal: a failed Observation create does not roll back the DataFile.
/// The Focale UI can reconcile later if needed.
/// </summary>
public sealed class FramePublisher {
    private readonly FocaleApiClient _api;
    private readonly TargetResolver _targets;

    public FramePublisher(FocaleApiClient api, TargetResolver targets) {
        _api     = api     ?? throw new ArgumentNullException(nameof(api));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    }

    /// <summary>Create an Observation row for a LIGHT (or unclassified) frame.</summary>
    public async Task PublishObservationAsync(
        string nightLogUuid,
        string fileName,
        DateTime startLocal,
        TimeSpan duration,
        string? ninaTargetName,
        double? positionAngleDegrees,
        CancellationToken ct) {
        var targetPk = await _targets.ResolveAsync(ninaTargetName, ct).ConfigureAwait(false);
        var startUtc = startLocal.Kind == DateTimeKind.Utc ? startLocal : startLocal.ToUniversalTime();
        try {
            await _api.PostJsonAsync<ObservationResponse>(
                string.Format(FocaleEndpoints.NightLogObservationsList, nightLogUuid),
                new ObservationCreate {
                    Name          = fileName,
                    Start         = startUtc,
                    Duration      = FormatDuration(duration),
                    Target        = targetPk,
                    PositionAngle = positionAngleDegrees,
                }, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            Logger.Warning($"Focale: Observation create for {fileName} failed: {ex.Message}");
        }
    }

    /// <summary>Create a Calibration row for a DARK/BIAS/FLAT frame.</summary>
    public async Task PublishCalibrationAsync(
        string nightLogUuid,
        string fileName,
        DateTime startLocal,
        TimeSpan duration,
        string? ninaImageType,
        CancellationToken ct) {
        var calibType = MapCalibrationType(ninaImageType);
        var startUtc  = startLocal.Kind == DateTimeKind.Utc ? startLocal : startLocal.ToUniversalTime();
        try {
            await _api.PostJsonAsync<CalibrationResponse>(
                string.Format(FocaleEndpoints.NightLogCalibrationsList, nightLogUuid),
                new CalibrationCreate {
                    Name     = fileName,
                    Start    = startUtc,
                    Duration = FormatDuration(duration),
                    Type     = calibType,
                }, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            Logger.Warning($"Focale: Calibration create for {fileName} failed: {ex.Message}");
        }
    }

    /// <summary>NINA imagetype → arcsecond-back Calibration.Type.</summary>
    public static string MapCalibrationType(string? ninaImageType) {
        if (string.IsNullOrWhiteSpace(ninaImageType)) return "";
        var t = ninaImageType.Trim().ToUpperInvariant();
        return t switch {
            "DARK"     => "Darks",
            "BIAS"     => "Biases",
            "FLAT"     => "Flats",
            "DARKFLAT" => "Darks",
            "FLATDARK" => "Darks",
            "BIASDARK" => "Biases",
            _          => "",
        };
    }

    /// <summary>Django DurationField wire format: "HH:MM:SS[.ffffff]".</summary>
    public static string FormatDuration(TimeSpan d) {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d.ToString(@"hh\:mm\:ss\.ffffff");
    }
}
