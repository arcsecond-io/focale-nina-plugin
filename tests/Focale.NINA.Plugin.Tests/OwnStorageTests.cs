using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Focale.NINA.Plugin.Api.Models;
using Focale.NINA.Plugin.Common;
using Focale.NINA.Plugin.Settings;
using Focale.NINA.Plugin.Upload;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class RelativePathTests {
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "NINA-images");

    [Test]
    public void A_frame_below_NINAs_image_folder_keeps_its_folders() {
        var file = Path.Combine(Root, "2026-10-27", "M31", "LIGHT", "M31_0001.fits");
        RelativePath.Of(file, Root).Should().Be("2026-10-27/M31/LIGHT/M31_0001.fits");
        RelativePath.Of(file, Root + Path.DirectorySeparatorChar).Should().Be("2026-10-27/M31/LIGHT/M31_0001.fits");
    }

    [Test]
    public void Anything_else_is_just_the_file_name() {
        var elsewhere = Path.Combine(Path.GetTempPath(), "Other", "x.fits");
        RelativePath.Of(elsewhere, Root).Should().Be("x.fits");
        RelativePath.Of(elsewhere, null).Should().Be("x.fits");
        RelativePath.Of(elsewhere, "").Should().Be("x.fits");
    }
}

[TestFixture]
public class OwnStorageUploadFieldsTests {
    [Test]
    public void The_relative_path_goes_with_own_storage_uploads_only() {
        UploadWorker.UploadFields(StorageMode.OwnStorage, "uuid-1", "2026-10-27/M31/x.fits")!["relative_path"]
            .Should().Be("2026-10-27/M31/x.fits");
        UploadWorker.UploadFields(StorageMode.FocaleStorage, null, "2026-10-27/M31/x.fits")!
            .Should().NotContainKey("relative_path");
        UploadWorker.UploadFields(StorageMode.IndexOnly, null, "2026-10-27/M31/x.fits")!
            .Should().NotContainKey("relative_path");
        UploadWorker.UploadFields(StorageMode.OwnStorage, "uuid-1", null)!.Should().NotContainKey("relative_path");
    }
}

[TestFixture]
public class DataStorageResponseTests {
    private static DataStorageResponse Parse(string json) => JsonConvert.DeserializeObject<DataStorageResponse>(json)!;

    [Test]
    public void The_servers_verdict_decides() {
        Parse("""{"uuid": "a", "type": "dropbox", "is_read_only": false, "can_receive_uploads": true}""")
            .CanReceiveUploads.Should().BeTrue();
        Parse("""{"uuid": "b", "type": "aws_s3", "is_read_only": false, "can_receive_uploads": false}""")
            .CanReceiveUploads.Should().BeFalse();
    }

    [Test]
    public void An_older_server_falls_back_to_the_types_it_could_write() {
        Parse("""{"uuid": "a", "type": "aws_s3", "is_read_only": false}""").CanReceiveUploads.Should().BeTrue();
        Parse("""{"uuid": "b", "type": "dropbox", "is_read_only": false}""").CanReceiveUploads.Should().BeFalse();
    }
}

[TestFixture]
public class UploadQueueMigrationTests {
    [Test]
    public async Task A_queue_from_the_previous_version_gains_the_relative_path_column() {
        var path = Path.Combine(Path.GetTempPath(), "focale-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var conn = new SqliteConnection($"Data Source={path}")) {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE upload_jobs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, local_path TEXT NOT NULL, sha256 TEXT NOT NULL,
                    capture_local TEXT NOT NULL, frame_class INTEGER NOT NULL DEFAULT 0,
                    enqueued_at_utc TEXT NOT NULL, not_before_utc TEXT NOT NULL,
                    attempt INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL, UNIQUE(sha256));
                INSERT INTO upload_jobs (local_path, sha256, capture_local, enqueued_at_utc, not_before_utc)
                VALUES ('C:\old.fits', 'old', '2026-10-01T22:00:00', '2026-10-01T20:00:00Z', '2026-10-01T20:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        using var queue = new UploadQueue(path);
        await queue.InitializeAsync();
        var old = await queue.ClaimNextAsync();
        old!.LocalPath.Should().Be(@"C:\old.fits");
        old.RelativePath.Should().BeNull();

        await queue.EnqueueAsync(new UploadJob { LocalPath = @"C:\n\x.fits", Sha256 = "new", RelativePath = "n/x.fits" });
        (await queue.ClaimNextAsync())!.RelativePath.Should().Be("n/x.fits");
    }
}
