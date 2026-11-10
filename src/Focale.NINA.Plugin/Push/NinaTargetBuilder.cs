using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Imaging;

namespace Focale.NINA.Plugin.Push;

/// <summary>
/// Builds a NINA target container from a <see cref="PlannedTarget"/> and one of the user's own target
/// templates, so the result runs the way they run every target (slew and center, autofocus, dithering,
/// triggers…): the template is cloned, its target set (name, J2000 coordinates, position angle), and
/// its Smart Exposures replaced by one per planned line — each a clone of the template's first, so
/// its own settings (dithering, error behaviour) carry over. With no line planned, the template's
/// exposures are kept as they are.
///
/// When the plan is known (<see cref="PlannedTarget.PlanUuid"/>), the target starts with "Focale: mark
/// active" and ends with "Focale: mark done" (<see cref="PlanStatusItem"/>), so Focale learns what ran.
///
/// Only members present in NINA 3.2 and 3.3 are used (see tools/NinaCompatCheck). In 3.3 the exposure
/// time, gain, offset and iterations are expressions; their scalar setters, used here, set them.
/// </summary>
public static class NinaTargetBuilder {
    public sealed class BuildException : Exception {
        public BuildException(string message) : base(message) { }
    }

    public static IDeepSkyObjectContainer Build(IDeepSkyObjectContainer template, PlannedTarget plan,
                                                IReadOnlyList<FilterInfo> wheelFilters) {
        if (!plan.CanAdd) throw new BuildException(plan.Blocker!);
        if (template.Clone() is not DeepSkyObjectContainer container) {
            throw new BuildException("The chosen template is not a target container.");
        }

        container.Name = plan.Name;
        var target = container.Target;
        target.TargetName = plan.Name;
        target.InputCoordinates.Coordinates = new Coordinates(
            Angle.ByDegree(plan.RaDegrees!.Value), Angle.ByDegree(plan.DecDegrees!.Value), Epoch.J2000);
        if (plan.PositionAngle is { } pa) target.PositionAngle = pa;

        if (!plan.KeepsTemplateExposures) PlanExposures(container, plan, wheelFilters);
        AddStatusSteps(container, plan);
        return container;
    }

    /// <summary>
    /// Puts "mark active" first and "mark done" last in the target, for its entry. Steps a template saved
    /// from an earlier target carry another entry: they are removed first.
    /// </summary>
    internal static void AddStatusSteps(SequenceContainer container, PlannedTarget plan) {
        foreach (var stale in Descendants(container).OfType<PlanStatusItem>().ToList()) {
            (stale.Parent as SequenceContainer)?.Remove(stale);
        }
        if (string.IsNullOrEmpty(plan.PlanUuid) || string.IsNullOrEmpty(plan.EntryUuid)) return;
        container.InsertIntoSequenceBlocks(0, new MarkPlanEntryActive { PlanUuid = plan.PlanUuid, EntryUuid = plan.EntryUuid });
        container.Add(new MarkPlanEntryDone { PlanUuid = plan.PlanUuid, EntryUuid = plan.EntryUuid });
    }

    private static void PlanExposures(SequenceContainer container, PlannedTarget plan, IReadOnlyList<FilterInfo> wheelFilters) {

        var smartExposures = Descendants(container).OfType<SmartExposure>().ToList();
        if (smartExposures.Count == 0) {
            throw new BuildException("The chosen template has no Smart Exposure to plan the exposures with. " +
                                     "Add one to the template, or choose another.");
        }
        var prototype = smartExposures[0];
        if (prototype.Parent is not SequenceContainer parent) {
            throw new BuildException("The template's Smart Exposure is not inside a container.");
        }
        var index = parent.Items.IndexOf(prototype);
        foreach (var existing in smartExposures) {
            (existing.Parent as SequenceContainer)?.Remove(existing);
        }

        foreach (var (exposure, offset) in plan.Exposures.Select((e, i) => (e, i))) {
            if (prototype.Clone() is not SmartExposure item) continue;
            var filter = wheelFilters.FirstOrDefault(f => f.Name == exposure.Filter);
            if (filter is not null) item.GetSwitchFilter().Filter = filter;
            var takeExposure = item.GetTakeExposure();
            takeExposure.ExposureTime = exposure.ExposureSeconds;
            takeExposure.Binning = new BinningMode((short)exposure.Binning, (short)exposure.Binning);
            item.GetLoopCondition().Iterations = exposure.Count;
            item.Name = $"{exposure.Filter} {exposure.Count}×{exposure.ExposureSeconds:0.##}s";
            parent.InsertIntoSequenceBlocks(Math.Min(index + offset, parent.Items.Count), item);
        }
    }

    private static IEnumerable<ISequenceItem> Descendants(ISequenceContainer container) {
        foreach (var item in container.Items) {
            yield return item;
            if (item is ISequenceContainer child) {
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
    }
}
