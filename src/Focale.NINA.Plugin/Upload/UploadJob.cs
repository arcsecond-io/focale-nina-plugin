using System;
using Focale.NINA.Plugin.Api;

namespace Focale.NINA.Plugin.Upload;

public enum UploadJobStatus {
    Pending  = 0,
    Working  = 1,
    Done     = 2,
    Dead     = 3,   // permanent failure (4xx, missing file, etc.) — not retried
}

public sealed class UploadJob {
    public long Id { get; set; }
    public string LocalPath { get; set; } = "";
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// Local capture time (from <c>ImageMetaData.Image.ExposureStart</c>). The
    /// backend reads the canonical DATE-OBS from the FITS header — this field
    /// is purely diagnostic now, kept for queue / dead-letter introspection
    /// and the friendly enqueue log line.
    /// </summary>
    public DateTime CaptureLocal { get; set; }

    /// <summary>
    /// Observation / Calibration — derived client-side from <c>IMAGETYP</c>
    /// for diagnostics & log filtering. The backend re-classifies from the
    /// same header card (`_common.classify_from_header`) so the result is
    /// authoritative there, not here.
    /// </summary>
    public FrameClass FrameClass { get; set; }

    public DateTime EnqueuedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NotBeforeUtc { get; set; } = DateTime.UtcNow;
    public int Attempt { get; set; }
    public UploadJobStatus Status { get; set; } = UploadJobStatus.Pending;
    public string? LastError { get; set; }
}
