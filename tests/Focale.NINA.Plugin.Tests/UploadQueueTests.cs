using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Focale.NINA.Plugin.Upload;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class UploadQueueTests {
    private string _dbPath = "";

    [SetUp]
    public void SetUp() {
        _dbPath = Path.Combine(Path.GetTempPath(), "focale-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    [TearDown]
    public void TearDown() {
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    private static UploadJob NewJob(string sha = "abc") => new() {
        LocalPath    = @"C:\fake\path.fits",
        Sha256       = sha,
        CaptureLocal = new DateTime(2026, 5, 11, 23, 0, 0, DateTimeKind.Local),
        FrameClass   = Focale.NINA.Plugin.Api.FrameClass.Observation,
    };

    [Test]
    public async Task EnqueueClaimDone_RoundTrip() {
        using var queue = new UploadQueue(_dbPath);
        await queue.InitializeAsync();

        var id = await queue.EnqueueAsync(NewJob());

        var claimed = await queue.ClaimNextAsync();
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(id);
        claimed.Status.Should().Be(UploadJobStatus.Working);

        await queue.MarkDoneAsync(id);
        (await queue.ClaimNextAsync()).Should().BeNull("queue should be empty after MarkDone");
    }

    [Test]
    public async Task Enqueue_DuplicateSha_DoesNotInsertTwice() {
        using var queue = new UploadQueue(_dbPath);
        await queue.InitializeAsync();

        var id1 = await queue.EnqueueAsync(NewJob("sha-x"));
        var id2 = await queue.EnqueueAsync(NewJob("sha-x"));

        id2.Should().Be(id1, "ON CONFLICT(sha256) DO NOTHING — must dedupe");
        (await queue.CountAsync(UploadJobStatus.Pending)).Should().Be(1);
    }

    [Test]
    public async Task ClaimNext_RespectsNotBeforeUtc() {
        using var queue = new UploadQueue(_dbPath);
        await queue.InitializeAsync();

        await queue.EnqueueAsync(new UploadJob {
            LocalPath = "x", Sha256 = "y",
            CaptureLocal = new DateTime(2026, 5, 11, 23, 0, 0, DateTimeKind.Local),
            FrameClass = Focale.NINA.Plugin.Api.FrameClass.Observation,
            NotBeforeUtc = DateTime.UtcNow.AddMinutes(5),  // future
        });

        (await queue.ClaimNextAsync()).Should().BeNull("not_before is in the future");
    }

    [Test]
    public async Task RestartRecovery_ReleasesStuckWorkingRows() {
        long id;
        using (var q = new UploadQueue(_dbPath)) {
            await q.InitializeAsync();
            id = await q.EnqueueAsync(NewJob("recover-1"));
            (await q.ClaimNextAsync()).Should().NotBeNull();   // leave row in Working
        }

        using var q2 = new UploadQueue(_dbPath);
        await q2.InitializeAsync();   // recovery sweep reverts Working → Pending

        var claimed = await q2.ClaimNextAsync();
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(id);
    }

    [Test]
    public async Task MarkRetry_ThenClaim_ReturnsSameRow() {
        using var queue = new UploadQueue(_dbPath);
        await queue.InitializeAsync();

        var id = await queue.EnqueueAsync(NewJob("retry-1"));
        var claimed = await queue.ClaimNextAsync();
        claimed.Should().NotBeNull();

        await queue.MarkRetryAsync(id, attempt: 1, notBefore: DateTime.UtcNow.AddMilliseconds(-1), lastError: "oops");
        var again = await queue.ClaimNextAsync();
        again.Should().NotBeNull();
        again!.Attempt.Should().Be(1);
        again.LastError.Should().Be("oops");
    }

    [Test]
    public async Task MarkDead_RowExcludedFromClaim_ButCounted() {
        using var queue = new UploadQueue(_dbPath);
        await queue.InitializeAsync();

        var id = await queue.EnqueueAsync(NewJob("dead-1"));
        await queue.ClaimNextAsync();
        await queue.MarkDeadAsync(id, "permanent failure");

        (await queue.ClaimNextAsync()).Should().BeNull();
        (await queue.CountAsync(UploadJobStatus.Dead)).Should().Be(1);

        var dead = await queue.ListDeadAsync(10);
        dead.Should().HaveCount(1);
        dead[0].LastError.Should().Be("permanent failure");
    }
}
