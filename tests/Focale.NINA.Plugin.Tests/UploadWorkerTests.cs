using System;
using FluentAssertions;
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
