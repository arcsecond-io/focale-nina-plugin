using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Settings;
using Focale.NINA.Plugin.Upload;
using NINA.Core.Utility;
using Newtonsoft.Json.Linq;

namespace Focale.NINA.Plugin.Events;

/// <summary>
/// Files recorded events under their night's log, in batches, in the background. Like the
/// upload worker it never loses anything to being offline, signed out or refused by the plan:
/// events stay in the store and go later. Only events the server rejects as such (a 4xx other
/// than 401/402/403/507, e.g. a night log deleted on the web) are given up.
/// </summary>
public sealed class NightEventSender : IDisposable {
    private const int BatchSize = 200;
    private static readonly TimeSpan Poll           = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefusedPause   = TimeSpan.FromHours(1);
    /// <summary>A span NINA never closed (closed mid-sequence) goes without an end after this.</summary>
    private static readonly TimeSpan StaleOpenAfter = TimeSpan.FromHours(18);
    /// <summary>Filed events, and events of nights no frame was ever uploaded for, are dropped after this.</summary>
    private static readonly TimeSpan Keep           = TimeSpan.FromDays(7);

    private readonly NightEventStore _store;
    private readonly FocaleApiClient _api;
    private readonly FocaleSettings _settings;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private DateTime _lastPurgeUtc = DateTime.MinValue;

    public NightEventSender(NightEventStore store, FocaleApiClient api, FocaleSettings settings) {
        _store    = store;
        _api      = api;
        _settings = settings;
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

    /// <summary>Sends now, e.g. once an upload taught the night log of a night with waiting events.</summary>
    public void Wake() {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Lifts a plan pause (the user upgraded).</summary>
    public void Resume() {
        _pausedUntilUtc = DateTime.MinValue;
        Wake();
    }

    private async Task RunAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                if (_settings.EnableNightEvents && _api.IsAuthenticated && _api.Plan is not { IncludesNinaSync: false }
                    && DateTime.UtcNow >= _pausedUntilUtc) {
                    await SendPendingAsync(ct).ConfigureAwait(false);
                }
                if (DateTime.UtcNow - _lastPurgeUtc > TimeSpan.FromHours(6)) {
                    await _store.PurgeAsync(Keep, ct).ConfigureAwait(false);
                    _lastPurgeUtc = DateTime.UtcNow;
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                Logger.Error("Focale: night event sending failed", ex);
            }
            try { await _wake.WaitAsync(Poll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendPendingAsync(CancellationToken ct) {
        var pending = await _store.ListSendableAsync(BatchSize, StaleOpenAfter, ct).ConfigureAwait(false);
        foreach (var night in pending.GroupBy(p => p.NightLogUuid)) {
            var rows = night.Select(p => p.Event).ToList();
            try {
                await _api.PostNightEventsAsync(night.Key, rows.Select(ToPayload).ToList(), ct).ConfigureAwait(false);
                await _store.MarkSentAsync(rows.Select(r => r.ClientId), ct).ConfigureAwait(false);
                Logger.Debug($"Focale: filed {rows.Count} night event(s) under night log {night.Key}");
            } catch (FocaleApiException ex) {
                switch (UploadWorker.Classify(ex)) {
                    case UploadFailureKind.SignedOut:
                        return;   // the upload worker shows the sign-in state; try again later
                    case UploadFailureKind.Refused:
                        _pausedUntilUtc = DateTime.UtcNow + RefusedPause;
                        Logger.Warning($"Focale: night events paused for an hour — {ex.ServerMessage ?? ex.Message}");
                        return;
                    case UploadFailureKind.Transient:
                        Logger.Warning($"Focale: night events will be retried — {ex.Message}");
                        return;
                    default:
                        await _store.MarkDeadAsync(rows.Select(r => r.ClientId), ct).ConfigureAwait(false);
                        Logger.Error($"Focale: {rows.Count} night event(s) refused for night log {night.Key} — {ex.Message}");
                        break;
                }
            } catch (Exception ex) when (UploadWorker.IsNetworkFailure(ex) && !ct.IsCancellationRequested) {
                return;   // offline: everything stays in the store
            }
        }
    }

    internal static NightEventPayload ToPayload(NightEventRow r) => new() {
        ClientId = r.ClientId,
        Kind     = r.Kind,
        Start    = DateTime.SpecifyKind(r.StartUtc, DateTimeKind.Utc),
        End      = r.EndUtc is null ? null : DateTime.SpecifyKind(r.EndUtc.Value, DateTimeKind.Utc),
        Success  = r.Success,
        Data     = JObject.Parse(string.IsNullOrWhiteSpace(r.DataJson) ? "{}" : r.DataJson),
    };

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
        _wake.Dispose();
    }
}
