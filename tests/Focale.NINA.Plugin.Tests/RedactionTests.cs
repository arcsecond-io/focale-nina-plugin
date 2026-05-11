using FluentAssertions;
using Focale.NINA.Plugin.Sync;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class RedactionTests {
    private sealed class FakeProfile {
        public string Username { get; set; } = "alice";
        public string Password { get; set; } = "hunter2";
        public string ApiKey   { get; set; } = "sk-deadbeef";
        public string Token    { get; set; } = "abc.def.ghi";
        public string MyAuthSecret { get; set; } = "topsecret";
        public NestedSettings Plate { get; set; } = new();
        public string ServerUrl { get; set; } = "https://example.com/path";
        public string ConnString { get; set; } = "https://user:pass@example.com/api";
    }

    private sealed class NestedSettings {
        public string AstrometryAPIKey       { get; set; } = "abcdef";
        public string PinPointAllSkyApiKey   { get; set; } = "ghijkl";
        public int    Timeout                { get; set; } = 30;
    }

    [Test]
    public void IsSecretName_MatchesAllExpectedPatterns() {
        Redaction.IsSecretName("Password").Should().BeTrue();
        Redaction.IsSecretName("ApiKey").Should().BeTrue();
        Redaction.IsSecretName("api_key").Should().BeTrue();
        Redaction.IsSecretName("AccessToken").Should().BeTrue();
        Redaction.IsSecretName("secret").Should().BeTrue();
        Redaction.IsSecretName("Credential").Should().BeTrue();
        Redaction.IsSecretName("AuthHeader").Should().BeTrue();
        Redaction.IsSecretName("BearerJwt").Should().BeTrue();

        Redaction.IsSecretName("Username").Should().BeFalse();
        Redaction.IsSecretName("Timeout").Should().BeFalse();
        Redaction.IsSecretName("ServerUrl").Should().BeFalse();
    }

    [Test]
    public void Serialize_RedactsSecretFieldsAtAnyDepth() {
        var json = Redaction.ToRedactedJObject(new FakeProfile());

        ((string?)json["Password"]).Should().Be(Redaction.Marker);
        ((string?)json["ApiKey"]).Should().Be(Redaction.Marker);
        ((string?)json["Token"]).Should().Be(Redaction.Marker);
        ((string?)json["MyAuthSecret"]).Should().Be(Redaction.Marker);

        ((string?)json["Plate"]?["AstrometryAPIKey"]).Should().Be(Redaction.Marker);
        ((string?)json["Plate"]?["PinPointAllSkyApiKey"]).Should().Be(Redaction.Marker);

        ((string?)json["Username"]).Should().Be("alice");
        ((int?)json["Plate"]?["Timeout"]).Should().Be(30);
    }

    [Test]
    public void Serialize_StripsUserInfoFromUriStrings() {
        var json = Redaction.ToRedactedJObject(new FakeProfile());

        // Plain URL untouched.
        ((string?)json["ServerUrl"]).Should().Be("https://example.com/path");

        // user:pass@ stripped.
        var connStr = (string?)json["ConnString"];
        connStr.Should().NotContain("user:pass@");
        connStr.Should().StartWith("https://example.com");
    }

    [Test]
    public void StripUriCredentials_NonUri_ReturnsInputUnchanged() {
        Redaction.StripUriCredentials("not a uri").Should().Be("not a uri");
        Redaction.StripUriCredentials("").Should().Be("");
        Redaction.StripUriCredentials(null).Should().BeNull();
    }
}
