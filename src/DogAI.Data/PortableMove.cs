using System.Text.Json;
using DogAI.Core;
using Microsoft.Data.Sqlite;

namespace DogAI.Data;

/// <summary>
/// A portable install can be moved or copied to another folder or drive. The database holds full paths to models (models
/// folder, default models, last model, assistants' models, downloaded-model records), so when the folder moved those paths are
/// rewritten from the old location to the new one. Call at start, before anything opens the database.
/// </summary>
public static class PortableMove
{
    /// <summary>
    /// The folder a portable install last ran from, when that is not where it is now; null when it has not moved (or is not
    /// portable). Reads only the small record file, never the database, so it can be asked before the data is unlocked.
    /// </summary>
    public static string? MovedFrom(AppPaths paths)
    {
        if (paths.PortableHome is not { } home) return null;
        string? previous = null;
        try { if (File.Exists(paths.HomeRecordFile)) previous = File.ReadAllText(paths.HomeRecordFile).Trim(); }
        catch (IOException) { }
        return !string.IsNullOrEmpty(previous) && !string.Equals(previous, home, StringComparison.OrdinalIgnoreCase) ? previous : null;
    }

    /// <summary>
    /// Returns a sentence about what changed, or null when the install has not moved (or is not portable).
    /// <paramref name="key"/> is the database key when the data is locked; this then has to run after the unlock,
    /// because it rewrites paths stored in the database.
    /// </summary>
    public static string? Apply(AppPaths paths, byte[]? key = null)
    {
        if (paths.PortableHome is not { } home) return null;
        var previous = MovedFrom(paths);

        string? result = null;
        if (previous is not null && File.Exists(paths.DatabaseFile))
        {
            var changed = Rebase(paths.DatabaseFile, previous, home, key);
            result = $"DOG.ai moved from {previous} to {home}; {changed} saved paths updated.";
        }
        File.WriteAllText(paths.HomeRecordFile, home);
        return result;
    }

    /// <summary>Rewrites every saved path under <paramref name="from"/> to sit under <paramref name="to"/>. Returns how many rows changed.</summary>
    public static int Rebase(string databaseFile, string from, string to, byte[]? key = null)
    {
        var oldPrefix = WithSeparator(from);
        var newPrefix = WithSeparator(to);
        using var db = new SqliteConnection(DatabaseLock.ConnectionString(databaseFile, key));
        db.Open();
        using var tx = db.BeginTransaction();
        var changed = 0;

        using (var records = db.CreateCommand())
        {
            records.Transaction = tx;
            records.CommandText = """
                UPDATE OR IGNORE ModelRecords SET Path = $new || substr(Path, length($old) + 1)
                WHERE lower(substr(Path, 1, length($old))) = lower($old)
                """;
            records.Parameters.AddWithValue("$old", oldPrefix);
            records.Parameters.AddWithValue("$new", newPrefix);
            changed += records.ExecuteNonQuery();
        }

        // Settings are JSON documents, where a path's backslashes are written doubled.
        using (var settings = db.CreateCommand())
        {
            settings.Transaction = tx;
            settings.CommandText = "UPDATE Settings SET Json = replace(Json, $old, $new) WHERE instr(Json, $old) > 0";
            settings.Parameters.AddWithValue("$old", JsonText(oldPrefix));
            settings.Parameters.AddWithValue("$new", JsonText(newPrefix));
            changed += settings.ExecuteNonQuery();
        }

        tx.Commit();
        SqliteConnection.ClearAllPools();
        return changed;
    }

    private static string WithSeparator(string folder) =>
        folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string JsonText(string value) => JsonSerializer.Serialize(value)[1..^1];
}
