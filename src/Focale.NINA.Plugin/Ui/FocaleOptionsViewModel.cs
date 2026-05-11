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

public sealed partial class FocaleOptionsViewModel : ObservableObject {
    private readonly FocaleSettings _settings;
    private readonly FocaleApiClient _api;
    private readonly UploadQueue _queue;

    [ObservableProperty] private string serverUrl              = FocaleSettings.DefaultServerUrl;
    [ObservableProperty] private bool   enableFitsUpload       = true;
    [ObservableProperty] private bool   enableMetadataSync     = true;

    // Sign-in fields. Both are PasswordBox-bound via code-behind for the password.
    [ObservableProperty] private string usernameEntry          = "";
    [ObservableProperty] private string passwordEntry          = "";   // cleared after sign-in
    [ObservableProperty] private bool   isSignedIn;
    [ObservableProperty] private string signedInAs             = "";
    [ObservableProperty] private string authStatus             = "";

    [ObservableProperty] private int    pendingCount;
    [ObservableProperty] private int    deadCount;
    [ObservableProperty] private string lastQueueRefreshLabel  = "";

    public FocaleOptionsViewModel(FocaleSettings settings, FocaleApiClient api, UploadQueue queue) {
        _settings          = settings;
        _api               = api;
        _queue             = queue;
        ServerUrl          = _settings.ServerUrl;
        EnableFitsUpload   = _settings.EnableFitsUpload;
        EnableMetadataSync = _settings.EnableMetadataSync;
        IsSignedIn         = _settings.HasRefreshToken();
        SignedInAs         = _settings.Username ?? "";
    }

    partial void OnServerUrlChanged(string value)        => _settings.ServerUrl          = value?.Trim() ?? FocaleSettings.DefaultServerUrl;
    partial void OnEnableFitsUploadChanged(bool value)   => _settings.EnableFitsUpload   = value;
    partial void OnEnableMetadataSyncChanged(bool value) => _settings.EnableMetadataSync = value;

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
        } catch (FocaleApiException ex) {
            PasswordEntry = "";
            AuthStatus    = $"Sign-in failed: {(int?)ex.StatusCode} {ex.Message}";
            Logger.Warning("Focale: sign-in failed", ex);
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
}
