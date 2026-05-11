using System;

namespace Focale.NINA.Plugin.Common;

/// <summary>
/// Focale "night" convention: a night runs from local noon (date D) to local noon (date D+1)
/// and is dated D.
///   T.Hour &lt; 12  → night = T.Date - 1
///   T.Hour ≥ 12  → night = T.Date
///
/// Used to key the NightLog and to name the per-night Datasets:
///     "NINA - {NightDate:yyyy-MM-dd} - Observations"
///     "NINA - {NightDate:yyyy-MM-dd} - Calibrations"
///
/// Always computed from local time. Frames captured at 02:00 local on May 12
/// belong to night 2026-05-11, not 2026-05-12.
/// </summary>
public static class NightDate {
    /// <summary>Returns the date of the night that contains the given local timestamp.</summary>
    public static DateOnly FromLocal(DateTime localTimestamp) {
        var d = DateOnly.FromDateTime(localTimestamp);
        return localTimestamp.Hour < 12 ? d.AddDays(-1) : d;
    }

    /// <summary>Returns the date of the night that contains the given UTC timestamp, evaluated in the local zone.</summary>
    public static DateOnly FromUtc(DateTime utcTimestamp) =>
        FromLocal(utcTimestamp.ToLocalTime());

    public static DateOnly Today() => FromLocal(DateTime.Now);

    /// <summary>Stringly formatted as ISO yyyy-MM-dd, suitable for Dataset names and DateField bodies.</summary>
    public static string Format(DateOnly d) => d.ToString("yyyy-MM-dd");
}
