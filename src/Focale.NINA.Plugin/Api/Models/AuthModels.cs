using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Api.Models;

public sealed class LoginRequest {
    [JsonProperty("username")] public string Username { get; set; } = "";
    [JsonProperty("password")] public string Password { get; set; } = "";
    /// <summary>
    /// arcsecond-back accepts the client type either via this body field or the
    /// <c>X-Focale-Client</c> header. We send both for robustness.
    /// </summary>
    [JsonProperty("client")]   public string Client   { get; set; } = Settings.FocaleSettings.ClientType;
}

public sealed class TokenPairResponse {
    [JsonProperty("access")]      public string  Access      { get; set; } = "";
    [JsonProperty("access_exp")]  public long    AccessExp   { get; set; }   // unix seconds
    [JsonProperty("refresh")]     public string? Refresh     { get; set; }    // null when web-cookie mode
    [JsonProperty("refresh_exp")] public long    RefreshExp  { get; set; }
    [JsonProperty("token_type")]  public string? TokenType   { get; set; }
    [JsonProperty("expires_in")]  public long    ExpiresIn   { get; set; }   // seconds
    [JsonProperty("username")]    public string? Username    { get; set; }
}

public sealed class RefreshRequest {
    [JsonProperty("refresh")] public string Refresh { get; set; } = "";
    [JsonProperty("client")]  public string Client  { get; set; } = Settings.FocaleSettings.ClientType;
}
