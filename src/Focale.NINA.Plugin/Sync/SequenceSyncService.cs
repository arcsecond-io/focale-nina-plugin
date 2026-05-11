using System;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Common;
using Focale.NINA.Plugin.Settings;
using NINA.Core.Utility;
using NINA.Sequencer.Interfaces.Mediator;

namespace Focale.NINA.Plugin.Sync;

/// <summary>
/// On <c>SequenceStarting</c>, posts the NINA Advanced Sequencer tree as one ObservingSequence
/// + one SequenceStep per target + one SequenceTask per instruction. On <c>SequenceFinished</c>,
/// transitions the ObservingSequence to <c>completed</c>.
///
/// Mid-sequence status transitions (paused / running) are not pushed for v1 — NINA does not
/// expose per-instruction events through ISequenceMediator. The next event will catch up.
/// </summary>
public sealed class SequenceSyncService : IDisposable {
    private readonly FocaleSettings _settings;
    private readonly FocaleApiClient _api;
    private readonly SessionLifecycle _session;
    private readonly ISequenceMediator _sequenceMediator;

    private long? _currentSequenceId;

    public SequenceSyncService(
        FocaleSettings settings,
        FocaleApiClient api,
        SessionLifecycle session,
        ISequenceMediator sequenceMediator) {
        _settings         = settings;
        _api              = api;
        _session          = session;
        _sequenceMediator = sequenceMediator;
    }

    public void Start() {
        _sequenceMediator.SequenceStarting += OnSequenceStarting;
        _sequenceMediator.SequenceFinished += OnSequenceFinished;
    }

    public void Stop() {
        _sequenceMediator.SequenceStarting -= OnSequenceStarting;
        _sequenceMediator.SequenceFinished -= OnSequenceFinished;
    }

    private async Task OnSequenceStarting(object sender, EventArgs e) {
        if (!_settings.EnableMetadataSync) return;
        try {
            await PostFreshSequenceAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            Logger.Error("Focale: failed to push ObservingSequence at start", ex);
        }
    }

    private async Task OnSequenceFinished(object sender, EventArgs e) {
        if (!_settings.EnableMetadataSync) return;
        if (_currentSequenceId is null) return;
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _api.PatchJsonAsync<ObservingSequenceResponse>(
                string.Format(FocaleEndpoints.ObservingSequence, _currentSequenceId.Value),
                new ObservingSequencePatch { Status = "completed" },
                cts.Token).ConfigureAwait(false);
            Logger.Info($"Focale: ObservingSequence {_currentSequenceId} marked completed");
        } catch (Exception ex) {
            Logger.Error("Focale: failed to mark ObservingSequence completed", ex);
        } finally {
            _currentSequenceId = null;
        }
    }

    private async Task PostFreshSequenceAsync(CancellationToken ct) {
        if (!_api.IsAuthenticated) {
            Logger.Trace("Focale: sequence push skipped — not signed in");
            return;
        }

        var night = NightDate.Today();
        // Ensure the NightLog exists so cross-resource correlation works downstream.
        await _session.EnsureNightLogAsync(DateTime.Now, ct).ConfigureAwait(false);

        var seq = await _api.PostJsonAsync<ObservingSequenceResponse>(
            FocaleEndpoints.ObservingSequences,
            new ObservingSequenceCreate {
                Name   = $"NINA — {NightDate.Format(night)}",
                Night  = NightDate.Format(night),
                Status = "running",
            }, ct).ConfigureAwait(false);
        if (seq.Id is null) {
            Logger.Warning("Focale: ObservingSequence response had no id; aborting tree push");
            return;
        }
        _currentSequenceId = seq.Id;

        var targets = _sequenceMediator.GetAllTargetsInAdvancedSequence();
        var plans = SequenceTreeBuilder.Build(targets);

        int targetIndex = 0;
        foreach (var plan in plans) {
            SequenceStepResponse? step;
            try {
                step = await _api.PostJsonAsync<SequenceStepResponse>(
                    FocaleEndpoints.ObservingSequenceSteps,
                    new SequenceStepCreate {
                        Sequence       = seq.Id.Value,
                        Name           = plan.Name,
                        Description    = plan.Description,
                        Index          = targetIndex++,
                        TaskMode       = "sequential",
                        Classification = "observation",
                    }, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error($"Focale: SequenceStep '{plan.Name}' POST failed", ex);
                continue;
            }
            if (step?.Id is null) continue;

            int taskIndex = 0;
            foreach (var instr in plan.Instructions) {
                try {
                    await _api.PostJsonAsync<object>(
                        FocaleEndpoints.ObservingSequenceTasks,
                        new SequenceTaskCreate {
                            Step       = step.Id.Value,
                            Name       = instr.Name,
                            Index      = taskIndex++,
                            TaskName   = instr.TaskName,
                            TaskKwargs = instr.Kwargs,
                        }, ct).ConfigureAwait(false);
                } catch (Exception ex) {
                    Logger.Warning($"Focale: SequenceTask '{instr.Name}' POST failed: {ex.Message}");
                }
            }
        }

        Logger.Info($"Focale: pushed ObservingSequence {seq.Id} with {plans.Count} target(s)");
    }

    public void Dispose() => Stop();
}
