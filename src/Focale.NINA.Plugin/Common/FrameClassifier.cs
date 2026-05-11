using System;
using Focale.NINA.Plugin.Api;

namespace Focale.NINA.Plugin.Common;

/// <summary>
/// Classifies a captured frame as observation (light) vs calibration (dark/bias/flat).
/// Maps NINA's ImageType strings — see NINA.Core.Enum.CaptureSequence.ImageTypes — onto Focale's two-dataset model.
///
/// NINA writes the same string into the FITS <c>IMAGETYP</c> keyword
/// (NINA.Image/FileFormat/FITS/CFitsioFITS.cs:102), so the server-side classifier
/// can reach the same conclusion from headers alone.
/// </summary>
public static class FrameClassifier {
    public static FrameClass FromNinaImageType(string? imageType) {
        if (string.IsNullOrWhiteSpace(imageType)) return FrameClass.Observation;
        var t = imageType.Trim().ToUpperInvariant();
        return t switch {
            "LIGHT"     => FrameClass.Observation,
            "SNAPSHOT"  => FrameClass.Observation,
            "DARK"      => FrameClass.Calibration,
            "BIAS"      => FrameClass.Calibration,
            "FLAT"      => FrameClass.Calibration,
            "DARKFLAT"  => FrameClass.Calibration,
            "FLATDARK"  => FrameClass.Calibration,
            "BIASDARK"  => FrameClass.Calibration,
            _           => FrameClass.Observation,   // unknown → safer to keep with science frames
        };
    }
}
