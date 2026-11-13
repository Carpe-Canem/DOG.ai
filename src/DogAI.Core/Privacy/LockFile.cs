using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DogAI.Core.Privacy;

/// <summary>The lock file cannot be used, with a reason fit to show.</summary>
public sealed class LockFileException(string message) : Exception(message);

/// <summary>Something the owner asked for that is finished at the next start, after the password is entered.</summary>
public enum LockPending
{
    None,

    /// <summary>The lock is being turned off: the next unlock decrypts the database and removes the lock file.</summary>
    TurnOff,
}

/// <summary>What <see cref="LockFile.Create"/> gives back. The database key and recovery key exist nowhere else.</summary>
public sealed record NewLock(LockFile File, byte[] DatabaseKey, string RecoveryKey);

/// <summary>
/// <c>lock.json</c>, the small plain file beside the database that holds the database key in wrapped form. It has to be
/// outside the database because it is read before the database can be opened.
///
/// The database key is 32 random bytes. It is never stored as it is. It is stored encrypted (AES-GCM) under:
/// a key made from the password (PBKDF2-SHA256, 600,000 rounds, random salt); a key made from the recovery key, the same
/// way; and, for each PC where Windows Hello was turned on, a key made from that PC's Hello signature of a stored
/// challenge. The file holds salts, round counts and wrapped keys only: nothing in it opens the database without the
/// password, the recovery key or the owner's Windows Hello.
/// </summary>
public sealed class LockFile
{
    public const string FileName = "lock.json";
    public const int CurrentVersion = 1;
    public const int DefaultRounds = 600_000;
    public const int KeyBytes = 32;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int ChallengeBytes = 32;
    private const int CheckBytes = 16;
    private const int MaxRounds = 10_000_000;

    private sealed class Slot
    {
        public byte[] Salt { get; set; } = [];
        public int Rounds { get; set; }
        public byte[] Nonce { get; set; } = [];
        public byte[] Wrapped { get; set; } = [];
        public byte[] Tag { get; set; } = [];
    }

    private sealed class HelloSlot
    {
        public string Pc { get; set; } = "";
        public byte[] Challenge { get; set; } = [];
        public byte[] Salt { get; set; } = [];
        public byte[] Nonce { get; set; } = [];
        public byte[] Wrapped { get; set; } = [];
        public byte[] Tag { get; set; } = [];
    }

    private sealed class Document
    {
        public int Version { get; set; }
        public Slot? Password { get; set; }
        public Slot? Recovery { get; set; }
        public List<HelloSlot> Hello { get; set; } = [];
        public byte[] Check { get; set; } = [];

        [JsonConverter(typeof(JsonStringEnumConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public LockPending Pending { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Skin { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Theme { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Document _doc;

    private LockFile(Document doc) => _doc = doc;

    /// <summary>
    /// Makes a new lock: a random database key, wrapped by <paramref name="password"/> and by a new recovery key.
    /// <paramref name="rounds"/> is only lowered by tests.
    /// </summary>
    public static NewLock Create(string password, int rounds = DefaultRounds)
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var recovery = RecoveryKey.New();
        var doc = new Document
        {
            Version = CurrentVersion,
            Password = Wrap(key, PasswordSecret(password), rounds, PasswordPurpose),
            Recovery = Wrap(key, Encoding.UTF8.GetBytes(recovery), rounds, RecoveryPurpose),
            Check = CheckOf(key),
        };
        return new NewLock(new LockFile(doc), key, recovery);
    }

    /// <summary>The database key if <paramref name="password"/> is right, else null.</summary>
    public byte[]? OpenWithPassword(string? password) =>
        string.IsNullOrEmpty(password) ? null : Checked(Unwrap(_doc.Password!, PasswordSecret(password), PasswordPurpose));

    /// <summary>The database key if <paramref name="typed"/> is the recovery key (spaces, dashes and small letters forgiven), else null.</summary>
    public byte[]? OpenWithRecoveryKey(string? typed) =>
        RecoveryKey.Read(typed) is { } recovery ? Checked(Unwrap(_doc.Recovery!, Encoding.UTF8.GetBytes(recovery), RecoveryPurpose)) : null;

    /// <summary>True when <paramref name="databaseKey"/> is the key this file wraps.</summary>
    public bool Wraps(byte[]? databaseKey) =>
        databaseKey is { Length: KeyBytes } && CryptographicOperations.FixedTimeEquals(CheckOf(databaseKey), _doc.Check);

    /// <summary>
    /// Sets a new password. Only the wrapping changes; the database key, and so the database, stay as they are.
    /// The recovery key and Windows Hello keep working.
    /// </summary>
    public void ChangePassword(byte[] databaseKey, string newPassword)
    {
        RequireKey(databaseKey);
        _doc.Password = Wrap(databaseKey, PasswordSecret(newPassword), _doc.Password!.Rounds, PasswordPurpose);
    }

    /// <summary>Makes a new recovery key and returns it. The old one stops working.</summary>
    public string NewRecoveryKey(byte[] databaseKey)
    {
        RequireKey(databaseKey);
        var recovery = RecoveryKey.New();
        _doc.Recovery = Wrap(databaseKey, Encoding.UTF8.GetBytes(recovery), _doc.Recovery!.Rounds, RecoveryPurpose);
        return recovery;
    }

    // ---- Windows Hello, one entry per PC ----

    /// <summary>True when Windows Hello was turned on for this lock on the PC with this id.</summary>
    public bool HasHello(string pcId) => HelloOf(pcId) is not null;

    /// <summary>How many PCs have Windows Hello turned on for this lock.</summary>
    public int HelloCount => _doc.Hello.Count;

    /// <summary>
    /// Turns Windows Hello on for this PC: Hello signs a new random challenge and the database key is wrapped under a key
    /// made from that signature. False when the owner cancelled or Hello is not available. Replaces this PC's earlier entry.
    /// </summary>
    public async Task<bool> AddHelloAsync(IHelloSigner hello, byte[] databaseKey)
    {
        RequireKey(databaseKey);
        var challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        if (await hello.SignAsync(challenge, create: true) is not { Length: > 0 } signature) return false;
        // The scheme depends on the same challenge giving the same signature. If this PC's Hello does not do that
        // (it would be a key type other than the RSA one Windows makes today), say no rather than save an entry that never opens.
        if (await hello.SignAsync(challenge, create: false) is not { } again || !signature.AsSpan().SequenceEqual(again)) return false;

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var slot = new HelloSlot { Pc = hello.PcId, Challenge = challenge, Salt = salt };
        Seal(databaseKey, HelloKey(signature, salt), HelloPurpose(hello.PcId), out var nonce, out var wrapped, out var tag);
        (slot.Nonce, slot.Wrapped, slot.Tag) = (nonce, wrapped, tag);
        _doc.Hello.RemoveAll(h => h.Pc == hello.PcId);
        _doc.Hello.Add(slot);
        return true;
    }

    /// <summary>The database key through Windows Hello on this PC; null when there is no entry, the owner cancelled, or the Hello key changed.</summary>
    public async Task<byte[]?> OpenWithHelloAsync(IHelloSigner hello)
    {
        if (HelloOf(hello.PcId) is not { } slot) return null;
        if (await hello.SignAsync(slot.Challenge, create: false) is not { Length: > 0 } signature) return null;
        return Checked(Open(HelloKey(signature, slot.Salt), slot.Nonce, slot.Wrapped, slot.Tag, HelloPurpose(slot.Pc)));
    }

    /// <summary>Turns Windows Hello off for one PC. True if there was an entry.</summary>
    public bool RemoveHello(string pcId) => _doc.Hello.RemoveAll(h => h.Pc == pcId) > 0;

    /// <summary>A copy for a backup: Windows Hello entries only work on their own PC, so they are left out.</summary>
    public LockFile WithoutHello()
    {
        var copy = Parse(ToJson());
        copy._doc.Hello.Clear();
        copy._doc.Pending = LockPending.None;
        return copy;
    }

    private HelloSlot? HelloOf(string pcId) => _doc.Hello.FirstOrDefault(h => h.Pc == pcId);

    // ---- Other things the file carries ----

    /// <summary>Set when the owner asked to turn the lock off; carried out at the next unlock.</summary>
    public LockPending Pending
    {
        get => _doc.Pending;
        set => _doc.Pending = value;
    }

    /// <summary>The look of the app (skin id), copied here so the lock screen can match before the settings can be read.</summary>
    public string? Skin
    {
        get => _doc.Skin;
        set => _doc.Skin = value;
    }

    /// <summary>"dark" or "light", copied here like <see cref="Skin"/>.</summary>
    public string? Theme
    {
        get => _doc.Theme;
        set => _doc.Theme = value;
    }

    /// <summary>The PBKDF2 round count used for the password.</summary>
    public int PasswordRounds => _doc.Password!.Rounds;

    // ---- Reading and writing ----

    public string ToJson() => JsonSerializer.Serialize(_doc, Json);

    /// <summary>Reads lock file text. Throws <see cref="LockFileException"/> if it is damaged or from a newer DOG.ai.</summary>
    public static LockFile Parse(string json)
    {
        Document? doc;
        try
        {
            doc = JsonSerializer.Deserialize<Document>(json, Json);
        }
        catch (JsonException)
        {
            throw new LockFileException(Damaged);
        }
        if (doc is null || doc.Version < 1) throw new LockFileException(Damaged);
        if (doc.Version > CurrentVersion) throw new LockFileException("The lock file was made by a newer DOG.ai. Update DOG.ai, then try again.");
        if (!Usable(doc.Password) || !Usable(doc.Recovery) || doc.Check is not { Length: CheckBytes }) throw new LockFileException(Damaged);
        // A damaged Windows Hello entry must not stand in the way of the password: it is dropped.
        doc.Hello = (doc.Hello ?? []).Where(h => h is { Pc.Length: > 0, Challenge.Length: > 0, Salt.Length: > 0, Nonce.Length: NonceBytes, Wrapped.Length: KeyBytes, Tag.Length: TagBytes }).ToList();
        if (!Enum.IsDefined(doc.Pending)) doc.Pending = LockPending.None;
        return new LockFile(doc);
    }

    private const string Damaged = "The lock file (lock.json) is damaged. Put back a copy of it from a backup of your DOG.ai data folder.";

    private static bool Usable(Slot? slot) =>
        slot is { Salt.Length: >= 8, Nonce.Length: NonceBytes, Wrapped.Length: KeyBytes, Tag.Length: TagBytes, Rounds: >= 1 and <= MaxRounds };

    /// <summary>The lock file at <paramref name="file"/>, or null when there is none (the lock is off).</summary>
    public static LockFile? Read(string file)
    {
        string json;
        try
        {
            if (!File.Exists(file)) return null;
            json = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LockFileException("The lock file (lock.json) could not be read: " + e.Message);
        }
        return Parse(json);
    }

    /// <summary>Saves the file so that a crash halfway leaves either the old file or the new one, never a broken one.</summary>
    public void Write(string file)
    {
        var temp = file + ".new";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(ToJson()));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, file, overwrite: true);
    }

    // ---- The wrapping itself ----

    private const string PasswordPurpose = "DOG.ai lock v1 password";
    private const string RecoveryPurpose = "DOG.ai lock v1 recovery";
    private static string HelloPurpose(string pcId) => "DOG.ai lock v1 hello " + pcId;

    /// <summary>The same password typed on another keyboard can arrive as different characters that look the same; this makes them one.</summary>
    private static byte[] PasswordSecret(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A password is needed.", nameof(password));
        return Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
    }

    private static Slot Wrap(byte[] databaseKey, byte[] secret, int rounds, string purpose)
    {
        if (rounds is < 1 or > MaxRounds) throw new ArgumentOutOfRangeException(nameof(rounds));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(secret, salt, rounds, HashAlgorithmName.SHA256, KeyBytes);
        Seal(databaseKey, wrappingKey, purpose, out var nonce, out var wrapped, out var tag);
        return new Slot { Salt = salt, Rounds = rounds, Nonce = nonce, Wrapped = wrapped, Tag = tag };
    }

    private static byte[]? Unwrap(Slot slot, byte[] secret, string purpose) =>
        Open(Rfc2898DeriveBytes.Pbkdf2(secret, slot.Salt, slot.Rounds, HashAlgorithmName.SHA256, KeyBytes), slot.Nonce, slot.Wrapped, slot.Tag, purpose);

    private static byte[] HelloKey(byte[] signature, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, KeyBytes, salt, Encoding.UTF8.GetBytes("DOG.ai lock v1 hello key"));

    private static void Seal(byte[] databaseKey, byte[] wrappingKey, string purpose, out byte[] nonce, out byte[] wrapped, out byte[] tag)
    {
        nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        wrapped = new byte[databaseKey.Length];
        tag = new byte[TagBytes];
        using var aes = new AesGcm(wrappingKey, TagBytes);
        aes.Encrypt(nonce, databaseKey, wrapped, tag, Encoding.UTF8.GetBytes(purpose));
    }

    private static byte[]? Open(byte[] wrappingKey, byte[] nonce, byte[] wrapped, byte[] tag, string purpose)
    {
        var key = new byte[wrapped.Length];
        try
        {
            using var aes = new AesGcm(wrappingKey, TagBytes);
            aes.Decrypt(nonce, wrapped, tag, key, Encoding.UTF8.GetBytes(purpose));
            return key;
        }
        catch (CryptographicException)
        {
            // Wrong password, recovery key or signature (or the entry was changed).
            return null;
        }
    }

    /// <summary>A fingerprint of the database key that cannot be turned back into it; lets code confirm it holds the right key before wrapping it again.</summary>
    private static byte[] CheckOf(byte[] databaseKey) =>
        HMACSHA256.HashData(databaseKey, Encoding.UTF8.GetBytes("DOG.ai lock v1 key check"))[..CheckBytes];

    private byte[]? Checked(byte[]? key) => key is not null && Wraps(key) ? key : null;

    private void RequireKey(byte[] databaseKey)
    {
        if (!Wraps(databaseKey)) throw new ArgumentException("That is not this lock's database key.", nameof(databaseKey));
    }
}
