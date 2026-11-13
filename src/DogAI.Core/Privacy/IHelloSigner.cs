namespace DogAI.Core.Privacy;

/// <summary>
/// Windows Hello as the lock needs it: a key that lives in this PC's Windows account and signs a challenge only after the
/// owner shows their face, fingerprint or Windows PIN. The same challenge always gives the same signature (checked on the
/// owner's PC before this was built), so the signature can be turned into a key without storing any secret.
/// Implemented in the App project; tests use a fake.
/// </summary>
public interface IHelloSigner
{
    /// <summary>Names this PC and Windows account in <see cref="LockFile"/>. Says nothing readable about either.</summary>
    string PcId { get; }

    /// <summary>True when Windows Hello is set up for this Windows account.</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Asks Windows Hello to sign <paramref name="challenge"/>. With <paramref name="create"/> DOG.ai's Hello key is made
    /// first if this account does not have one yet. Null when the owner cancels, Hello is not set up, or the key is not there.
    /// </summary>
    Task<byte[]?> SignAsync(byte[] challenge, bool create);
}

/// <summary>The key that opens the database during this run of DOG.ai. <see cref="Bytes"/> is null when the lock is off.</summary>
public sealed class DatabaseKey(byte[]? bytes)
{
    public static DatabaseKey None { get; } = new(null);

    public byte[]? Bytes { get; } = bytes;

    public bool Locked => Bytes is not null;
}
