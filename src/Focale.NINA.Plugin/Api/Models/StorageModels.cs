using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>The subset of <c>GET /datastorages/</c> the plugin needs to offer upload destinations.</summary>
public sealed class DataStorageResponse {
    [JsonProperty("uuid")]         public string  Uuid       { get; set; } = "";
    [JsonProperty("name")]         public string? Name       { get; set; }
    [JsonProperty("type")]         public string? Type       { get; set; }
    [JsonProperty("is_read_only")] public bool    IsReadOnly { get; set; }

    /// <summary>
    /// The back-end's own answer (DataStorage.can_receive_uploads): S3, SFTP, SMB and FTP storages,
    /// and Dropbox storages connected with "Connect Dropbox". Absent from older servers.
    /// </summary>
    [JsonProperty("can_receive_uploads")] public bool? CanReceiveUploadsFromServer { get; set; }

    /// <summary>What a server that does not say yet could write to.</summary>
    private static readonly string[] LegacyWritableTypes = { "aws_s3", "sftp", "smb", "disk_local" };

    public bool CanReceiveUploads =>
        CanReceiveUploadsFromServer ?? (!IsReadOnly && System.Array.IndexOf(LegacyWritableTypes, Type) >= 0);

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Uuid : $"{Name} ({Type})";
}
