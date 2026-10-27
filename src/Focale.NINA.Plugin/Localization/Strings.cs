namespace Focale.NINA.Plugin.Localization;

/// <summary>
/// Plugin-local UI strings. Kept as constants for now; promote to a .resx with satellite
/// assemblies if/when translations are added.
///
/// NINA-shared strings (e.g. labels reused across the host app) should come from
/// NINA.Core.Locale.Loc.Instance, not from here. Adding new keys to NINA's resx must
/// go through Crowdin per the upstream contribution guide.
/// </summary>
public static class Strings {
    public const string PluginDisplayName = "Focale";
    public const string PluginShortDesc   = "Uploads every FITS / XISF frame NINA saves to Focale.";
}
