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

    /// <summary>Local capture time (from <c>ImageMetaData.Image.ExposureStart</c>). Used by SessionLifecycle to pick the correct night-date.</summary>
    public DateTime CaptureLocal { get; set; }

    /// <summary>Observations or Calibrations — picks which of the night's two Datasets the upload lands in.</summary>
    public FrameClass FrameClass { get; set; }

    /// <summary>Original NINA image type (LIGHT / DARK / BIAS / FLAT / …) — drives Calibration.type mapping.</summary>
    public string ImageType { get; set; } = "";

    /// <summary>Target name from NINA (used to find-or-create the XTarget). May be empty for calibrations.</summary>
    public string TargetName { get; set; } = "";

    public double ExposureSeconds { get; set; }

    public double? PositionAngleDegrees { get; set; }

    public DateTime EnqueuedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NotBeforeUtc { get; set; } = DateTime.UtcNow;
    public int Attempt { get; set; }
    public UploadJobStatus Status { get; set; } = UploadJobStatus.Pending;
    public string? LastError { get; set; }
}
