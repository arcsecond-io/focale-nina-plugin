using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>The subset of <c>GET /datastorages/</c> the plugin needs to offer upload destinations.</summary>
public sealed class DataStorageResponse {
    [JsonProperty("uuid")]         public string  Uuid       { get; set; } = "";
    [JsonProperty("name")]         public string? Name       { get; set; }
    [JsonProperty("type")]         public string? Type       { get; set; }
    [JsonProperty("is_read_only")] public bool    IsReadOnly { get; set; }

    /// <summary>
    /// Storage types the back-end can write uploads to (xstorages WRITABLE_STORAGE_TYPES):
    /// Dropbox and FTP storages are scanned, never written to.
    /// </summary>
    public static readonly string[] WritableTypes = { "aws_s3", "sftp", "smb", "disk_local" };

    public bool CanReceiveUploads => !IsReadOnly && System.Array.IndexOf(WritableTypes, Type) >= 0;

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Uuid : $"{Name} ({Type})";
}
