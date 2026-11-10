namespace Focale.NINA.Plugin.Api;

/// <summary>
/// Endpoint paths on the Focale API (arcsecond-back). The back-end mounts every app at the URL root
/// (no /api/v1 prefix), so paths here begin with the app namespace.
///
/// Mappings (no new endpoints created — the plugin reuses what arcsecond-back already exposes):
///   Auth       — accounts.urls
///   Plan       — profiles.urls (the user's Focale tier)
///   Frames     — datasets.urls (DataFile, auto-ingest)
///   Storages   — xstorages.urls (DataStorage: the user's own storages, as upload destinations)
///   Events     — observingruns.urls (NightEvent, nested under the NightLog)
///   Plans      — observingruns.urls (NightPlan and its exposure plan, read to build NINA targets;
///                each entry's status set as NINA runs its target)
/// </summary>
public static class FocaleEndpoints {
    // --- Auth (accounts/urls.py) ---
    public const string Login            = "/auth/token/";
    public const string RefreshToken     = "/auth/token/refresh/";
    public const string Logout           = "/auth/logout/";

    // --- Profile (profiles/urls.py) ---
    /// <summary>GET: the owner's private profile, with <c>focale_tier</c> and <c>focale_storage</c>.</summary>
    public const string Profile          = "/profiles/{0}/";

    // --- File upload (datasets/urls.py) ---
    /// <summary>POST: auto-ingest endpoint; GET ?sha256=h1,h2: the files the user already holds. Multipart with `auto_ingest=true` +
    /// `persist_file=true` + `source=observer_console` + `sha256=<hex>`. One
    /// round-trip creates the DataFile, NightLog, Dataset, ObservingSite/
    /// Telescope/Camera (from the file header), Observation/Calibration, target
    /// resolution, and pixel-metrics extraction.</summary>
    public const string DataFiles        = "/datafiles/";

    // --- The user's storages (xstorages/urls.py) ---
    /// <summary>GET: the storages the user is a member of.</summary>
    public const string DataStorages     = "/datastorages/";

    // --- Night events (observingruns/urls.py) ---
    /// <summary>POST one event or a batch onto a NightLog (idempotent on `client_id`); GET lists them.</summary>
    public const string NightLogEvents   = "/nightlogs/{0}/events/";

    // --- Night plans (observingruns/urls.py) ---
    /// <summary>GET ?date=yyyy-MM-dd: the user's night plans of that night (Focale's Tonight).</summary>
    public const string NightPlans         = "/nightplans/";
    /// <summary>GET: a night plan's entries with their exposure lines.</summary>
    public const string NightPlanExposures = "/nightplans/{0}/exposures/";
    /// <summary>PATCH <c>{"status": "active"|"done"|"skipped"}</c>: one entry's progress through the night
    /// (observingruns.views.observations; owner only, not plan-gated). {0} the plan, {1} the entry (an Observation).</summary>
    public const string NightPlanEntry     = "/nightplans/{0}/observations/{1}/";
}
