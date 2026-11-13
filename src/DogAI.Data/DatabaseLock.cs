using System.Text;
using DogAI.Core;
using DogAI.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace DogAI.Data;

/// <summary>Turning the lock on or off did not work, with a reason fit to show. The database is as it was.</summary>
public sealed class DataLockException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a database file on disk is, told from its first bytes without opening it.</summary>
public enum DatabaseFileKind
{
    /// <summary>No file, or an empty one (SQLite treats that as a new database).</summary>
    Missing,
    Plain,
    Encrypted,
}

/// <summary>
/// The encrypted database file (M8). The SQLite build is SQLite3 Multiple Ciphers with its default cipher
/// (ChaCha20-Poly1305); the 32-byte database key is given as a raw key through the connection string's Password, so no
/// second password stretching runs on every open (the stretching is done once, on the password, in <see cref="LockFile"/>).
///
/// Encrypting or decrypting an existing database is a copy with a new key (<c>VACUUM INTO</c>), checked table by table, then
/// swapped in. The original is not touched until the copy has been checked. <c>PRAGMA rekey</c> is not used: it refuses
/// databases in WAL mode, which DOG.ai's is.
/// </summary>
public static class DatabaseLock
{
    private const string PlainHeader = "SQLite format 3\0";

    /// <summary>The database key as the SQLite library takes it.</summary>
    public static string Password(byte[] key)
    {
        if (key.Length != LockFile.KeyBytes) throw new ArgumentException("A database key is 32 bytes.", nameof(key));
        return "raw:" + Convert.ToHexString(key).ToLowerInvariant();
    }

    /// <summary>A connection string for <paramref name="file"/>; <paramref name="key"/> is null for a plain database.</summary>
    public static string ConnectionString(string file, byte[]? key, bool readOnly = false, bool pooling = false)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = file, Pooling = pooling };
        if (readOnly) builder.Mode = SqliteOpenMode.ReadOnly;
        if (key is not null) builder.Password = Password(key);
        return builder.ToString();
    }

    public static DatabaseFileKind KindOf(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[PlainHeader.Length];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return DatabaseFileKind.Missing;
            return Encoding.ASCII.GetString(head) == PlainHeader ? DatabaseFileKind.Plain : DatabaseFileKind.Encrypted;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return DatabaseFileKind.Missing;
        }
    }

    /// <summary>True when <paramref name="key"/> opens the database (or there is no database yet).</summary>
    public static bool Opens(string file, byte[]? key)
    {
        if (KindOf(file) == DatabaseFileKind.Missing) return true;
        try
        {
            using var db = new SqliteConnection(ConnectionString(file, key, readOnly: true));
            db.Open();
            Scalar(db, "SELECT COUNT(*) FROM sqlite_master");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Encrypts a plain database in place under <paramref name="key"/>. Call only when nothing else has the database open.</summary>
    public static void Encrypt(string file, byte[] key) => Recode(file, null, key);

    /// <summary>Decrypts a database in place back to a plain file. Call only when nothing else has the database open.</summary>
    public static void Decrypt(string file, byte[] key) => Recode(file, key, null);

    private static string Converting(string file) => file + ".converting";
    private static string Old(string file) => file + ".old";

    /// <summary>
    /// Tidies up after a conversion that was cut short (power loss, crash). Whatever happened, afterwards the database is
    /// either the complete old one or the complete, checked new one. Call at start, before anything opens the database.
    /// </summary>
    public static void Recover(string file)
    {
        // The old file is only moved aside after the new one was checked; if the new one never arrived, put the old one back.
        if (File.Exists(Old(file)) && !File.Exists(file)) File.Move(Old(file), file);
        DeleteWithSidecars(Old(file));
        DeleteWithSidecars(Converting(file));
    }

    private static void Recode(string file, byte[]? fromKey, byte[]? toKey)
    {
        Recover(file);
        if (KindOf(file) == DatabaseFileKind.Missing) return;
        var temp = Converting(file);
        CopyTo(file, fromKey, temp, toKey);

        // Swap. The log files beside the old database belong to it and must never meet the new one.
        try
        {
            Retry(() => File.Move(file, Old(file)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteWithSidecars(temp);
            throw new DataLockException("The database is in use by another program, so it could not be replaced. Nothing was changed.", e);
        }
        try
        {
            DeleteSidecars(file);
            File.Move(temp, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(file)) File.Move(Old(file), file);
            DeleteWithSidecars(temp);
            throw new DataLockException("The new database could not be put in place. Nothing was changed.", e);
        }
        DeleteWithSidecars(Old(file));
    }

    /// <summary>
    /// Writes a complete copy of <paramref name="source"/> to <paramref name="target"/> under another key (null for plain),
    /// then checks it: the copy must open with its key, pass SQLite's integrity check, have the same tables, indexes and
    /// triggers, and the same number of rows in every table. Throws <see cref="DataLockException"/> and removes the copy otherwise.
    /// </summary>
    public static void CopyTo(string source, byte[]? sourceKey, string target, byte[]? targetKey)
    {
        DeleteWithSidecars(target);
        try
        {
            string journal;
            using (var db = new SqliteConnection(ConnectionString(source, sourceKey)))
            {
                db.Open();
                journal = Scalar(db, "PRAGMA journal_mode") as string ?? "";
                using var vacuum = db.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $target";
                // The key travels in the file address; an empty key means a plain file. (A plain path would copy the source's key.)
                vacuum.Parameters.AddWithValue("$target", FileUri(target) + "?key=" + (targetKey is null ? "" : Password(targetKey)));
                vacuum.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var kind = KindOf(target);
            if (kind != (targetKey is null ? DatabaseFileKind.Plain : DatabaseFileKind.Encrypted))
                throw new DataLockException(targetKey is null ? "The copy did not come out as a plain database." : "The copy did not come out encrypted.");
            Compare(source, sourceKey, target, targetKey);

            // A copy made this way starts in SQLite's older journal mode; put it back the way DOG.ai runs.
            if (string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase)) UseWal(target, targetKey);
        }
        catch (SqliteException e)
        {
            SqliteConnection.ClearAllPools();
            DeleteWithSidecars(target);
            throw new DataLockException("The database could not be copied (" + e.Message + "). Nothing was changed.", e);
        }
        catch (DataLockException)
        {
            SqliteConnection.ClearAllPools();
            DeleteWithSidecars(target);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Switches a database to write-ahead logging, which lets the app read while background work writes.</summary>
    public static void UseWal(string file, byte[]? key)
    {
        using (var db = new SqliteConnection(ConnectionString(file, key)))
        {
            db.Open();
            Scalar(db, "PRAGMA journal_mode = wal");
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>The number of rows in every table, by table name. Used to check a copy and by tests.</summary>
    public static IReadOnlyDictionary<string, long> RowCounts(string file, byte[]? key)
    {
        try
        {
            using var db = new SqliteConnection(ConnectionString(file, key, readOnly: true));
            db.Open();
            return RowCounts(db);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static Dictionary<string, long> RowCounts(SqliteConnection db)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in Schema(db).Where(s => s.Type == "table").Select(s => s.Name))
            counts[table] = (long)Scalar(db, $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\"")!;
        return counts;
    }

    private static List<(string Type, string Name, string Sql)> Schema(SqliteConnection db)
    {
        var items = new List<(string, string, string)>();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT type, name, coalesce(sql, '') FROM sqlite_master ORDER BY type, name";
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return items;
    }

    private static void Compare(string source, byte[]? sourceKey, string target, byte[]? targetKey)
    {
        using var before = new SqliteConnection(ConnectionString(source, sourceKey, readOnly: true));
        using var after = new SqliteConnection(ConnectionString(target, targetKey, readOnly: true));
        before.Open();
        after.Open();

        if (Scalar(after, "PRAGMA integrity_check") as string != "ok") throw new DataLockException("The copy of the database did not pass its check. Nothing was changed.");
        if (!Schema(before).SequenceEqual(Schema(after))) throw new DataLockException("The copy of the database is missing a table, index or trigger. Nothing was changed.");
        var was = RowCounts(before);
        var now = RowCounts(after);
        foreach (var (table, rows) in was)
            if (!now.TryGetValue(table, out var copied) || copied != rows)
                throw new DataLockException($"The copy of the database has {(now.TryGetValue(table, out var n) ? n : 0)} rows in {table} where the original has {rows}. Nothing was changed.");
    }

    /// <summary>A file path as SQLite's "file:" address, so a key can ride along as a parameter.</summary>
    private static string FileUri(string path)
    {
        var full = Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/')
            .Replace("%", "%25").Replace("?", "%3f").Replace("#", "%23");
        // A network path (//server/share/...) keeps both slashes after the empty host; a drive path gets one.
        return full.StartsWith("//", StringComparison.Ordinal) ? "file://" + full : "file:///" + full;
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void DeleteSidecars(string file)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) File.Delete(file + suffix);
    }

    internal static void DeleteWithSidecars(string file)
    {
        Retry(() => File.Delete(file));
        DeleteSidecars(file);
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(150);
            }
        }
    }
}

/// <summary>Where the lock stands when DOG.ai starts, read from the data folder before anything is unlocked.</summary>
public enum DataLockState
{
    /// <summary>No lock: the database is a plain file.</summary>
    Off,

    /// <summary>Locked: the database is encrypted and needs the password, recovery key or Windows Hello.</summary>
    Locked,

    /// <summary>The owner chose a password but the database is still plain; it is encrypted as soon as the password is entered.</summary>
    TurningOn,

    /// <summary>The owner asked to turn the lock off; the database is decrypted as soon as the password is entered.</summary>
    TurningOff,
}

/// <summary>
/// The lock as a whole at start-up: what state the data folder is in, and finishing a pending change once the key is known.
/// Encrypting and decrypting only ever happen here, at start, because that is the one moment nothing else has the database open.
/// </summary>
public static class DataLock
{
    /// <summary>
    /// Reads the state without needing the key. Throws <see cref="LockFileException"/> if the lock file is damaged, or missing
    /// while the database is encrypted.
    /// </summary>
    public static DataLockState Look(AppPaths paths, out LockFile? file)
    {
        DatabaseLock.Recover(paths.DatabaseFile);
        file = LockFile.Read(paths.LockFile);
        var kind = DatabaseLock.KindOf(paths.DatabaseFile);
        if (file is null)
        {
            if (kind == DatabaseFileKind.Encrypted)
                throw new LockFileException("Your DOG.ai data is locked, but its lock file (lock.json) is missing from the data folder. " +
                    "Put the file back from a copy of the folder, or restore a backup. Without it the data cannot be opened.");
            return DataLockState.Off;
        }
        if (file.Pending == LockPending.TurnOff)
        {
            if (kind == DatabaseFileKind.Encrypted) return DataLockState.TurningOff;
            // The database was already decrypted when DOG.ai stopped; only the lock file was left to remove.
            RemoveLock(paths);
            file = null;
            return DataLockState.Off;
        }
        return kind == DatabaseFileKind.Plain ? DataLockState.TurningOn : DataLockState.Locked;
    }

    /// <summary>
    /// Call once the owner has proved who they are and <paramref name="key"/> is known. Finishes turning the lock on or off if
    /// that was pending. Returns the key to run with: the same key, or null when the lock was just turned off.
    /// Throws <see cref="DataLockException"/> (database unchanged) if the conversion fails or the key does not fit the database.
    /// </summary>
    public static byte[]? Settle(AppPaths paths, LockFile file, byte[] key)
    {
        if (!file.Wraps(key)) throw new ArgumentException("That is not this lock's database key.", nameof(key));
        var database = paths.DatabaseFile;
        switch (Look(paths, out _))
        {
            case DataLockState.TurningOn:
                DatabaseLock.Encrypt(database, key);
                return key;
            case DataLockState.TurningOff:
                DatabaseLock.Decrypt(database, key);
                RemoveLock(paths);
                return null;
            case DataLockState.Off:
                return null;
            default:
                if (!DatabaseLock.Opens(database, key))
                    throw new DataLockException("The lock file in the data folder does not belong to this database, so the database cannot be opened with it. " +
                        "Put back the lock.json that goes with it, or restore a backup.");
                return key;
        }
    }

    /// <summary>
    /// Backs out of turning the lock on, for an owner who cannot repeat the password they just chose. Only works while the
    /// database is still plain, so nothing can be lost. True if the lock file was removed.
    /// </summary>
    public static bool CancelTurningOn(AppPaths paths)
    {
        if (Look(paths, out _) != DataLockState.TurningOn) return false;
        RemoveLock(paths);
        return true;
    }

    private static void RemoveLock(AppPaths paths)
    {
        File.Delete(paths.LockFile);
        File.Delete(paths.UnlockTriesFile);
    }
}
