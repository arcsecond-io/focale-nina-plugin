using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>Wire shape for <c>POST /observingsequences/</c>.</summary>
public sealed class ObservingSequenceCreate {
    [JsonProperty("name")]    public string  Name    { get; set; } = "";
    [JsonProperty("night")]   public string  Night   { get; set; } = "";   // ISO yyyy-MM-dd (Focale night-date)
    [JsonProperty("status")]  public string  Status  { get; set; } = "pending";
}

public sealed class ObservingSequenceResponse {
    [JsonProperty("id")]   public long?   Id   { get; set; }
    [JsonProperty("name")] public string? Name { get; set; }
}

/// <summary>Used to transition status — e.g. running → completed/aborted.</summary>
public sealed class ObservingSequencePatch {
    [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)]
    public string? Status { get; set; }
}

/// <summary>
/// Wire shape for <c>POST /observingsequencesteps/</c> — one per target container in NINA.
///
/// We do NOT carry target name / RA / Dec / PA here. That data belongs on the per-frame
/// <see cref="ObservationCreate"/> rows (which already FK to <c>XTarget</c>) and on
/// <c>Observation.position_angle</c>. Duplicating it on the step would be a second
/// source of truth.
/// </summary>
public sealed class SequenceStepCreate {
    [JsonProperty("sequence")]       public long   Sequence       { get; set; }
    [JsonProperty("name")]           public string Name           { get; set; } = "";
    [JsonProperty("description")]    public string? Description   { get; set; }
    [JsonProperty("index")]          public int    Index          { get; set; }
    [JsonProperty("task_mode")]      public string TaskMode       { get; set; } = "sequential";
    [JsonProperty("classification")] public string Classification { get; set; } = "observation";
}

public sealed class SequenceStepResponse {
    [JsonProperty("id")]   public long?   Id   { get; set; }
    [JsonProperty("name")] public string? Name { get; set; }
}

/// <summary>Wire shape for <c>POST /observingsequencetasks/</c> — one per NINA sequence instruction.</summary>
public sealed class SequenceTaskCreate {
    [JsonProperty("step")]        public long    Step        { get; set; }
    [JsonProperty("name")]        public string  Name        { get; set; } = "";
    [JsonProperty("index")]       public int     Index       { get; set; }
    [JsonProperty("task_name")]   public string  TaskName    { get; set; } = "";
    [JsonProperty("task_kwargs")] public JObject TaskKwargs  { get; set; } = new();
}
