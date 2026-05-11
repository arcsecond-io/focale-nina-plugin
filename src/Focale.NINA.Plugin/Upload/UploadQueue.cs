using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Focale.NINA.Plugin.Api;
using Microsoft.Data.Sqlite;

namespace Focale.NINA.Plugin.Upload;

/// <summary>
/// SQLite-backed durable FIFO. Survives NINA restart. Single-process single-DB —
/// concurrency between the producer (image-saved hook) and the consumer (worker)
/// is serialized via the connection mutex inside this class.
/// </summary>
public sealed class UploadQueue : IDisposable {
    private readonly string _dbPath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private SqliteConnection? _conn;
    private bool _disposed;

    public UploadQueue(string dbPath) {
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
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS upload_jobs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    local_path      TEXT    NOT NULL,
                    sha256          TEXT    NOT NULL,
                    capture_local   TEXT    NOT NULL,
                    frame_class     INTEGER NOT NULL DEFAULT 0,
                    image_type      TEXT    NOT NULL DEFAULT '',
                    target_name     TEXT    NOT NULL DEFAULT '',
                    exposure_seconds REAL   NOT NULL DEFAULT 0,
                    position_angle  REAL    NULL,
                    enqueued_at_utc TEXT    NOT NULL,
                    not_before_utc  TEXT    NOT NULL,
                    attempt         INTEGER NOT NULL DEFAULT 0,
                    status          INTEGER NOT NULL DEFAULT 0,
                    last_error      TEXT    NULL,
                    UNIQUE(sha256)
                );
                CREATE INDEX IF NOT EXISTS ix_upload_jobs_pickup
                    ON upload_jobs(status, not_before_utc);
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Recover any rows stuck in Working from a previous crash.
            using var fix = _conn.CreateCommand();
            fix.CommandText = "UPDATE upload_jobs SET status = 0 WHERE status = 1;";
            await fix.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        } finally {
            _mutex.Release();
        }
    }

    /// <summary>Enqueues a job. If the same sha256 is already present, returns the existing id.</summary>
    public async Task<long> EnqueueAsync(UploadJob job, CancellationToken ct = default) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO upload_jobs (local_path, sha256, capture_local, frame_class,
                                         image_type, target_name, exposure_seconds, position_angle,
                                         enqueued_at_utc, not_before_utc, attempt, status)
                VALUES ($lp, $sh, $cl, $fc, $it, $tn, $es, $pa, $en, $nb, 0, 0)
                ON CONFLICT(sha256) DO NOTHING;
                SELECT id FROM upload_jobs WHERE sha256 = $sh;
                """;
            cmd.Parameters.AddWithValue("$lp", job.LocalPath);
            cmd.Parameters.AddWithValue("$sh", job.Sha256);
            cmd.Parameters.AddWithValue("$cl", job.CaptureLocal.ToString("O"));
            cmd.Parameters.AddWithValue("$fc", (int)job.FrameClass);
            cmd.Parameters.AddWithValue("$it", job.ImageType ?? string.Empty);
            cmd.Parameters.AddWithValue("$tn", job.TargetName ?? string.Empty);
            cmd.Parameters.AddWithValue("$es", job.ExposureSeconds);
            cmd.Parameters.AddWithValue("$pa", (object?)job.PositionAngleDegrees ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$en", job.EnqueuedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$nb", job.NotBeforeUtc.ToString("O"));
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            job.Id = id;
            return id;
        } finally {
            _mutex.Release();
        }
    }

    /// <summary>Atomically claims the next pending job that is past its not_before time.</summary>
    public async Task<UploadJob?> ClaimNextAsync(CancellationToken ct = default) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var tx = (SqliteTransaction)await _conn!.BeginTransactionAsync(ct).ConfigureAwait(false);

            using var sel = _conn.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT id, local_path, sha256, capture_local, frame_class,
                       image_type, target_name, exposure_seconds, position_angle,
                       enqueued_at_utc, not_before_utc, attempt, status, last_error
                  FROM upload_jobs
                 WHERE status = 0 AND not_before_utc <= $now
                 ORDER BY id ASC
                 LIMIT 1;
                """;
            sel.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));

            UploadJob? job = null;
            using (var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false)) {
                if (await r.ReadAsync(ct).ConfigureAwait(false)) {
                    job = Read(r);
                }
            }

            if (job is null) {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return null;
            }

            using var upd = _conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE upload_jobs SET status = 1 WHERE id = $id;";
            upd.Parameters.AddWithValue("$id", job.Id);
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return job;
        } finally {
            _mutex.Release();
        }
    }

    public async Task MarkDoneAsync(long id, CancellationToken ct = default) =>
        await ExecAsync(
            "DELETE FROM upload_jobs WHERE id = $id;",
            ("$id", (object)id),
            ct).ConfigureAwait(false);

    public async Task MarkRetryAsync(long id, int attempt, DateTime notBefore, string? lastError, CancellationToken ct = default) =>
        await ExecAsync(
            """
            UPDATE upload_jobs
               SET status = 0,
                   attempt = $a,
                   not_before_utc = $nb,
                   last_error = $err
             WHERE id = $id;
            """,
            ct,
            ("$id",  id),
            ("$a",   attempt),
            ("$nb",  notBefore.ToString("O")),
            ("$err", (object?)lastError ?? DBNull.Value)
            ).ConfigureAwait(false);

    public async Task MarkDeadAsync(long id, string? reason, CancellationToken ct = default) =>
        await ExecAsync(
            "UPDATE upload_jobs SET status = 3, last_error = $err WHERE id = $id;",
            ct,
            ("$id",  id),
            ("$err", (object?)reason ?? DBNull.Value)
            ).ConfigureAwait(false);

    public async Task<int> CountAsync(UploadJobStatus status, CancellationToken ct = default) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM upload_jobs WHERE status = $s;";
            cmd.Parameters.AddWithValue("$s", (int)status);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        } finally { _mutex.Release(); }
    }

    public async Task<IReadOnlyList<UploadJob>> ListDeadAsync(int limit, CancellationToken ct = default) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT id, local_path, sha256, capture_local, frame_class,
                       image_type, target_name, exposure_seconds, position_angle,
                       enqueued_at_utc, not_before_utc, attempt, status, last_error
                  FROM upload_jobs WHERE status = 3 ORDER BY id DESC LIMIT $l;
                """;
            cmd.Parameters.AddWithValue("$l", limit);
            var list = new List<UploadJob>();
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) {
                list.Add(Read(r));
            }
            return list;
        } finally { _mutex.Release(); }
    }

    private static UploadJob Read(Microsoft.Data.Sqlite.SqliteDataReader r) {
        return new UploadJob {
            Id                   = r.GetInt64(0),
            LocalPath            = r.GetString(1),
            Sha256               = r.GetString(2),
            CaptureLocal         = DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            FrameClass           = (FrameClass)r.GetInt32(4),
            ImageType            = r.IsDBNull(5) ? "" : r.GetString(5),
            TargetName           = r.IsDBNull(6) ? "" : r.GetString(6),
            ExposureSeconds      = r.IsDBNull(7) ? 0  : r.GetDouble(7),
            PositionAngleDegrees = r.IsDBNull(8) ? null : (double?)r.GetDouble(8),
            EnqueuedAtUtc        = DateTime.Parse(r.GetString(9),  null, System.Globalization.DateTimeStyles.RoundtripKind),
            NotBeforeUtc         = DateTime.Parse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Attempt              = r.GetInt32(11),
            Status               = (UploadJobStatus)r.GetInt32(12),
            LastError            = r.IsDBNull(13) ? null : r.GetString(13),
        };
    }

    private async Task ExecAsync(string sql, (string name, object value) p1, CancellationToken ct) {
        EnsureOpen();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue(p1.name, p1.value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private void EnsureOpen() {
        if (_disposed) throw new ObjectDisposedException(nameof(UploadQueue));
        if (_conn is null) throw new InvalidOperationException("Call InitializeAsync() before use.");
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _conn?.Dispose();
        _mutex.Dispose();
    }
}
