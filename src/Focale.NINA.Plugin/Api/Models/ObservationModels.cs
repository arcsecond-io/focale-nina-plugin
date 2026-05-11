using System;
using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>
/// Body for <c>POST /observingruns/nightlogs/&lt;uuid&gt;/observations/</c>.
/// One row per saved LIGHT (or unclassified) FITS frame.
///
/// The DataFile linkage is OneToOne and handled server-side via a separate upload step;
/// the plugin currently does not attach the data_file directly. Until the back-end accepts
/// <c>data_file = &lt;uuid&gt;</c> on this serializer, the join is implicit (same NightLog +
/// timestamp) and the user (or a server-side reconciler) can finalise it later.
/// </summary>
public sealed class ObservationCreate {
    [JsonProperty("name")]           public string  Name          { get; set; } = "";
    [JsonProperty("start")]          public DateTime Start        { get; set; }
    [JsonProperty("duration")]       public string  Duration      { get; set; } = "0:00:00";   // Django DurationField "HH:MM:SS[.ffffff]"
    [JsonProperty("target")]         public long?   Target        { get; set; }                 // XTarget pk
    [JsonProperty("position_angle")] public double? PositionAngle { get; set; }
}

public sealed class ObservationResponse {
    [JsonProperty("uuid")] public string? Uuid { get; set; }
}

/// <summary>Body for <c>POST /observingruns/nightlogs/&lt;uuid&gt;/calibrations/</c>.</summary>
public sealed class CalibrationCreate {
    [JsonProperty("name")]     public string  Name     { get; set; } = "";
    [JsonProperty("start")]    public DateTime Start   { get; set; }
    [JsonProperty("duration")] public string  Duration { get; set; } = "0:00:00";
    /// <summary>One of <c>Biases</c>, <c>Darks</c>, <c>Flats</c>.</summary>
    [JsonProperty("type")]     public string  Type     { get; set; } = "";
}

public sealed class CalibrationResponse {
    [JsonProperty("uuid")] public string? Uuid { get; set; }
}
