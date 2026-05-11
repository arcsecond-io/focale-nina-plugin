using System;
using System.Threading;

namespace Focale.NINA.Plugin.Api;

/// <summary>
/// In-memory cache for the short-lived access token. The refresh token lives in
/// <see cref="Settings.FocaleSettings"/> (DPAPI-encrypted at rest). This class is
/// intentionally thread-safe — both the auth handler and the refresh path read/write it.
/// </summary>
public sealed class TokenStore {
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private string? _access;
    private DateTime _accessExpiresUtc = DateTime.MinValue;

    /// <summary>True when the cached access token is non-null and at least 30s away from expiry.</summary>
    public bool HasFreshAccess() {
        _lock.EnterReadLock();
        try {
            return !string.IsNullOrEmpty(_access)
                   && _accessExpiresUtc - DateTime.UtcNow > TimeSpan.FromSeconds(30);
        } finally { _lock.ExitReadLock(); }
    }

    public string? GetAccess() {
        _lock.EnterReadLock();
        try { return _access; } finally { _lock.ExitReadLock(); }
    }

    public void SetAccess(string token, DateTime expiresUtc) {
        _lock.EnterWriteLock();
        try {
            _access           = token;
            _accessExpiresUtc = expiresUtc;
        } finally { _lock.ExitWriteLock(); }
    }

    public void Clear() {
        _lock.EnterWriteLock();
        try {
            _access           = null;
            _accessExpiresUtc = DateTime.MinValue;
        } finally { _lock.ExitWriteLock(); }
    }
}
