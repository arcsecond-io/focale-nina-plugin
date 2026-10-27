using FluentAssertions;
using Focale.NINA.Plugin.Api;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class FocaleApiClientErrorBodyTests {
    [Test]
    public void SubscriptionGate_IsReadFromTheBody() {
        // entitlements.exceptions.SubscriptionRequired
        var (code, message) = FocaleApiClient.ParseErrorBody(
            """{"error": "subscription_required", "gate": "fits_indexing", "message": "Upgrade to index more files."}""");
        code.Should().Be("subscription_required");
        message.Should().Be("Upgrade to index more files.");
    }

    [Test]
    public void DrfDetail_IsUsedAsTheMessage() {
        var (code, message) = FocaleApiClient.ParseErrorBody("""{"detail": "Authentication credentials were not provided."}""");
        code.Should().BeNull();
        message.Should().Be("Authentication credentials were not provided.");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("<html>502 Bad Gateway</html>")]
    [TestCase("""["a list"]""")]
    [TestCase("""{"error": {"nested": true}}""")]
    public void AnythingElse_YieldsNothing(string? body) {
        var (code, message) = FocaleApiClient.ParseErrorBody(body);
        code.Should().BeNull();
        message.Should().BeNull();
    }
}
