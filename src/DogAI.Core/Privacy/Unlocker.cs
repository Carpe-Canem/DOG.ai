namespace DogAI.Core.Privacy;

/// <summary>
/// What the lock screen does, without the screen: tries a password, a recovery key or Windows Hello against the lock file,
/// and enforces the wait after wrong tries (<see cref="UnlockThrottle"/>). A try made while a wait is running is not
/// even looked at.
/// </summary>
public sealed class Unlocker(LockFile file, string lockFilePath, string triesFilePath, IHelloSigner? hello = null, Func<DateTime>? utcNow = null)
{
    private readonly UnlockThrottle _throttle = UnlockThrottle.Load(triesFilePath);
    private readonly Func<DateTime> _now = utcNow ?? (() => DateTime.UtcNow);

    public LockFile File => file;

    /// <summary>How long until the next try is allowed; zero when one may be made now.</summary>
    public TimeSpan WaitLeft => _throttle.WaitLeft(_now());

    /// <summary>Wrong tries in a row so far.</summary>
    public int Failures => _throttle.Failures;

    /// <summary>The database key if the password is right. Null if it is wrong (counted) or a wait is running (not counted).</summary>
    public byte[]? TryPassword(string? password) => Try(() => file.OpenWithPassword(password));

    /// <summary>
    /// The database key if the recovery key is right. Null if it is wrong (counted), a wait is running, or what was typed
    /// is not shaped like a recovery key at all (not counted).
    /// </summary>
    public byte[]? TryRecoveryKey(string? typed) => RecoveryKey.Read(typed) is null ? null : Try(() => file.OpenWithRecoveryKey(typed));

    private byte[]? Try(Func<byte[]?> open)
    {
        if (WaitLeft > TimeSpan.Zero) return null;
        var key = open();
        if (key is null) _throttle.Failed(_now());
        else _throttle.Succeeded();
        _throttle.Save(triesFilePath);
        return key;
    }

    /// <summary>True when Windows Hello was turned on for this lock on this PC.</summary>
    public bool HelloIsOn => hello is not null && file.HasHello(hello.PcId);

    /// <summary>True when Windows Hello could be turned on here: it is set up in Windows and not yet on for this lock.</summary>
    public async Task<bool> CanTurnOnHelloAsync() => hello is not null && !file.HasHello(hello.PcId) && await hello.IsAvailableAsync();

    /// <summary>
    /// The database key through Windows Hello. Null when it is not on for this PC, the owner cancelled, or the key no longer
    /// fits. Not counted as a wrong try: Windows itself limits Hello attempts, and there is nothing to guess.
    /// </summary>
    public async Task<byte[]?> TryHelloAsync()
    {
        if (hello is null) return null;
        var key = await file.OpenWithHelloAsync(hello);
        if (key is not null)
        {
            _throttle.Succeeded();
            _throttle.Save(triesFilePath);
        }
        return key;
    }

    /// <summary>Turns Windows Hello on for this PC and saves the lock file. False when the owner cancelled or Hello is not available.</summary>
    public async Task<bool> TurnOnHelloAsync(byte[] databaseKey)
    {
        if (hello is null || !await file.AddHelloAsync(hello, databaseKey)) return false;
        file.Write(lockFilePath);
        return true;
    }

    /// <summary>
    /// True when Windows Hello should be offered after this unlock: it is set up in Windows, not on for this lock here, and
    /// the owner has not said "Not now" on this PC before.
    /// </summary>
    public async Task<bool> ShouldOfferHelloAsync() =>
        hello is not null && !file.HelloDeclined(hello.PcId) && await CanTurnOnHelloAsync();

    /// <summary>Remembers "Not now" for Windows Hello on this PC (it can still be turned on in Settings).</summary>
    public void DeclineHello()
    {
        if (hello is null) return;
        file.SetHelloDeclined(hello.PcId, true);
        file.Write(lockFilePath);
    }

    /// <summary>Takes back a request to turn the lock off. Needs no key: it only keeps things as they are.</summary>
    public void KeepLocked()
    {
        file.Pending = LockPending.None;
        file.Write(lockFilePath);
    }

    /// <summary>Sets a new password (after the recovery key opened the lock) and saves the lock file.</summary>
    public void ChangePassword(byte[] databaseKey, string newPassword)
    {
        file.ChangePassword(databaseKey, newPassword);
        file.Write(lockFilePath);
    }
}
