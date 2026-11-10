using System;

namespace Focale.NINA.Plugin.Api;

public enum FocaleTier { Free = 0, Observer = 1, Imager = 2, Pro = 3 }

/// <summary>
/// The user's Focale plan, read from <c>focale_tier</c> on their profile, and what it includes for
/// N.I.N.A. Mirrors the back-end gates (entitlements/focale.py): <c>nina_import</c> needs Observer,
/// <c>nina_push</c> needs Imager.
/// </summary>
public sealed record FocalePlan(FocaleTier Tier, long? StorageLimitBytes) {
    public const string UpgradeUrl = "https://focale.space/#pricing";

    /// <summary>NINA → Focale sync (frames, sessions, night events): Observer and above.</summary>
    public bool IncludesNinaSync => Tier >= FocaleTier.Observer;

    /// <summary>Focale → NINA plans (targets and exposure plans written in Focale's Tonight): Imager and above.</summary>
    public bool IncludesNinaPlans => Tier >= FocaleTier.Imager;

    public string Name => Tier.ToString();

    /// <summary>Null for a value the plugin does not know (a newer back-end): no gate is assumed then.</summary>
    public static FocaleTier? ParseTier(string? value) => value?.Trim().ToLowerInvariant() switch {
        "free"     => FocaleTier.Free,
        "observer" => FocaleTier.Observer,
        "imager"   => FocaleTier.Imager,
        "pro"      => FocaleTier.Pro,
        _          => null,
    };

    /// <summary>One line for the settings page.</summary>
    public string Describe() {
        var storage = StorageLimitBytes is > 0 ? $", {FormatBytes(StorageLimitBytes.Value)} of Focale storage" : "";
        var nina = IncludesNinaPlans ? "NINA → Focale sync and Focale → NINA plans included"
                 : IncludesNinaSync  ? "NINA → Focale sync included; Focale → NINA plans need Imager"
                 :                     "NINA → Focale sync needs Observer or above";
        return $"Focale {Name} plan{storage}. {nina}.";
    }

    private static string FormatBytes(long bytes) {
        const double gb = 1024d * 1024 * 1024;
        return bytes >= 1024 * gb ? $"{bytes / (1024 * gb):0.#} TB" : $"{Math.Round(bytes / gb)} GB";
    }
}
