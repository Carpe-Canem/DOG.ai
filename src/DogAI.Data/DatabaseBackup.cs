using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core;
using DogAI.Core.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DogAI.Data;

/// <summary>
/// Backup and restore of everything the user made. All of it lives in the one SQLite file (chats, stories, characters with their
/// pictures, documents and their passages, voices, styles, assistants, prompts, settings), so a backup is that file, copied in a
/// consistent state, in a zip with a small description. Models, voice models and the engine are not in it: they are large and can
/// be downloaded again.
///
/// Restoring never touches the database while the app runs (background indexing writes to it and connections are pooled).
/// <see cref="StageAsync"/> checks the archive and puts the database in a waiting folder; <see cref="ApplyPending"/> swaps it in at the
/// next start, before anything opens the database. Migrations then bring an older backup up to date.
/// </summary>
public static class DatabaseBackup
{
    public const string AppName = "DOG AI";
    public const int FormatVersion = 1;
    public const string DatabaseEntry = "dogai.db";
    public const string ManifestEntry = "backup.json";

    /// <summary>Settings that belong to this PC rather than to the data. A restore keeps this PC's values.</summary>
    private static readonly string[] MachineSettings = ["ModelsDirectory", "ExtraModelFolders", "HfTokenProtected", "Microphone", "Speakers", "LastModelPath"];

    private static string StagingFolder(AppPaths paths) => Path.Combine(paths.Root, "restore");
    private static string SafetyFolder(AppPaths paths) => Path.Combine(paths.Root, "before-restore");

    /// <summary>True when a restore is waiting for the next start.</summary>
    public static bool HasPending(AppPaths paths) => File.Exists(Path.Combine(StagingFolder(paths), DatabaseEntry));

    /// <summary>Throws away a restore that was queued but has not been applied.</summary>
    public static void CancelPending(AppPaths paths)
    {
        try { Directory.Delete(StagingFolder(paths), true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>Where the database as it was before the last restore is kept, if there was one.</summary>
    public static string? SafetyCopy(AppPaths paths)
    {
        var file = Path.Combine(SafetyFolder(paths), DatabaseEntry);
        return File.Exists(file) ? file : null;
    }

    /// <summary>Writes a backup to <paramref name="zipPath"/>. The Hugging Face token is left out of the file.</summary>
    public static async Task<BackupManifest> CreateAsync(AppPaths paths, string zipPath, string appVersion, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "dogai-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var copy = Path.Combine(work, DatabaseEntry);
        var tempZip = zipPath + ".part";
        try
        {
            // VACUUM INTO writes a consistent snapshot even while the app is using the database.
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Pooling = false }.ToString()))
            {
                await source.OpenAsync(ct);
                await using var vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.Add(new SqliteParameter("$path", copy));
                await vacuum.ExecuteNonQueryAsync(ct);
            }

            BackupManifest manifest;
            await using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
            {
                await snapshot.OpenAsync(ct);
                // A secret does not belong in a file that gets copied around.
                await Execute(snapshot, "UPDATE Settings SET Json = json_remove(Json, '$.HfTokenProtected') WHERE Key = 'app'", ct);
                manifest = new BackupManifest(AppName, FormatVersion, appVersion, DateTime.UtcNow,
                    await Scalar<string>(snapshot, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", ct),
                    (int)(await Scalar<long?>(snapshot, "SELECT COUNT(*) FROM Conversations", ct) ?? 0),
                    (int)(await Scalar<long?>(snapshot, "SELECT COUNT(*) FROM Messages", ct) ?? 0));
            }

            File.Delete(tempZip);
            await using (var file = File.Create(tempZip))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using (var stream = entry.Open()) await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: ct);
                zip.CreateEntryFromFile(copy, DatabaseEntry, CompressionLevel.Optimal);
            }
            File.Move(tempZip, zipPath, overwrite: true);
            return manifest;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempZip);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Checks a backup file and, if it is a good one, puts its database where the next start will pick it up.
    /// Throws <see cref="BackupException"/> with a plain reason if the file is not a usable backup. Nothing changes then.
    /// </summary>
    public static async Task<BackupManifest> StageAsync(AppPaths paths, string zipPath, CancellationToken ct = default)
    {
        var staging = StagingFolder(paths);
        var incoming = Path.Combine(paths.Root, "restore-incoming");
        try
        {
            if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
            Directory.CreateDirectory(incoming);
            var extracted = Path.Combine(incoming, DatabaseEntry);

            BackupManifest manifest;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                // Only the two files a backup holds, at the top level. Anything else (or a path that climbs out) is not one of ours.
                foreach (var e in zip.Entries)
                    if (e.FullName != ManifestEntry && e.FullName != DatabaseEntry)
                        throw new BackupException("This file is not a DOG AI backup (it holds files a backup never has).");
                var manifestEntry = zip.GetEntry(ManifestEntry);
                var databaseEntry = zip.GetEntry(DatabaseEntry);
                if (manifestEntry is null || databaseEntry is null) throw new BackupException("This file is not a DOG AI backup (a part is missing).");
                if (manifestEntry.Length > 64 * 1024) throw new BackupException("This file is not a DOG AI backup.");

                await using (var stream = manifestEntry.Open())
                    manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: ct)
                        ?? throw new BackupException("This file is not a DOG AI backup.");
                if (manifest.App != AppName) throw new BackupException("This file is not a DOG AI backup.");
                if (manifest.Format > FormatVersion) throw new BackupException("This backup was made by a newer DOG AI. Update DOG AI, then restore it.");

                databaseEntry.ExtractToFile(extracted, overwrite: true);
            }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                throw new BackupException("This file is not a DOG AI backup (it could not be read as one).");
            }

            await CheckDatabaseAsync(extracted, ct);

            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.Move(incoming, staging);
            return manifest;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(incoming)) Directory.Delete(incoming, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Throws unless the file is a healthy DOG AI database no newer than this version can read.</summary>
    private static async Task CheckDatabaseAsync(string file, CancellationToken ct)
    {
        try
        {
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await db.OpenAsync(ct);
            if (await Scalar<string>(db, "PRAGMA integrity_check", ct) != "ok") throw new BackupException("The database inside this backup is damaged.");
            if (await Scalar<long?>(db, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('__EFMigrationsHistory', 'Conversations', 'Settings')", ct) != 3)
                throw new BackupException("This file is not a DOG AI backup (its database is not DOG AI's).");

            var known = KnownMigrations();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!known.Contains(reader.GetString(0)))
                    throw new BackupException("This backup was made by a newer DOG AI. Update DOG AI, then restore it.");
        }
        catch (SqliteException)
        {
            throw new BackupException("The database inside this backup could not be read.");
        }
    }

    private static HashSet<string> KnownMigrations()
    {
        using var context = new DogDbContext(new DbContextOptionsBuilder<DogDbContext>().UseSqlite("Data Source=:memory:").Options);
        return [.. context.Database.GetMigrations()];
    }

    /// <summary>
    /// Swaps in a staged restore. Call before anything opens the database (the very start of the app). The database it replaces is kept
    /// in the "before-restore" folder. Machine-specific settings (models folder, token, devices) stay as this PC had them.
    /// Returns a sentence about what happened, or null if nothing was waiting.
    /// </summary>
    public static string? ApplyPending(AppPaths paths)
    {
        var staging = StagingFolder(paths);
        var staged = Path.Combine(staging, DatabaseEntry);
        if (!File.Exists(staged)) return null;

        var current = paths.DatabaseFile;
        string? oldSettings = null;
        try
        {
            if (File.Exists(current)) oldSettings = ReadSettings(current);
        }
        catch (SqliteException) { }

        var safety = SafetyFolder(paths);
        Directory.CreateDirectory(safety);

        // After a crash the newest writes can still be in the write-ahead log. Fold them into the main file first so the safety
        // copy is complete on its own.
        try
        {
            if (File.Exists(current))
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = current, Pooling = false }.ToString());
                db.Open();
                using var checkpoint = db.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
            }
        }
        catch (SqliteException) { }
        SqliteConnection.ClearAllPools();

        // The app that asked for the restore may still be closing; wait for it to let go of the file.
        Retry(() =>
        {
            if (File.Exists(current)) File.Move(current, Path.Combine(safety, DatabaseEntry), overwrite: true);
        });
        // Anything left of the log goes along with the safety copy under matching names, never into the restored database.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var leftover = current + suffix;
            if (!File.Exists(leftover)) continue;
            try { File.Move(leftover, Path.Combine(safety, DatabaseEntry + suffix), overwrite: true); }
            catch (IOException) { TryDelete(leftover); }
        }
        File.Move(staged, current, overwrite: true);
        try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        try
        {
            if (oldSettings is not null) CarryMachineSettings(current, oldSettings);
        }
        catch (Exception e) when (e is SqliteException or JsonException)
        {
            // The restored settings are used as they are.
        }
        return "Restored your backup. The database from before it is kept in " + safety + ".";
    }

    private static string? ReadSettings(string file)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Json FROM Settings WHERE Key = 'app'";
        return command.ExecuteScalar() as string;
    }

    private static void CarryMachineSettings(string restoredFile, string oldJson)
    {
        var old = JsonNode.Parse(oldJson)?.AsObject();
        if (old is null) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restoredFile, Pooling = false }.ToString());
        db.Open();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT Json FROM Settings WHERE Key = 'app'";
        var restored = read.ExecuteScalar() is string json ? JsonNode.Parse(json)?.AsObject() : null;

        restored ??= new JsonObject();
        foreach (var name in MachineSettings)
        {
            if (old.TryGetPropertyValue(name, out var value) && value is not null) restored[name] = value.DeepClone();
            else restored.Remove(name);
        }
        using var write = db.CreateCommand();
        write.CommandText = "INSERT INTO Settings (Key, Json) VALUES ('app', $json) ON CONFLICT(Key) DO UPDATE SET Json = $json";
        write.Parameters.Add(new SqliteParameter("$json", restored.ToJsonString()));
        write.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task Execute(SqliteConnection db, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> Scalar<T>(SqliteConnection db, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }
}

/// <summary>The backup service the app uses: <see cref="DatabaseBackup"/> bound to this install's data folder.</summary>
public sealed class SqliteBackupService(AppPaths paths) : IBackupService
{
    private static string Version => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";

    public Task<BackupManifest> CreateAsync(string zipPath, CancellationToken ct = default) => DatabaseBackup.CreateAsync(paths, zipPath, Version, ct);
    public Task<BackupManifest> StageAsync(string zipPath, CancellationToken ct = default) => DatabaseBackup.StageAsync(paths, zipPath, ct);
    public bool RestorePending => DatabaseBackup.HasPending(paths);
    public void CancelRestore() => DatabaseBackup.CancelPending(paths);
    public string? SafetyCopy => DatabaseBackup.SafetyCopy(paths);
}
