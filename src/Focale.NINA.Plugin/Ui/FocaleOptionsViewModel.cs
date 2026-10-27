using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Settings;
using Focale.NINA.Plugin.Upload;
using NINA.Core.Utility;

namespace Focale.NINA.Plugin.Ui;

public sealed partial class FocaleOptionsViewModel : ObservableObject, IDisposable {
    private readonly FocaleSettings _settings;
    private readonly FocaleApiClient _api;
    private readonly UploadQueue _queue;
    private readonly UploadWorker _worker;

    [ObservableProperty] private string serverUrl              = FocaleSettings.DefaultServerUrl;
    [ObservableProperty] private bool   enableFitsUpload       = true;

    // Sign-in fields. Both are PasswordBox-bound via code-behind for the password.
    [ObservableProperty] private string usernameEntry          = "";
    [ObservableProperty] private string passwordEntry          = "";   // cleared after sign-in
    [ObservableProperty] private bool   isSignedIn;
    [ObservableProperty] private string signedInAs             = "";
    [ObservableProperty] private string authStatus             = "";

    [ObservableProperty] private string uploadStatus           = "";
    [ObservableProperty] private bool   isUploadPaused;
    [ObservableProperty] private int    pendingCount;
    [ObservableProperty] private int    deadCount;
    [ObservableProperty] private string lastQueueRefreshLabel  = "";

    public FocaleOptionsViewModel(FocaleSettings settings, FocaleApiClient api, UploadQueue queue, UploadWorker worker) {
        _settings          = settings;
        _api               = api;
        _queue             = queue;
        _worker            = worker;
        ServerUrl          = _settings.ServerUrl;
        EnableFitsUpload   = _settings.EnableFitsUpload;
        IsSignedIn         = _settings.HasRefreshToken();
        SignedInAs         = _settings.Username ?? "";
        UpdateUploadStatus();
        _worker.StateChanged += OnWorkerStateChanged;
    }

    partial void OnServerUrlChanged(string value)        => _settings.ServerUrl          = value?.Trim() ?? FocaleSettings.DefaultServerUrl;
    partial void OnEnableFitsUploadChanged(bool value) {
        _settings.EnableFitsUpload = value;
        _worker.Wake();
    }

    private void OnWorkerStateChanged(object? sender, EventArgs e) {
        UpdateUploadStatus();
        // A refresh token rejected in the background signs the user out: reflect it.
        IsSignedIn = _settings.HasRefreshToken();
        _ = RefreshQueueAsync();
    }

    private void UpdateUploadStatus() {
        IsUploadPaused = _worker.State == UploadWorkerState.Paused;
        UploadStatus = _worker.State switch {
            UploadWorkerState.Disabled  => "Automatic upload is off.",
            UploadWorkerState.SignedOut => _worker.PauseMessage ?? "Signed out — frames wait in the queue.",
            UploadWorkerState.Paused    => $"Paused: {_worker.PauseMessage} Retrying at {_worker.PausedUntilUtc?.ToLocalTime():HH:mm}.",
            _                           => "Uploading.",
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
            var pair = await _api.LoginAsync(UsernameEntry.Trim(), PasswordEntry, cts.Token).ConfigureAwait(false);
            // Drop plaintext password from VM state immediately.
            PasswordEntry = "";
            IsSignedIn    = _settings.HasRefreshToken();
            SignedInAs    = _settings.Username ?? UsernameEntry.Trim();
            AuthStatus    = $"Signed in as {SignedInAs}";
            // Frames queued while signed out go up now, not at the next poll.
            _worker.Wake();
        } catch (FocaleApiException ex) {
            PasswordEntry = "";
            AuthStatus    = $"Sign-in failed: {(int?)ex.StatusCode} {ex.Message}";
            Logger.Warning($"Focale: sign-in failed — {ex.Message}");
        } catch (Exception ex) {
            PasswordEntry = "";
            AuthStatus    = $"Sign-in failed: {ex.Message}";
            Logger.Error("Focale: sign-in error", ex);
        }
    }

    [RelayCommand]
    private async Task SignOutAsync() {
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _api.LogoutAsync(cts.Token).ConfigureAwait(false);
        } finally {
            IsSignedIn = false;
            SignedInAs = "";
            AuthStatus = "Signed out.";
            _worker.Wake();
        }
    }

    /// <summary>Lifts a plan/quota pause right away — e.g. after upgrading on focale.space.</summary>
    [RelayCommand]
    private void ResumeUploads() => _worker.Resume();

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

    public void Dispose() => _worker.StateChanged -= OnWorkerStateChanged;
}
