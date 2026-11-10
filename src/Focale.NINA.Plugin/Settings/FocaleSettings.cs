using System;
using NINA.Profile.Interfaces;

namespace Focale.NINA.Plugin.Settings;

/// <summary>
/// The plugin's configuration. What belongs to the Focale account (server URL, username, refresh
/// token) lives in the <see cref="AccountStore"/>, shared by every NINA profile. What belongs to the
/// rig (storage mode and destination, features, the Tonight template) lives in the <em>active</em>
/// NINA profile's plugin settings, read at each access so a profile switch is followed.
///
/// Authentication model: the plugin obtains a JWT access/refresh pair from the Focale API's
/// /auth/token/ endpoint with client_type = "nina-plugin". The refresh token is the long-lived
/// secret and is stored DPAPI-encrypted at rest. The access token is short-lived and kept
/// only in memory. Both are revocable server-side per (user, client_type) so revoking the
/// plugin does not invalidate the user's web/desktop sessions.
/// </summary>
public sealed class FocaleSettings {
    private const string KeyRefreshTokenProtected = "Auth.RefreshToken.Protected";
    private const string KeyUsername              = "Auth.Username";
    private const string KeyServerUrl             = "Server.Url";
    private const string KeyEnableUpload          = "Feature.EnableFitsUpload";
    private const string KeyEnableNightEvents     = "Feature.EnableNightEvents";
    private const string KeyStorageMode           = "Upload.StorageMode";
    private const string KeyDestinationUuid       = "Upload.DestinationStorage.Uuid";
    private const string KeyDestinationName       = "Upload.DestinationStorage.Name";
    private const string KeyTonightTemplate       = "Tonight.TemplateName";

    public const string DefaultServerUrl = "https://api.focale.space";

    /// <summary>Sent in every request as <c>X-Focale-Client</c> header and on login.</summary>
    public const string ClientType = "nina-plugin";

    private readonly Guid _pluginId;
    private readonly Func<IPluginSettings> _profileStore;
    private readonly AccountStore _account;

    /// <param name="profileStore">The active NINA profile's plugin settings, asked for at each access.</param>
    public FocaleSettings(Guid pluginId, Func<IPluginSettings> profileStore, AccountStore account) {
        _pluginId     = pluginId;
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _account      = account      ?? throw new ArgumentNullException(nameof(account));
        MigrateAccountFromProfile();
    }

    private IPluginSettings Store => _profileStore();

    /// <summary>
    /// Earlier builds kept the account in the NINA profile. Move it to the account store once,
    /// so an existing sign-in survives the update, and clear it from the profile.
    /// </summary>
    private void MigrateAccountFromProfile() {
        if (_account.Exists) return;
        try {
            var store = Store;
            if (store.TryGetValue(_pluginId, KeyServerUrl, out string url) && !string.IsNullOrWhiteSpace(url)) {
                _account.ServerUrl = url;
            }
            if (store.TryGetValue(_pluginId, KeyUsername, out string user) && !string.IsNullOrEmpty(user)) {
                _account.Username = user;
            }
            if (store.TryGetValue(_pluginId, KeyRefreshTokenProtected, out string token) && !string.IsNullOrEmpty(token)) {
                _account.ProtectedRefreshToken = token;
                store.SetValue(_pluginId, KeyRefreshTokenProtected, string.Empty);
            }
        } catch {
            // Best effort: the worst case is signing in again.
        }
    }

    /// <summary>The Focale API. Only changed for a self-hosted Focale; shared by every NINA profile.</summary>
    public string ServerUrl {
        get => string.IsNullOrWhiteSpace(_account.ServerUrl) ? DefaultServerUrl : _account.ServerUrl!;
        set => _account.ServerUrl = string.IsNullOrWhiteSpace(value) || value.Trim() == DefaultServerUrl ? null : value.Trim();
    }

    public bool EnableFitsUpload {
        get => !Store.TryGetValue(_pluginId, KeyEnableUpload, out bool v) || v;  // default true
        set => Store.SetValue(_pluginId, KeyEnableUpload, value);
    }

    /// <summary>Send what happens during the night (autofocus, flips, guiding RMS, …) to the night's log.</summary>
    public bool EnableNightEvents {
        get => !Store.TryGetValue(_pluginId, KeyEnableNightEvents, out bool v) || v;  // default true
        set => Store.SetValue(_pluginId, KeyEnableNightEvents, value);
    }

    /// <summary>
    /// Where uploaded frames' pixels go. <see cref="StorageMode.Unset"/> until the user picks one:
    /// nothing is uploaded before that (frames wait in the queue).
    /// </summary>
    public StorageMode StorageMode {
        get => Store.TryGetValue(_pluginId, KeyStorageMode, out int v) && Enum.IsDefined(typeof(StorageMode), v)
            ? (StorageMode)v
            : StorageMode.Unset;
        set => Store.SetValue(_pluginId, KeyStorageMode, (int)value);
    }

    /// <summary>The user's own storage (a Focale DataStorage uuid) for <see cref="StorageMode.OwnStorage"/>.</summary>
    public string? DestinationStorageUuid {
        get => Store.TryGetValue(_pluginId, KeyDestinationUuid, out string v) && !string.IsNullOrEmpty(v) ? v : null;
        set => Store.SetValue(_pluginId, KeyDestinationUuid, value ?? string.Empty);
    }

    public string? DestinationStorageName {
        get => Store.TryGetValue(_pluginId, KeyDestinationName, out string v) && !string.IsNullOrEmpty(v) ? v : null;
        set => Store.SetValue(_pluginId, KeyDestinationName, value ?? string.Empty);
    }

    /// <summary>The NINA target template the Focale Tonight panel builds targets from (by name).</summary>
    public string? TonightTemplateName {
        get => Store.TryGetValue(_pluginId, KeyTonightTemplate, out string v) && !string.IsNullOrEmpty(v) ? v : null;
        set => Store.SetValue(_pluginId, KeyTonightTemplate, value ?? string.Empty);
    }

    public string? Username {
        get => string.IsNullOrEmpty(_account.Username) ? null : _account.Username;
        set => _account.Username = string.IsNullOrEmpty(value) ? null : value;
    }

    public string? GetRefreshToken() => SecretProtector.Unprotect(_account.ProtectedRefreshToken);

    public void SetRefreshToken(string? plaintext) {
        _account.ProtectedRefreshToken = string.IsNullOrEmpty(plaintext) ? null : SecretProtector.Protect(plaintext);
    }

    public bool HasRefreshToken() => !string.IsNullOrEmpty(GetRefreshToken());
}

public enum StorageMode {
    /// <summary>Not chosen yet: nothing is uploaded.</summary>
    Unset = 0,
    /// <summary>Focale indexes the frame and keeps its pixels in Focale's cloud storage (counts against the plan's storage).</summary>
    FocaleStorage = 1,
    /// <summary>
    /// Focale indexes the frame and copies it to one of the user's own storages (S3, SFTP, SMB, FTP,
    /// Dropbox), as Focale/&lt;its path below NINA's image folder&gt; under the storage's root.
    /// </summary>
    OwnStorage = 2,
    /// <summary>Focale reads the frame and computes its metrics, then discards the pixels. The file stays on this machine only.</summary>
    IndexOnly = 3,
}
