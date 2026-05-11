using System;
using FluentAssertions;
using Focale.NINA.Plugin.Settings;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

/// <summary>
/// DPAPI is Windows-only (System.Security.Cryptography.ProtectedData throws
/// PlatformNotSupportedException elsewhere). Gate the whole fixture so devs on macOS / Linux
/// can still run the rest of the suite. Run on Windows in CI.
/// </summary>
[TestFixture, Platform("Win")]
public class SecretProtectorTests {
    [Test]
    public void Roundtrip_ProtectThenUnprotect_ReturnsOriginalString() {
        var input = "sk-test-1234567890-abcdefABCDEF";

        var encrypted = SecretProtector.Protect(input);
        var decrypted = SecretProtector.Unprotect(encrypted);

        encrypted.Should().NotBe(input, "ciphertext must differ from plaintext");
        Convert.FromBase64String(encrypted).Should().NotBeEmpty("output must be valid base64");
        decrypted.Should().Be(input);
    }

    [Test]
    public void Unprotect_NullOrEmpty_ReturnsNull() {
        SecretProtector.Unprotect(null).Should().BeNull();
        SecretProtector.Unprotect(string.Empty).Should().BeNull();
    }

    [Test]
    public void Unprotect_GarbageBase64_ReturnsNull_DoesNotThrow() {
        // Valid base64, but not produced by ProtectedData.Protect with our entropy.
        SecretProtector.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })).Should().BeNull();
    }

    [Test]
    public void Unprotect_NonBase64_ReturnsNull_DoesNotThrow() {
        SecretProtector.Unprotect("not-base64-!!!").Should().BeNull();
    }
}
