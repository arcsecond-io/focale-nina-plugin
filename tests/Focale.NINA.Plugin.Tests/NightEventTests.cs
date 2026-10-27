using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Common;
using Focale.NINA.Plugin.Events;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class NightEventRecorderTests {
    private string _dbPath = "";
    private NightEventStore _store = null!;
    private NightEventRecorder _recorder = null!;

    // 22:00 local on 2026-10-27: the night of 2026-10-27.
    private static readonly DateTime Evening = new DateTime(2026, 10, 27, 22, 0, 0, DateTimeKind.Local).ToUniversalTime();
    private static readonly string Night = NightDate.Format(NightDate.FromLocal(Evening.ToLocalTime()));

    [SetUp]
    public async Task SetUp() {
        _dbPath = Path.Combine(Path.GetTempPath(), "focale-tests", Guid.NewGuid().ToString("N") + ".db");
        _store = new NightEventStore(_dbPath);
        await _store.InitializeAsync();
        await _store.RememberNightLogAsync(Night, "log-1");
        _recorder = new NightEventRecorder(_store);
    }

    [TearDown]
    public void TearDown() {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    private async Task<NightEventRow[]> Sendable() =>
        (await _store.ListSendableAsync(100, TimeSpan.FromHours(18))).Select(p => p.Event).ToArray();

    [Test]
    public async Task A_running_sequence_is_not_sent_until_it_ends() {
        await _recorder.SequenceStartedAsync(Evening);
        (await Sendable()).Should().BeEmpty();

        await _recorder.SequenceFinishedAsync(Evening.AddHours(6));
        var events = await Sendable();
        events.Should().ContainSingle();
        events[0].Kind.Should().Be("sequence");
        events[0].NightDate.Should().Be(Night);
        events[0].EndUtc.Should().Be(Evening.AddHours(6));
    }

    [Test]
    public async Task A_successful_autofocus_run_is_a_span_with_its_measurements() {
        await _recorder.AutofocusStartedAsync(Evening);
        await _recorder.AutofocusFinishedAsync(Evening.AddMinutes(3), 12345, 8.437, "L");
        var af = (await Sendable()).Single();
        af.Kind.Should().Be("autofocus");
        af.Success.Should().BeTrue();
        af.EndUtc.Should().Be(Evening.AddMinutes(3));
        JObject.Parse(af.DataJson).Should().BeEquivalentTo(
            JObject.Parse("""{"position": 12345.0, "temperature_c": 8.44, "filter": "L"}"""));
    }

    [Test]
    public async Task An_autofocus_run_that_never_ended_is_closed_as_failed() {
        await _recorder.AutofocusStartedAsync(Evening);
        await _recorder.AutofocusStartedAsync(Evening.AddMinutes(10));   // NINA only reports successes
        await _recorder.AutofocusFinishedAsync(Evening.AddMinutes(13), 100, null, null);
        var runs = (await Sendable()).OrderBy(e => e.StartUtc).ToArray();
        runs.Should().HaveCount(2);
        runs[0].Success.Should().BeFalse();
        runs[0].EndUtc.Should().BeNull();
        runs[1].Success.Should().BeTrue();
    }

    [Test]
    public async Task The_end_of_the_sequence_closes_whatever_was_left_open() {
        await _recorder.SequenceStartedAsync(Evening);
        await _recorder.MeridianFlipStartingAsync(Evening.AddHours(2));
        await _recorder.SequenceFinishedAsync(Evening.AddHours(3));
        var flip = (await Sendable()).Single(e => e.Kind == "meridian_flip");
        flip.Success.Should().BeFalse();
    }

    [Test]
    public async Task Instants_and_flips() {
        await _recorder.MeridianFlipStartingAsync(Evening);
        await _recorder.MeridianFlipFinishedAsync(Evening.AddMinutes(4), success: true);
        await _recorder.DitheredAsync(Evening.AddMinutes(10));
        await _recorder.ParkedAsync(Evening.AddHours(7));
        await _recorder.SafetyChangedAsync(Evening.AddHours(5), isSafe: false);
        var events = await Sendable();
        events.Select(e => e.Kind).Should().BeEquivalentTo(new[] { "meridian_flip", "dither", "safety", "park" });
        events.Single(e => e.Kind == "meridian_flip").Success.Should().BeTrue();
        JObject.Parse(events.Single(e => e.Kind == "safety").DataJson)["is_safe"]!.Value<bool>().Should().BeFalse();
    }

    [Test]
    public async Task Guiding_is_sent_in_arcseconds_when_the_scale_is_known() {
        await _recorder.ExposureGuidedAsync(Evening, Evening.AddSeconds(300),
            new GuidingRms(Ra: 0.5, Dec: 0.4, Total: 0.64, PeakRa: 1.2, PeakDec: 1.0, DataPoints: 150, ArcsecPerPixel: 1.5),
            "M31_LIGHT_0001.fits");
        var data = JObject.Parse((await Sendable()).Single().DataJson);
        data["rms_total_arcsec"]!.Value<double>().Should().Be(0.96);
        data["file_name"]!.Value<string>().Should().Be("M31_LIGHT_0001.fits");
        data.ContainsKey("rms_total_px").Should().BeFalse();
    }

    [Test]
    public void Guiding_falls_back_to_pixels_without_a_scale() {
        var data = new GuidingRms(0.5, 0.4, 0.64, 1.2, 1.0, 150, 0).ToJson();
        data["rms_total_px"]!.Value<double>().Should().Be(0.64);
        data.ContainsKey("rms_total_arcsec").Should().BeFalse();
        new GuidingRms(0, 0, 0, 0, 0, 0, 1.5).HasData.Should().BeFalse();
    }

    [Test]
    public async Task Events_wait_for_their_nights_log_then_go_once() {
        var nextNight = Evening.AddDays(1);
        await _recorder.DitheredAsync(nextNight);
        (await Sendable()).Should().BeEmpty();   // no frame uploaded for that night yet

        await _store.RememberNightLogAsync(NightDate.Format(NightDate.FromLocal(nextNight.ToLocalTime())), "log-2");
        var pending = await _store.ListSendableAsync(100, TimeSpan.FromHours(18));
        pending.Should().ContainSingle().Which.NightLogUuid.Should().Be("log-2");

        await _store.MarkSentAsync(pending.Select(p => p.Event.ClientId));
        (await Sendable()).Should().BeEmpty();
    }

    [Test]
    public async Task A_span_NINA_never_closed_goes_without_an_end_once_stale() {
        await _recorder.SequenceStartedAsync(Evening);
        (await _store.ListSendableAsync(100, TimeSpan.Zero)).Should().ContainSingle()
            .Which.Event.EndUtc.Should().BeNull();
    }

    [Test]
    public void Local_and_unspecified_times_are_taken_as_local() {
        var local = new DateTime(2026, 10, 27, 22, 0, 0, DateTimeKind.Unspecified);
        NightEventRecorder.AsUtc(local).Should().Be(DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime());
        NightEventRecorder.AsUtc(Evening).Should().Be(Evening);
    }
}

[TestFixture]
public class NightEventWireFormatTests {
    [Test]
    public void Events_serialize_to_the_back_ends_shape() {
        var row = new NightEventRow {
            ClientId = "abc", Kind = "autofocus",
            StartUtc = new DateTime(2026, 10, 27, 21, 4, 10, DateTimeKind.Utc),
            EndUtc   = new DateTime(2026, 10, 27, 21, 6, 55, DateTimeKind.Utc),
            Success  = true, DataJson = """{"position": 12345}""",
        };
        var raw = FocaleApiClient.SerializeEvents(new[] { NightEventSender.ToPayload(row) });
        // UTC, with an explicit Z: the back-end stores aware datetimes.
        raw.Should().Contain("\"start\":\"2026-10-27T21:04:10.000Z\"");
        raw.Should().Contain("\"end\":\"2026-10-27T21:06:55.000Z\"");
        var e = (JObject)JArray.Parse(raw)[0];
        e["client_id"]!.Value<string>().Should().Be("abc");
        e["kind"]!.Value<string>().Should().Be("autofocus");
        e["success"]!.Value<bool>().Should().BeTrue();
        e["data"]!["position"]!.Value<int>().Should().Be(12345);
    }
}
