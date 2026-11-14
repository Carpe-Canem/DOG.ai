using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DogAI.Core.Privacy;

namespace DogAI.Tests;

/// <summary>The lock file, the recovery key and the wait after wrong tries (M8). No Windows Hello needed: a fake signs.</summary>
public sealed class PrivacyTests
{
    /// <summary>Real locks use 600,000 rounds; the tests use few so they stay quick. One test checks the real number.</summary>
    private const int Rounds = 1000;

    /// <summary>Signs like Windows Hello does: the same challenge always gives the same bytes, another PC gives other bytes.</summary>
    internal sealed class FakeHello(string pcId, bool available = true) : IHelloSigner
    {
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
        private bool _hasKey;

        public string PcId => pcId;
        public bool Cancel { get; set; }
        public bool Random { get; set; }
        public int Asked { get; private set; }

        public Task<bool> IsAvailableAsync() => Task.FromResult(available);

        public Task<byte[]?> SignAsync(byte[] challenge, bool create)
        {
            Asked++;
            if (!available || Cancel) return Task.FromResult<byte[]?>(null);
            if (!_hasKey && !create) return Task.FromResult<byte[]?>(null);
            _hasKey = true;
            if (Random) return Task.FromResult<byte[]?>(RandomNumberGenerator.GetBytes(256));
            return Task.FromResult<byte[]?>(HMACSHA512.HashData(_secret, challenge));
        }

        public void LoseKey() => _hasKey = false;
    }

    [Fact]
    public void A_new_lock_opens_with_its_password_and_gives_the_database_key()
    {
        var made = LockFile.Create("correct horse", Rounds);

        Assert.Equal(LockFile.KeyBytes, made.DatabaseKey.Length);
        Assert.Equal(made.DatabaseKey, made.File.OpenWithPassword("correct horse"));
        Assert.True(made.File.Wraps(made.DatabaseKey));
    }

    [Fact]
    public void A_wrong_password_is_refused()
    {
        var file = LockFile.Create("correct horse", Rounds).File;

        Assert.Null(file.OpenWithPassword("correct horsf"));
        Assert.Null(file.OpenWithPassword("Correct horse"));
        Assert.Null(file.OpenWithPassword(""));
        Assert.Null(file.OpenWithPassword(null));
    }

    [Fact]
    public void A_lock_cannot_be_made_with_an_empty_password()
    {
        Assert.Throws<ArgumentException>(() => LockFile.Create("", Rounds));
    }

    [Fact]
    public void Two_locks_with_the_same_password_have_different_keys_and_salts()
    {
        var a = LockFile.Create("same", Rounds);
        var b = LockFile.Create("same", Rounds);

        Assert.NotEqual(a.DatabaseKey, b.DatabaseKey);
        Assert.NotEqual(a.RecoveryKey, b.RecoveryKey);
        Assert.NotEqual(JsonNode.Parse(a.File.ToJson())!["Password"]!["Salt"]!.GetValue<string>(), JsonNode.Parse(b.File.ToJson())!["Password"]!["Salt"]!.GetValue<string>());
    }

    [Fact]
    public void The_same_password_typed_as_composed_or_separate_accents_opens_the_lock()
    {
        var made = LockFile.Create("café", Rounds);

        Assert.Equal(made.DatabaseKey, made.File.OpenWithPassword("café"));
    }

    [Fact]
    public void A_real_lock_uses_600000_rounds()
    {
        var made = LockFile.Create("correct horse");

        Assert.Equal(600_000, made.File.PasswordRounds);
        Assert.Equal(600_000, JsonNode.Parse(made.File.ToJson())!["Recovery"]!["Rounds"]!.GetValue<int>());
        Assert.Equal(made.DatabaseKey, made.File.OpenWithPassword("correct horse"));
    }

    [Fact]
    public void The_recovery_key_opens_the_lock_however_it_is_typed()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var shown = RecoveryKey.Format(made.RecoveryKey);

        Assert.Equal(made.DatabaseKey, made.File.OpenWithRecoveryKey(made.RecoveryKey));
        Assert.Equal(made.DatabaseKey, made.File.OpenWithRecoveryKey(shown));
        Assert.Equal(made.DatabaseKey, made.File.OpenWithRecoveryKey("  " + shown.ToLowerInvariant().Replace('-', ' ') + " "));
    }

    [Fact]
    public void A_wrong_recovery_key_is_refused()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var other = RecoveryKey.New();

        Assert.Null(made.File.OpenWithRecoveryKey(other));
        Assert.Null(made.File.OpenWithRecoveryKey("not a key"));
        Assert.Null(made.File.OpenWithRecoveryKey(null));
        // The password is not the recovery key, and the recovery key is not the password.
        Assert.Null(made.File.OpenWithRecoveryKey("correct horse"));
        Assert.Null(made.File.OpenWithPassword(made.RecoveryKey));
    }

    [Fact]
    public void Changing_the_password_keeps_the_same_database_key()
    {
        var made = LockFile.Create("old password", Rounds);

        made.File.ChangePassword(made.DatabaseKey, "new password");

        Assert.Null(made.File.OpenWithPassword("old password"));
        Assert.Equal(made.DatabaseKey, made.File.OpenWithPassword("new password"));
        Assert.Equal(made.DatabaseKey, made.File.OpenWithRecoveryKey(made.RecoveryKey));
    }

    [Fact]
    public void The_password_cannot_be_changed_with_a_key_that_is_not_the_locks()
    {
        var made = LockFile.Create("old password", Rounds);
        var wrong = RandomNumberGenerator.GetBytes(LockFile.KeyBytes);

        Assert.Throws<ArgumentException>(() => made.File.ChangePassword(wrong, "new password"));
        Assert.Throws<ArgumentException>(() => made.File.NewRecoveryKey(wrong));
        Assert.Equal(made.DatabaseKey, made.File.OpenWithPassword("old password"));
    }

    [Fact]
    public void The_old_recovery_key_is_refused_after_a_new_one_is_made()
    {
        var made = LockFile.Create("correct horse", Rounds);

        var newer = made.File.NewRecoveryKey(made.DatabaseKey);

        Assert.NotEqual(made.RecoveryKey, newer);
        Assert.Null(made.File.OpenWithRecoveryKey(made.RecoveryKey));
        Assert.Equal(made.DatabaseKey, made.File.OpenWithRecoveryKey(newer));
        Assert.Equal(made.DatabaseKey, made.File.OpenWithPassword("correct horse"));
    }

    [Fact]
    public async Task Windows_hello_opens_the_lock_on_the_pc_where_it_was_turned_on()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var hello = new FakeHello("pc-one");

        Assert.False(made.File.HasHello("pc-one"));
        Assert.Null(await made.File.OpenWithHelloAsync(hello));
        Assert.True(await made.File.AddHelloAsync(hello, made.DatabaseKey));

        Assert.True(made.File.HasHello("pc-one"));
        Assert.Equal(made.DatabaseKey, await made.File.OpenWithHelloAsync(hello));
        // After saving and reading the file again, as on the next start.
        Assert.Equal(made.DatabaseKey, await LockFile.Parse(made.File.ToJson()).OpenWithHelloAsync(hello));
    }

    [Fact]
    public async Task Windows_hello_on_one_pc_does_not_open_the_lock_on_another()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var first = new FakeHello("pc-one");
        var second = new FakeHello("pc-two");
        await made.File.AddHelloAsync(first, made.DatabaseKey);

        Assert.Null(await made.File.OpenWithHelloAsync(second));
        // Another Windows account that claims the first PC's id signs with a different key: refused.
        Assert.Null(await made.File.OpenWithHelloAsync(new FakeHello("pc-one")));

        Assert.True(await made.File.AddHelloAsync(second, made.DatabaseKey));
        Assert.Equal(2, made.File.HelloCount);
        Assert.Equal(made.DatabaseKey, await made.File.OpenWithHelloAsync(first));
        Assert.Equal(made.DatabaseKey, await made.File.OpenWithHelloAsync(second));
    }

    [Fact]
    public async Task Windows_hello_cancelled_or_missing_gives_nothing_and_changes_nothing()
    {
        var made = LockFile.Create("correct horse", Rounds);

        Assert.False(await made.File.AddHelloAsync(new FakeHello("pc-one") { Cancel = true }, made.DatabaseKey));
        Assert.False(await made.File.AddHelloAsync(new FakeHello("pc-one", available: false), made.DatabaseKey));
        Assert.Equal(0, made.File.HelloCount);

        var hello = new FakeHello("pc-one");
        await made.File.AddHelloAsync(hello, made.DatabaseKey);
        hello.Cancel = true;
        Assert.Null(await made.File.OpenWithHelloAsync(hello));
        hello.Cancel = false;
        hello.LoseKey();
        Assert.Null(await made.File.OpenWithHelloAsync(hello));
        // The password still works whatever Hello does.
        Assert.Equal(made.DatabaseKey, made.File.OpenWithPassword("correct horse"));
    }

    [Fact]
    public async Task A_hello_key_whose_signatures_change_is_not_accepted()
    {
        var made = LockFile.Create("correct horse", Rounds);

        Assert.False(await made.File.AddHelloAsync(new FakeHello("pc-one") { Random = true }, made.DatabaseKey));
        Assert.False(made.File.HasHello("pc-one"));
    }

    [Fact]
    public async Task Hello_keeps_working_after_a_password_change_and_can_be_turned_off()
    {
        var made = LockFile.Create("old password", Rounds);
        var hello = new FakeHello("pc-one");
        await made.File.AddHelloAsync(hello, made.DatabaseKey);

        made.File.ChangePassword(made.DatabaseKey, "new password");
        Assert.Equal(made.DatabaseKey, await made.File.OpenWithHelloAsync(hello));

        Assert.True(made.File.RemoveHello("pc-one"));
        Assert.False(made.File.RemoveHello("pc-one"));
        Assert.Null(await made.File.OpenWithHelloAsync(hello));
    }

    [Fact]
    public async Task The_copy_for_a_backup_has_no_hello_entries_but_opens_with_password_and_recovery_key()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var hello = new FakeHello("pc-one");
        await made.File.AddHelloAsync(hello, made.DatabaseKey);
        made.File.Pending = LockPending.TurnOff;

        var copy = made.File.WithoutHello();

        Assert.Equal(0, copy.HelloCount);
        Assert.Equal(LockPending.None, copy.Pending);
        Assert.DoesNotContain("pc-one", copy.ToJson());
        Assert.Equal(made.DatabaseKey, copy.OpenWithPassword("correct horse"));
        Assert.Equal(made.DatabaseKey, copy.OpenWithRecoveryKey(made.RecoveryKey));
        // The original is not changed.
        Assert.Equal(1, made.File.HelloCount);
        Assert.Equal(LockPending.TurnOff, made.File.Pending);
    }

    [Fact]
    public async Task The_lock_file_survives_being_saved_and_read_back()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dogai-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, LockFile.FileName);
            Assert.Null(LockFile.Read(path));

            var made = LockFile.Create("correct horse", Rounds);
            var hello = new FakeHello("pc-one");
            await made.File.AddHelloAsync(hello, made.DatabaseKey);
            made.File.Skin = "fireside";
            made.File.Theme = "light";
            made.File.Pending = LockPending.TurnOff;
            made.File.Write(path);
            made.File.Write(path);

            var read = LockFile.Read(path);
            Assert.NotNull(read);
            Assert.Equal(made.DatabaseKey, read.OpenWithPassword("correct horse"));
            Assert.Equal(made.DatabaseKey, read.OpenWithRecoveryKey(made.RecoveryKey));
            Assert.Equal(made.DatabaseKey, await read.OpenWithHelloAsync(hello));
            Assert.Equal("fireside", read.Skin);
            Assert.Equal("light", read.Theme);
            Assert.Equal(LockPending.TurnOff, read.Pending);
            Assert.Equal(made.File.ToJson(), read.ToJson());
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_lock_file_holds_no_password_recovery_key_or_database_key()
    {
        var made = LockFile.Create("correct horse battery", Rounds);
        var json = made.File.ToJson();

        Assert.DoesNotContain("correct horse battery", json);
        Assert.DoesNotContain(made.RecoveryKey, json);
        Assert.DoesNotContain(RecoveryKey.Format(made.RecoveryKey), json);
        Assert.DoesNotContain(Convert.ToBase64String(made.DatabaseKey), json);
        Assert.DoesNotContain(Convert.ToHexString(made.DatabaseKey), json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("[1,2,3]")]
    public void A_damaged_lock_file_is_refused(string text)
    {
        var error = Assert.Throws<LockFileException>(() => LockFile.Parse(text));
        Assert.Contains("damaged", error.Message);
    }

    [Fact]
    public void A_lock_file_with_a_part_cut_short_or_missing_is_refused()
    {
        var json = LockFile.Create("correct horse", Rounds).File.ToJson();

        foreach (var change in new Action<JsonObject>[]
        {
            doc => doc.Remove("Password"),
            doc => doc.Remove("Recovery"),
            doc => doc.Remove("Check"),
            doc => doc["Password"]!["Wrapped"] = Convert.ToBase64String(new byte[8]),
            doc => doc["Password"]!["Tag"] = "",
            doc => doc["Recovery"]!["Nonce"] = Convert.ToBase64String(new byte[4]),
            doc => doc["Password"]!["Rounds"] = 0,
            doc => doc["Password"]!["Rounds"] = int.MaxValue,
            doc => doc["Password"]!["Salt"] = "***not base64***",
        })
        {
            var doc = JsonNode.Parse(json)!.AsObject();
            change(doc);
            Assert.Throws<LockFileException>(() => LockFile.Parse(doc.ToJsonString()));
        }
        Assert.Throws<LockFileException>(() => LockFile.Parse(json[..(json.Length / 2)]));
    }

    [Fact]
    public void A_lock_file_whose_wrapped_key_was_changed_does_not_open()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var doc = JsonNode.Parse(made.File.ToJson())!.AsObject();
        var wrapped = Convert.FromBase64String(doc["Password"]!["Wrapped"]!.GetValue<string>());
        wrapped[0] ^= 1;
        doc["Password"]!["Wrapped"] = Convert.ToBase64String(wrapped);

        var changed = LockFile.Parse(doc.ToJsonString());

        Assert.Null(changed.OpenWithPassword("correct horse"));
        Assert.Equal(made.DatabaseKey, changed.OpenWithRecoveryKey(made.RecoveryKey));
    }

    [Fact]
    public void The_password_entry_cannot_be_swapped_for_the_recovery_entry()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var doc = JsonNode.Parse(made.File.ToJson())!.AsObject();
        var recovery = doc["Recovery"]!.DeepClone();
        doc["Password"] = recovery;

        // The recovery key typed as a password would otherwise open the lock through the swapped entry.
        Assert.Null(LockFile.Parse(doc.ToJsonString()).OpenWithPassword(made.RecoveryKey));
    }

    [Fact]
    public async Task A_damaged_hello_entry_is_dropped_and_the_password_still_works()
    {
        var made = LockFile.Create("correct horse", Rounds);
        await made.File.AddHelloAsync(new FakeHello("pc-one"), made.DatabaseKey);
        var doc = JsonNode.Parse(made.File.ToJson())!.AsObject();
        doc["Hello"]![0]!["Wrapped"] = "";

        var read = LockFile.Parse(doc.ToJsonString());

        Assert.Equal(0, read.HelloCount);
        Assert.Equal(made.DatabaseKey, read.OpenWithPassword("correct horse"));
    }

    [Fact]
    public void A_lock_file_from_a_newer_version_says_so()
    {
        var doc = JsonNode.Parse(LockFile.Create("correct horse", Rounds).File.ToJson())!.AsObject();
        doc["Version"] = LockFile.CurrentVersion + 1;

        var error = Assert.Throws<LockFileException>(() => LockFile.Parse(doc.ToJsonString()));
        Assert.Contains("newer", error.Message);
    }

    // ---- The lock screen's logic ----

    private sealed class LockFolder : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "dogai-unlock-" + Guid.NewGuid().ToString("N"));
        public string LockPath => Path.Combine(Folder, LockFile.FileName);
        public string TriesPath => Path.Combine(Folder, "unlock-tries.json");
        public DateTime Now { get; set; } = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
        public NewLock Made { get; }

        public LockFolder()
        {
            Directory.CreateDirectory(Folder);
            Made = LockFile.Create("correct horse", Rounds);
            Made.File.Write(LockPath);
        }

        /// <summary>A lock screen as a fresh start of DOG.ai would show it.</summary>
        public Unlocker Screen(IHelloSigner? hello = null) => new(LockFile.Read(LockPath)!, LockPath, TriesPath, hello, () => Now);

        public void Dispose() => Directory.Delete(Folder, true);
    }

    [Fact]
    public void After_five_wrong_tries_even_the_right_password_must_wait_and_restarting_does_not_help()
    {
        using var folder = new LockFolder();
        var screen = folder.Screen();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(TimeSpan.Zero, screen.WaitLeft);
            Assert.Null(screen.TryPassword("wrong " + i));
        }
        Assert.Equal(5, screen.Failures);
        Assert.Equal(TimeSpan.FromSeconds(30), screen.WaitLeft);

        // During the wait nothing is looked at: the right password gets nothing and a wrong one is not counted.
        Assert.Null(screen.TryPassword("correct horse"));
        Assert.Null(screen.TryPassword("wrong again"));
        Assert.Equal(5, screen.Failures);

        // Closing DOG.ai and starting it again does not clear the wait.
        folder.Now = folder.Now.AddSeconds(10);
        var restarted = folder.Screen();
        Assert.Equal(5, restarted.Failures);
        Assert.Equal(TimeSpan.FromSeconds(20), restarted.WaitLeft);
        Assert.Null(restarted.TryPassword("correct horse"));

        // A sixth wrong try after the wait costs a minute.
        folder.Now = folder.Now.AddSeconds(20);
        Assert.Null(restarted.TryPassword("wrong six"));
        Assert.Equal(TimeSpan.FromMinutes(1), restarted.WaitLeft);

        // After the wait the right password opens the lock and the count is cleared.
        folder.Now = folder.Now.AddMinutes(1);
        Assert.Equal(folder.Made.DatabaseKey, restarted.TryPassword("correct horse"));
        Assert.Equal(0, restarted.Failures);
        Assert.False(File.Exists(folder.TriesPath));
        Assert.Equal(0, folder.Screen().Failures);
    }

    [Fact]
    public void A_wrong_recovery_key_counts_as_a_wrong_try_but_a_mistyped_shape_does_not()
    {
        using var folder = new LockFolder();
        var screen = folder.Screen();

        Assert.Null(screen.TryRecoveryKey("ABCD-EFGH"));
        Assert.Null(screen.TryRecoveryKey(""));
        Assert.Equal(0, screen.Failures);

        Assert.Null(screen.TryRecoveryKey(RecoveryKey.Format(RecoveryKey.New())));
        Assert.Equal(1, screen.Failures);

        Assert.Equal(folder.Made.DatabaseKey, screen.TryRecoveryKey(RecoveryKey.Format(folder.Made.RecoveryKey)));
        Assert.Equal(0, screen.Failures);
    }

    [Fact]
    public void A_forgotten_password_is_replaced_through_the_recovery_key_and_the_new_one_is_saved()
    {
        using var folder = new LockFolder();
        var screen = folder.Screen();

        var key = screen.TryRecoveryKey(folder.Made.RecoveryKey)!;
        screen.ChangePassword(key, "a new password");

        var next = folder.Screen();
        Assert.Null(next.TryPassword("correct horse"));
        Assert.Equal(folder.Made.DatabaseKey, next.TryPassword("a new password"));
        // The recovery key still works afterwards; a new one is only made when the owner asks.
        Assert.Equal(folder.Made.DatabaseKey, next.TryRecoveryKey(folder.Made.RecoveryKey));
    }

    [Fact]
    public async Task Windows_hello_is_offered_after_a_password_unlock_and_opens_the_lock_from_then_on()
    {
        using var folder = new LockFolder();
        var hello = new FakeHello("pc-one");
        var screen = folder.Screen(hello);

        Assert.False(screen.HelloIsOn);
        Assert.True(await screen.CanTurnOnHelloAsync());
        Assert.Null(await screen.TryHelloAsync());
        var key = screen.TryPassword("correct horse")!;
        Assert.True(await screen.TurnOnHelloAsync(key));

        var next = folder.Screen(hello);
        Assert.True(next.HelloIsOn);
        Assert.False(await next.CanTurnOnHelloAsync());
        Assert.Equal(key, await next.TryHelloAsync());

        // Another PC with the same data folder (a portable copy): the password once, then Hello can be turned on there too.
        var otherPc = folder.Screen(new FakeHello("pc-two"));
        Assert.False(otherPc.HelloIsOn);
        Assert.Null(await otherPc.TryHelloAsync());
        Assert.True(await otherPc.CanTurnOnHelloAsync());

        // No Windows Hello set up at all: never offered.
        Assert.False(await folder.Screen(new FakeHello("pc-three", available: false)).CanTurnOnHelloAsync());
        Assert.False(await folder.Screen().CanTurnOnHelloAsync());
    }

    [Fact]
    public async Task Cancelling_windows_hello_is_not_a_wrong_try_and_using_it_clears_the_count()
    {
        using var folder = new LockFolder();
        var hello = new FakeHello("pc-one");
        var first = folder.Screen(hello);
        await first.TurnOnHelloAsync(first.TryPassword("correct horse")!);

        var screen = folder.Screen(hello);
        Assert.Null(screen.TryPassword("wrong"));
        hello.Cancel = true;
        Assert.Null(await screen.TryHelloAsync());
        Assert.Equal(1, screen.Failures);

        hello.Cancel = false;
        Assert.Equal(folder.Made.DatabaseKey, await screen.TryHelloAsync());
        Assert.Equal(0, screen.Failures);
    }

    // ---- Recovery key ----

    [Fact]
    public void A_recovery_key_is_24_letters_and_digits_without_lookalikes()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => RecoveryKey.New()).ToList();

        Assert.All(keys, key =>
        {
            Assert.Equal(24, key.Length);
            Assert.All(key, c => Assert.Contains(c, RecoveryKey.Alphabet));
            Assert.DoesNotContain('I', key);
            Assert.DoesNotContain('O', key);
            Assert.DoesNotContain('0', key);
            Assert.DoesNotContain('1', key);
        });
        Assert.Equal(200, keys.Distinct().Count());
        Assert.Equal(32, RecoveryKey.Alphabet.Distinct().Count());
    }

    [Fact]
    public void A_recovery_key_is_shown_in_groups_of_four()
    {
        Assert.Equal("ABCD-EFGH-JKLM-NPQR-STUV-WXYZ", RecoveryKey.Format("ABCDEFGHJKLMNPQRSTUVWXYZ"));
    }

    [Theory]
    [InlineData("ABCD-EFGH-JKLM-NPQR-STUV-WXYZ")]
    [InlineData("abcd efgh jklm npqr stuv wxyz")]
    [InlineData("  ABCDEFGH JKLMNPQR\tSTUVWXYZ\r\n")]
    [InlineData("abcd–efgh—jklm_npqr.stuv,wxyz")]
    public void A_recovery_key_is_read_back_with_spaces_dashes_and_small_letters_forgiven(string typed)
    {
        Assert.Equal("ABCDEFGHJKLMNPQRSTUVWXYZ", RecoveryKey.Read(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFGH-JKLM-NPQR-STUV")]
    [InlineData("ABCD-EFGH-JKLM-NPQR-STUV-WXYZ-2345")]
    [InlineData("ABCD-EFGH-JKLM-NPQR-STUV-WXY0")]
    [InlineData("ABCD-EFGH-JKLM-NPQR-STUV-WXY!")]
    public void Something_that_is_not_a_recovery_key_is_not_read(string? typed)
    {
        Assert.Null(RecoveryKey.Read(typed));
    }

    // ---- Wait after wrong tries ----

    [Fact]
    public void The_first_five_wrong_tries_cost_no_wait_then_30_seconds_one_minute_and_five_minutes()
    {
        Assert.Equal(TimeSpan.Zero, UnlockThrottle.DelayAfter(0));
        Assert.Equal(TimeSpan.Zero, UnlockThrottle.DelayAfter(4));
        Assert.Equal(TimeSpan.FromSeconds(30), UnlockThrottle.DelayAfter(5));
        Assert.Equal(TimeSpan.FromMinutes(1), UnlockThrottle.DelayAfter(6));
        Assert.Equal(TimeSpan.FromMinutes(5), UnlockThrottle.DelayAfter(7));
        Assert.Equal(TimeSpan.FromMinutes(5), UnlockThrottle.DelayAfter(500));
    }

    [Fact]
    public void The_wait_runs_down_with_the_clock_and_a_right_answer_clears_it()
    {
        var start = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
        var throttle = new UnlockThrottle();

        for (var i = 0; i < 4; i++) throttle.Failed(start);
        Assert.Equal(TimeSpan.Zero, throttle.WaitLeft(start));

        throttle.Failed(start);
        Assert.Equal(TimeSpan.FromSeconds(30), throttle.WaitLeft(start));
        Assert.Equal(TimeSpan.FromSeconds(10), throttle.WaitLeft(start.AddSeconds(20)));
        Assert.Equal(TimeSpan.Zero, throttle.WaitLeft(start.AddSeconds(30)));

        var sixth = start.AddSeconds(31);
        throttle.Failed(sixth);
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.WaitLeft(sixth));

        var seventh = sixth.AddMinutes(1);
        throttle.Failed(seventh);
        Assert.Equal(TimeSpan.FromMinutes(5), throttle.WaitLeft(seventh));
        Assert.Equal(TimeSpan.Zero, throttle.WaitLeft(seventh.AddMinutes(5)));

        throttle.Succeeded();
        Assert.Equal(0, throttle.Failures);
        Assert.Equal(TimeSpan.Zero, throttle.WaitLeft(seventh));
    }

    [Fact]
    public void Setting_the_clock_back_does_not_shorten_the_wait()
    {
        var start = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
        var throttle = new UnlockThrottle(5, start);

        Assert.Equal(TimeSpan.FromSeconds(30), throttle.WaitLeft(start.AddHours(-3)));
    }

    [Fact]
    public void The_count_of_wrong_tries_survives_a_restart_and_a_broken_file_counts_as_none()
    {
        var file = Path.Combine(Path.GetTempPath(), "dogai-tries-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Equal(0, UnlockThrottle.Load(file).Failures);

            var start = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);
            var throttle = new UnlockThrottle();
            for (var i = 0; i < 6; i++) throttle.Failed(start);
            throttle.Save(file);

            var loaded = UnlockThrottle.Load(file);
            Assert.Equal(6, loaded.Failures);
            Assert.Equal(TimeSpan.FromSeconds(45), loaded.WaitLeft(start.AddSeconds(15)));

            loaded.Succeeded();
            loaded.Save(file);
            Assert.False(File.Exists(file));

            File.WriteAllText(file, "{broken");
            Assert.Equal(0, UnlockThrottle.Load(file).Failures);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
