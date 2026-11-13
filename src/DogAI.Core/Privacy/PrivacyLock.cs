using System.Security.Cryptography;

namespace DogAI.Core.Privacy;

/// <summary>Where the lock stands, as Settings → Privacy sees it while DOG.ai runs.</summary>
public enum LockStatus
{
    /// <summary>No lock: the database is a plain file.</summary>
    Off,

    /// <summary>Locked: this run opened the database with the key.</summary>
    On,

    /// <summary>A password was chosen; the data is encrypted when it is typed at the lock screen after a restart.</summary>
    TurningOn,

    /// <summary>Turning the lock off was asked for; the data is decrypted when the password is typed after a restart.</summary>
    TurningOff,
}

/// <summary>Rules for a new password, with reasons fit to show.</summary>
public static class LockPassword
{
    public const int MinLength = 8;

    /// <summary>Null when <paramref name="password"/> can be used; otherwise what is wrong with it.</summary>
    public static string? Problem(string? password, string? repeat)
    {
        if (string.IsNullOrEmpty(password)) return "Choose a password.";
        if (password.Trim().Length < MinLength) return $"Use at least {MinLength} characters. A longer password is much harder to guess.";
        if (password != repeat) return "The two passwords are not the same.";
        return null;
    }
}

/// <summary>
/// The check that the owner really wrote the recovery key down: two of its six groups, picked at random, typed back.
/// </summary>
public sealed record RecoveryCheck(int First, int Second)
{
    public static int Groups => RecoveryKey.Length / RecoveryKey.GroupSize;

    /// <summary>Two different groups, numbered from 1, the smaller first.</summary>
    public static RecoveryCheck Pick()
    {
        var a = RandomNumberGenerator.GetInt32(1, Groups + 1);
        int b;
        do b = RandomNumberGenerator.GetInt32(1, Groups + 1); while (b == a);
        return new RecoveryCheck(Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>True when both typed groups match the key (small letters and spaces forgiven).</summary>
    public bool Matches(string recoveryKey, string? first, string? second) =>
        Same(Group(recoveryKey, First), first) && Same(Group(recoveryKey, Second), second);

    private static string Group(string key, int number) => key.Substring((number - 1) * RecoveryKey.GroupSize, RecoveryKey.GroupSize);

    private static bool Same(string group, string? typed) =>
        typed is not null && string.Equals(group, new string(typed.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()), StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the lock screen needs to look like the app, copied into <c>lock.json</c> because the settings are locked away.</summary>
/// <param name="Skin">Skin id.</param>
/// <param name="Theme">"dark" or "light"; null when DOG.ai follows Windows.</param>
/// <param name="Tray">The tray icon is on.</param>
public sealed record LockLook(string Skin, string? Theme, bool Tray);

/// <summary>
/// The lock as the running DOG.ai changes it (Settings → Privacy). Every change re-reads <c>lock.json</c> from disk first,
/// because the lock screen may have changed it since this run started (for example by adding Windows Hello).
///
/// Encrypting and decrypting never happen here: they need the database closed, so they are finished at the lock screen
/// after a restart (<c>DataLock.Settle</c>). Turning the lock on writes <c>lock.json</c> only once the owner has typed
/// part of the recovery key back; a new recovery key replaces the old one only after the same check.
/// The slow password work (PBKDF2) runs off the caller's thread.
/// </summary>
public sealed class PrivacyLock(AppPaths paths, DatabaseKey key, IHelloSigner? hello = null, int rounds = LockFile.DefaultRounds)
{
    public LockStatus Status
    {
        get
        {
            var file = Read();
            if (file is null) return LockStatus.Off;
            if (file.Pending == LockPending.TurnOff) return LockStatus.TurningOff;
            return key.Locked ? LockStatus.On : LockStatus.TurningOn;
        }
    }

    /// <summary>True when this run is locked and nothing is pending: Lock now and idle lock make sense.</summary>
    public bool CanLock => key.Locked && Status == LockStatus.On;

    // ---- Turning the lock on ----

    /// <summary>Makes a new lock in memory only. Nothing is written until <see cref="TurnOn"/>.</summary>
    public Task<NewLock> PrepareAsync(string password) => Task.Run(() => LockFile.Create(password, rounds));

    /// <summary>
    /// Writes <c>lock.json</c>. The data is encrypted at the lock screen once the new password is typed after a restart,
    /// so data is never encrypted under a password the owner cannot repeat.
    /// </summary>
    public void TurnOn(NewLock made, LockLook look)
    {
        if (Read() is not null) throw new InvalidOperationException("The lock is already on.");
        Apply(made.File, look);
        made.File.Write(paths.LockFile);
    }

    /// <summary>
    /// Takes back a lock that was chosen this run but not yet put into effect (the data is still plain). Nothing is lost.
    /// </summary>
    public bool CancelTurningOn()
    {
        if (key.Locked || Status != LockStatus.TurningOn) return false;
        File.Delete(paths.LockFile);
        File.Delete(paths.UnlockTriesFile);
        return true;
    }

    // ---- Changes that need the current password ----

    /// <summary>True when <paramref name="password"/> is the current password.</summary>
    public Task<bool> CheckPasswordAsync(string password) => Task.Run(() => Read()?.OpenWithPassword(password) is not null);

    /// <summary>Sets a new password. False when <paramref name="current"/> is wrong. The data itself is not touched.</summary>
    public Task<bool> ChangePasswordAsync(string current, string next) => Task.Run(() =>
    {
        if (Read() is not { } file || file.OpenWithPassword(current) is not { } k) return false;
        file.ChangePassword(k, next);
        file.Write(paths.LockFile);
        return true;
    });

    /// <summary>
    /// A new recovery key to show, or null when <paramref name="current"/> is not the password. Nothing changes until
    /// <see cref="UseRecoveryKeyAsync"/>: until then the old recovery key still works.
    /// </summary>
    public async Task<string?> NewRecoveryKeyAsync(string current) =>
        await CheckPasswordAsync(current) ? RecoveryKey.New() : null;

    /// <summary>Makes <paramref name="recovery"/> the recovery key; the old one stops working.</summary>
    public Task UseRecoveryKeyAsync(string recovery) => Task.Run(() =>
    {
        var (file, k) = Open();
        file.UseRecoveryKey(k, recovery);
        file.Write(paths.LockFile);
    });

    /// <summary>
    /// Asks for the lock to be turned off. False when <paramref name="password"/> is wrong. The data is decrypted at the
    /// lock screen after a restart (the database must be closed for that).
    /// </summary>
    public Task<bool> TurnOffAsync(string password) => Task.Run(() =>
    {
        if (Read() is not { } file || file.OpenWithPassword(password) is null) return false;
        file.Pending = LockPending.TurnOff;
        file.Write(paths.LockFile);
        return true;
    });

    /// <summary>Takes back <see cref="TurnOffAsync"/> before the restart.</summary>
    public bool KeepLocked()
    {
        if (Read() is not { Pending: LockPending.TurnOff } file) return false;
        file.Pending = LockPending.None;
        file.Write(paths.LockFile);
        return true;
    }

    // ---- Windows Hello on this PC ----

    public bool HelloOnHere => hello is not null && Read()?.HasHello(hello.PcId) == true;

    public async Task<bool> HelloAvailableAsync() => hello is not null && await hello.IsAvailableAsync();

    /// <summary>Turns Windows Hello on for this PC (Windows asks for the face, fingerprint or PIN). False when cancelled.</summary>
    public async Task<bool> TurnOnHelloAsync()
    {
        if (hello is null) return false;
        var (file, k) = Open();
        if (!await file.AddHelloAsync(hello, k)) return false;
        file.SetHelloDeclined(hello.PcId, false);
        file.Write(paths.LockFile);
        return true;
    }

    public bool TurnOffHello()
    {
        if (hello is null || Read() is not { } file || !file.RemoveHello(hello.PcId)) return false;
        file.Write(paths.LockFile);
        return true;
    }

    // ---- The lock screen's look ----

    /// <summary>Copies the look into <c>lock.json</c> when it differs. Nothing happens while the lock is off.</summary>
    public bool SaveLook(LockLook look)
    {
        if (Read() is not { } file) return false;
        if (file.Skin == look.Skin && file.Theme == look.Theme && file.Tray == look.Tray) return false;
        Apply(file, look);
        file.Write(paths.LockFile);
        return true;
    }

    private static void Apply(LockFile file, LockLook look)
    {
        file.Skin = look.Skin;
        file.Theme = look.Theme;
        file.Tray = look.Tray;
    }

    private LockFile? Read() => LockFile.Read(paths.LockFile);

    /// <summary>The lock file and this run's key, for changes that re-wrap the key.</summary>
    private (LockFile File, byte[] Key) Open()
    {
        var file = Read() ?? throw new InvalidOperationException("The lock is off.");
        if (key.Bytes is not { } k || !file.Wraps(k)) throw new InvalidOperationException("This change needs DOG.ai to have been unlocked.");
        return (file, k);
    }
}
