using System;
using NINA.Profile.Interfaces;

namespace Focale.NINA.Plugin.Settings;

/// <summary>
/// Typed wrapper around <see cref="IPluginSettings"/> for this plugin's configuration.
/// Keys are namespaced under the plugin's Guid via PluginSettings storage.
///
/// Authentication model: the plugin obtains a JWT access/refresh pair from arcsecond-back's
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

    public const string DefaultServerUrl = "https://api.focale.space";

    /// <summary>Sent in every request as <c>X-Focale-Client</c> header and on login.</summary>
    public const string ClientType = "nina-plugin";

    private readonly Guid _pluginId;
    private readonly IPluginSettings _store;

    public FocaleSettings(Guid pluginId, IPluginSettings store) {
        _pluginId = pluginId;
        _store    = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string ServerUrl {
        get => _store.TryGetValue(_pluginId, KeyServerUrl, out string value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : DefaultServerUrl;
        set => _store.SetValue(_pluginId, KeyServerUrl, value ?? DefaultServerUrl);
    }

    public bool EnableFitsUpload {
        get => !_store.TryGetValue(_pluginId, KeyEnableUpload, out bool v) || v;  // default true
        set => _store.SetValue(_pluginId, KeyEnableUpload, value);
    }

    /// <summary>Send what happens during the night (autofocus, flips, guiding RMS, …) to the night's log.</summary>
    public bool EnableNightEvents {
        get => !_store.TryGetValue(_pluginId, KeyEnableNightEvents, out bool v) || v;  // default true
        set => _store.SetValue(_pluginId, KeyEnableNightEvents, value);
    }

    /// <summary>
    /// Where uploaded frames' pixels go. <see cref="StorageMode.Unset"/> until the user picks one:
    /// nothing is uploaded before that (frames wait in the queue).
    /// </summary>
    public StorageMode StorageMode {
        get => _store.TryGetValue(_pluginId, KeyStorageMode, out int v) && Enum.IsDefined(typeof(StorageMode), v)
            ? (StorageMode)v
            : StorageMode.Unset;
        set => _store.SetValue(_pluginId, KeyStorageMode, (int)value);
    }

    /// <summary>The user's own storage (a Focale DataStorage uuid) for <see cref="StorageMode.OwnStorage"/>.</summary>
    public string? DestinationStorageUuid {
        get => _store.TryGetValue(_pluginId, KeyDestinationUuid, out string v) && !string.IsNullOrEmpty(v) ? v : null;
        set => _store.SetValue(_pluginId, KeyDestinationUuid, value ?? string.Empty);
    }

    public string? DestinationStorageName {
        get => _store.TryGetValue(_pluginId, KeyDestinationName, out string v) && !string.IsNullOrEmpty(v) ? v : null;
        set => _store.SetValue(_pluginId, KeyDestinationName, value ?? string.Empty);
    }

    public string? Username {
        get => _store.TryGetValue(_pluginId, KeyUsername, out string v) && !string.IsNullOrEmpty(v) ? v : null;
        set => _store.SetValue(_pluginId, KeyUsername, value ?? string.Empty);
    }

    public string? GetRefreshToken() {
        if (_store.TryGetValue(_pluginId, KeyRefreshTokenProtected, out string protectedBase64)) {
            return SecretProtector.Unprotect(protectedBase64);
        }
        return null;
    }

    public void SetRefreshToken(string? plaintext) {
        if (string.IsNullOrEmpty(plaintext)) {
            _store.SetValue(_pluginId, KeyRefreshTokenProtected, string.Empty);
            return;
        }
        _store.SetValue(_pluginId, KeyRefreshTokenProtected, SecretProtector.Protect(plaintext));
    }

    public bool HasRefreshToken() => !string.IsNullOrEmpty(GetRefreshToken());
}

public enum StorageMode {
    /// <summary>Not chosen yet: nothing is uploaded.</summary>
    Unset = 0,
    /// <summary>Focale indexes the frame and keeps its pixels in Focale's cloud storage (counts against the plan's storage).</summary>
    FocaleStorage = 1,
    /// <summary>Focale indexes the frame and stores its pixels in one of the user's own storages (S3, SFTP, SMB).</summary>
    OwnStorage = 2,
    /// <summary>Focale indexes the frame and computes its metrics, then discards the pixels. The file stays on this machine only.</summary>
    IndexOnly = 3,
}
