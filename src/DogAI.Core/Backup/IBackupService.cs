namespace DogAI.Core.Backup;

/// <summary>What a backup file says about itself.</summary>
/// <param name="Locked">
/// The database inside is encrypted and the backup carries its lock file: it opens with the password or recovery key it was
/// made with. A locked backup's own description does not say how many chats it holds; that is read once it is unlocked.
/// </param>
public sealed record BackupManifest(string App, int Format, string Version, DateTime CreatedUtc, string? LastMigration, int Chats, int Messages, bool Locked = false);

/// <summary>A backup file that cannot be restored, with a reason fit to show.</summary>
public class BackupException(string message) : Exception(message);

/// <summary>
/// The backup is locked and needs the password (or recovery key) it was made with. <see cref="Wrong"/> is false when none
/// was given yet, true when the one given does not open it.
/// </summary>
public sealed class BackupLockedException(string message, bool wrong) : BackupException(message)
{
    public bool Wrong { get; } = wrong;
}

/// <summary>Backing up and restoring everything the user made (not models or the engine).</summary>
public interface IBackupService
{
    /// <summary>Writes a backup zip. The download token is not included.</summary>
    Task<BackupManifest> CreateAsync(string zipPath, CancellationToken ct = default);

    /// <summary>
    /// Checks a backup and queues it. It takes effect the next time DOG.ai starts, never while it runs.
    /// Throws <see cref="BackupException"/> with a plain reason if the file is not a usable backup, and
    /// <see cref="BackupLockedException"/> if it is a locked backup and <paramref name="backupPassword"/> (its password or
    /// recovery key) is missing or wrong.
    /// </summary>
    Task<BackupManifest> StageAsync(string zipPath, CancellationToken ct = default, string? backupPassword = null);

    /// <summary>A restore is queued and waits for the next start.</summary>
    bool RestorePending { get; }

    /// <summary>Throws away a queued restore. Nothing else changes.</summary>
    void CancelRestore();

    /// <summary>The database as it was before the last restore, if there was one.</summary>
    string? SafetyCopy { get; }
}
