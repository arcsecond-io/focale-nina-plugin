using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>
/// The fields of <c>GET /profiles/&lt;username&gt;/</c> (the owner's private profile) the plugin reads:
/// the Focale plan and its storage.
/// </summary>
public sealed class ProfileResponse {
    [JsonProperty("username")]       public string? Username      { get; set; }
    /// <summary><c>free</c>, <c>observer</c>, <c>imager</c> or <c>pro</c> (<c>pro</c> on a self-hosted Focale).</summary>
    [JsonProperty("focale_tier")]    public string? FocaleTier    { get; set; }
    [JsonProperty("focale_storage")] public FocaleStorageSummary? FocaleStorage { get; set; }
}

public sealed class FocaleStorageSummary {
    [JsonProperty("plan_gbytes")] public double? PlanGbytes { get; set; }
    [JsonProperty("packs")]       public int?    Packs      { get; set; }
    [JsonProperty("limit_bytes")] public long?   LimitBytes { get; set; }
}

/// <summary>A row of <c>GET /datafiles/?sha256=…</c>: a file the user's Focale already holds.</summary>
public sealed class KnownDataFile {
    [JsonProperty("sha256")]    public string? Sha256    { get; set; }
    /// <summary>Whether its bytes are kept (false for a frame filed in index-only mode).</summary>
    [JsonProperty("stored")]    public bool    Stored    { get; set; }
    [JsonProperty("ingestion")] public UploadResponse.IngestionInfo? Ingestion { get; set; }
}
