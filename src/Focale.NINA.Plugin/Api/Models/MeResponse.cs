using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>Minimal shape for the /me endpoint — used by the "Test connection" button.</summary>
public sealed class MeResponse {
    [JsonProperty("username")] public string? Username { get; set; }
    [JsonProperty("email")]    public string? Email    { get; set; }
}
