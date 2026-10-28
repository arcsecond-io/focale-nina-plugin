using System;
using System.Collections.Generic;
using System.Linq;
using Focale.NINA.Plugin.Api.Models;

namespace Focale.NINA.Plugin.Push;

/// <summary>One exposure line, matched to a filter of the NINA profile's filter wheel.</summary>
public sealed record PlannedExposure(string Filter, double ExposureSeconds, int Count, int Binning);

/// <summary>
/// What the plugin will build for one entry of Focale's night plan, and why not when it can't. Pure:
/// no NINA type, so it is testable; <see cref="NinaTargetBuilder"/> turns it into a NINA target.
/// </summary>
public sealed record PlannedTarget(
    string EntryUuid,
    string Name,
    double? RaDegrees,
    double? DecDegrees,
    double? PositionAngle,
    IReadOnlyList<PlannedExposure> Exposures,
    IReadOnlyList<string> UnknownFilters,
    string? Blocker) {

    /// <summary>The entry can be added to the sequence (it may still have warnings).</summary>
    public bool CanAdd => Blocker is null;

    /// <summary>No exposure line planned: the template's own exposures are kept.</summary>
    public bool KeepsTemplateExposures => Exposures.Count == 0;

    /// <summary>One line for the panel: "Ha 20×300s · OIII 15×300s", or why it cannot be added.</summary>
    public string Summary {
        get {
            if (Blocker is not null) return Blocker;
            var parts = Exposures.Select(e => $"{e.Filter} {e.Count}×{e.ExposureSeconds:0.##}s" + (e.Binning > 1 ? $" bin{e.Binning}" : ""));
            var summary = Exposures.Count == 0 ? "No exposures planned: the template's are kept" : string.Join(" · ", parts);
            return UnknownFilters.Count == 0
                ? summary
                : summary + $" — not in NINA's filter wheel: {string.Join(", ", UnknownFilters)}";
        }
    }
}

public static class TargetPlanner {
    /// <summary>
    /// Plans one night plan entry against the NINA profile's filter names. A target without fixed
    /// coordinates cannot be added (NINA's target container takes J2000 coordinates). Lines whose filter
    /// is not in the wheel are left out and reported: the plugin never guesses another filter.
    /// </summary>
    public static PlannedTarget Plan(ExposurePlanEntry entry, IReadOnlyCollection<string> wheelFilters) {
        var target = entry.Target;
        var name = target?.Name ?? entry.Name ?? "Target";
        if (target is null) {
            return new PlannedTarget(entry.Uuid, name, null, null, entry.PositionAngle,
                Array.Empty<PlannedExposure>(), Array.Empty<string>(), "No target on this entry");
        }
        if (target.RightAscension is null || target.Declination is null) {
            return new PlannedTarget(entry.Uuid, name, null, null, entry.PositionAngle,
                Array.Empty<PlannedExposure>(), Array.Empty<string>(),
                "No fixed coordinates (a planet or a comet): add it in NINA directly");
        }

        var exposures = new List<PlannedExposure>();
        var unknown = new List<string>();
        foreach (var line in entry.Lines) {
            var filter = MatchFilter(line.Filter, wheelFilters);
            if (filter is null) {
                if (!unknown.Contains(line.Filter)) unknown.Add(line.Filter);
                continue;
            }
            exposures.Add(new PlannedExposure(filter, line.ExposureSeconds, line.Count, Math.Max(1, line.Binning)));
        }
        string? blocker = entry.Lines.Count > 0 && exposures.Count == 0
            ? $"None of its filters is in NINA's filter wheel ({string.Join(", ", unknown)})"
            : null;
        return new PlannedTarget(entry.Uuid, name, target.RightAscension, target.Declination, entry.PositionAngle,
            exposures, unknown, blocker);
    }

    /// <summary>
    /// The wheel's filter for a planned name: exact (ignoring case) first, then ignoring everything but
    /// letters and digits ("H-alpha" and "Halpha", "OIII " and "OIII"). Null when none matches.
    /// </summary>
    public static string? MatchFilter(string planned, IReadOnlyCollection<string> wheelFilters) {
        if (string.IsNullOrWhiteSpace(planned)) return null;
        var exact = wheelFilters.FirstOrDefault(f => string.Equals(f.Trim(), planned.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var key = Normalize(planned);
        return wheelFilters.FirstOrDefault(f => Normalize(f) == key);
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
