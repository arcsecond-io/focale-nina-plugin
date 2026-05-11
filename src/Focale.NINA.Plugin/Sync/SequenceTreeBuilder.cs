using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;

namespace Focale.NINA.Plugin.Sync;

/// <summary>
/// Walks NINA's <c>IDeepSkyObjectContainer</c> targets and produces a flat list of
/// (target, instructions) suitable for the arcsecond-back observingsequences endpoints:
///
///   target       → SequenceStep  (one per IDeepSkyObjectContainer)
///   instruction  → SequenceTask  (one per child ISequenceItem the user added)
///
/// The walk is intentionally shallow — recursive containers (loops, conditions, triggers)
/// are flattened at the top level for v1. A richer mapping can come once the arcsecond-back
/// model supports nested steps.
/// </summary>
public static class SequenceTreeBuilder {

    public sealed record TargetPlan(
        string  Name,
        string  Description,
        string? TargetName,
        double? RaHours,
        double? DecDegrees,
        double? PositionAngle,
        List<InstructionPlan> Instructions);

    public sealed record InstructionPlan(
        string  Name,
        string  TaskName,
        JObject Kwargs);

    public static List<TargetPlan> Build(IList<IDeepSkyObjectContainer> targets) {
        var result = new List<TargetPlan>(targets.Count);
        foreach (var target in targets) {
            if (target is null) continue;
            result.Add(BuildOne(target));
        }
        return result;
    }

    private static TargetPlan BuildOne(IDeepSkyObjectContainer target) {
        var inputTarget = target.Target;
        var instructions = new List<InstructionPlan>();
        try {
            foreach (var item in target.Items) {
                if (item is null) continue;
                instructions.Add(BuildInstruction(item));
            }
        } catch (Exception ex) {
            // Defensive: skip if NINA's tree shape surprises us.
            instructions.Add(new InstructionPlan(
                Name: "tree_walk_error",
                TaskName: "Error",
                Kwargs: new JObject { ["message"] = ex.Message }));
        }

        return new TargetPlan(
            Name:          inputTarget?.TargetName ?? target.Name ?? "Target",
            Description:   target.Description ?? string.Empty,
            TargetName:    inputTarget?.TargetName,
            RaHours:       TryRaHours(inputTarget),
            DecDegrees:    TryDecDegrees(inputTarget),
            PositionAngle: inputTarget?.PositionAngle,
            Instructions:  instructions);
    }

    private static InstructionPlan BuildInstruction(ISequenceItem item) {
        var typeName = item.GetType().Name;
        // The kwargs capture the minimal set of properties most NINA items expose. Anything
        // exotic falls through and is recorded under "raw_type" so the server can still see
        // what NINA was doing even without a typed parser.
        var kwargs = new JObject {
            ["raw_type"] = typeName,
        };
        // Best-effort extraction of common properties via reflection — avoids a switch over
        // every NINA item type. Each property is optional.
        foreach (var prop in new[] { "ExposureTime", "ExposureCount", "Iterations", "Gain", "Offset", "Binning", "Filter", "Roi" }) {
            try {
                var pi = item.GetType().GetProperty(prop);
                if (pi is null) continue;
                var value = pi.GetValue(item);
                if (value is null) continue;
                kwargs[prop] = JToken.FromObject(value);
            } catch { /* ignore — best-effort */ }
        }
        return new InstructionPlan(
            Name:     item.Name ?? typeName,
            TaskName: typeName,
            Kwargs:   kwargs);
    }

    private static double? TryRaHours(NINA.Astrometry.InputTarget? t) {
        try { return t?.InputCoordinates?.Coordinates?.RA; }
        catch { return null; }
    }

    private static double? TryDecDegrees(NINA.Astrometry.InputTarget? t) {
        try { return t?.InputCoordinates?.Coordinates?.Dec; }
        catch { return null; }
    }
}
