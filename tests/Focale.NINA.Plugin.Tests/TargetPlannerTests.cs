using System.Collections.Generic;
using FluentAssertions;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Push;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class TargetPlannerTests {
    private static readonly string[] Wheel = { "L", "R", "G", "B", "H-alpha", "OIII", "SII" };

    private static ExposurePlanEntry Entry(params ExposureLine[] lines) => new() {
        Uuid = "obs-1", Index = 0, PositionAngle = 90,
        Target = new PlanTarget { Id = 31, Name = "M31", RightAscension = 10.6847, Declination = 41.2690 },
        Lines = new List<ExposureLine>(lines),
    };

    private static ExposureLine Line(string filter, double seconds, int count, int binning = 1) =>
        new() { Filter = filter, ExposureSeconds = seconds, Count = count, Binning = binning };

    [TestCase("L", "L")]
    [TestCase("oiii", "OIII")]
    [TestCase("Halpha", "H-alpha")]
    [TestCase(" H-Alpha ", "H-alpha")]
    public void Filters_match_by_name_case_and_punctuation_aside(string planned, string expected) {
        TargetPlanner.MatchFilter(planned, Wheel).Should().Be(expected);
    }

    [TestCase("Ha")]   // not guessed: H-alpha is another name
    [TestCase("UV")]
    [TestCase("")]
    public void No_filter_is_guessed(string planned) {
        TargetPlanner.MatchFilter(planned, Wheel).Should().BeNull();
    }

    [Test]
    public void A_planned_target_carries_its_coordinates_rotation_and_lines() {
        var plan = TargetPlanner.Plan(Entry(Line("Halpha", 300, 20), Line("OIII", 300, 15, 2)), Wheel);
        plan.CanAdd.Should().BeTrue();
        plan.RaDegrees.Should().Be(10.6847);
        plan.DecDegrees.Should().Be(41.2690);
        plan.PositionAngle.Should().Be(90);
        plan.Exposures.Should().Equal(new PlannedExposure("H-alpha", 300, 20, 1), new PlannedExposure("OIII", 300, 15, 2));
        plan.Summary.Should().Be("H-alpha 20×300s · OIII 15×300s bin2");
    }

    [Test]
    public void Lines_whose_filter_the_wheel_lacks_are_left_out_and_said() {
        var plan = TargetPlanner.Plan(Entry(Line("L", 120, 30), Line("UV", 60, 10)), Wheel);
        plan.CanAdd.Should().BeTrue();
        plan.Exposures.Should().ContainSingle().Which.Filter.Should().Be("L");
        plan.UnknownFilters.Should().Equal("UV");
        plan.Summary.Should().EndWith("not in NINA's filter wheel: UV");
    }

    [Test]
    public void A_plan_none_of_whose_filters_exists_cannot_be_added() {
        var plan = TargetPlanner.Plan(Entry(Line("UV", 60, 10)), Wheel);
        plan.CanAdd.Should().BeFalse();
        plan.Blocker.Should().Contain("UV");
    }

    [Test]
    public void Without_lines_the_templates_exposures_are_kept() {
        var plan = TargetPlanner.Plan(Entry(), Wheel);
        plan.CanAdd.Should().BeTrue();
        plan.KeepsTemplateExposures.Should().BeTrue();
        plan.Summary.Should().Contain("template's are kept");
    }

    [Test]
    public void A_target_without_fixed_coordinates_is_left_to_NINA() {
        var entry = Entry(Line("L", 60, 1));
        entry.Target!.RightAscension = null;
        var plan = TargetPlanner.Plan(entry, Wheel);
        plan.CanAdd.Should().BeFalse();
        plan.Blocker.Should().Contain("No fixed coordinates");
    }

    [Test]
    public void The_servers_plan_reads_as_sent() {
        var json = """
            {"night_plan": "p", "date": "2026-10-28", "entries": [
              {"uuid": "o1", "index": 0, "name": "", "start": "2026-10-28T20:00:00+00:00", "duration_seconds": 7200,
               "status": "planned", "position_angle": null,
               "target": {"id": 31, "name": "M31", "target_class": "AstronomicalObject", "right_ascension": 10.68, "declination": 41.27},
               "lines": [{"filter": "Ha", "exposure_seconds": 300.0, "count": 20, "binning": 1}]}]}
            """;
        var plan = JsonConvert.DeserializeObject<ExposurePlanResponse>(json)!;
        plan.Entries.Should().ContainSingle();
        plan.Entries[0].Target!.RightAscension.Should().Be(10.68);
        plan.Entries[0].Lines[0].Count.Should().Be(20);
        plan.Entries[0].PositionAngle.Should().BeNull();
    }
}
