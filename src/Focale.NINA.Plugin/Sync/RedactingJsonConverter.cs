using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Focale.NINA.Plugin.Sync;

/// <summary>
/// Replaces values of property names matching the secret regex with a redaction marker.
/// Applied at serialization time via a custom <see cref="DefaultContractResolver"/>.
/// Also strips userinfo (user:pass@) from URI-shaped string values to catch credentials
/// embedded in equipment driver connection strings.
/// </summary>
public static class Redaction {
    public const string Marker = "***REDACTED***";

    private static readonly Regex SecretNameRegex = new(
        @"(?:password|passwd|pwd|api[_\-]?key|secret|token|credential|bearer|auth)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Explicit allowlist of property names that LOOK like secrets but actually aren't.
    /// Add here only after a deliberate review. Empty by default — fail safe.
    /// </summary>
    public static readonly HashSet<string> AllowList = new(StringComparer.OrdinalIgnoreCase) {
        // e.g. "OAuthEndpointUrl" — not a secret despite containing "Auth"
    };

    public static bool IsSecretName(string propertyName) {
        if (string.IsNullOrEmpty(propertyName)) return false;
        if (AllowList.Contains(propertyName)) return false;
        return SecretNameRegex.IsMatch(propertyName);
    }

    public static string? StripUriCredentials(string? value) {
        if (string.IsNullOrEmpty(value)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        if (string.IsNullOrEmpty(uri.UserInfo)) return value;

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.ToString();
    }

    public static JsonSerializerSettings BuildSettings() => new() {
        ContractResolver       = new RedactingContractResolver(),
        ReferenceLoopHandling  = ReferenceLoopHandling.Ignore,
        NullValueHandling      = NullValueHandling.Include,
        Formatting             = Formatting.None,
        // Defensive cap. NINA profiles are tree-shaped; deeper than this is almost certainly a cycle.
        MaxDepth               = 32,
    };

    public static string SerializeRedacted(object obj) =>
        JsonConvert.SerializeObject(obj, BuildSettings());

    public static JObject ToRedactedJObject(object obj) =>
        JObject.Parse(SerializeRedacted(obj));
}

internal sealed class RedactingContractResolver : DefaultContractResolver {
    protected override JsonProperty CreateProperty(System.Reflection.MemberInfo member, MemberSerialization memberSerialization) {
        var prop = base.CreateProperty(member, memberSerialization);
        var name = prop.PropertyName ?? member.Name;

        if (Redaction.IsSecretName(name)) {
            prop.ValueProvider = new ConstantValueProvider(Redaction.Marker);
            return prop;
        }

        // For string-typed fields, post-process to strip userinfo from URIs.
        if (prop.PropertyType == typeof(string) && prop.ValueProvider != null) {
            prop.ValueProvider = new UriCredentialStrippingValueProvider(prop.ValueProvider);
        }
        return prop;
    }

    private sealed class ConstantValueProvider : IValueProvider {
        private readonly object? _value;
        public ConstantValueProvider(object? value) => _value = value;
        public object? GetValue(object target) => _value;
        public void SetValue(object target, object? value) { /* read-only at serialization */ }
    }

    private sealed class UriCredentialStrippingValueProvider : IValueProvider {
        private readonly IValueProvider _inner;
        public UriCredentialStrippingValueProvider(IValueProvider inner) => _inner = inner;
        public object? GetValue(object target) {
            var raw = _inner.GetValue(target) as string;
            return Redaction.StripUriCredentials(raw);
        }
        public void SetValue(object target, object? value) => _inner.SetValue(target, value!);
    }
}
