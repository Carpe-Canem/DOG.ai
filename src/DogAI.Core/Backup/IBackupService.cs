namespace DogAI.Core.Backup;

/// <summary>What a backup file says about itself.</summary>
public sealed record BackupManifest(string App, int Format, string Version, DateTime CreatedUtc, string? LastMigration, int Chats, int Messages);

/// <summary>A backup file that cannot be restored, with a reason fit to show.</summary>
public sealed class BackupException(string message) : Exception(message);

/// <summary>Backing up and restoring everything the user made (not models or the engine).</summary>
public interface IBackupService
{
    /// <summary>Writes a backup zip. The download token is not included.</summary>
    Task<BackupManifest> CreateAsync(string zipPath, CancellationToken ct = default);

    /// <summary>
    /// Checks a backup and queues it. It takes effect the next time DOG.ai starts, never while it runs.
    /// Throws <see cref="BackupException"/> with a plain reason if the file is not a usable backup.
    /// </summary>
    Task<BackupManifest> StageAsync(string zipPath, CancellationToken ct = default);

    /// <summary>A restore is queued and waits for the next start.</summary>
    bool RestorePending { get; }

    /// <summary>Throws away a queued restore. Nothing else changes.</summary>
    void CancelRestore();

    /// <summary>The database as it was before the last restore, if there was one.</summary>
    string? SafetyCopy { get; }
}
