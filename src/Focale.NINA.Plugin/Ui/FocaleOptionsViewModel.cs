using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Events;
using Focale.NINA.Plugin.Settings;
using Focale.NINA.Plugin.Upload;
using NINA.Core.Utility;

namespace Focale.NINA.Plugin.Ui;

public sealed partial class FocaleOptionsViewModel : ObservableObject, IDisposable {
    private readonly FocaleSettings _settings;
    private readonly FocaleApiClient _api;
    private readonly UploadQueue _queue;
    private readonly UploadWorker _worker;
    private readonly NightEventSender _eventSender;

    [ObservableProperty] private string serverUrl              = FocaleSettings.DefaultServerUrl;
    [ObservableProperty] private bool   enableFitsUpload       = true;
    [ObservableProperty] private bool   enableNightEvents      = true;

    // Storage mode: none is selected until the user picks one (nothing is uploaded before).
    [ObservableProperty] private bool   isFocaleStorage;
    [ObservableProperty] private bool   isOwnStorage;
    [ObservableProperty] private bool   isIndexOnly;
    [ObservableProperty] private DataStorageResponse? selectedStorage;
    [ObservableProperty] private string storagesStatus         = "";
    public ObservableCollection<DataStorageResponse> Storages { get; } = new();

    // Sign-in fields. Both are PasswordBox-bound via code-behind for the password.
    [ObservableProperty] private string usernameEntry          = "";
    [ObservableProperty] private string passwordEntry          = "";   // cleared after sign-in
    [ObservableProperty] private bool   isSignedIn;
    [ObservableProperty] private string signedInAs             = "";
    [ObservableProperty] private string authStatus             = "";

    // The user's Focale plan, read after sign-in.
    [ObservableProperty] private string planStatus             = "";
    [ObservableProperty] private bool   canUpgrade;

    [ObservableProperty] private string uploadStatus           = "";
    [ObservableProperty] private bool   isUploadPaused;
    [ObservableProperty] private int    pendingCount;
    [ObservableProperty] private int    deadCount;
    [ObservableProperty] private string lastQueueRefreshLabel  = "";

    public FocaleOptionsViewModel(FocaleSettings settings, FocaleApiClient api, UploadQueue queue, UploadWorker worker,
                                  NightEventSender eventSender) {
        _settings          = settings;
        _api               = api;
        _queue             = queue;
        _worker            = worker;
        _eventSender       = eventSender;
        ServerUrl          = _settings.ServerUrl;
        LoadProfileSettings();
        UpdateAccount();
        UpdateUploadStatus();
        _worker.StateChanged += OnWorkerStateChanged;
        _api.AccountChanged  += OnAccountChanged;
    }

    /// <summary>The plugin's version, shown on the settings page.</summary>
    public string VersionLabel => $"Version {FocaleApiClient.Version}";

    /// <summary>Called when the user switches NINA profile: its storage choice and features apply now.</summary>
    public void ReloadProfileSettings() => RunOnUi(LoadProfileSettings);

    private bool _loading;

    private void LoadProfileSettings() {
        _loading = true;
        try {
            EnableFitsUpload  = _settings.EnableFitsUpload;
            EnableNightEvents = _settings.EnableNightEvents;
            Storages.Clear();
            SelectedStorage = null;
            if (_settings.DestinationStorageUuid is { } uuid) {
                // Shown before the list is loaded, so the current choice is visible.
                var current = new DataStorageResponse { Uuid = uuid, Name = _settings.DestinationStorageName };
                Storages.Add(current);
                SelectedStorage = current;
            }
            // Mirrors the saved mode; none checked while it is Unset.
            IsFocaleStorage = _settings.StorageMode == StorageMode.FocaleStorage;
            IsOwnStorage    = _settings.StorageMode == StorageMode.OwnStorage;
            IsIndexOnly     = _settings.StorageMode == StorageMode.IndexOnly;
        } finally {
            _loading = false;
        }
        UpdateUploadStatus();
    }

    private void OnAccountChanged(object? sender, EventArgs e) => RunOnUi(UpdateAccount);

    private void UpdateAccount() {
        IsSignedIn = _settings.HasRefreshToken();
        SignedInAs = _settings.Username ?? "";
        var plan = _api.Plan;
        PlanStatus = !IsSignedIn   ? ""
                   : plan is null  ? "Reading your Focale plan…"
                   : plan.Describe();
        CanUpgrade = IsSignedIn && plan is { IncludesNinaPlans: false };
    }

    private static void RunOnUi(Action action) {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    partial void OnServerUrlChanged(string value) => _settings.ServerUrl = value?.Trim() ?? FocaleSettings.DefaultServerUrl;
    partial void OnEnableFitsUploadChanged(bool value) {
        if (_loading) return;
        _settings.EnableFitsUpload = value;
        _worker.Wake();
    }

    partial void OnEnableNightEventsChanged(bool value) {
        if (_loading) return;
        _settings.EnableNightEvents = value;
        _eventSender.Wake();
    }

    partial void OnIsFocaleStorageChanged(bool value) { if (value && !_loading) SetStorageMode(StorageMode.FocaleStorage); }
    partial void OnIsIndexOnlyChanged(bool value)     { if (value && !_loading) SetStorageMode(StorageMode.IndexOnly); }
    partial void OnIsOwnStorageChanged(bool value) {
        if (!value || _loading) return;
        SetStorageMode(StorageMode.OwnStorage);
        if (Storages.Count <= 1) _ = LoadStoragesAsync();
    }

    partial void OnSelectedStorageChanged(DataStorageResponse? value) {
        if (_loading) return;
        _settings.DestinationStorageUuid = value?.Uuid;
        _settings.DestinationStorageName = value?.Name;
        _worker.Resume();   // a refused storage paused the queue: try the new one now
    }

    private void SetStorageMode(StorageMode mode) {
        _settings.StorageMode = mode;
        _worker.Resume();
        UpdateUploadStatus();
    }

    private void OnWorkerStateChanged(object? sender, EventArgs e) => RunOnUi(() => {
        UpdateUploadStatus();
        _ = RefreshQueueAsync();
    });

    private void UpdateUploadStatus() {
        IsUploadPaused = _worker.State is UploadWorkerState.Paused or UploadWorkerState.NeedsUpgrade;
        UploadStatus = _worker.State switch {
            UploadWorkerState.Disabled  => "Automatic upload is off.",
            UploadWorkerState.SignedOut => _worker.PauseMessage ?? "Signed out — frames wait in the queue.",
            UploadWorkerState.AwaitingStorageChoice => _worker.PauseMessage ?? "Choose where your frames go — nothing is uploaded until you do.",
            UploadWorkerState.NeedsUpgrade => _worker.PauseMessage ?? "Your Focale plan does not include NINA → Focale sync.",
            UploadWorkerState.Paused    => $"Paused: {_worker.PauseMessage} Retrying at {_worker.PausedUntilUtc?.ToLocalTime():HH:mm}.",
            _                           => "Syncing: every saved frame goes to Focale.",
        };
    }

    [RelayCommand]
    private async Task SignInAsync() {
        if (string.IsNullOrWhiteSpace(UsernameEntry) || string.IsNullOrWhiteSpace(PasswordEntry)) {
            AuthStatus = "Enter username and password.";
            return;
        }
        AuthStatus = "Signing in…";
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api.LoginAsync(UsernameEntry.Trim(), PasswordEntry, cts.Token).ConfigureAwait(true);
            // Drop plaintext password from VM state immediately.
            PasswordEntry = "";
            UpdateAccount();
            AuthStatus    = "";
            await RefreshPlanAsync().ConfigureAwait(true);
            // Frames queued while signed out go up now, not at the next poll.
            _worker.Resume();
            _eventSender.Resume();
        } catch (FocaleApiException ex) {
            PasswordEntry = "";
            AuthStatus    = ex.ServerMessage ?? $"Sign-in failed ({(int?)ex.StatusCode}).";
            Logger.Warning($"Focale: sign-in failed — {ex.Message}");
        } catch (Exception ex) when (UploadWorker.IsNetworkFailure(ex)) {
            PasswordEntry = "";
            AuthStatus    = "Cannot reach Focale. Check the internet connection, then try again.";
            Logger.Warning($"Focale: sign-in failed — {ex.Message}");
        } catch (Exception ex) {
            PasswordEntry = "";
            AuthStatus    = $"Sign-in failed: {ex.Message}";
            Logger.Error("Focale: sign-in error", ex);
        }
    }

    /// <summary>Reads the plan again — after an upgrade on focale.space, say.</summary>
    [RelayCommand]
    private async Task RefreshPlanAsync() {
        if (!_api.IsAuthenticated) return;
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api.RefreshPlanAsync(cts.Token).ConfigureAwait(true);
            if (_api.Plan is null) PlanStatus = "Could not tell which Focale plan you are on.";
        } catch (Exception ex) {
            PlanStatus = "Could not read your Focale plan. It is read again before the next upload.";
            Logger.Warning($"Focale: reading the plan failed — {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenUpgrade() => OpenUrl(FocalePlan.UpgradeUrl);

    [RelayCommand]
    private void OpenFocale() => OpenUrl("https://focale.space");

    private static void OpenUrl(string url) {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warning($"Focale: could not open {url} — {ex.Message}"); }
    }

    [RelayCommand]
    private async Task SignOutAsync() {
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _api.LogoutAsync(cts.Token).ConfigureAwait(false);
        } finally {
            UpdateAccount();
            AuthStatus = "";
            _worker.Wake();
        }
    }

    /// <summary>Lifts a plan/quota pause right away — e.g. after upgrading on focale.space — reading the plan again first.</summary>
    [RelayCommand]
    private async Task ResumeUploadsAsync() {
        await RefreshPlanAsync().ConfigureAwait(true);
        _worker.Resume();
        _eventSender.Resume();
    }

    /// <summary>The user's storages Focale can upload to (S3, SFTP, SMB, FTP, and Dropbox connected with "Connect Dropbox").</summary>
    [RelayCommand]
    private async Task LoadStoragesAsync() {
        if (!_api.IsAuthenticated) {
            StoragesStatus = "Sign in first.";
            return;
        }
        StoragesStatus = "Loading…";
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var storages = await _api.ListUploadDestinationsAsync(cts.Token).ConfigureAwait(true);
            var keep = _settings.DestinationStorageUuid;
            Storages.Clear();
            foreach (var st in storages) Storages.Add(st);
            SelectedStorage = Storages.FirstOrDefault(st => st.Uuid == keep);
            StoragesStatus = storages.Count == 0
                ? "No storage Focale can upload to. Add one on focale.space (S3, SFTP, SMB, FTP, or Connect Dropbox)."
                : "";
        } catch (Exception ex) {
            StoragesStatus = $"Could not load your storages: {ex.Message}";
            Logger.Warning($"Focale: loading storages failed — {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshQueueAsync() {
        try {
            PendingCount         = await _queue.CountAsync(UploadJobStatus.Pending).ConfigureAwait(false);
            DeadCount            = await _queue.CountAsync(UploadJobStatus.Dead).ConfigureAwait(false);
            LastQueueRefreshLabel = $"Refreshed {DateTime.Now:HH:mm:ss}";
        } catch (Exception ex) {
            LastQueueRefreshLabel = "Refresh failed";
            Logger.Error("Focale: RefreshQueue failed", ex);
        }
    }

    public void Dispose() {
        _worker.StateChanged -= OnWorkerStateChanged;
        _api.AccountChanged  -= OnAccountChanged;
    }
}
