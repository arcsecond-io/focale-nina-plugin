using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Upload;
using NINA.Core.Utility;

namespace Focale.NINA.Plugin.Push;

/// <summary>The statuses NINA sets on a night plan entry (observingruns <c>Observation.PlanStatus</c>).</summary>
public static class PlanEntryStatus {
    public const string Active  = "active";
    public const string Done    = "done";
    public const string Skipped = "skipped";
}

/// <summary>
/// Tells Focale, in the background, where each night plan entry the plugin built a NINA target for stands:
/// <c>active</c> when NINA starts the target, <c>done</c> when it finishes. Best-effort: <see cref="Report"/>
/// never blocks or fails the sequence. While offline or signed out the last status of each entry waits in
/// memory and goes later (only the latest matters, so a late "active" never overwrites a "done"); a status
/// the server refuses (the entry or plan deleted, not the user's) is dropped, and so is one still waiting
/// after <see cref="Keep"/>. Nothing is kept across NINA restarts.
/// </summary>
public sealed class PlanStatusReporter : IDisposable {
    internal static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan Keep  = TimeSpan.FromHours(18);

    /// <summary>Sends one status: (plan uuid, entry uuid, status).</summary>
    public delegate Task Sender(string planUuid, string entryUuid, string status, CancellationToken ct);

    private sealed record Pending(string PlanUuid, string EntryUuid, string Status, DateTime ReportedUtc, long Version);

    private readonly Sender _send;
    private readonly Func<bool> _canSend;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private long _version;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PlanStatusReporter(FocaleApiClient api)
        : this(api.SetPlanEntryStatusAsync, () => api.IsAuthenticated) { }

    internal PlanStatusReporter(Sender send, Func<bool> canSend, Func<DateTime>? utcNow = null) {
        _send    = send;
        _canSend = canSend;
        _utcNow  = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The statuses waiting to be sent, by entry uuid.</summary>
    internal IReadOnlyDictionary<string, string> PendingStatuses {
        get { lock (_lock) return _pending.ToDictionary(p => p.Key, p => p.Value.Status); }
    }

    /// <summary>Queues an entry's status and wakes the sender. Never throws.</summary>
    public void Report(string? planUuid, string? entryUuid, string status) {
        if (string.IsNullOrEmpty(planUuid) || string.IsNullOrEmpty(entryUuid)) return;
        lock (_lock) {
            _pending[entryUuid] = new Pending(planUuid, entryUuid, status, _utcNow(), ++_version);
        }
        Wake();
    }

    public void Start() {
        if (_loop is not null) return;
        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync(TimeSpan grace) {
        _cts?.Cancel();
        if (_loop is null) return;
        try {
            using var timeout = new CancellationTokenSource(grace);
            await _loop.WaitAsync(timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) { }
    }

    private void Wake() {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try { await FlushAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Logger.Error("Focale: setting night plan statuses failed", ex); }
            try { await _wake.WaitAsync(Retry, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Sends every waiting status once. Stops at the first one that can't go now (offline, signed out, a
    /// server error) and leaves it and the rest for the next try.
    /// </summary>
    internal async Task FlushAsync(CancellationToken ct) {
        List<Pending> batch;
        lock (_lock) {
            var expired = _pending.Values.Where(p => _utcNow() - p.ReportedUtc > Keep).ToList();
            foreach (var p in expired) _pending.Remove(p.EntryUuid);
            batch = _pending.Values.OrderBy(p => p.Version).ToList();
        }
        if (batch.Count == 0 || !_canSend()) return;

        foreach (var p in batch) {
            try {
                await _send(p.PlanUuid, p.EntryUuid, p.Status, ct).ConfigureAwait(false);
                Logger.Debug($"Focale: night plan entry {p.EntryUuid} is {p.Status}");
                Forget(p);
            } catch (FocaleApiException ex) {
                switch (UploadWorker.Classify(ex)) {
                    case UploadFailureKind.SignedOut:
                    case UploadFailureKind.Transient:
                        Logger.Warning($"Focale: night plan status will be retried — {ex.Message}");
                        return;
                    default:
                        Logger.Warning($"Focale: night plan entry {p.EntryUuid} refused \"{p.Status}\" — {ex.ServerMessage ?? ex.Message}");
                        Forget(p);
                        break;
                }
            } catch (Exception ex) when (UploadWorker.IsNetworkFailure(ex) && !ct.IsCancellationRequested) {
                return;   // offline: keep it for later
            }
        }
    }

    /// <summary>Drops a sent status, unless a newer one for the same entry came in meanwhile.</summary>
    private void Forget(Pending sent) {
        lock (_lock) {
            if (_pending.TryGetValue(sent.EntryUuid, out var current) && current.Version == sent.Version) {
                _pending.Remove(sent.EntryUuid);
            }
        }
    }

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
        _wake.Dispose();
    }
}
