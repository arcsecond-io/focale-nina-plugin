using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>
/// What the plugin reads back from an auto-ingest upload: the night log the frame was filed
/// under (datasets.serializers.datafiles._serialize_ingestion_result), so the night's events
/// can be sent to the same log.
/// </summary>
public sealed class UploadResponse {
    [JsonProperty("ingestion")] public IngestionInfo? Ingestion { get; set; }

    public sealed class IngestionInfo {
        [JsonProperty("night_log")]      public NightLogRef? NightLog      { get; set; }
        [JsonProperty("file_persisted")] public bool?        FilePersisted { get; set; }
    }

    public sealed class NightLogRef {
        [JsonProperty("uuid")] public string? Uuid { get; set; }
        [JsonProperty("date")] public string? Date { get; set; }   // yyyy-MM-dd, the night's date
    }
}
