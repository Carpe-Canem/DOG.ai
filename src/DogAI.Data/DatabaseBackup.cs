using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core;
using DogAI.Core.Backup;
using DogAI.Core.Privacy;
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
///
/// Locked data (M8): a backup of an encrypted database holds the encrypted copy and the lock file (without Windows Hello
/// entries, which only work on their own PC), so it opens with the same password or recovery key on any PC, and nothing in
/// the zip is readable without them. Restoring covers four cases:
/// a plain backup into a plain DOG.ai (as before);
/// a plain backup into a locked DOG.ai (encrypted under this DOG.ai's key while staging, so no plain copy waits on disk);
/// a locked backup into a plain DOG.ai (asks for the backup's password, then DOG.ai takes over the backup's lock);
/// a locked backup into a locked DOG.ai (asks for the backup's password, then the data is re-encrypted under this DOG.ai's
/// key, so the current password and Windows Hello keep working).
/// </summary>
public static class DatabaseBackup
{
    public const string AppName = "DOG AI";

    /// <summary>The newest backup format this version reads. Plain backups are still written as format 1, locked ones as 2.</summary>
    public const int FormatVersion = 2;
    public const string DatabaseEntry = "dogai.db";
    public const string ManifestEntry = "backup.json";

    /// <summary>Only in a backup of locked data: the lock file, without Windows Hello entries.</summary>
    public const string LockEntry = LockFile.FileName;

    private const string FinishMarker = "finish-restore.txt";

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

    /// <summary>
    /// Writes a backup to <paramref name="zipPath"/>. The Hugging Face token is left out of the file.
    /// <paramref name="key"/> is the database key when the data is locked; the backup is then encrypted too and carries the lock file.
    /// </summary>
    public static async Task<BackupManifest> CreateAsync(AppPaths paths, string zipPath, string appVersion, CancellationToken ct = default, byte[]? key = null)
    {
        LockFile? lockFile = null;
        if (key is not null)
        {
            try { lockFile = LockFile.Read(paths.LockFile); }
            catch (LockFileException e) { throw new BackupException(e.Message); }
            if (lockFile is null || !lockFile.Wraps(key))
                throw new BackupException("The lock file that goes with your data is missing, so a backup could not be opened later. No backup was made.");
        }

        var work = Path.Combine(Path.GetTempPath(), "dogai-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var copy = Path.Combine(work, DatabaseEntry);
        var tempZip = zipPath + ".part";
        try
        {
            // VACUUM INTO writes a consistent snapshot even while the app is using the database.
            // From an encrypted database the copy comes out encrypted under the same key.
            await using (var source = new SqliteConnection(DatabaseLock.ConnectionString(paths.DatabaseFile, key)))
            {
                await source.OpenAsync(ct);
                await using var vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.Add(new SqliteParameter("$path", copy));
                await vacuum.ExecuteNonQueryAsync(ct);
            }
            // Locked data must never leave as a readable file.
            if (key is not null && DatabaseLock.KindOf(copy) != DatabaseFileKind.Encrypted)
                throw new BackupException("The copy for the backup did not come out encrypted. No backup was made.");

            BackupManifest manifest;
            await using (var snapshot = new SqliteConnection(DatabaseLock.ConnectionString(copy, key)))
            {
                await snapshot.OpenAsync(ct);
                // A secret does not belong in a file that gets copied around.
                await Execute(snapshot, "UPDATE Settings SET Json = json_remove(Json, '$.HfTokenProtected') WHERE Key = 'app'", ct);
                manifest = new BackupManifest(AppName, key is null ? 1 : FormatVersion, appVersion, DateTime.UtcNow,
                    await Scalar<string>(snapshot, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", ct),
                    (int)(await Scalar<long?>(snapshot, "SELECT COUNT(*) FROM Conversations", ct) ?? 0),
                    (int)(await Scalar<long?>(snapshot, "SELECT COUNT(*) FROM Messages", ct) ?? 0),
                    Locked: key is not null);
            }
            // A locked backup's description stays silent about what is inside.
            var written = key is null ? manifest : manifest with { Chats = 0, Messages = 0 };

            File.Delete(tempZip);
            await using (var file = File.Create(tempZip))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using (var stream = entry.Open()) await JsonSerializer.SerializeAsync(stream, written, cancellationToken: ct);
                // Encrypted bytes do not compress.
                zip.CreateEntryFromFile(copy, DatabaseEntry, key is null ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                if (lockFile is not null)
                {
                    var lockEntry = zip.CreateEntry(LockEntry, CompressionLevel.Optimal);
                    await using var stream = lockEntry.Open();
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(lockFile.WithoutHello().ToJson()), ct);
                }
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
    /// <paramref name="key"/> is this DOG.ai's database key when its data is locked. <paramref name="backupPassword"/> is the
    /// password or recovery key of a locked backup; without it (or with a wrong one) a locked backup throws
    /// <see cref="BackupLockedException"/>.
    /// </summary>
    public static async Task<BackupManifest> StageAsync(AppPaths paths, string zipPath, CancellationToken ct = default, byte[]? key = null, string? backupPassword = null)
    {
        var staging = StagingFolder(paths);
        var incoming = Path.Combine(paths.Root, "restore-incoming");
        try
        {
            if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
            Directory.CreateDirectory(incoming);
            var extracted = Path.Combine(incoming, DatabaseEntry);

            BackupManifest manifest;
            LockFile? backupLock = null;
            byte[]? backupKey = null;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                // Only the files a backup holds, at the top level. Anything else (or a path that climbs out) is not one of ours.
                foreach (var e in zip.Entries)
                    if (e.FullName != ManifestEntry && e.FullName != DatabaseEntry && e.FullName != LockEntry)
                        throw new BackupException("This file is not a DOG.ai backup (it holds files a backup never has).");
                var manifestEntry = zip.GetEntry(ManifestEntry);
                var databaseEntry = zip.GetEntry(DatabaseEntry);
                if (manifestEntry is null || databaseEntry is null) throw new BackupException("This file is not a DOG.ai backup (a part is missing).");
                if (manifestEntry.Length > 64 * 1024) throw new BackupException("This file is not a DOG.ai backup.");

                await using (var stream = manifestEntry.Open())
                    manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: ct)
                        ?? throw new BackupException("This file is not a DOG.ai backup.");
                if (manifest.App != AppName) throw new BackupException("This file is not a DOG.ai backup.");
                if (manifest.Format > FormatVersion) throw new BackupException("This backup was made by a newer DOG.ai. Update DOG.ai, then restore it.");

                var lockEntry = zip.GetEntry(LockEntry);
                if (manifest.Locked && lockEntry is null) throw new BackupException("This backup is locked, but the lock file that opens it is missing from it.");
                if (lockEntry is not null)
                {
                    if (lockEntry.Length > 64 * 1024) throw new BackupException("This file is not a DOG.ai backup.");
                    try
                    {
                        using var reader = new StreamReader(lockEntry.Open());
                        backupLock = LockFile.Parse(await reader.ReadToEndAsync(ct));
                    }
                    catch (LockFileException)
                    {
                        throw new BackupException("The lock file inside this backup is damaged, so the backup cannot be opened.");
                    }
                    if (string.IsNullOrEmpty(backupPassword))
                        throw new BackupLockedException("This backup is locked. Enter the password it was made with, or its recovery key.", wrong: false);
                    backupKey = backupLock.OpenWithPassword(backupPassword) ?? backupLock.OpenWithRecoveryKey(backupPassword);
                    if (backupKey is null)
                        throw new BackupLockedException("That is not the password or the recovery key this backup was made with.", wrong: true);
                }

                databaseEntry.ExtractToFile(extracted, overwrite: true);
            }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                throw new BackupException("This file is not a DOG.ai backup (it could not be read as one).");
            }

            // What the file is must agree with what the backup says it is.
            var kind = DatabaseLock.KindOf(extracted);
            if (backupKey is null ? kind == DatabaseFileKind.Encrypted : kind != DatabaseFileKind.Encrypted)
                throw new BackupException("The database inside this backup could not be read.");
            await CheckDatabaseAsync(extracted, backupKey, ct);

            if (backupKey is not null)
            {
                // A locked backup does not describe its contents in the open; count now that it is unlocked.
                await using var db = new SqliteConnection(DatabaseLock.ConnectionString(extracted, backupKey, readOnly: true));
                await db.OpenAsync(ct);
                manifest = manifest with
                {
                    Chats = (int)(await Scalar<long?>(db, "SELECT COUNT(*) FROM Conversations", ct) ?? 0),
                    Messages = (int)(await Scalar<long?>(db, "SELECT COUNT(*) FROM Messages", ct) ?? 0),
                    Locked = true,
                };
            }
            SqliteConnection.ClearAllPools();

            if (key is not null)
            {
                // This DOG.ai is locked: what waits for the restart is encrypted under its own key, whatever the backup was.
                if (backupKey is null || !backupKey.AsSpan().SequenceEqual(key))
                {
                    var converted = Path.Combine(incoming, "converted.db");
                    try
                    {
                        DatabaseLock.CopyTo(extracted, backupKey, converted, key);
                    }
                    catch (DataLockException e)
                    {
                        throw new BackupException("The backup could not be encrypted for this DOG.ai: " + e.Message);
                    }
                    DatabaseLock.DeleteWithSidecars(extracted);
                    File.Move(converted, extracted);
                }
            }
            else if (backupLock is not null)
            {
                // This DOG.ai is not locked: it takes over the backup's lock, so the restored data stays protected.
                await File.WriteAllTextAsync(Path.Combine(incoming, LockEntry), backupLock.WithoutHello().ToJson(), ct);
            }

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

    /// <summary>Throws unless the file is a healthy DOG.ai database no newer than this version can read.</summary>
    private static async Task CheckDatabaseAsync(string file, byte[]? key, CancellationToken ct)
    {
        try
        {
            await using var db = new SqliteConnection(DatabaseLock.ConnectionString(file, key, readOnly: true));
            await db.OpenAsync(ct);
            if (await Scalar<string>(db, "PRAGMA integrity_check", ct) != "ok") throw new BackupException("The database inside this backup is damaged.");
            if (await Scalar<long?>(db, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('__EFMigrationsHistory', 'Conversations', 'Settings')", ct) != 3)
                throw new BackupException("This file is not a DOG.ai backup (its database is not DOG.ai's).");

            var known = KnownMigrations();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!known.Contains(reader.GetString(0)))
                    throw new BackupException("This backup was made by a newer DOG.ai. Update DOG.ai, then restore it.");
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
    ///
    /// This runs before the owner has unlocked anything, so it never needs a key: it only moves files. When either database is
    /// encrypted, carrying this PC's settings over is left to <see cref="FinishRestore"/>, which runs after the unlock.
    /// </summary>
    public static string? ApplyPending(AppPaths paths)
    {
        var staging = StagingFolder(paths);
        var staged = Path.Combine(staging, DatabaseEntry);
        if (!File.Exists(staged)) return null;

        var current = paths.DatabaseFile;
        var oldEncrypted = DatabaseLock.KindOf(current) == DatabaseFileKind.Encrypted;
        var newEncrypted = DatabaseLock.KindOf(staged) == DatabaseFileKind.Encrypted;
        string? oldSettings = null;
        try
        {
            if (File.Exists(current) && !oldEncrypted) oldSettings = ReadSettings(current, null);
        }
        catch (SqliteException) { }

        var safety = SafetyFolder(paths);
        Directory.CreateDirectory(safety);
        TryDelete(Path.Combine(safety, LockEntry));
        TryDelete(Path.Combine(safety, FinishMarker));

        // After a crash the newest writes can still be in the write-ahead log. Fold them into the main file first so the safety
        // copy is complete on its own. (An encrypted database keeps its log files beside it in the safety folder instead.)
        try
        {
            if (File.Exists(current) && !oldEncrypted)
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
        // An encrypted safety copy is only worth keeping together with the lock file that opens it.
        if (oldEncrypted && File.Exists(paths.LockFile))
        {
            try { File.Copy(paths.LockFile, Path.Combine(safety, LockEntry), overwrite: true); }
            catch (IOException) { }
        }

        File.Move(staged, current, overwrite: true);
        // A locked backup restored into a DOG.ai that had no lock brings its own lock file.
        var stagedLock = Path.Combine(staging, LockEntry);
        if (File.Exists(stagedLock))
        {
            File.Move(stagedLock, paths.LockFile, overwrite: true);
            TryDelete(paths.UnlockTriesFile);
        }
        try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        if (oldEncrypted || newEncrypted)
        {
            // Finished after the unlock, when the key is known.
            File.WriteAllText(Path.Combine(safety, FinishMarker), "This restore is finished the next time DOG.ai is unlocked.");
        }
        else
        {
            try
            {
                if (oldSettings is not null) CarryMachineSettings(current, null, oldSettings);
                // A database that came out of a backup is in SQLite's older journal mode; put it back the way DOG.ai runs.
                DatabaseLock.UseWal(current, null);
            }
            catch (Exception e) when (e is SqliteException or JsonException)
            {
                // The restored settings are used as they are.
            }
        }
        return "Restored your backup. The database from before it is kept in " + safety + ".";
    }

    /// <summary>
    /// The part of a restore that needs the database key: this PC's own settings (models folder, token, devices) are carried
    /// from the replaced database into the restored one. Call after the unlock, before the app opens the database.
    /// <paramref name="key"/> is null when the lock is off. Does nothing when no restore is waiting to be finished.
    /// </summary>
    public static void FinishRestore(AppPaths paths, byte[]? key)
    {
        var safety = SafetyFolder(paths);
        var marker = Path.Combine(safety, FinishMarker);
        if (!File.Exists(marker)) return;
        try
        {
            var old = Path.Combine(safety, DatabaseEntry);
            string? oldSettings = null;
            try
            {
                var kind = DatabaseLock.KindOf(old);
                if (kind != DatabaseFileKind.Missing) oldSettings = ReadSettings(old, kind == DatabaseFileKind.Encrypted ? key : null);
            }
            catch (SqliteException) { }
            if (oldSettings is not null) CarryMachineSettings(paths.DatabaseFile, key, oldSettings);
            DatabaseLock.UseWal(paths.DatabaseFile, key);
        }
        catch (Exception e) when (e is SqliteException or JsonException)
        {
            // The restored settings are used as they are.
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(marker);
        }
    }

    private static string? ReadSettings(string file, byte[]? key)
    {
        using var db = new SqliteConnection(DatabaseLock.ConnectionString(file, key, readOnly: true));
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Json FROM Settings WHERE Key = 'app'";
        return command.ExecuteScalar() as string;
    }

    private static void CarryMachineSettings(string restoredFile, byte[]? key, string oldJson)
    {
        var old = JsonNode.Parse(oldJson)?.AsObject();
        if (old is null) return;
        using var db = new SqliteConnection(DatabaseLock.ConnectionString(restoredFile, key));
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

/// <summary>The backup service the app uses: <see cref="DatabaseBackup"/> bound to this install's data folder and, when the data is locked, its key.</summary>
public sealed class SqliteBackupService(AppPaths paths, DatabaseKey? key = null) : IBackupService
{
    private static string Version => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";

    public Task<BackupManifest> CreateAsync(string zipPath, CancellationToken ct = default) => DatabaseBackup.CreateAsync(paths, zipPath, Version, ct, key?.Bytes);

    public Task<BackupManifest> StageAsync(string zipPath, CancellationToken ct = default, string? backupPassword = null) =>
        DatabaseBackup.StageAsync(paths, zipPath, ct, key?.Bytes, backupPassword);

    public bool RestorePending => DatabaseBackup.HasPending(paths);
    public void CancelRestore() => DatabaseBackup.CancelPending(paths);
    public string? SafetyCopy => DatabaseBackup.SafetyCopy(paths);
}
