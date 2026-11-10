using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Newtonsoft.Json;

namespace Focale.NINA.Plugin.Push;

/// <summary>
/// A step <see cref="NinaTargetBuilder"/> puts at the start and at the end of each target it builds from
/// Focale's night plan, so Focale learns what ran: it sets the entry's status ("active", then "done").
/// It carries the plan and entry it reports on, so a saved sequence keeps reporting when reloaded. It never
/// fails the sequence and never waits: the status is handed to <see cref="PlanStatusReporter"/>, which
/// sends it in the background (later, when offline).
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public abstract class PlanStatusItem : SequenceItem, IValidatable {
    private IList<string> _issues = new List<string>();

    /// <summary>
    /// Sets what NINA's item factory sets from the [ExportMetadata] for an item picked in the sequencer, as
    /// <see cref="NinaTargetBuilder"/> creates these itself.
    /// </summary>
    protected PlanStatusItem(string name, string description) {
        Name        = name;
        Description = description;
        Category    = "Focale";
        try {
            if (System.Windows.Application.Current?.TryFindResource("Focale_MarkSVG") is System.Windows.Media.GeometryGroup icon) Icon = icon;
        } catch { /* outside NINA (tests): no icon */ }
    }

    protected PlanStatusItem(PlanStatusItem copyMe) {
        CopyMetaData(copyMe);
        PlanUuid  = copyMe.PlanUuid;
        EntryUuid = copyMe.EntryUuid;
    }

    /// <summary>The status this step sets.</summary>
    public abstract string PlanStatus { get; }

    public const string ActiveDescription = "Sets the night plan entry this target was built from to \"active\" in Focale. Added by the \"Focale → NINA plans\" panel.";
    public const string DoneDescription   = "Sets the night plan entry this target was built from to \"done\" in Focale. Added by the \"Focale → NINA plans\" panel.";

    [JsonProperty] public string? PlanUuid  { get; set; }
    [JsonProperty] public string? EntryUuid { get; set; }

    public IList<string> Issues { get => _issues; set { _issues = value; RaisePropertyChanged(); } }

    /// <summary>What the step shows in the sequencer.</summary>
    public string Explanation => HasEntry
        ? $"Sets this target's status in Focale's night plan to \"{PlanStatus}\"."
        : "Not linked to a night plan entry: add targets from the \"Focale → NINA plans\" panel.";

    private bool HasEntry => !string.IsNullOrEmpty(PlanUuid) && !string.IsNullOrEmpty(EntryUuid);

    public bool Validate() {
        var issues = new List<string>();
        if (!HasEntry) issues.Add("Not linked to a Focale night plan entry; it does nothing. Add targets from the \"Focale → NINA plans\" panel.");
        Issues = issues;
        return issues.Count == 0;
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
        try {
            FocaleRuntime.PlanStatus?.Report(PlanUuid, EntryUuid, PlanStatus);
        } catch {
            // Best-effort: reporting to Focale never fails the sequence.
        }
        return Task.CompletedTask;
    }

    public override string ToString() => $"Category: {Category}, Item: {GetType().Name}, Status: {PlanStatus}, Entry: {EntryUuid}";
}

[ExportMetadata("Name", "Focale: mark active")]
[ExportMetadata("Description", ActiveDescription)]
[ExportMetadata("Icon", "Focale_MarkSVG")]
[ExportMetadata("Category", "Focale")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class MarkPlanEntryActive : PlanStatusItem {
    [ImportingConstructor]
    public MarkPlanEntryActive() : base("Focale: mark active", ActiveDescription) { }

    private MarkPlanEntryActive(MarkPlanEntryActive copyMe) : base(copyMe) { }

    public override string PlanStatus => PlanEntryStatus.Active;
    public override object Clone() => new MarkPlanEntryActive(this);
}

[ExportMetadata("Name", "Focale: mark done")]
[ExportMetadata("Description", DoneDescription)]
[ExportMetadata("Icon", "Focale_MarkSVG")]
[ExportMetadata("Category", "Focale")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class MarkPlanEntryDone : PlanStatusItem {
    [ImportingConstructor]
    public MarkPlanEntryDone() : base("Focale: mark done", DoneDescription) { }

    private MarkPlanEntryDone(MarkPlanEntryDone copyMe) : base(copyMe) { }

    public override string PlanStatus => PlanEntryStatus.Done;
    public override object Clone() => new MarkPlanEntryDone(this);
}
