using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Focale.NINA.Plugin.Events;

/// <summary>One recorded event, as kept locally until it is filed under its night's log.</summary>
public sealed class NightEventRow {
    public string    ClientId  { get; set; } = Guid.NewGuid().ToString("N");
    public string    NightDate { get; set; } = "";   // yyyy-MM-dd, see Common.NightDate
    public string    Kind      { get; set; } = "";
    public DateTime  StartUtc  { get; set; }
    public DateTime? EndUtc    { get; set; }
    public bool?     Success   { get; set; }
    public string    DataJson  { get; set; } = "{}";
    /// <summary>A span still waiting for its end (a running sequence, an autofocus run in progress).</summary>
    public bool      IsOpen    { get; set; }
    public DateTime  CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// SQLite-backed store for night events and for the night logs they go to. Survives NINA
/// restarts and offline nights, like the upload queue (a separate file, so neither blocks
/// the other).
///
/// The night log of a date is only learnt from an upload response: Focale files a frame under
/// the night its header says, and the plugin sends that night's events to the same log. Events
/// of a night without any uploaded frame therefore wait, and are eventually dropped.
/// </summary>
public sealed class NightEventStore : IDisposable {
    private const int StatusPending = 0, StatusSent = 1, StatusDead = 2;

    private readonly string _dbPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private SqliteConnection? _conn;
    private bool _disposed;

    public NightEventStore(string dbPath) {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    public async Task InitializeAsync(CancellationToken ct = default) {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            _conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
            await _conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous  = NORMAL;
                CREATE TABLE IF NOT EXISTS nights (
                    night_date     TEXT PRIMARY KEY,
                    night_log_uuid TEXT NOT NULL,
                    updated_utc    TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS night_events (
                    client_id   TEXT PRIMARY KEY,
                    night_date  TEXT    NOT NULL,
                    kind        TEXT    NOT NULL,
                    start_utc   TEXT    NOT NULL,
                    end_utc     TEXT    NULL,
                    success     INTEGER NULL,
                    data        TEXT    NOT NULL,
                    is_open     INTEGER NOT NULL DEFAULT 0,
                    status      INTEGER NOT NULL DEFAULT 0,
                    created_utc TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_night_events_pending ON night_events(status, night_date);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        } finally {
            _mutex.Release();
        }
    }

    public Task AddAsync(NightEventRow e, CancellationToken ct = default) => ExecAsync("""
        INSERT OR IGNORE INTO night_events (client_id, night_date, kind, start_utc, end_utc, success, data, is_open, status, created_utc)
        VALUES ($id, $nd, $k, $s, $e, $ok, $d, $o, 0, $c);
        """, ct,
        ("$id", e.ClientId), ("$nd", e.NightDate), ("$k", e.Kind), ("$s", Iso(e.StartUtc)),
        ("$e", (object?)IsoOrNull(e.EndUtc) ?? DBNull.Value), ("$ok", (object?)ToInt(e.Success) ?? DBNull.Value),
        ("$d", e.DataJson), ("$o", e.IsOpen ? 1 : 0), ("$c", Iso(e.CreatedUtc)));

    /// <summary>Closes an open span. <paramref name="dataJson"/> replaces its data when given.</summary>
    public Task CloseAsync(string clientId, DateTime? endUtc, bool? success, string? dataJson, CancellationToken ct = default) => ExecAsync("""
        UPDATE night_events SET is_open = 0, end_utc = $e, success = $ok, data = COALESCE($d, data)
         WHERE client_id = $id AND is_open = 1;
        """, ct,
        ("$id", clientId), ("$e", (object?)IsoOrNull(endUtc) ?? DBNull.Value),
        ("$ok", (object?)ToInt(success) ?? DBNull.Value), ("$d", (object?)dataJson ?? DBNull.Value));

    /// <summary>Remembers the night log Focale filed a night's frames under (the latest one wins).</summary>
    public Task RememberNightLogAsync(string nightDate, string nightLogUuid, CancellationToken ct = default) => ExecAsync("""
        INSERT INTO nights (night_date, night_log_uuid, updated_utc) VALUES ($nd, $u, $t)
        ON CONFLICT(night_date) DO UPDATE SET night_log_uuid = $u, updated_utc = $t;
        """, ct, ("$nd", nightDate), ("$u", nightLogUuid), ("$t", Iso(DateTime.UtcNow)));

    /// <summary>
    /// Pending events ready to go, with their night log: closed ones, and spans left open for
    /// longer than <paramref name="staleOpenAfter"/> (NINA was closed mid-sequence) — those go
    /// with no end. Events of nights whose log is unknown are not returned.
    /// </summary>
    public async Task<IReadOnlyList<(NightEventRow Event, string NightLogUuid)>> ListSendableAsync(
            int limit, TimeSpan staleOpenAfter, CancellationToken ct = default) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT e.client_id, e.night_date, e.kind, e.start_utc, e.end_utc, e.success, e.data, e.is_open, e.created_utc,
                       n.night_log_uuid
                  FROM night_events e JOIN nights n ON n.night_date = e.night_date
                 WHERE e.status = 0 AND (e.is_open = 0 OR e.created_utc <= $stale)
                 ORDER BY e.start_utc
                 LIMIT $l;
                """;
            cmd.Parameters.AddWithValue("$stale", Iso(DateTime.UtcNow - staleOpenAfter));
            cmd.Parameters.AddWithValue("$l", limit);
            var list = new List<(NightEventRow, string)>();
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) {
                list.Add((new NightEventRow {
                    ClientId   = r.GetString(0),
                    NightDate  = r.GetString(1),
                    Kind       = r.GetString(2),
                    StartUtc   = ParseUtc(r.GetString(3)),
                    EndUtc     = r.IsDBNull(4) ? null : ParseUtc(r.GetString(4)),
                    Success    = r.IsDBNull(5) ? null : r.GetInt32(5) != 0,
                    DataJson   = r.GetString(6),
                    IsOpen     = r.GetInt32(7) != 0,
                    CreatedUtc = ParseUtc(r.GetString(8)),
                }, r.GetString(9)));
            }
            return list;
        } finally { _mutex.Release(); }
    }

    public Task MarkSentAsync(IEnumerable<string> clientIds, CancellationToken ct = default) => MarkAsync(clientIds, StatusSent, ct);

    /// <summary>Events the server will never accept (a refused payload, a deleted night log).</summary>
    public Task MarkDeadAsync(IEnumerable<string> clientIds, CancellationToken ct = default) => MarkAsync(clientIds, StatusDead, ct);

    public async Task<int> CountPendingAsync(CancellationToken ct = default) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM night_events WHERE status = 0;";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        } finally { _mutex.Release(); }
    }

    /// <summary>
    /// Drops what no longer needs keeping: events already filed, and events of nights that never
    /// got a night log (no frame was uploaded), once they are older than <paramref name="keep"/>.
    /// </summary>
    public Task PurgeAsync(TimeSpan keep, CancellationToken ct = default) => ExecAsync("""
        DELETE FROM night_events
         WHERE created_utc <= $cut
           AND (status <> 0 OR night_date NOT IN (SELECT night_date FROM nights));
        DELETE FROM nights WHERE updated_utc <= $cut;
        """, ct, ("$cut", Iso(DateTime.UtcNow - keep)));

    private async Task MarkAsync(IEnumerable<string> clientIds, int status, CancellationToken ct) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var tx = (SqliteTransaction)await _conn!.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var id in clientIds) {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE night_events SET status = $s, is_open = 0 WHERE client_id = $id;";
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        } finally { _mutex.Release(); }
    }

    private async Task ExecAsync(string sql, CancellationToken ct, params (string name, object value)[] ps) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        } finally { _mutex.Release(); }
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O");
    private static string? IsoOrNull(DateTime? utc) => utc is null ? null : Iso(utc.Value);
    private static int? ToInt(bool? b) => b is null ? null : b.Value ? 1 : 0;
    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private void EnsureOpen() {
        if (_disposed) throw new ObjectDisposedException(nameof(NightEventStore));
        if (_conn is null) throw new InvalidOperationException("Call InitializeAsync() before use.");
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _conn?.Dispose();
        _mutex.Dispose();
    }
}
