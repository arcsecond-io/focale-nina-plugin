using System;
using System.Security.Cryptography;
using System.Text;

namespace Focale.NINA.Plugin.Settings;

/// <summary>
/// Wraps Windows DPAPI for storing the Focale API key at rest.
/// Entropy is a fixed plugin-scoped salt so the ciphertext cannot be lifted into
/// a different application context on the same machine without knowing the salt.
/// </summary>
public static class SecretProtector {
    // Stable per-plugin entropy. Changing this value invalidates all previously stored secrets.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Focale.NINA.Plugin/v1/api-key");

    public static string Protect(string plaintext) {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string? Unprotect(string? base64) {
        if (string.IsNullOrEmpty(base64)) return null;
        try {
            var protectedBytes = Convert.FromBase64String(base64);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        } catch (CryptographicException) {
            // The ciphertext was produced under a different user/machine and cannot be decrypted here.
            return null;
        } catch (FormatException) {
            // Not valid base64 — treat as absent rather than crashing the plugin.
            return null;
        }
    }
}
