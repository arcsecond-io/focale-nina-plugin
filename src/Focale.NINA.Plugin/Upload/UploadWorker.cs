using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Settings;
using NINA.Core.Utility;

// Worker streams each FITS/XISF to Focale via `POST /datafiles/` with `auto_ingest=true`.
// The server reads the header itself and creates the DataFile, NightLog, Dataset,
// ObservingSite/Telescope/Camera, Observation/Calibration, and target resolution in a
// single transaction.

namespace Focale.NINA.Plugin.Upload;

/// <summary>What a failed upload says about what to do with the frame.</summary>
public enum UploadFailureKind {
    /// <summary>401: the session is gone. Keep the frame; wait for the user to sign in.</summary>
    SignedOut,
    /// <summary>402/403/507: a plan limit or quota. Keep the frame; pause the whole queue.</summary>
    Refused,
    /// <summary>408/425/429/5xx: try this frame again later, with backoff.</summary>
    Transient,
    /// <summary>Any other 4xx: the server will never accept this file. Dead-letter it.</summary>
    Permanent,
}

public enum UploadWorkerState {
    /// <summary>The user turned automatic upload off.</summary>
    Disabled,
    /// <summary>No session: frames wait in the queue until the user signs in.</summary>
    SignedOut,
    /// <summary>Focale refused uploads (plan limit, quota). Frames wait until <see cref="UploadWorker.Resume"/> or the pause expires.</summary>
    Paused,
    Running,
}

/// <summary>
/// Drains the UploadQueue by streaming each frame to Focale via the auto-ingest
/// multipart endpoint, one at a time, so we never saturate the user's uplink during imaging.
///
/// A frame is only dead-lettered for reasons that belong to that file (missing, modified,
/// or refused with a 4xx). Being signed out, hitting a plan limit, or being offline never
/// loses a frame: it stays queued and goes up once the condition clears.
/// </summary>
public sealed class UploadWorker : IDisposable {
    /// <summary>Server-side transient errors (5xx, 429) — about a day at the 5-minute backoff cap.</summary>
    private const int MaxAttempts = 300;
    private static readonly TimeSpan IdlePoll          = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NetworkRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthRetryDelay    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefusedPause      = TimeSpan.FromHours(1);

    /// <summary>
    /// NightLog.Source value the backend stamps on rows created from this
    /// upload path. NINA captures are live-at-the-console by definition, so
    /// the resulting NightLog is attributed to `observer_console` rather
    /// than the default `data_upload_ingestion` (which is for Focale Memory
    /// drag-and-drop uploads from the web UI).
    /// </summary>
    private const string IngestSource = "observer_console";

    private readonly UploadQueue _queue;
    private readonly FocaleApiClient _api;
    private readonly FocaleSettings _settings;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private UploadWorkerState _state = UploadWorkerState.Running;
    private string? _pauseMessage;
    private DateTime _pausedUntilUtc = DateTime.MinValue;

    public UploadWorker(UploadQueue queue, FocaleApiClient api, FocaleSettings settings) {
        _queue    = queue;
        _api      = api;
        _settings = settings;
    }

    public UploadWorkerState State { get { lock (_stateLock) return _state; } }

    /// <summary>Why the queue is paused, in words for the user (the server's own message when it sent one).</summary>
    public string? PauseMessage { get { lock (_stateLock) return _pauseMessage; } }

    /// <summary>When a <see cref="UploadWorkerState.Paused"/> queue tries again on its own.</summary>
    public DateTime? PausedUntilUtc { get { lock (_stateLock) return _state == UploadWorkerState.Paused ? _pausedUntilUtc : null; } }

    /// <summary>Raised on the worker thread whenever <see cref="State"/> or <see cref="PauseMessage"/> changes.</summary>
    public event EventHandler? StateChanged;

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

    /// <summary>Lifts a refusal pause (the user upgraded or freed space) and wakes the worker now.</summary>
    public void Resume() {
        lock (_stateLock) {
            _pausedUntilUtc = DateTime.MinValue;
        }
        Wake();
    }

    /// <summary>Cuts the current idle wait short, e.g. right after the user signs in.</summary>
    public void Wake() {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public static UploadFailureKind Classify(FocaleApiException ex) {
        var status = (int?)ex.StatusCode;
        return status switch {
            401                   => UploadFailureKind.SignedOut,
            402 or 403 or 507     => UploadFailureKind.Refused,
            _ when ex.IsRetryable => UploadFailureKind.Transient,
            _                     => UploadFailureKind.Permanent,
        };
    }

    /// <summary>The request never got an HTTP answer: no network, DNS, TLS, or the client timed out.</summary>
    public static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or TimeoutException or OperationCanceledException;

    private async Task RunAsync(CancellationToken ct) {
        Logger.Info("Focale: upload worker started");
        while (!ct.IsCancellationRequested) {
            if (!_settings.EnableFitsUpload) {
                SetState(UploadWorkerState.Disabled, null);
                await IdleAsync(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }
            if (!_api.IsAuthenticated) {
                SetState(UploadWorkerState.SignedOut, "Sign in to Focale to upload. Frames wait in the queue.");
                await IdleAsync(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }
            if (IsPausedByRefusal()) {
                await IdleAsync(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }
            SetState(UploadWorkerState.Running, null);

            UploadJob? job;
            try {
                job = await _queue.ClaimNextAsync(ct).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                Logger.Error("Focale: queue claim failed", ex);
                await IdleAsync(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }
            if (job is null) {
                await IdleAsync(IdlePoll, ct).ConfigureAwait(false);
                continue;
            }

            try {
                await ProcessAsync(job, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            }
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

            // Refresh up front: the access token lives in memory only, and a streamed upload
            // that gets a 401 cannot be replayed. Whatever goes wrong here is not the file's
            // fault, so it never costs the frame an attempt.
            bool signedIn;
            try {
                signedIn = await _api.EnsureFreshAccessAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is FocaleApiException || (IsNetworkFailure(ex) && !ct.IsCancellationRequested)) {
                await RequeueAsync(job, job.Attempt, NetworkRetryDelay, "token refresh failed: " + ex.Message).ConfigureAwait(false);
                Logger.Warning($"Focale: token refresh failed, frame {job.Id} waits — {ex.Message}");
                return;
            }
            if (!signedIn) {
                // The refresh token is gone (never signed in, or rejected): the loop now shows SignedOut.
                await RequeueAsync(job, job.Attempt, TimeSpan.Zero, "signed out").ConfigureAwait(false);
                return;
            }

            // Single-shot auto-ingest. The server reads the header to derive everything:
            // NightLog (with `source=observer_console` since this is live console capture),
            // Dataset, ObservingSite, Telescope, Camera, Observation/Calibration, target, and
            // pixel metrics. The sha256 doubles as idempotency key so retried uploads return
            // the existing DataFile rather than duplicating.
            var extraFields = new Dictionary<string, string> {
                ["auto_ingest"]  = "true",
                ["persist_file"] = "true",
                ["source"]       = IngestSource,
            };
            await _api.UploadFileMultipartAsync(
                FocaleEndpoints.DataFiles, fi, job.Sha256, ct, extraFields).ConfigureAwait(false);
            await _queue.MarkDoneAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            Logger.Info($"Focale: uploaded {fi.Name} ({fi.Length} bytes, attempt {job.Attempt + 1})");
        } catch (FocaleApiException apiEx) {
            await HandleApiFailureAsync(job, apiEx).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Shutdown — release the row back to pending so the next run picks it up.
            await RequeueAsync(job, job.Attempt, TimeSpan.Zero, "shutdown").ConfigureAwait(false);
            throw;
        } catch (Exception ex) when (IsNetworkFailure(ex)) {
            // Offline observatories are the norm, not the exception: wait for the network
            // without spending the frame's retry budget.
            await RequeueAsync(job, job.Attempt, NetworkRetryDelay, ex.Message).ConfigureAwait(false);
            Logger.Warning($"Focale: network unavailable, frame {job.Id} waits — {ex.Message}");
        } catch (Exception ex) {
            await RetryOrDieAsync(job, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleApiFailureAsync(UploadJob job, FocaleApiException apiEx) {
        switch (Classify(apiEx)) {
            case UploadFailureKind.SignedOut:
                // The access token was rejected mid-flight (revoked server-side). Force a
                // refresh: if the refresh token was revoked too, it is forgotten and the loop
                // shows SignedOut; otherwise the frame goes up with the new token.
                try {
                    await _api.ForceRefreshAsync(CancellationToken.None).ConfigureAwait(false);
                } catch (Exception ex) {
                    Logger.Warning($"Focale: refresh after 401 failed — {ex.Message}");
                }
                await RequeueAsync(job, job.Attempt, AuthRetryDelay, apiEx.Message).ConfigureAwait(false);
                Logger.Warning($"Focale: upload of job {job.Id} unauthorized, frame kept in the queue");
                break;

            case UploadFailureKind.Refused:
                await RequeueAsync(job, job.Attempt, TimeSpan.Zero, apiEx.Message).ConfigureAwait(false);
                var message = apiEx.ServerMessage
                              ?? $"Focale refused the upload ({(int?)apiEx.StatusCode}). Check your plan and storage on focale.space.";
                PauseForRefusal(message);
                Logger.Warning($"Focale: uploads paused until {DateTime.Now + RefusedPause:HH:mm} — {message}");
                break;

            case UploadFailureKind.Transient:
                await RetryOrDieAsync(job, apiEx.Message).ConfigureAwait(false);
                break;

            default:
                await _queue.MarkDeadAsync(job.Id, apiEx.Message, CancellationToken.None).ConfigureAwait(false);
                Logger.Error($"Focale: job {job.Id} dead — {apiEx.Message}");
                break;
        }
    }

    private async Task RetryOrDieAsync(UploadJob job, string error) {
        var attempt = job.Attempt + 1;
        if (attempt >= MaxAttempts) {
            await _queue.MarkDeadAsync(job.Id, "max retries exceeded: " + error, CancellationToken.None).ConfigureAwait(false);
            Logger.Error($"Focale: job {job.Id} dead after {attempt} attempts — {error}");
            return;
        }
        var delay = Backoff(attempt);
        await RequeueAsync(job, attempt, delay, error).ConfigureAwait(false);
        Logger.Warning($"Focale: job {job.Id} retry {attempt}/{MaxAttempts} in {delay.TotalSeconds:0}s — {error}");
    }

    private Task RequeueAsync(UploadJob job, int attempt, TimeSpan delay, string? reason) =>
        _queue.MarkRetryAsync(job.Id, attempt, DateTime.UtcNow + delay, reason, CancellationToken.None);

    private void PauseForRefusal(string message) {
        lock (_stateLock) {
            _pausedUntilUtc = DateTime.UtcNow + RefusedPause;
        }
        SetState(UploadWorkerState.Paused, message);
    }

    /// <summary>True while a refusal pause holds; lifts it once it expires or after <see cref="Resume"/>.</summary>
    private bool IsPausedByRefusal() {
        lock (_stateLock) {
            if (_state != UploadWorkerState.Paused) return false;
            if (DateTime.UtcNow < _pausedUntilUtc) return true;
        }
        SetState(UploadWorkerState.Running, null);
        return false;
    }

    private void SetState(UploadWorkerState state, string? message) {
        lock (_stateLock) {
            if (_state == state && _pauseMessage == message) return;
            _state        = state;
            _pauseMessage = message;
        }
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Logger.Error("Focale: StateChanged handler failed", ex); }
    }

    /// <summary>
    /// Exponential backoff: attempt n → 2^min(n,10) seconds, capped at 300s.
    /// Sequence per attempt: 2, 4, 8, 16, 32, 64, 128, 256, 300, 300, …
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

    private async Task IdleAsync(TimeSpan d, CancellationToken ct) {
        try { await _wake.WaitAsync(d, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
        _wake.Dispose();
    }
}
