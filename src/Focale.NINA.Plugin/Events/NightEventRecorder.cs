using System;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Common;
using Newtonsoft.Json.Linq;

namespace Focale.NINA.Plugin.Events;

/// <summary>The event kinds of the back-end's NightEvent (observingruns.models.events).</summary>
public static class NightEventKinds {
    public const string Sequence     = "sequence";
    public const string Autofocus    = "autofocus";
    public const string MeridianFlip = "meridian_flip";
    public const string Guiding      = "guiding";
    public const string Dither       = "dither";
    public const string Park         = "park";
    public const string Unpark       = "unpark";
    public const string Safety       = "safety";
}

/// <summary>
/// Turns what NINA reports into night events: spans for what has a beginning and an end (the
/// sequence, an autofocus run, a meridian flip), instants for the rest, and one guiding event per
/// exposure. Knows nothing of NINA's types — <see cref="NinaEventSource"/> translates — so the
/// rules below are testable on their own.
///
/// - A span opens when it starts and closes when it ends. NINA only reports *successful*
///   autofocus runs, so a run still open when the next starts, or when the sequence ends, is
///   closed as failed, with no end. The same goes for a meridian flip.
/// - Each event belongs to the night (local noon to noon) its start falls in.
/// - Calls may come from any NINA thread; they are applied one at a time.
/// </summary>
public sealed class NightEventRecorder {
    private readonly NightEventStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _openSequence;
    private string? _openAutofocus;
    private string? _openFlip;

    public NightEventRecorder(NightEventStore store) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task SequenceStartedAsync(DateTime utc) => Locked(async () => {
        await CloseAsync(ref _openSequence, end: null, success: null).ConfigureAwait(false);
        _openSequence = await OpenAsync(NightEventKinds.Sequence, utc).ConfigureAwait(false);
    });

    public Task SequenceFinishedAsync(DateTime utc) => Locked(async () => {
        await CloseAsync(ref _openAutofocus, end: null, success: false).ConfigureAwait(false);
        await CloseAsync(ref _openFlip, end: null, success: false).ConfigureAwait(false);
        if (_openSequence is null) return;
        await CloseAsync(ref _openSequence, end: utc, success: null).ConfigureAwait(false);
    });

    public Task AutofocusStartedAsync(DateTime utc) => Locked(async () => {
        await CloseAsync(ref _openAutofocus, end: null, success: false).ConfigureAwait(false);
        _openAutofocus = await OpenAsync(NightEventKinds.Autofocus, utc).ConfigureAwait(false);
    });

    public Task AutofocusFinishedAsync(DateTime utc, double? position, double? temperatureC, string? filter) => Locked(async () => {
        var data = new JObject();
        if (position is { } p && double.IsFinite(p)) data["position"] = p;
        if (temperatureC is { } t && double.IsFinite(t)) data["temperature_c"] = Math.Round(t, 2);
        if (!string.IsNullOrWhiteSpace(filter)) data["filter"] = filter;
        if (_openAutofocus is null) {
            // The start was missed (the plugin loaded mid-run): record the run as it ended.
            await AddAsync(NightEventKinds.Autofocus, utc, utc, success: true, data).ConfigureAwait(false);
            return;
        }
        var id = _openAutofocus;
        _openAutofocus = null;
        await _store.CloseAsync(id, utc, true, data.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
    });

    public Task MeridianFlipStartingAsync(DateTime utc) => Locked(async () => {
        await CloseAsync(ref _openFlip, end: null, success: false).ConfigureAwait(false);
        _openFlip = await OpenAsync(NightEventKinds.MeridianFlip, utc).ConfigureAwait(false);
    });

    public Task MeridianFlipFinishedAsync(DateTime utc, bool success) => Locked(async () => {
        if (_openFlip is null) {
            await AddAsync(NightEventKinds.MeridianFlip, utc, utc, success, new JObject()).ConfigureAwait(false);
            return;
        }
        await CloseAsync(ref _openFlip, end: utc, success: success).ConfigureAwait(false);
    });

    public Task DitheredAsync(DateTime utc) => Instant(NightEventKinds.Dither, utc);
    public Task ParkedAsync(DateTime utc) => Instant(NightEventKinds.Park, utc);
    public Task UnparkedAsync(DateTime utc) => Instant(NightEventKinds.Unpark, utc);

    public Task SafetyChangedAsync(DateTime utc, bool isSafe) =>
        Instant(NightEventKinds.Safety, utc, new JObject { ["is_safe"] = isSafe });

    /// <summary>
    /// The guiding over one exposure. NINA records RMS in guide-camera pixels plus the pixel scale;
    /// arcseconds are sent when the scale is known, pixels otherwise.
    /// </summary>
    public Task ExposureGuidedAsync(DateTime startUtc, DateTime endUtc, GuidingRms rms, string? fileName) => Locked(async () => {
        var data = rms.ToJson();
        if (!string.IsNullOrWhiteSpace(fileName)) data["file_name"] = fileName;
        await AddAsync(NightEventKinds.Guiding, startUtc, endUtc, success: null, data).ConfigureAwait(false);
    });

    private Task Instant(string kind, DateTime utc, JObject? data = null) => Locked(() =>
        AddAsync(kind, utc, end: null, success: null, data ?? new JObject()));

    private async Task<string> OpenAsync(string kind, DateTime startUtc) {
        var row = NewRow(kind, startUtc, end: null, success: null, new JObject());
        row.IsOpen = true;
        await _store.AddAsync(row).ConfigureAwait(false);
        return row.ClientId;
    }

    private Task AddAsync(string kind, DateTime start, DateTime? end, bool? success, JObject data) =>
        _store.AddAsync(NewRow(kind, start, end, success, data));

    private Task CloseAsync(ref string? openId, DateTime? end, bool? success) {
        if (openId is null) return Task.CompletedTask;
        var id = openId;
        openId = null;
        return _store.CloseAsync(id, end, success, null);
    }

    private static NightEventRow NewRow(string kind, DateTime start, DateTime? end, bool? success, JObject data) {
        var startUtc = AsUtc(start);
        return new NightEventRow {
            Kind      = kind,
            NightDate = NightDate.Format(NightDate.FromLocal(startUtc.ToLocalTime())),
            StartUtc  = startUtc,
            EndUtc    = end is null ? null : AsUtc(end.Value),
            Success   = success,
            DataJson  = data.ToString(Newtonsoft.Json.Formatting.None),
        };
    }

    /// <summary>NINA's timestamps are local unless marked otherwise.</summary>
    public static DateTime AsUtc(DateTime t) => t.Kind switch {
        DateTimeKind.Utc   => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(t, DateTimeKind.Local).ToUniversalTime(),
    };

    private async Task Locked(Func<Task> action) {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

/// <summary>NINA's per-exposure guiding RMS (NINA.Core.Model.RMS), in guide-camera pixels.</summary>
public readonly record struct GuidingRms(double Ra, double Dec, double Total, double PeakRa, double PeakDec,
                                         int DataPoints, double ArcsecPerPixel) {
    public bool HasData => DataPoints > 0 && double.IsFinite(Total);

    public JObject ToJson() {
        var o = new JObject { ["data_points"] = DataPoints };
        if (ArcsecPerPixel > 0 && double.IsFinite(ArcsecPerPixel)) {
            o["rms_ra_arcsec"]    = Round(Ra * ArcsecPerPixel);
            o["rms_dec_arcsec"]   = Round(Dec * ArcsecPerPixel);
            o["rms_total_arcsec"] = Round(Total * ArcsecPerPixel);
            o["peak_ra_arcsec"]   = Round(PeakRa * ArcsecPerPixel);
            o["peak_dec_arcsec"]  = Round(PeakDec * ArcsecPerPixel);
        } else {
            o["rms_ra_px"]    = Round(Ra);
            o["rms_dec_px"]   = Round(Dec);
            o["rms_total_px"] = Round(Total);
        }
        return o;
    }

    private static double Round(double v) => Math.Round(v, 3);
}
