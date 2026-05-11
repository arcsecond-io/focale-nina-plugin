using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>Minimal subset of the XTarget shape returned by <c>GET/POST /targets/</c>.</summary>
public sealed class XTargetResponse {
    [JsonProperty("pk")]           public long?   Pk           { get; set; }
    [JsonProperty("id")]           public long?   Id           { get; set; }
    [JsonProperty("name")]         public string? Name         { get; set; }
    [JsonProperty("identifier")]   public string? Identifier   { get; set; }
    [JsonProperty("target_class")] public string? TargetClass  { get; set; }
}

/// <summary>
/// Body for <c>POST /targets/</c> in "auto" mode — the server resolves an
/// <c>AstronomicalObject</c> from the name (NGC, Messier, …) and links it to the new XTarget.
/// For uncatalogued targets the back-end falls back to manual mode; the plugin doesn't push
/// coordinates explicitly because NINA's framing assistant already resolved a known object.
/// </summary>
public sealed class XTargetCreate {
    [JsonProperty("name")]         public string Name         { get; set; } = "";
    [JsonProperty("identifier")]   public string Identifier   { get; set; } = "";
    [JsonProperty("target_class")] public string TargetClass  { get; set; } = "AstronomicalObject";
    [JsonProperty("mode")]         public string Mode         { get; set; } = "auto";
}

/// <summary>Paginated-or-flat list of XTarget — accepts either shape.</summary>
public sealed class XTargetListResponse {
    [JsonProperty("results")] public System.Collections.Generic.List<XTargetResponse>? Results { get; set; }
    [JsonProperty("count")]   public int? Count { get; set; }
}
