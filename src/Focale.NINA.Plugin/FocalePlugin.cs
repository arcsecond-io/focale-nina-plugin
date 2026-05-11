using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Common;
using Focale.NINA.Plugin.Settings;
using Focale.NINA.Plugin.Sync;
using Focale.NINA.Plugin.Ui;
using Focale.NINA.Plugin.Upload;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;

namespace Focale.NINA.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class FocalePlugin : PluginBase {
    private readonly IProfileService _profileService;
    private readonly IImageSaveMediator _imageSaveMediator;
    private readonly ISequenceMediator _sequenceMediator;
    private readonly ICameraMediator _cameraMediator;
    private readonly ITelescopeMediator _telescopeMediator;
    private readonly IFocuserMediator _focuserMediator;
    private readonly IFilterWheelMediator _filterWheelMediator;
    private readonly IGuiderMediator _guiderMediator;
    private readonly IRotatorMediator _rotatorMediator;
    private readonly IDomeMediator _domeMediator;
    private readonly IWeatherDataMediator _weatherDataMediator;
    private readonly ISafetyMonitorMediator _safetyMonitorMediator;

    private FocaleSettings? _settings;
    private FocaleApiClient? _api;
    private SessionLifecycle? _session;
    private TargetResolver? _targets;
    private FramePublisher? _publisher;
    private UploadQueue? _queue;
    private UploadWorker? _worker;
    private SequenceSyncService? _sequenceSync;
    private FocaleOptionsViewModel? _optionsVm;

    [ImportingConstructor]
    public FocalePlugin(
        IProfileService profileService,
        IImageSaveMediator imageSaveMediator,
        ISequenceMediator sequenceMediator,
        ICameraMediator cameraMediator,
        ITelescopeMediator telescopeMediator,
        IFocuserMediator focuserMediator,
        IFilterWheelMediator filterWheelMediator,
        IGuiderMediator guiderMediator,
        IRotatorMediator rotatorMediator,
        IDomeMediator domeMediator,
        IWeatherDataMediator weatherDataMediator,
        ISafetyMonitorMediator safetyMonitorMediator) {
        _profileService        = profileService;
        _imageSaveMediator     = imageSaveMediator;
        _sequenceMediator      = sequenceMediator;
        _cameraMediator        = cameraMediator;
        _telescopeMediator     = telescopeMediator;
        _focuserMediator       = focuserMediator;
        _filterWheelMediator   = filterWheelMediator;
        _guiderMediator        = guiderMediator;
        _rotatorMediator       = rotatorMediator;
        _domeMediator          = domeMediator;
        _weatherDataMediator   = weatherDataMediator;
        _safetyMonitorMediator = safetyMonitorMediator;
    }

    /// <summary>Exposed so the auto-discovered DataTemplate ("Focale_Options") can bind to it.</summary>
    public FocaleOptionsViewModel? OptionsViewModel => _optionsVm;

    public override Task Initialize() {
        try {
            var pluginId = Guid.Parse(Identifier);
            _settings  = new FocaleSettings(pluginId, _profileService.ActiveProfile.PluginSettings);
            _api       = new FocaleApiClient(_settings);
            _session   = new SessionLifecycle(_api);
            _targets   = new TargetResolver(_api);
            _publisher = new FramePublisher(_api, _targets);

            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "PluginData", "Focale");
            Directory.CreateDirectory(dataDir);
            _queue = new UploadQueue(Path.Combine(dataDir, "queue.db"));
            _ = _queue.InitializeAsync();   // fire-and-forget; the worker tolerates startup races

            _worker = new UploadWorker(_queue, _api, _settings, _session, _publisher);
            _worker.Start();

            _imageSaveMediator.ImageSaved += OnImageSaved;

            _sequenceSync = new SequenceSyncService(_settings, _api, _session, _sequenceMediator);
            _sequenceSync.Start();

            _optionsVm = new FocaleOptionsViewModel(_settings, _api, _queue);

            Logger.Info($"Focale plugin initialized. Tonight is {NightDate.Format(NightDate.Today())}.");
            return Task.CompletedTask;
        } catch (Exception ex) {
            Logger.Error("Focale plugin failed to initialize", ex);
            throw;
        }
    }

    public override async Task Teardown() {
        try { _imageSaveMediator.ImageSaved -= OnImageSaved; } catch { }
        try { _sequenceSync?.Stop(); } catch (Exception ex) { Logger.Error("Focale: sequenceSync.Stop failed", ex); }

        if (_worker is not null) {
            try { await _worker.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Error("Focale: worker.StopAsync failed", ex); }
        }
        _worker?.Dispose();
        _api?.Dispose();
        _queue?.Dispose();
        Logger.Info("Focale plugin torn down");
    }

    /// <summary>
    /// Picks the best-available rotator angle for the captured frame. Prefers the planned
    /// target's PositionAngle (per-target framing) over the live rotator's MechanicalPosition
    /// (which can drift). Both are written to FITS by NINA; we read them off the same
    /// ImageMetaData to keep the value consistent with what's stamped in the headers.
    /// </summary>
    private static double? SafePositionAngle(ImageSavedEventArgs e) {
        try {
            var pa = e.MetaData?.Target?.PositionAngle;
            if (pa is double d && !double.IsNaN(d)) return d;
            var rot = e.MetaData?.Rotator?.Position;
            if (rot is double r && !double.IsNaN(r)) return r;
        } catch { /* best-effort */ }
        return null;
    }

    private async void OnImageSaved(object? sender, ImageSavedEventArgs e) {
        // Runs on the save-pipeline thread. Must not throw.
        try {
            if (_settings is null || _queue is null) return;
            if (!_settings.EnableFitsUpload) return;
            if (e?.PathToImage is null) return;

            var fileInfo = new FileInfo(e.PathToImage.LocalPath);
            if (!fileInfo.Exists) {
                Logger.Warning($"Focale: ImageSaved fired but file missing: {e.PathToImage}");
                return;
            }

            // Capture time comes from NINA's ImageMetaData (start of exposure).
            // ExposureStart defaults to DateTime.MinValue when unset, so we fall back to "now".
            var captureLocal = DateTime.Now;
            try {
                var ts = e.MetaData?.Image?.ExposureStart ?? DateTime.MinValue;
                if (ts != DateTime.MinValue) {
                    captureLocal = ts.Kind == DateTimeKind.Utc ? ts.ToLocalTime() : ts;
                }
            } catch { /* metadata access is best-effort */ }

            var imageType  = e.MetaData?.Image?.ImageType ?? string.Empty;
            var frameClass = FrameClassifier.FromNinaImageType(imageType);

            var sha256 = await UploadWorker.ComputeSha256Async(fileInfo, CancellationToken.None).ConfigureAwait(false);

            var job = new UploadJob {
                LocalPath            = fileInfo.FullName,
                Sha256               = sha256,
                CaptureLocal         = captureLocal,
                FrameClass           = frameClass,
                ImageType            = imageType,
                TargetName           = e.MetaData?.Target?.Name ?? string.Empty,
                ExposureSeconds      = e.Duration,
                PositionAngleDegrees = SafePositionAngle(e),
                EnqueuedAtUtc        = DateTime.UtcNow,
                NotBeforeUtc         = DateTime.UtcNow,
            };
            await _queue.EnqueueAsync(job).ConfigureAwait(false);
            Logger.Trace($"Focale: enqueued {frameClass} upload for {fileInfo.Name} (night {NightDate.Format(NightDate.FromLocal(captureLocal))})");
        } catch (Exception ex) {
            Logger.Error("Focale: failed to enqueue upload", ex);
        }
    }
}
