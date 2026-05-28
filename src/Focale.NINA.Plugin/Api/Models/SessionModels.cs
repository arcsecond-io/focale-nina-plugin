using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

public sealed class NightLogResponse {
    [JsonProperty("uuid")] public string? Uuid { get; set; }
    [JsonProperty("name")] public string? Name { get; set; }
    [JsonProperty("date")] public string? Date { get; set; }
}

/// <summary>
/// Body for <c>POST /observingruns/nightlogs/</c>. Used by SequenceSyncService
/// so the night row exists before any SequenceStep/SequenceTask references its
/// date. Frame upload no longer goes through this — the auto-ingest endpoint
/// creates the NightLog itself from the FITS header.
/// </summary>
public sealed class NightLogCreate {
    [JsonProperty("name")]   public string Name   { get; set; } = "";
    [JsonProperty("date")]   public string Date   { get; set; } = "";  // ISO yyyy-MM-dd of the night-date
    [JsonProperty("source")] public string Source { get; set; } = "observer_console";
}
