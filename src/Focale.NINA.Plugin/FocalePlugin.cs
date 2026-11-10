using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Common;
using Focale.NINA.Plugin.Events;
using Focale.NINA.Plugin.Settings;
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
    private readonly IFocuserMediator _focuserMediator;
    private readonly ITelescopeMediator _telescopeMediator;
    private readonly IGuiderMediator _guiderMediator;
    private readonly ISafetyMonitorMediator _safetyMonitorMediator;

    private FocaleSettings? _settings;
    private FocaleApiClient? _api;
    private UploadQueue? _queue;
    private UploadWorker? _worker;
    private NightEventStore? _eventStore;
    private NightEventSender? _eventSender;
    private NinaEventSource? _eventSource;
    private FocaleOptionsViewModel? _optionsVm;

    [ImportingConstructor]
    public FocalePlugin(
        IProfileService profileService,
        IImageSaveMediator imageSaveMediator,
        ISequenceMediator sequenceMediator,
        IFocuserMediator focuserMediator,
        ITelescopeMediator telescopeMediator,
        IGuiderMediator guiderMediator,
        ISafetyMonitorMediator safetyMonitorMediator) {
        _profileService        = profileService;
        _imageSaveMediator     = imageSaveMediator;
        _sequenceMediator      = sequenceMediator;
        _focuserMediator       = focuserMediator;
        _telescopeMediator     = telescopeMediator;
        _guiderMediator        = guiderMediator;
        _safetyMonitorMediator = safetyMonitorMediator;
    }

    /// <summary>Exposed so the auto-discovered DataTemplate ("Focale_Options") can bind to it.</summary>
    public FocaleOptionsViewModel? OptionsViewModel => _optionsVm;

    public override Task Initialize() {
        try {
            var pluginId = Guid.Parse(Identifier);
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "PluginData", "Focale");
            Directory.CreateDirectory(dataDir);

            // The account is shared by every NINA profile; the rest follows the active profile.
            var account = new AccountStore(Path.Combine(dataDir, "account.json"));
            _settings = new FocaleSettings(pluginId, () => _profileService.ActiveProfile.PluginSettings, account);
            _api      = new FocaleApiClient(_settings);
            FocaleRuntime.Settings = _settings;
            FocaleRuntime.Api      = _api;
            _queue = new UploadQueue(Path.Combine(dataDir, "queue.db"));
            _ = _queue.InitializeAsync();   // fire-and-forget; the worker tolerates startup races

            // Night events: recorded from NINA as they happen, kept locally, and filed under the
            // night log each upload reports (the worker's callback below).
            _eventStore  = new NightEventStore(Path.Combine(dataDir, "events.db"));
            var eventsReady = _eventStore.InitializeAsync();
            _eventSender = new NightEventSender(_eventStore, _api, _settings);
            _eventSource = new NinaEventSource(new NightEventRecorder(_eventStore), _settings,
                _sequenceMediator, _focuserMediator, _telescopeMediator, _guiderMediator,
                _safetyMonitorMediator, _imageSaveMediator);
            _ = eventsReady.ContinueWith(t => {
                if (t.IsFaulted) { Logger.Error("Focale: night event store failed to open", t.Exception); return; }
                _eventSource.Start();
                _eventSender.Start();
            }, TaskScheduler.Default);

            _worker = new UploadWorker(_queue, _api, _settings, async (nightDate, nightLogUuid) => {
                await _eventStore.RememberNightLogAsync(nightDate, nightLogUuid).ConfigureAwait(false);
                _eventSender.Wake();
            });
            _worker.Start();

            _imageSaveMediator.ImageSaved += OnImageSaved;

            _optionsVm = new FocaleOptionsViewModel(_settings, _api, _queue, _worker, _eventSender);
            _profileService.ProfileChanged += OnProfileChanged;

            // Read the plan now, so the settings page and the panels show it before the first frame.
            if (_api.IsAuthenticated) {
                _ = Task.Run(async () => {
                    try { await _api.RefreshPlanAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { Logger.Warning($"Focale: could not read the plan — {ex.Message}"); }
                });
            }

            Logger.Info($"Focale plugin initialized. Tonight is {NightDate.Format(NightDate.Today())}.");
            return Task.CompletedTask;
        } catch (Exception ex) {
            Logger.Error("Focale plugin failed to initialize", ex);
            throw;
        }
    }

    public override async Task Teardown() {
        try { _imageSaveMediator.ImageSaved -= OnImageSaved; } catch { }
        try { _profileService.ProfileChanged -= OnProfileChanged; } catch { }
        try { _eventSource?.Dispose(); } catch (Exception ex) { Logger.Error("Focale: event source teardown failed", ex); }
        if (_eventSender is not null) {
            try { await _eventSender.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Error("Focale: event sender stop failed", ex); }
        }

        if (_worker is not null) {
            try { await _worker.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Error("Focale: worker.StopAsync failed", ex); }
        }
        _optionsVm?.Dispose();
        _worker?.Dispose();
        _eventSender?.Dispose();
        _eventStore?.Dispose();
        _api?.Dispose();
        _queue?.Dispose();
        Logger.Info("Focale plugin torn down");
    }

    /// <summary>The storage choice and features belong to the profile: show and use the new one's.</summary>
    private void OnProfileChanged(object? sender, EventArgs e) {
        try {
            _optionsVm?.ReloadProfileSettings();
            _worker?.Resume();
            _eventSender?.Wake();
        } catch (Exception ex) {
            Logger.Error("Focale: profile change handling failed", ex);
        }
    }

    private async void OnImageSaved(object? sender, ImageSavedEventArgs e) {
        // Runs on the save-pipeline thread. Must not throw.
        try {
            if (_settings is null || _queue is null) return;
            if (!_settings.EnableFitsUpload) return;
            if (e?.PathToImage is null) return;

            var localPath = e.PathToImage.LocalPath;
            if (!ImageFileTypes.IsSupported(localPath)) {
                Logger.Trace($"Focale: skipping {Path.GetFileName(localPath)} — only FITS and XISF are uploaded");
                return;
            }

            var fileInfo = new FileInfo(localPath);
            if (!fileInfo.Exists) {
                Logger.Warning($"Focale: ImageSaved fired but file missing: {e.PathToImage}");
                return;
            }

            // Capture time + frame classification are kept on the queue row for
            // log readability and dead-letter triage. The server reads the
            // authoritative DATE-OBS / IMAGETYP straight from the file header
            // on auto-ingest, so these never leave the plugin.
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

            string? imageFolder = null;
            try { imageFolder = _profileService.ActiveProfile?.ImageFileSettings?.FilePath; }
            catch { /* profile access is best-effort */ }

            var job = new UploadJob {
                LocalPath     = fileInfo.FullName,
                RelativePath  = RelativePath.Of(fileInfo.FullName, imageFolder),
                Sha256        = sha256,
                CaptureLocal  = captureLocal,
                FrameClass    = frameClass,
                EnqueuedAtUtc = DateTime.UtcNow,
                NotBeforeUtc  = DateTime.UtcNow,
            };
            await _queue.EnqueueAsync(job).ConfigureAwait(false);
            Logger.Trace($"Focale: enqueued {frameClass} upload for {fileInfo.Name} (night {NightDate.Format(NightDate.FromLocal(captureLocal))})");
        } catch (Exception ex) {
            Logger.Error("Focale: failed to enqueue upload", ex);
        }
    }
}
