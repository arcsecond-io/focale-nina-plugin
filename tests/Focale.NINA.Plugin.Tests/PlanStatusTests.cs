using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Push;
using Focale.NINA.Plugin.Settings;
using Newtonsoft.Json.Linq;
using NINA.Sequencer.Container;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class PlanStatusReporterTests {
    private readonly List<(string Plan, string Entry, string Status)> _sent = new();
    private Func<string, Exception?> _failWith = _ => null;   // by entry uuid
    private bool _signedIn = true;
    private DateTime _now = new(2026, 11, 10, 22, 0, 0, DateTimeKind.Utc);

    private PlanStatusReporter NewReporter() => new((plan, entry, status, _) => {
        if (_failWith(entry) is { } ex) throw ex;
        _sent.Add((plan, entry, status));
        return Task.CompletedTask;
    }, () => _signedIn, () => _now);

    private static FocaleApiException Http(int status) =>
        new($"Focale API responded {status}", (HttpStatusCode)status,
            retryable: status is 408 or 425 or 429 or >= 500);

    [SetUp]
    public void SetUp() {
        _sent.Clear();
        _failWith = _ => null;
        _signedIn = true;
        _now = new DateTime(2026, 11, 10, 22, 0, 0, DateTimeKind.Utc);
    }

    [Test]
    public async Task A_reported_status_is_sent_once() {
        using var reporter = NewReporter();
        reporter.Report("plan-1", "obs-1", PlanEntryStatus.Active);
        await reporter.FlushAsync(CancellationToken.None);
        await reporter.FlushAsync(CancellationToken.None);

        _sent.Should().Equal(("plan-1", "obs-1", "active"));
        reporter.PendingStatuses.Should().BeEmpty();
    }

    [Test]
    public async Task Only_the_latest_status_of_an_entry_goes() {
        using var reporter = NewReporter();
        reporter.Report("plan-1", "obs-1", PlanEntryStatus.Active);
        reporter.Report("plan-1", "obs-2", PlanEntryStatus.Active);
        reporter.Report("plan-1", "obs-1", PlanEntryStatus.Done);
        await reporter.FlushAsync(CancellationToken.None);

        _sent.Should().Equal(("plan-1", "obs-2", "active"), ("plan-1", "obs-1", "done"));
    }

    [TestCase(null, "obs-1")]
    [TestCase("plan-1", null)]
    [TestCase("", "obs-1")]
    public async Task A_step_not_linked_to_an_entry_reports_nothing(string? plan, string? entry) {
        using var reporter = NewReporter();
        reporter.Report(plan, entry, PlanEntryStatus.Done);
        await reporter.FlushAsync(CancellationToken.None);
        _sent.Should().BeEmpty();
        reporter.PendingStatuses.Should().BeEmpty();
    }

    [Test]
    public async Task Offline_the_status_waits_and_goes_later() {
        using var reporter = NewReporter();
        _failWith = _ => new HttpRequestException("No such host is known.");
        reporter.Report("plan-1", "obs-1", PlanEntryStatus.Active);
        await reporter.FlushAsync(CancellationToken.None);
        reporter.PendingStatuses.Should().ContainKey("obs-1");

        _failWith = _ => null;
        await reporter.FlushAsync(CancellationToken.None);
        _sent.Should().Equal(("plan-1", "obs-1", "active"));
    }

    [TestCase(401)]
    [TestCase(429)]
    [TestCase(503)]
    public async Task Signed_out_or_a_server_error_keeps_the_status(int status) {
        using var reporter = NewReporter();
        _failWith = _ => Http(status);
        reporter.Report("plan-1", "obs-1", PlanEntryStatus.Done);
        await reporter.FlushAsync(CancellationToken.None);
        reporter.PendingStatuses.Should().Equal(new Dictionary<string, string> { ["obs-1"] = "done" });
    }

    [TestCase(400)]
    [TestCase(403)]
    [TestCase(404)]
    public async Task A_refused_status_is_dropped_and_the_others_still_go(int status) {
        using var reporter = NewReporter();
        reporter.Report("plan-1", "deleted", PlanEntryStatus.Active);
        reporter.Report("plan-1", "obs-2", PlanEntryStatus.Active);
        _failWith = entry => entry == "deleted" ? Http(status) : null;
        await reporter.FlushAsync(CancellationToken.None);

        _sent.Should().Equal(("plan-1", "obs-2", "active"));
        reporter.PendingStatuses.Should().BeEmpty();
    }

    [Test]
    public async Task Nothing_is_sent_while_signed_out() {
        using var reporter = NewReporter();
        _signedIn = false;
        reporter.Report("plan-1", "obs-1", PlanEntryStatus.Active);
        await reporter.FlushAsync(CancellationToken.None);
        _sent.Should().BeEmpty();
        reporter.PendingStatuses.Should().ContainKey("obs-1");
    }

    [Test]
    public async Task A_status_still_waiting_after_a_long_while_is_dropped() {
        using var reporter = NewReporter();
        _signedIn = false;
        reporter.Report("plan-1", "obs-1", PlanEntryStatus.Active);
        _now += PlanStatusReporter.Keep + TimeSpan.FromMinutes(1);
        _signedIn = true;
        await reporter.FlushAsync(CancellationToken.None);
        _sent.Should().BeEmpty();
        reporter.PendingStatuses.Should().BeEmpty();
    }

    [Test]
    public async Task A_status_reported_while_the_previous_one_is_sent_is_not_lost() {
        PlanStatusReporter? reporter = null;
        reporter = new PlanStatusReporter((plan, entry, status, _) => {
            _sent.Add((plan, entry, status));
            if (status == "active") reporter!.Report(plan, entry, PlanEntryStatus.Done);
            return Task.CompletedTask;
        }, () => true);
        using (reporter) {
            reporter.Report("plan-1", "obs-1", PlanEntryStatus.Active);
            await reporter.FlushAsync(CancellationToken.None);
            reporter.PendingStatuses.Should().Equal(new Dictionary<string, string> { ["obs-1"] = "done" });
            await reporter.FlushAsync(CancellationToken.None);
        }
        _sent.Select(s => s.Status).Should().Equal("active", "done");
    }
}

[TestFixture]
public class PlanStatusItemTests {
    [Test]
    public void The_steps_set_active_then_done() {
        new MarkPlanEntryActive().PlanStatus.Should().Be("active");
        new MarkPlanEntryDone().PlanStatus.Should().Be("done");
    }

    [Test]
    public void A_clone_keeps_its_entry() {
        var step = new MarkPlanEntryDone { PlanUuid = "plan-1", EntryUuid = "obs-1" };
        var clone = (MarkPlanEntryDone)step.Clone();
        clone.PlanUuid.Should().Be("plan-1");
        clone.EntryUuid.Should().Be("obs-1");
        clone.Name.Should().Be("Focale: mark done");
    }

    [Test]
    public void A_step_without_an_entry_is_flagged() {
        var step = new MarkPlanEntryActive();
        step.Validate().Should().BeFalse();
        step.Issues.Should().ContainSingle();
        new MarkPlanEntryActive { PlanUuid = "plan-1", EntryUuid = "obs-1" }.Validate().Should().BeTrue();
    }

    [Test]
    public async Task Running_a_step_never_fails_even_before_the_plugin_started() {
        var step = new MarkPlanEntryDone { PlanUuid = "plan-1", EntryUuid = "obs-1" };
        await step.Invoking(s => s.Execute(new Progress<global::NINA.Core.Model.ApplicationStatus>(), CancellationToken.None))
                  .Should().NotThrowAsync();
    }

    [Test]
    public void The_entry_survives_a_saved_sequence() {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new MarkPlanEntryActive { PlanUuid = "plan-1", EntryUuid = "obs-1" });
        var o = JObject.Parse(json);
        o["PlanUuid"]!.ToString().Should().Be("plan-1");
        o["EntryUuid"]!.ToString().Should().Be("obs-1");
    }
}

[TestFixture]
public class NinaTargetStatusStepTests {
    private static PlannedTarget Planned(string? planUuid) => new PlannedTarget(
        "obs-1", "M31", 10.68, 41.27, null, Array.Empty<PlannedExposure>(), Array.Empty<string>(), null) { PlanUuid = planUuid };

    [Test]
    public void The_target_starts_with_mark_active_and_ends_with_mark_done() {
        var container = new SequentialContainer();
        container.Add(new SequentialContainer());
        NinaTargetBuilder.AddStatusSteps(container, Planned("plan-1"));

        container.Items.First().Should().BeOfType<MarkPlanEntryActive>()
                 .Which.EntryUuid.Should().Be("obs-1");
        container.Items.Last().Should().BeOfType<MarkPlanEntryDone>()
                 .Which.PlanUuid.Should().Be("plan-1");
        container.Items.Should().HaveCount(3);
    }

    [Test]
    public void Steps_a_template_carried_over_are_replaced() {
        var container = new SequentialContainer();
        var inner = new SequentialContainer();
        inner.Add(new MarkPlanEntryDone { PlanUuid = "old-plan", EntryUuid = "old-entry" });
        container.Add(inner);
        container.Add(new MarkPlanEntryActive { PlanUuid = "old-plan", EntryUuid = "old-entry" });

        NinaTargetBuilder.AddStatusSteps(container, Planned("plan-1"));

        AllSteps(container).Select(s => s.EntryUuid).Should().Equal("obs-1", "obs-1");
    }

    [Test]
    public void Without_the_plan_the_target_reports_nothing() {
        var container = new SequentialContainer();
        NinaTargetBuilder.AddStatusSteps(container, Planned(null));
        container.Items.Should().BeEmpty();
    }

    private static IEnumerable<PlanStatusItem> AllSteps(ISequenceContainer container) {
        foreach (var item in container.Items) {
            if (item is PlanStatusItem step) yield return step;
            if (item is ISequenceContainer child) foreach (var s in AllSteps(child)) yield return s;
        }
    }
}

/// <summary>The PATCH itself, against a one-shot local HTTP server.</summary>
[TestFixture]
public class SetPlanEntryStatusTests {
    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "focale-planstatus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task It_patches_the_entry_with_its_status() {
        var (url, request) = ServeOnce("200 OK", """{"uuid": "obs-1", "status": "done"}""");
        using var api = NewClient(url);

        await api.SetPlanEntryStatusAsync("plan-1", "obs-1", PlanEntryStatus.Done, CancellationToken.None);

        var (requestLine, body) = await request;
        requestLine.Should().Be("PATCH /nightplans/plan-1/observations/obs-1/ HTTP/1.1");
        JObject.Parse(body)["status"]!.ToString().Should().Be("done");
    }

    [Test]
    public async Task A_refusal_surfaces_with_its_status_code() {
        var (url, _) = ServeOnce("404 Not Found", """{"detail": "Not found."}""");
        using var api = NewClient(url);

        var ex = await api.Invoking(a => a.SetPlanEntryStatusAsync("plan-1", "gone", PlanEntryStatus.Active, CancellationToken.None))
                          .Should().ThrowAsync<FocaleApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex.Which.IsRetryable.Should().BeFalse();
    }

    private FocaleApiClient NewClient(string url) {
        var settings = new FocaleSettings(Guid.NewGuid(), () => AccountStoreTests.FakePluginSettings.Create(),
                                          new AccountStore(Path.Combine(_dir, "account.json"))) { ServerUrl = url };
        return new FocaleApiClient(settings);
    }

    /// <summary>Answers one request with <paramref name="status"/> and returns its request line and body.</summary>
    private static (string Url, Task<(string RequestLine, string Body)> Request) ServeOnce(string status, string responseBody) {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = Task.Run(async () => {
            try {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int headerEnd;
                while ((headerEnd = IndexOf(buffer.ToArray(), "\r\n\r\n"u8.ToArray())) < 0) {
                    var n = await stream.ReadAsync(chunk);
                    if (n == 0) throw new IOException("connection closed before the headers");
                    buffer.Write(chunk, 0, n);
                }
                var head = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
                var lines = head.Split("\r\n");
                var length = lines.Select(l => l.Split(':', 2))
                                  .Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                                  .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
                while (buffer.Length < headerEnd + 4 + length) {
                    var n = await stream.ReadAsync(chunk);
                    if (n == 0) break;
                    buffer.Write(chunk, 0, n);
                }
                var body = Encoding.UTF8.GetString(buffer.ToArray(), headerEnd + 4, (int)buffer.Length - headerEnd - 4);
                var payload = Encoding.UTF8.GetBytes(responseBody);
                var response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);
                await stream.WriteAsync(payload);
                return (lines[0], body);
            } finally {
                listener.Stop();
            }
        });
        return ($"http://127.0.0.1:{port}", request);
    }

    private static int IndexOf(byte[] haystack, byte[] needle) {
        for (var i = 0; i <= haystack.Length - needle.Length; i++) {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }
}
