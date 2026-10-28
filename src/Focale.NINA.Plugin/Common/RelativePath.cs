using System;
using System.IO;

namespace Focale.NINA.Plugin.Common;

/// <summary>
/// Where a saved frame sits relative to NINA's image folder (Options → Imaging → Image file path),
/// e.g. <c>2026-10-27/M31/LIGHT/M31_0001.fits</c>. Sent with uploads to the user's own storage, where
/// Focale mirrors it under <c>Focale/</c>, so the copy keeps the folders NINA's file pattern made.
/// </summary>
public static class RelativePath {
    /// <summary>The path of <paramref name="file"/> below <paramref name="baseFolder"/>, with forward
    /// slashes; just the file name when it is not below it (or no folder is set).</summary>
    public static string Of(string file, string? baseFolder) {
        var name = Path.GetFileName(file);
        if (string.IsNullOrWhiteSpace(baseFolder)) return name;
        try {
            var relative = Path.GetRelativePath(Path.GetFullPath(baseFolder), Path.GetFullPath(file));
            if (Path.IsPathRooted(relative) || relative == "." || relative.StartsWith("..", StringComparison.Ordinal)) {
                return name;
            }
            return relative.Replace('\\', '/');
        } catch (Exception) {
            return name;
        }
    }
}
