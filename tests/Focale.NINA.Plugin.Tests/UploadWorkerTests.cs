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
