using System;
using System.IO;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Settings;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;

namespace Focale.NINA.Plugin.Events;

/// <summary>
/// Listens to NINA and hands what happens to the <see cref="NightEventRecorder"/>. Every handler
/// returns at once (recording is fire-and-forget) so NINA's sequence, slews and saves never wait
/// on the plugin, and does nothing while night events are turned off.
///
/// Only APIs present in both NINA 3.2 and 3.3 are used: NINA 3.3's ImageSaveFailed, for one, does
/// not exist in 3.2, which the shipped build targets.
/// </summary>
public sealed class NinaEventSource : IFocuserConsumer, IDisposable {
    private readonly NightEventRecorder _recorder;
    private readonly FocaleSettings _settings;
    private readonly ISequenceMediator _sequence;
    private readonly IFocuserMediator _focuser;
    private readonly ITelescopeMediator _telescope;
    private readonly IGuiderMediator _guider;
    private readonly ISafetyMonitorMediator _safety;
    private readonly IImageSaveMediator _imageSave;

    public NinaEventSource(NightEventRecorder recorder, FocaleSettings settings,
                           ISequenceMediator sequence, IFocuserMediator focuser, ITelescopeMediator telescope,
                           IGuiderMediator guider, ISafetyMonitorMediator safety, IImageSaveMediator imageSave) {
        _recorder  = recorder;
        _settings  = settings;
        _sequence  = sequence;
        _focuser   = focuser;
        _telescope = telescope;
        _guider    = guider;
        _safety    = safety;
        _imageSave = imageSave;
    }

    public void Start() {
        _sequence.SequenceStarting     += OnSequenceStarting;
        _sequence.SequenceFinished     += OnSequenceFinished;
        _telescope.BeforeMeridianFlip  += OnBeforeMeridianFlip;
        _telescope.AfterMeridianFlip   += OnAfterMeridianFlip;
        _telescope.Parked              += OnParked;
        _telescope.Unparked            += OnUnparked;
        _guider.AfterDither            += OnAfterDither;
        _safety.IsSafeChanged          += OnIsSafeChanged;
        _imageSave.ImageSaved          += OnImageSaved;
        _focuser.RegisterConsumer(this);
    }

    public void Dispose() {
        _sequence.SequenceStarting     -= OnSequenceStarting;
        _sequence.SequenceFinished     -= OnSequenceFinished;
        _telescope.BeforeMeridianFlip  -= OnBeforeMeridianFlip;
        _telescope.AfterMeridianFlip   -= OnAfterMeridianFlip;
        _telescope.Parked              -= OnParked;
        _telescope.Unparked            -= OnUnparked;
        _guider.AfterDither            -= OnAfterDither;
        _safety.IsSafeChanged          -= OnIsSafeChanged;
        _imageSave.ImageSaved          -= OnImageSaved;
        try { _focuser.RemoveConsumer(this); } catch { /* NINA shutting down */ }
    }

    private Task OnSequenceStarting(object sender, EventArgs e)  => Record(r => r.SequenceStartedAsync(DateTime.UtcNow));
    private Task OnSequenceFinished(object sender, EventArgs e)  => Record(r => r.SequenceFinishedAsync(DateTime.UtcNow));
    private Task OnBeforeMeridianFlip(object sender, BeforeMeridianFlipEventArgs e) => Record(r => r.MeridianFlipStartingAsync(DateTime.UtcNow));
    private Task OnAfterMeridianFlip(object sender, AfterMeridianFlipEventArgs e)   => Record(r => r.MeridianFlipFinishedAsync(DateTime.UtcNow, e.Success));
    private Task OnParked(object sender, EventArgs e)            => Record(r => r.ParkedAsync(DateTime.UtcNow));
    private Task OnUnparked(object sender, EventArgs e)          => Record(r => r.UnparkedAsync(DateTime.UtcNow));
    private Task OnAfterDither(object sender, EventArgs e)       => Record(r => r.DitheredAsync(DateTime.UtcNow));

    private void OnIsSafeChanged(object? sender, IsSafeEventArgs e) => Record(r => r.SafetyChangedAsync(DateTime.UtcNow, e.IsSafe));

    private void OnImageSaved(object? sender, ImageSavedEventArgs e) {
        var image = e?.MetaData?.Image;
        var rms = image?.RecordedRMS;
        if (image is null || rms is null) return;
        var guiding = new GuidingRms(rms.RA, rms.Dec, rms.Total, rms.PeakRA, rms.PeakDec, rms.DataPoints, rms.Scale);
        if (!guiding.HasData) return;
        var start = NightEventRecorder.AsUtc(image.ExposureStart);
        var end   = start + TimeSpan.FromSeconds(Math.Max(0, image.ExposureTime));
        var name  = e!.PathToImage is null ? null : Path.GetFileName(e.PathToImage.LocalPath);
        Record(r => r.ExposureGuidedAsync(start, end, guiding, name));
    }

    // --- IFocuserConsumer: NINA calls these on the focuser's thread. ---

    public void AutoFocusRunStarting() => Record(r => r.AutofocusStartedAsync(DateTime.UtcNow));

    public void UpdateEndAutoFocusRun(AutoFocusInfo info) =>
        Record(r => r.AutofocusFinishedAsync(
            info.Timestamp == default ? DateTime.UtcNow : NightEventRecorder.AsUtc(info.Timestamp),
            info.Position, info.Temperature, info.Filter));

    public void UpdateUserFocused(FocuserInfo info) { }
    public void UpdateDeviceInfo(FocuserInfo deviceInfo) { }
    public void NewAutoFocusPoint(OxyPlot.DataPoint dataPoint) { }

    private Task Record(Func<NightEventRecorder, Task> action) {
        if (!_settings.EnableNightEvents) return Task.CompletedTask;
        _ = RunAsync(action);
        return Task.CompletedTask;
    }

    private async Task RunAsync(Func<NightEventRecorder, Task> action) {
        try { await action(_recorder).ConfigureAwait(false); }
        catch (Exception ex) { Logger.Error("Focale: recording a night event failed", ex); }
    }
}
