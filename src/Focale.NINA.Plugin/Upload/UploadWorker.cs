using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Settings;
using NINA.Core.Utility;

// Worker streams FITS via FocaleApiClient.UploadFileMultipartAsync to /datasets/{uuid}/datafiles/.
// The dataset UUID is sourced from SessionLifecycle, which lazily creates a Dataset when the
// user is signed in.

namespace Focale.NINA.Plugin.Upload;

/// <summary>
/// Drains the UploadQueue by streaming each FITS to Focale via the multipart endpoint.
/// Concurrency is bounded by FocaleSettings.MaxParallelUpload (default 1) so we never
/// saturate the user's uplink during imaging.
/// </summary>
public sealed class UploadWorker : IDisposable {
    private const int MaxAttempts = 12;
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);

    private readonly UploadQueue _queue;
    private readonly FocaleApiClient _api;
    private readonly FocaleSettings _settings;
    private readonly SessionLifecycle _session;
    private readonly FramePublisher _publisher;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public UploadWorker(UploadQueue queue, FocaleApiClient api, FocaleSettings settings,
                        SessionLifecycle session, FramePublisher publisher) {
        _queue     = queue;
        _api       = api;
        _settings  = settings;
        _session   = session;
        _publisher = publisher;
    }

    public void Start() {
        if (_loop is not null) return;
        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync(TimeSpan? gracePeriod = null) {
        _cts?.Cancel();
        if (_loop is not null) {
            try {
                using var grace = new CancellationTokenSource(gracePeriod ?? TimeSpan.FromSeconds(10));
                await _loop.WaitAsync(grace.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) { /* drain timed out — let app shutdown anyway */ }
        }
    }

    private async Task RunAsync(CancellationToken ct) {
        Logger.Info("Focale: upload worker started");
        while (!ct.IsCancellationRequested) {
            if (!_settings.EnableFitsUpload) {
                await SafeDelay(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }
            UploadJob? job;
            try {
                job = await _queue.ClaimNextAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error("Focale: queue claim failed", ex);
                await SafeDelay(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }
            if (job is null) {
                await SafeDelay(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }

            await ProcessAsync(job, ct).ConfigureAwait(false);
        }
        Logger.Info("Focale: upload worker stopped");
    }

    private async Task ProcessAsync(UploadJob job, CancellationToken ct) {
        try {
            var fi = new FileInfo(job.LocalPath);
            if (!fi.Exists) {
                await _queue.MarkDeadAsync(job.Id, $"File missing: {job.LocalPath}", CancellationToken.None).ConfigureAwait(false);
                Logger.Warning($"Focale: job {job.Id} dead — file missing: {job.LocalPath}");
                return;
            }

            var actual = await ComputeSha256Async(fi, ct).ConfigureAwait(false);
            if (!string.Equals(actual, job.Sha256, StringComparison.OrdinalIgnoreCase)) {
                await _queue.MarkDeadAsync(job.Id, "sha256 mismatch (file mutated after enqueue)", CancellationToken.None).ConfigureAwait(false);
                Logger.Warning($"Focale: job {job.Id} dead — sha256 changed since enqueue");
                return;
            }

            var datasetUuid = await _session.EnsureDatasetAsync(job.CaptureLocal, job.FrameClass, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(datasetUuid)) {
                // Not authenticated yet (or backend rejected Dataset creation). Push the job back
                // with a short delay so we don't burn CPU.
                await _queue.MarkRetryAsync(job.Id, job.Attempt + 1,
                    DateTime.UtcNow + TimeSpan.FromSeconds(30),
                    "no Dataset UUID yet (not signed in?)", CancellationToken.None).ConfigureAwait(false);
                return;
            }
            var path = string.Format(FocaleEndpoints.DatasetFiles, datasetUuid);
            await _api.UploadFileMultipartAsync(path, fi, job.Sha256, ct).ConfigureAwait(false);
            await _queue.MarkDoneAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            Logger.Info($"Focale: uploaded {fi.Name} ({fi.Length} bytes, attempt {job.Attempt + 1})");

            // Best-effort: create the matching Observation / Calibration row under the night's
            // NightLog so the frame is reachable via typed FKs. Failures here are non-fatal:
            // the DataFile is already on the server.
            try {
                var nightLogUuid = await _session.EnsureNightLogAsync(job.CaptureLocal, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(nightLogUuid)) {
                    var duration = TimeSpan.FromSeconds(Math.Max(0, job.ExposureSeconds));
                    if (job.FrameClass == FrameClass.Observation) {
                        await _publisher.PublishObservationAsync(
                            nightLogUuid, fi.Name, job.CaptureLocal, duration,
                            job.TargetName, job.PositionAngleDegrees, ct).ConfigureAwait(false);
                    } else {
                        await _publisher.PublishCalibrationAsync(
                            nightLogUuid, fi.Name, job.CaptureLocal, duration,
                            job.ImageType, ct).ConfigureAwait(false);
                    }
                }
            } catch (Exception ex) {
                Logger.Warning($"Focale: post-upload publish for {fi.Name} failed: {ex.Message}");
            }
        } catch (FocaleApiException apiEx) when (!apiEx.IsRetryable) {
            await _queue.MarkDeadAsync(job.Id, apiEx.Message, CancellationToken.None).ConfigureAwait(false);
            Logger.Error($"Focale: job {job.Id} dead — {apiEx.Message}");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Shutdown — release the row back to pending so the next run picks it up.
            await _queue.MarkRetryAsync(job.Id, job.Attempt, DateTime.UtcNow, "shutdown", CancellationToken.None).ConfigureAwait(false);
            throw;
        } catch (Exception ex) {
            var attempt = job.Attempt + 1;
            if (attempt >= MaxAttempts) {
                await _queue.MarkDeadAsync(job.Id, "max retries exceeded: " + ex.Message, CancellationToken.None).ConfigureAwait(false);
                Logger.Error($"Focale: job {job.Id} dead after {attempt} attempts — {ex.Message}");
                return;
            }
            var notBefore = DateTime.UtcNow + Backoff(attempt);
            await _queue.MarkRetryAsync(job.Id, attempt, notBefore, ex.Message, CancellationToken.None).ConfigureAwait(false);
            Logger.Warning($"Focale: job {job.Id} retry {attempt}/{MaxAttempts} at {notBefore:O} — {ex.Message}");
        }
    }

    /// <summary>
    /// Exponential backoff: attempt n → 2^min(n,10) seconds, capped at 300s.
    /// Sequence per attempt: 2, 4, 8, 16, 32, 64, 128, 256, 300, 300, 300, 300.
    /// </summary>
    public static TimeSpan Backoff(int attempt) {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempt, 10)));
        return TimeSpan.FromSeconds(seconds);
    }

    public static async Task<string> ComputeSha256Async(FileInfo file, CancellationToken ct) {
        await using var s = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task SafeDelay(TimeSpan d, CancellationToken ct) {
        try { await Task.Delay(d, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
