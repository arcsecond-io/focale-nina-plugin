using System;
using FluentAssertions;
using Focale.NINA.Plugin.Common;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class NightDateTests {
    [Test]
    public void Afternoon_BelongsToSameDate() {
        var t = new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 5, 11));
    }

    [Test]
    public void Evening_BelongsToSameDate() {
        var t = new DateTime(2026, 5, 11, 23, 30, 0, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 5, 11));
    }

    [Test]
    public void EarlyMorning_BelongsToPreviousDate() {
        var t = new DateTime(2026, 5, 12, 2, 0, 0, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 5, 11));
    }

    [Test]
    public void JustBeforeNoon_BelongsToPreviousDate() {
        var t = new DateTime(2026, 5, 12, 11, 59, 59, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 5, 11));
    }

    [Test]
    public void Noon_StartsNewNight() {
        var t = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 5, 12));
    }

    [Test]
    public void JustAfterNoon_BelongsToNewDate() {
        var t = new DateTime(2026, 5, 12, 12, 0, 1, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 5, 12));
    }

    [Test]
    public void MonthBoundary_LateNight_BelongsToLastDayOfPreviousMonth() {
        var t = new DateTime(2026, 6, 1, 3, 0, 0, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 5, 31));
    }

    [Test]
    public void YearBoundary_LateNight_BelongsToLastDayOfPreviousYear() {
        var t = new DateTime(2027, 1, 1, 4, 0, 0, DateTimeKind.Local);
        NightDate.FromLocal(t).Should().Be(new DateOnly(2026, 12, 31));
    }

    [Test]
    public void Format_RoundtripsIso() {
        NightDate.Format(new DateOnly(2026, 5, 11)).Should().Be("2026-05-11");
        NightDate.Format(new DateOnly(2026, 1, 1)).Should().Be("2026-01-01");
    }
}
