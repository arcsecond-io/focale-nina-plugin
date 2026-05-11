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
    /// <summary>POST: create a Dataset container (one per night per classification).</summary>
    public const string Datasets         = "/datasets/";
    /// <summary>POST {uuid}: register a DataFile + receive a presigned upload URL.</summary>
    public const string DatasetFiles     = "/datasets/{0}/datafiles/";

    // --- Targets (targets/urls.py) ---
    public const string Targets         = "/targets/";
    public const string TargetDetail    = "/targets/{0}/";

    // --- Per-frame records (observingruns/urls.py) ---
    public const string NightLogObservationsList = "/observingruns/nightlogs/{0}/observations/";
    public const string NightLogCalibrationsList = "/observingruns/nightlogs/{0}/calibrations/";

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
