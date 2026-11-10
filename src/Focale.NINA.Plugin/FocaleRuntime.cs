using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Push;
using Focale.NINA.Plugin.Settings;

namespace Focale.NINA.Plugin;

/// <summary>
/// What the plugin's other MEF parts share with <see cref="FocalePlugin"/>. NINA composes a dock panel
/// and sequence items on its own, without the plugin instance, so they read the API client and the settings here;
/// they are set when the plugin initializes, and null before (the panel then says so).
/// </summary>
internal static class FocaleRuntime {
    public static FocaleApiClient? Api { get; set; }
    public static FocaleSettings? Settings { get; set; }
    /// <summary>Where the sequence's Focale steps hand the night plan statuses they set.</summary>
    public static PlanStatusReporter? PlanStatus { get; set; }
}
