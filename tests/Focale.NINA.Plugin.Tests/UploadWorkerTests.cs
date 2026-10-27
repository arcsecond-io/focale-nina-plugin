using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Upload;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class UploadWorkerBackoffTests {
    [Test]
    public void Backoff_GrowsExponentially_AndCapsAt5Minutes() {
        UploadWorker.Backoff(1).Should().Be(TimeSpan.FromSeconds(2));
        UploadWorker.Backoff(2).Should().Be(TimeSpan.FromSeconds(4));
        UploadWorker.Backoff(3).Should().Be(TimeSpan.FromSeconds(8));
        UploadWorker.Backoff(8).Should().Be(TimeSpan.FromSeconds(256));

        // Cap kicks in.
        UploadWorker.Backoff(20).Should().Be(TimeSpan.FromSeconds(300));
        UploadWorker.Backoff(100).Should().Be(TimeSpan.FromSeconds(300));
    }
}

[TestFixture]
public class UploadWorkerClassifyTests {
    private static FocaleApiException Error(int status) {
        // Same retryable rule as FocaleApiClient.EnsureSuccessAsync.
        var retryable = status is 408 or 425 or 429 || status >= 500;
        return new FocaleApiException($"HTTP {status}", (HttpStatusCode)status, retryable);
    }

    [Test]
    public void Unauthorized_KeepsTheFrame_UntilSignIn() {
        UploadWorker.Classify(Error(401)).Should().Be(UploadFailureKind.SignedOut);
    }

    [TestCase(402)]
    [TestCase(403)]
    [TestCase(507)]
    public void PlanOrQuotaRefusals_PauseTheQueue(int status) {
        UploadWorker.Classify(Error(status)).Should().Be(UploadFailureKind.Refused);
    }

    [TestCase(408)]
    [TestCase(425)]
    [TestCase(429)]
    [TestCase(500)]
    [TestCase(502)]
    [TestCase(503)]
    public void ServerSideTransients_AreRetried(int status) {
        UploadWorker.Classify(Error(status)).Should().Be(UploadFailureKind.Transient);
    }

    [TestCase(400)]
    [TestCase(404)]
    [TestCase(409)]
    [TestCase(413)]
    [TestCase(415)]
    [TestCase(422)]
    public void FileSpecificRefusals_AreDeadLettered(int status) {
        UploadWorker.Classify(Error(status)).Should().Be(UploadFailureKind.Permanent);
    }

    [Test]
    public void NoHttpAnswer_IsANetworkFailure() {
        UploadWorker.IsNetworkFailure(new HttpRequestException("No such host is known.")).Should().BeTrue();
        UploadWorker.IsNetworkFailure(new TaskCanceledException("HttpClient.Timeout elapsed")).Should().BeTrue();
        UploadWorker.IsNetworkFailure(new TimeoutException()).Should().BeTrue();
        UploadWorker.IsNetworkFailure(new InvalidOperationException()).Should().BeFalse();
        UploadWorker.IsNetworkFailure(Error(500)).Should().BeFalse();
    }
}

[TestFixture]
public class UploadWorkerStorageModeTests {
    [Test]
    public void Nothing_is_uploaded_before_a_mode_is_chosen() {
        UploadWorker.UploadFields(Focale.NINA.Plugin.Settings.StorageMode.Unset, null).Should().BeNull();
        UploadWorker.UploadFields(Focale.NINA.Plugin.Settings.StorageMode.OwnStorage, null).Should().BeNull();
    }

    [Test]
    public void Each_mode_sends_its_fields() {
        var index = UploadWorker.UploadFields(Focale.NINA.Plugin.Settings.StorageMode.IndexOnly, null)!;
        index["persist_file"].Should().Be("false");
        index.Should().NotContainKey("destination");

        var focale = UploadWorker.UploadFields(Focale.NINA.Plugin.Settings.StorageMode.FocaleStorage, "ignored")!;
        focale["persist_file"].Should().Be("true");
        focale.Should().NotContainKey("destination");

        var own = UploadWorker.UploadFields(Focale.NINA.Plugin.Settings.StorageMode.OwnStorage, "uuid-1")!;
        own["persist_file"].Should().Be("true");
        own["destination"].Should().Be("uuid-1");

        foreach (var f in new[] { index, focale, own }) {
            f["auto_ingest"].Should().Be("true");
            f["source"].Should().Be("observer_console");
        }
    }

    [Test]
    public void A_refused_destination_pauses_instead_of_dropping_the_frame() {
        var ex = new FocaleApiException("400", System.Net.HttpStatusCode.BadRequest, retryable: false,
                                        fieldErrors: new[] { "destination" });
        UploadWorker.Classify(ex).Should().Be(UploadFailureKind.Refused);
        var other = new FocaleApiException("400", System.Net.HttpStatusCode.BadRequest, retryable: false,
                                           fieldErrors: new[] { "file" });
        UploadWorker.Classify(other).Should().Be(UploadFailureKind.Permanent);
    }
}
