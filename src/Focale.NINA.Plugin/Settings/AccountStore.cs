using System;
using System.IO;
using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Settings;

/// <summary>
/// The Focale account the plugin is signed in with: server URL, username and the refresh token
/// (DPAPI-encrypted). Kept in one file under the plugin's data folder, not in a NINA profile, so
/// switching NINA profiles keeps the user signed in. Writes are atomic (temp file + replace).
/// </summary>
public sealed class AccountStore {
    private readonly string _path;
    private readonly object _lock = new();
    private Data _data;

    public AccountStore(string path) {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _data = Load(path);
    }

    /// <summary>True when the file exists: the account has been written at least once.</summary>
    public bool Exists => File.Exists(_path);

    public string? ServerUrl {
        get { lock (_lock) return _data.ServerUrl; }
        set => Update(d => d.ServerUrl = value);
    }

    public string? Username {
        get { lock (_lock) return _data.Username; }
        set => Update(d => d.Username = value);
    }

    /// <summary>The refresh token as stored: DPAPI-protected, base64.</summary>
    public string? ProtectedRefreshToken {
        get { lock (_lock) return _data.RefreshTokenProtected; }
        set => Update(d => d.RefreshTokenProtected = value);
    }

    private void Update(Action<Data> change) {
        lock (_lock) {
            change(_data);
            Save();
        }
    }

    private void Save() {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(_data, Formatting.Indented));
        File.Move(tmp, _path, overwrite: true);
    }

    private static Data Load(string path) {
        try {
            if (File.Exists(path)) {
                return JsonConvert.DeserializeObject<Data>(File.ReadAllText(path)) ?? new Data();
            }
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            // A damaged file signs the user out rather than breaking the plugin.
        }
        return new Data();
    }

    private sealed class Data {
        [JsonProperty("server_url")]              public string? ServerUrl             { get; set; }
        [JsonProperty("username")]                public string? Username              { get; set; }
        [JsonProperty("refresh_token_protected")] public string? RefreshTokenProtected { get; set; }
    }
}
