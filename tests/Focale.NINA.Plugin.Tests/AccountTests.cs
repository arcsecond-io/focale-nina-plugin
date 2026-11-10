using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using FluentAssertions;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Settings;
using NINA.Profile.Interfaces;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class RefreshRejectionTests {
    // accounts.views.auth.TokenRefreshView answers 400 for an expired or revoked refresh token.
    [TestCase(HttpStatusCode.BadRequest, true)]
    [TestCase(HttpStatusCode.Unauthorized, true)]
    [TestCase(HttpStatusCode.Forbidden, true)]
    [TestCase(HttpStatusCode.TooManyRequests, false)]
    [TestCase(HttpStatusCode.InternalServerError, false)]
    public void Rejected_refresh_tokens_are_recognised(HttpStatusCode status, bool rejected) =>
        FocaleApiClient.IsRefreshTokenRejected(status).Should().Be(rejected);
}

[TestFixture]
public class LoginRefusalTests {
    [Test]
    public void Unverified_email_says_what_to_do() =>
        FocaleApiClient.LoginRefusalMessage(403, "email_not_verified").Should().Contain("not verified");

    [Test]
    public void Wrong_credentials() =>
        FocaleApiClient.LoginRefusalMessage(400, null).Should().Be("Wrong email, username or password.");

    [Test]
    public void Throttled() =>
        FocaleApiClient.LoginRefusalMessage(429, "Request was throttled.").Should().StartWith("Too many");

    [Test]
    public void Server_down() =>
        FocaleApiClient.LoginRefusalMessage(503, null).Should().Contain("not answering");

    [Test]
    public void Anything_else_shows_the_server_detail() =>
        FocaleApiClient.LoginRefusalMessage(403, "Account disabled.").Should().Be("Account disabled.");
}

[TestFixture]
public class FocalePlanTests {
    [TestCase("free", FocaleTier.Free)]
    [TestCase("Observer", FocaleTier.Observer)]
    [TestCase(" imager ", FocaleTier.Imager)]
    [TestCase("pro", FocaleTier.Pro)]
    public void Tiers_are_parsed(string value, FocaleTier tier) => FocalePlan.ParseTier(value).Should().Be(tier);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("enterprise")]
    public void Unknown_tiers_gate_nothing(string? value) => FocalePlan.ParseTier(value).Should().BeNull();

    [TestCase(FocaleTier.Free, false, false)]
    [TestCase(FocaleTier.Observer, true, false)]
    [TestCase(FocaleTier.Imager, true, true)]
    [TestCase(FocaleTier.Pro, true, true)]
    public void What_each_plan_includes(FocaleTier tier, bool sync, bool plans) {
        var plan = new FocalePlan(tier, null);
        plan.IncludesNinaSync.Should().Be(sync);
        plan.IncludesNinaPlans.Should().Be(plans);
    }

    [Test]
    public void Description_names_the_plan_and_its_storage() {
        var plan = new FocalePlan(FocaleTier.Observer, 50L * 1024 * 1024 * 1024);
        plan.Describe().Should().Be(
            "Focale Observer plan, 50 GB of Focale storage. NINA → Focale sync included; Focale → NINA plans need Imager.");
    }

    [Test]
    public void Description_of_free_says_what_is_missing() =>
        new FocalePlan(FocaleTier.Free, null).Describe().Should().Contain("needs Observer");
}

[TestFixture]
public class AccountStoreTests {
    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "focale-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void Values_survive_a_restart() {
        var path = Path.Combine(_dir, "account.json");
        var store = new AccountStore(path) { Username = "cedric", ServerUrl = "https://focale.example.org" };
        store.ProtectedRefreshToken = "cipher";

        var reloaded = new AccountStore(path);
        reloaded.Exists.Should().BeTrue();
        reloaded.Username.Should().Be("cedric");
        reloaded.ServerUrl.Should().Be("https://focale.example.org");
        reloaded.ProtectedRefreshToken.Should().Be("cipher");
    }

    [Test]
    public void A_damaged_file_reads_as_signed_out() {
        var path = Path.Combine(_dir, "account.json");
        File.WriteAllText(path, "{ not json");
        var store = new AccountStore(path);
        store.Username.Should().BeNull();
        store.ProtectedRefreshToken.Should().BeNull();
    }

    [Test]
    public void The_account_kept_in_a_profile_is_moved_to_the_store_once() {
        var id = Guid.NewGuid();
        var profile = FakePluginSettings.Create();
        profile.SetValue(id, "Auth.Username", "cedric");
        profile.SetValue(id, "Server.Url", "https://focale.example.org");
        profile.SetValue(id, "Auth.RefreshToken.Protected", "cipher");
        profile.SetValue(id, "Upload.StorageMode", 3);

        var account = new AccountStore(Path.Combine(_dir, "account.json"));
        var settings = new FocaleSettings(id, () => profile, account);

        settings.Username.Should().Be("cedric");
        settings.ServerUrl.Should().Be("https://focale.example.org");
        account.ProtectedRefreshToken.Should().Be("cipher");
        profile.TryGetValue(id, "Auth.RefreshToken.Protected", out string left).Should().BeTrue();
        left.Should().BeEmpty("the token must not stay in the profile");
        settings.StorageMode.Should().Be(StorageMode.IndexOnly, "rig settings stay in the profile");
    }

    [Test]
    public void Profile_settings_follow_the_active_profile() {
        var id = Guid.NewGuid();
        var first = FakePluginSettings.Create();
        var second = FakePluginSettings.Create();
        first.SetValue(id, "Upload.StorageMode", (int)StorageMode.FocaleStorage);
        var active = first;
        var settings = new FocaleSettings(id, () => active, new AccountStore(Path.Combine(_dir, "account.json")));

        settings.StorageMode.Should().Be(StorageMode.FocaleStorage);
        active = second;
        settings.StorageMode.Should().Be(StorageMode.Unset);
    }

    [Test]
    public void The_default_server_url_is_not_stored() {
        var account = new AccountStore(Path.Combine(_dir, "account.json"));
        var settings = new FocaleSettings(Guid.NewGuid(), () => FakePluginSettings.Create(), account);
        settings.ServerUrl = FocaleSettings.DefaultServerUrl;
        account.ServerUrl.Should().BeNull();
        settings.ServerUrl.Should().Be(FocaleSettings.DefaultServerUrl);
    }

    /// <summary>
    /// The settings of one NINA profile, in memory. IPluginSettings has a SetValue overload per type
    /// (and differs between NINA versions), so it is implemented with a proxy: any SetValue stores,
    /// TryGetValue&lt;T&gt; reads.
    /// </summary>
    private static class FakePluginSettings {
        public static IPluginSettings Create() {
            var proxy = DispatchProxy.Create<IPluginSettings, Handler>();
            ((Handler)(object)proxy).Values = new Dictionary<(Guid, string), object?>();
            return proxy;
        }

        public class Handler : DispatchProxy {
            public Dictionary<(Guid, string), object?> Values = new();

            protected override object? Invoke(MethodInfo? method, object?[]? args) {
                switch (method?.Name) {
                    case "SetValue":
                        Values[((Guid)args![0]!, (string)args[1]!)] = args[2];
                        return null;
                    case "TryGetValue": {
                        var type = method.IsGenericMethod
                            ? method.GetGenericArguments()[0]
                            : method.GetParameters()[2].ParameterType.GetElementType()!;
                        if (Values.TryGetValue(((Guid)args![0]!, (string)args[1]!), out var v) && v is not null && type.IsInstanceOfType(v)) {
                            args[2] = v;
                            return true;
                        }
                        args[2] = type.IsValueType ? Activator.CreateInstance(type) : null;
                        return false;
                    }
                    default:
                        return method?.ReturnType is { IsValueType: true } rt && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
                }
            }
        }
    }
}
