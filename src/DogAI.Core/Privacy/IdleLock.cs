namespace DogAI.Core.Privacy;

/// <summary>
/// Decides when a locked DOG.ai locks itself. "Idle" means nobody has used the keyboard or mouse anywhere on this PC for
/// the chosen time (like a screen saver), so the lock screen only appears when nobody is there. While DOG.ai is busy
/// (writing a reply, reading aloud, listening) the lock waits, but never longer than one more idle period, so a
/// microphone left listening cannot keep the data open for ever.
/// </summary>
public sealed class IdleLock
{
    /// <summary>Choices offered in Settings, in minutes; 0 is off.</summary>
    public static readonly int[] Choices = [0, 5, 15, 30, 60];

    public const int DefaultMinutes = 15;

    /// <summary>Test runs can shorten the idle time to this many seconds (the setting must still be on).</summary>
    public const string TestSecondsVariable = "DOGAI_IDLE_LOCK_SECONDS";

    /// <summary>The idle time for <paramref name="minutes"/> from Settings, or null when idle lock is off.</summary>
    public static TimeSpan? Limit(int minutes, string? testSeconds = null)
    {
        if (minutes <= 0) return null;
        if (int.TryParse(testSeconds, out var seconds) && seconds > 0) return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// True when DOG.ai should lock now. <paramref name="idle"/> is how long nobody has touched the keyboard or mouse;
    /// <paramref name="busy"/> says DOG.ai is in the middle of something.
    /// </summary>
    public static bool Due(TimeSpan? limit, TimeSpan idle, bool busy)
    {
        if (limit is not { } l || idle < l) return false;
        return !busy || idle >= l + l;
    }
}

/// <summary>Locking a running DOG.ai (implemented by the App: it restarts DOG.ai into the lock screen).</summary>
public interface IAppLock
{
    /// <summary>True when the data lock is on and this run is unlocked, so there is something to lock.</summary>
    bool CanLock { get; }

    /// <summary>Restarts DOG.ai into the lock screen, the only sure way to clear the key and every decrypted chat from memory. Does nothing when <see cref="CanLock"/> is false.</summary>
    void LockNow();

    /// <summary>Reads the idle time and tray setting again after Settings changed them.</summary>
    Task ApplyAsync();
}
