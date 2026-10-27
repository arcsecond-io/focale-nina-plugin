using System;
using System.IO;

namespace Focale.NINA.Plugin.Common;

/// <summary>
/// The image formats Focale ingests: FITS and XISF, the two NINA writes that carry a full
/// header (datasets.services.is_file_a_fits_or_xisf on the back-end). NINA's TIFF and RAW
/// saves are left alone — the server would refuse them, and each would end up dead-lettered.
/// </summary>
public static class ImageFileTypes {
    public static bool IsSupported(string? path) => KindOf(path) is not null;

    /// <summary>The multipart content type for the file part. The server sniffs the bytes anyway.</summary>
    public static string ContentTypeFor(string path) => KindOf(path) switch {
        Kind.Fits => "image/fits",
        _         => "application/octet-stream",   // XISF has no registered media type
    };

    private enum Kind { Fits, Xisf }

    private static Kind? KindOf(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var ext = Path.GetExtension(path);
        if (ext.Equals(".fits", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fit", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fts", StringComparison.OrdinalIgnoreCase)) return Kind.Fits;
        if (ext.Equals(".xisf", StringComparison.OrdinalIgnoreCase)) return Kind.Xisf;
        return null;
    }
}
