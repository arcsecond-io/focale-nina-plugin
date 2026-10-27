namespace Focale.NINA.Plugin.Api;

/// <summary>
/// Endpoint paths on arcsecond-back. The back-end mounts every app at the URL root
/// (no /api/v1 prefix), so paths here begin with the app namespace.
///
/// Mappings (no new endpoints created — the plugin reuses what arcsecond-back already exposes):
///   Auth       — accounts.urls
///   Frames     — datasets.urls (DataFile, auto-ingest)
/// </summary>
public static class FocaleEndpoints {
    // --- Auth (accounts/urls.py) ---
    public const string Login            = "/auth/token/";
    public const string RefreshToken     = "/auth/token/refresh/";
    public const string Logout           = "/auth/logout/";

    // --- File upload (datasets/urls.py) ---
    /// <summary>POST: auto-ingest endpoint. Multipart with `auto_ingest=true` +
    /// `persist_file=true` + `source=observer_console` + `sha256=<hex>`. One
    /// round-trip creates the DataFile, NightLog, Dataset, ObservingSite/
    /// Telescope/Camera (from the file header), Observation/Calibration, target
    /// resolution, and pixel-metrics extraction.</summary>
    public const string DataFiles        = "/datafiles/";
}
