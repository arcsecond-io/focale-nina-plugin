using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Focale.NINA.Plugin.Api.Models;

/// <summary>Wire shape of one event for <c>POST /nightlogs/&lt;uuid&gt;/events/</c> (observingruns NightEvent).</summary>
public sealed class NightEventPayload {
    [JsonProperty("client_id")] public string    ClientId { get; set; } = "";
    [JsonProperty("kind")]      public string    Kind     { get; set; } = "";
    [JsonProperty("start")]     public DateTime  Start    { get; set; }
    [JsonProperty("end")]       public DateTime? End      { get; set; }
    [JsonProperty("success")]   public bool?     Success  { get; set; }
    [JsonProperty("data")]      public JObject   Data     { get; set; } = new();
}
