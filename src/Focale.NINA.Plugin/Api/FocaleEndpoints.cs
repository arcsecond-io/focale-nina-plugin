namespace Focale.NINA.Plugin.Api;

/// <summary>
/// Endpoint paths on arcsecond-back. The back-end mounts every app at the URL root
/// (no /api/v1 prefix), so paths here begin with the app namespace.
///
/// Mappings (no new endpoints created — the plugin reuses what arcsecond-back already exposes):
///   Auth       — accounts.urls
///   Sessions   — observingruns.urls (NightLog)
///   Frames     — observingruns.urls (Observation) + datasets.urls (DataFile)
///   Targets    — targets.urls (XTarget)
///   Equipment  — equipments.urls (Camera, Mount, …) + equipmentmodels.urls
///   Sites      — observingsites.urls
/// </summary>
public static class FocaleEndpoints {
    // --- Auth (accounts/urls.py) ---
    public const string Login            = "/auth/token/";
    public const string RefreshToken     = "/auth/token/refresh/";
    public const string Logout           = "/auth/logout/";

    // --- Profile / whoami (profiles/urls.py) ---
    /// <summary>Resolves the current user's profile — used by "Test connection".</summary>
    public const string Profiles         = "/profiles/";   // GET ?username=…  (or /profiles/<username>/)

    // --- Session container (observingruns/urls.py) ---
    /// <summary>POST: create a NightLog. Used as the per-NINA-process session container.</summary>
    public const string NightLogs        = "/observingruns/nightlogs/";
    /// <summary>PATCH/GET {uuid}.</summary>
    public const string NightLog         = "/observingruns/nightlogs/{0}/";

    // --- File upload (datasets/urls.py) ---
    /// <summary>POST: auto-ingest endpoint. Multipart with `auto_ingest=true` +
    /// `persist_file=true` + `source=observer_console` + `sha256=<hex>`. One
    /// round-trip creates the DataFile, NightLog, Dataset, ObservingSite/
    /// Telescope/Camera (from FITS header), Observation/Calibration, target
    /// resolution, and pixel-metrics extraction. Replaces the previous
    /// two-write flow (`/datasets/<uuid>/datafiles/` + per-frame record POST).</summary>
    public const string DataFiles        = "/datafiles/";

    // --- Sequence push (observingsequences/urls.py) ---
    /// <summary>POST: create an ObservingSequence (one per NINA SequenceStarting event).</summary>
    public const string ObservingSequences        = "/observingsequences/";
    /// <summary>PATCH/GET {id}: status transitions etc.</summary>
    public const string ObservingSequence         = "/observingsequences/{0}/";
    /// <summary>POST: create a SequenceStep (one per NINA target container).</summary>
    public const string ObservingSequenceSteps    = "/observingsequencesteps/";
    /// <summary>POST: create a SequenceTask (one per NINA instruction).</summary>
    public const string ObservingSequenceTasks    = "/observingsequencetasks/";
}
