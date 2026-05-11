using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

public sealed class NightLogResponse {
    [JsonProperty("uuid")] public string? Uuid { get; set; }
    [JsonProperty("name")] public string? Name { get; set; }
    [JsonProperty("date")] public string? Date { get; set; }
}

public sealed class DatasetResponse {
    [JsonProperty("uuid")] public string? Uuid { get; set; }
    [JsonProperty("name")] public string? Name { get; set; }
}

/// <summary>
/// Body for <c>POST /observingruns/nightlogs/</c>. Minimum fields the plugin sets;
/// optional FKs (telescope, organisation) are added by the user later in Focale's UI.
/// </summary>
public sealed class NightLogCreate {
    [JsonProperty("name")]   public string Name   { get; set; } = "";
    [JsonProperty("date")]   public string Date   { get; set; } = "";  // ISO yyyy-MM-dd of the night-date
    [JsonProperty("source")] public string Source { get; set; } = "observer_console";
}

public sealed class DatasetCreate {
    [JsonProperty("name")]        public string  Name        { get; set; } = "";
    [JsonProperty("description")] public string? Description { get; set; }
    [JsonProperty("is_public")]   public bool    IsPublic    { get; set; }
}
