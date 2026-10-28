using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Common;
using Focale.NINA.Plugin.Push;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;

namespace Focale.NINA.Plugin.Ui;

/// <summary>A NINA target template the panel can build from.</summary>
public sealed class TonightTemplate {
    public TonightTemplate(string name, IDeepSkyObjectContainer container) {
        Name      = name;
        Container = container;
    }

    public string Name { get; }
    public IDeepSkyObjectContainer Container { get; }
    public override string ToString() => Name;
}

/// <summary>One entry of tonight's plan, as the panel lists it.</summary>
public sealed class TonightEntry {
    public TonightEntry(PlannedTarget plan, string time, string? status, ICommand add) {
        Plan       = plan;
        Time       = time;
        Status     = status;
        AddCommand = add;
    }

    public PlannedTarget Plan { get; }
    public string Name => Plan.Name;
    public string Summary => Plan.Summary;
    public string Time { get; }
    public string? Status { get; }
    public bool CanAdd => Plan.CanAdd;
    public bool HasWarning => Plan.UnknownFilters.Count > 0 || !Plan.CanAdd;
    public ICommand AddCommand { get; }
}

/// <summary>
/// "Focale Tonight": the night's plan from Focale's Tonight (targets, times, exposure lines), with
/// "Add to sequence" per target and "Add all". Each target is built from the template the user picks
/// (<see cref="NinaTargetBuilder"/>) and added to the Advanced Sequencer. Nothing changes in the
/// sequence unless the user clicks. Reading the plan is not gated; writing it (in Focale) is Imager.
/// </summary>
[Export(typeof(IDockableVM))]
public sealed class FocaleTonightDockable : DockableVM {
    private readonly ISequenceMediator _sequenceMediator;
    private string _status = "Open to load tonight's plan from Focale.";
    private string _nightLabel = "";
    private bool _isLoading;
    private TonightTemplate? _selectedTemplate;

    [ImportingConstructor]
    public FocaleTonightDockable(IProfileService profileService, ISequenceMediator sequenceMediator) : base(profileService) {
        _sequenceMediator = sequenceMediator;
        Title             = "Focale Tonight";
        // A target reticle, as NINA's own panels use a single-path icon.
        var icon = new GeometryGroup();
        icon.Children.Add(Geometry.Parse("M12,2A10,10 0 1,0 22,12A10,10 0 0,0 12,2M12,4A8,8 0 1,1 4,12A8,8 0 0,1 12,4M12,7A5,5 0 1,0 17,12A5,5 0 0,0 12,7M12,9A3,3 0 1,1 9,12A3,3 0 0,1 12,9M11,0H13V5H11ZM11,19H13V24H11ZM0,11H5V13H0ZM19,11H24V13H19Z"));
        icon.Freeze();
        ImageGeometry  = icon;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AddAllCommand  = new RelayCommand(AddAll);
    }

    public ObservableCollection<TonightEntry> Entries { get; } = new();
    public ObservableCollection<TonightTemplate> Templates { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand AddAllCommand { get; }

    public string Status { get => _status; private set { _status = value; RaisePropertyChanged(); } }
    public string NightLabel { get => _nightLabel; private set { _nightLabel = value; RaisePropertyChanged(); } }
    public bool IsLoading { get => _isLoading; private set { _isLoading = value; RaisePropertyChanged(); } }

    public TonightTemplate? SelectedTemplate {
        get => _selectedTemplate;
        set {
            _selectedTemplate = value;
            if (FocaleRuntime.Settings is { } settings && value is not null) settings.TonightTemplateName = value.Name;
            RaisePropertyChanged();
        }
    }

    private async Task RefreshAsync() {
        var api = FocaleRuntime.Api;
        if (api is null) { Status = "The Focale plugin is still starting."; return; }
        if (!api.IsAuthenticated) { Status = "Sign in to Focale in Options → Plugins → Focale."; return; }

        LoadTemplates();
        var night = NightDate.Format(NightDate.Today());
        NightLabel = $"Night of {night}";
        IsLoading  = true;
        Status     = "Loading tonight's plan…";
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var plan = await api.GetExposurePlanAsync(night, cts.Token);
            Entries.Clear();
            if (plan is null || plan.Entries.Count == 0) {
                Status = "No plan for tonight. Plan it in Focale's Tonight, then refresh.";
                return;
            }
            var filters = WheelFilterNames();
            foreach (var entry in plan.Entries) {
                var planned = TargetPlanner.Plan(entry, filters);
                Entries.Add(new TonightEntry(planned, TimeLabel(entry.Start, entry.DurationSeconds), entry.Status,
                                             new RelayCommand(() => Add(planned))));
            }
            Status = $"{plan.Entries.Count} target(s) planned. Choose a template, then add them to the sequence.";
        } catch (FocaleApiException ex) {
            Status = $"Focale refused: {ex.ServerMessage ?? ex.Message}";
        } catch (Exception ex) {
            Status = $"Could not load tonight's plan: {ex.Message}";
            Logger.Warning($"Focale: loading tonight's plan failed — {ex.Message}");
        } finally {
            IsLoading = false;
        }
    }

    private void LoadTemplates() {
        var keep = _selectedTemplate?.Name ?? FocaleRuntime.Settings?.TonightTemplateName;
        Templates.Clear();
        foreach (var container in _sequenceMediator.GetDeepSkyObjectContainerTemplates()) {
            var name = (container as SequenceContainer)?.Name ?? container.ToString() ?? "Template";
            Templates.Add(new TonightTemplate(name, container));
        }
        SelectedTemplate = Templates.FirstOrDefault(t => t.Name == keep) ?? Templates.FirstOrDefault();
    }

    private System.Collections.Generic.List<string> WheelFilterNames() =>
        profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.Select(f => f.Name).ToList()
        ?? new System.Collections.Generic.List<string>();

    private bool Add(PlannedTarget plan, bool quiet = false) {
        if (_selectedTemplate is null) {
            Status = "Choose a target template first (save one from the Advanced Sequencer if you have none).";
            return false;
        }
        try {
            var filters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.ToList();
            var container = NinaTargetBuilder.Build(_selectedTemplate.Container, plan, filters);
            _sequenceMediator.AddAdvancedTarget(container);
            if (!quiet) Status = $"{plan.Name} added to the sequence.";
            return true;
        } catch (NinaTargetBuilder.BuildException ex) {
            Status = $"{plan.Name}: {ex.Message}";
        } catch (Exception ex) {
            Status = $"{plan.Name} could not be added: {ex.Message}";
            Logger.Error($"Focale: building {plan.Name} failed", ex);
        }
        return false;
    }

    private void AddAll() {
        var candidates = Entries.Where(e => e.CanAdd && e.Status is not ("done" or "skipped")).ToList();
        if (candidates.Count == 0) { Status = "Nothing to add."; return; }
        var added = candidates.Count(entry => Add(entry.Plan, quiet: true));
        if (added == candidates.Count) Status = $"{added} target(s) added to the sequence.";
        else if (added > 0) Status = $"{added} of {candidates.Count} target(s) added; see the others' notes.";
    }

    private static string TimeLabel(string? startIso, double durationSeconds) {
        var duration = durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : (TimeSpan?)null;
        var durationText = duration is { } d ? (d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00}" : $"{d.Minutes} min") : "";
        if (DateTimeOffset.TryParse(startIso, out var start)) {
            var local = start.ToLocalTime();
            return durationText.Length > 0 ? $"{local:HH:mm} · {durationText}" : $"{local:HH:mm}";
        }
        return durationText;
    }
}
