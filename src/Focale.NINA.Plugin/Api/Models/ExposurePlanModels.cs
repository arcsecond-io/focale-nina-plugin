using System.Collections.Generic;
using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>
/// <c>GET /nightplans/&lt;uuid&gt;/exposures/</c>: a night plan's entries, in order, with what to take of
/// each target (observingruns.views.exposure_plans). Written in Focale's Tonight; the plugin builds one
/// NINA target per entry from it.
/// </summary>
public sealed class ExposurePlanResponse {
    [JsonProperty("night_plan")] public string? NightPlan { get; set; }
    [JsonProperty("date")]       public string? Date      { get; set; }
    [JsonProperty("entries")]    public List<ExposurePlanEntry> Entries { get; set; } = new();
}

public sealed class ExposurePlanEntry {
    [JsonProperty("uuid")]             public string  Uuid            { get; set; } = "";
    [JsonProperty("index")]            public int     Index           { get; set; }
    [JsonProperty("name")]             public string? Name            { get; set; }
    [JsonProperty("start")]            public string? Start           { get; set; }
    [JsonProperty("duration_seconds")] public double  DurationSeconds { get; set; }
    [JsonProperty("status")]           public string? Status          { get; set; }
    [JsonProperty("position_angle")]   public double? PositionAngle   { get; set; }
    [JsonProperty("target")]           public PlanTarget? Target      { get; set; }
    [JsonProperty("lines")]            public List<ExposureLine> Lines { get; set; } = new();
}

public sealed class PlanTarget {
    [JsonProperty("id")]              public long    Id             { get; set; }
    [JsonProperty("name")]            public string  Name           { get; set; } = "";
    [JsonProperty("target_class")]    public string? TargetClass    { get; set; }
    /// <summary>J2000, degrees. Null for a target without fixed coordinates (a planet, a comet).</summary>
    [JsonProperty("right_ascension")] public double? RightAscension { get; set; }
    [JsonProperty("declination")]     public double? Declination    { get; set; }
}

public sealed class ExposureLine {
    [JsonProperty("filter")]           public string Filter          { get; set; } = "";
    [JsonProperty("exposure_seconds")] public double ExposureSeconds { get; set; }
    [JsonProperty("count")]            public int    Count           { get; set; }
    [JsonProperty("binning")]          public int    Binning         { get; set; } = 1;
}
