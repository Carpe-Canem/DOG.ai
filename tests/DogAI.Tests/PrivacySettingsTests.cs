using DogAI.Core;
using DogAI.Core.Privacy;

namespace DogAI.Tests;

/// <summary>M8 part 2: what Settings → Privacy, the lock screen's new steps and the idle lock do, without any screen.</summary>
public sealed class PrivacySettingsTests : IDisposable
{
    private const int Rounds = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-privacy-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public PrivacySettingsTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static readonly LockLook Look = new("fireside", "light", true);

    /// <summary>Turns the lock on as Settings does, then gives a PrivacyLock for a run unlocked with the key.</summary>
    private async Task<(PrivacyLock Lock, NewLock Made)> LockedAsync(PrivacySettingsTests.Hello? hello = null)
    {
        var off = new PrivacyLock(_paths, DatabaseKey.None, hello, Rounds);
        var made = await off.PrepareAsync("correct horse");
        off.TurnOn(made, Look);
        return (new PrivacyLock(_paths, new DatabaseKey(made.DatabaseKey), hello, Rounds), made);
    }

    internal sealed class Hello(string pc) : IHelloSigner
    {
        public string PcId => pc;
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
        public Task<byte[]?> SignAsync(byte[] challenge, bool create) =>
            Task.FromResult<byte[]?>(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pc), challenge));
    }

    [Fact]
    public async Task Preparing_a_lock_writes_nothing_until_it_is_turned_on()
    {
        var off = new PrivacyLock(_paths, DatabaseKey.None, null, Rounds);
        Assert.Equal(LockStatus.Off, off.Status);
        var made = await off.PrepareAsync("correct horse");
        Assert.False(File.Exists(_paths.LockFile));
        Assert.Equal(LockStatus.Off, off.Status);

        off.TurnOn(made, Look);
        Assert.Equal(LockStatus.TurningOn, off.Status);
        var file = LockFile.Read(_paths.LockFile)!;
        Assert.NotNull(file.OpenWithPassword("correct horse"));
        Assert.NotNull(file.OpenWithRecoveryKey(made.RecoveryKey));
        Assert.Equal(("fireside", "light", true), (file.Skin, file.Theme, file.Tray));
        Assert.False(off.CanLock);
    }

    [Fact]
    public async Task Turning_on_can_be_taken_back_before_the_restart()
    {
        var off = new PrivacyLock(_paths, DatabaseKey.None, null, Rounds);
        off.TurnOn(await off.PrepareAsync("correct horse"), Look);
        Assert.True(off.CancelTurningOn());
        Assert.Equal(LockStatus.Off, off.Status);
        Assert.False(File.Exists(_paths.LockFile));
    }

    [Fact]
    public async Task A_locked_run_can_lock_and_change_its_password()
    {
        var (on, made) = await LockedAsync();
        Assert.Equal(LockStatus.On, on.Status);
        Assert.True(on.CanLock);
        Assert.False(on.CancelTurningOn());

        Assert.False(await on.ChangePasswordAsync("wrong password", "new password 1"));
        Assert.True(await on.ChangePasswordAsync("correct horse", "new password 1"));
        var file = LockFile.Read(_paths.LockFile)!;
        Assert.Null(file.OpenWithPassword("correct horse"));
        Assert.Equal(made.DatabaseKey, file.OpenWithPassword("new password 1"));
        Assert.Equal(made.DatabaseKey, file.OpenWithRecoveryKey(made.RecoveryKey));
        Assert.Equal("fireside", file.Skin);
    }

    [Fact]
    public async Task A_new_recovery_key_replaces_the_old_one_only_once_it_is_used()
    {
        var (on, made) = await LockedAsync();
        Assert.Null(await on.NewRecoveryKeyAsync("wrong password"));
        var fresh = await on.NewRecoveryKeyAsync("correct horse");
        Assert.NotNull(fresh);
        // Shown, but not confirmed yet: the old key still works and the new one does not.
        Assert.NotNull(LockFile.Read(_paths.LockFile)!.OpenWithRecoveryKey(made.RecoveryKey));
        Assert.Null(LockFile.Read(_paths.LockFile)!.OpenWithRecoveryKey(fresh));

        await on.UseRecoveryKeyAsync(fresh!);
        var file = LockFile.Read(_paths.LockFile)!;
        Assert.Null(file.OpenWithRecoveryKey(made.RecoveryKey));
        Assert.Equal(made.DatabaseKey, file.OpenWithRecoveryKey(fresh));
        Assert.Equal(made.DatabaseKey, file.OpenWithPassword("correct horse"));
    }

    [Fact]
    public async Task Changes_keep_what_the_lock_screen_added_since_the_run_started()
    {
        var hello = new Hello("pc-one");
        var (on, made) = await LockedAsync(hello);
        // The lock screen (another LockFile object) turns Hello on after this run's PrivacyLock was made.
        var screen = LockFile.Read(_paths.LockFile)!;
        Assert.True(await screen.AddHelloAsync(hello, made.DatabaseKey));
        screen.Write(_paths.LockFile);

        Assert.True(await on.ChangePasswordAsync("correct horse", "another password"));
        Assert.True(on.HelloOnHere);
        Assert.Equal(made.DatabaseKey, await LockFile.Read(_paths.LockFile)!.OpenWithHelloAsync(hello));
    }

    [Fact]
    public async Task Hello_is_turned_on_and_off_for_this_pc_only()
    {
        var one = new Hello("pc-one");
        var (on, made) = await LockedAsync(one);
        Assert.False(on.HelloOnHere);
        Assert.True(await on.TurnOnHelloAsync());
        Assert.True(on.HelloOnHere);
        var other = new PrivacyLock(_paths, new DatabaseKey(made.DatabaseKey), new Hello("pc-two"), Rounds);
        Assert.False(other.HelloOnHere);
        Assert.False(other.TurnOffHello());
        Assert.True(on.TurnOffHello());
        Assert.False(on.HelloOnHere);
    }

    [Fact]
    public async Task Turning_off_needs_the_password_and_can_be_taken_back()
    {
        var (on, _) = await LockedAsync();
        Assert.False(await on.TurnOffAsync("wrong password"));
        Assert.Equal(LockStatus.On, on.Status);
        Assert.True(await on.TurnOffAsync("correct horse"));
        Assert.Equal(LockStatus.TurningOff, on.Status);
        Assert.False(on.CanLock);
        Assert.Equal(LockPending.TurnOff, LockFile.Read(_paths.LockFile)!.Pending);
        Assert.True(on.KeepLocked());
        Assert.Equal(LockStatus.On, on.Status);
    }

    [Fact]
    public async Task The_lock_screen_can_take_back_turning_off_without_a_key()
    {
        var (on, made) = await LockedAsync();
        await on.TurnOffAsync("correct horse");
        var unlocker = new Unlocker(LockFile.Read(_paths.LockFile)!, _paths.LockFile, _paths.UnlockTriesFile);
        unlocker.KeepLocked();
        var file = LockFile.Read(_paths.LockFile)!;
        Assert.Equal(LockPending.None, file.Pending);
        Assert.Equal(made.DatabaseKey, file.OpenWithPassword("correct horse"));
    }

    [Fact]
    public async Task The_look_is_saved_only_when_it_changes_and_only_while_locked()
    {
        var off = new PrivacyLock(_paths, DatabaseKey.None, null, Rounds);
        Assert.False(off.SaveLook(Look));
        Assert.False(File.Exists(_paths.LockFile));

        var (on, _) = await LockedAsync();
        Assert.False(on.SaveLook(Look));
        Assert.True(on.SaveLook(new LockLook("playhouse", null, false)));
        var file = LockFile.Read(_paths.LockFile)!;
        Assert.Equal(("playhouse", (string?)null, false), (file.Skin, file.Theme, file.Tray));
    }

    [Fact]
    public async Task Hello_is_offered_once_per_pc_after_a_password_unlock()
    {
        var (_, made) = await LockedAsync();
        var hello = new Hello("pc-one");
        var unlocker = new Unlocker(LockFile.Read(_paths.LockFile)!, _paths.LockFile, _paths.UnlockTriesFile, hello);
        Assert.True(await unlocker.ShouldOfferHelloAsync());
        unlocker.DeclineHello();
        // Remembered in lock.json for the next start, on this PC only.
        var again = new Unlocker(LockFile.Read(_paths.LockFile)!, _paths.LockFile, _paths.UnlockTriesFile, hello);
        Assert.False(await again.ShouldOfferHelloAsync());
        var elsewhere = new Unlocker(LockFile.Read(_paths.LockFile)!, _paths.LockFile, _paths.UnlockTriesFile, new Hello("pc-two"));
        Assert.True(await elsewhere.ShouldOfferHelloAsync());
        // A backup does not carry this PC's answer.
        Assert.False(LockFile.Read(_paths.LockFile)!.WithoutHello().HelloDeclined("pc-one"));
        // Turning Hello on in Settings later forgets the "Not now".
        var on = new PrivacyLock(_paths, new DatabaseKey(made.DatabaseKey), hello, Rounds);
        Assert.True(await on.TurnOnHelloAsync());
        Assert.False(LockFile.Read(_paths.LockFile)!.HelloDeclined("pc-one"));
    }

    [Fact]
    public void A_lock_file_from_part_one_still_reads()
    {
        var made = LockFile.Create("correct horse", Rounds);
        var json = System.Text.Json.Nodes.JsonNode.Parse(made.File.ToJson())!.AsObject();
        json.Remove("HelloDeclined");
        json.Remove("Tray");
        var file = LockFile.Parse(json.ToJsonString());
        Assert.False(file.Tray);
        Assert.False(file.HelloDeclined("any"));
        Assert.NotNull(file.OpenWithPassword("correct horse"));
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("short", "short", false)]
    [InlineData("long enough", "long enougH", false)]
    [InlineData("        x", "        x", false)]
    [InlineData("long enough", "long enough", true)]
    public void New_passwords_are_checked(string password, string repeat, bool ok) =>
        Assert.Equal(ok, LockPassword.Problem(password, repeat) is null);

    [Fact]
    public void The_recovery_check_asks_for_two_different_groups_and_forgives_case_and_spaces()
    {
        for (var i = 0; i < 50; i++)
        {
            var check = RecoveryCheck.Pick();
            Assert.InRange(check.First, 1, 6);
            Assert.InRange(check.Second, 1, 6);
            Assert.True(check.First < check.Second);
        }
        const string key = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        var two = new RecoveryCheck(2, 5);
        Assert.True(two.Matches(key, "EFGH", "STUV"));
        Assert.True(two.Matches(key, " efgh", "st uv"));
        Assert.False(two.Matches(key, "EFGH", "STUW"));
        Assert.False(two.Matches(key, "ABCD", "STUV"));
        Assert.False(two.Matches(key, null, "STUV"));
    }

    [Fact]
    public void Idle_lock_waits_for_the_limit_and_for_busy_work_but_not_for_ever()
    {
        var limit = IdleLock.Limit(15);
        Assert.Equal(TimeSpan.FromMinutes(15), limit);
        Assert.Null(IdleLock.Limit(0));
        Assert.Null(IdleLock.Limit(0, "20"));
        Assert.Equal(TimeSpan.FromSeconds(20), IdleLock.Limit(15, "20"));
        Assert.Equal(TimeSpan.FromMinutes(5), IdleLock.Limit(5, "nonsense"));

        Assert.False(IdleLock.Due(null, TimeSpan.FromHours(5), busy: false));
        Assert.False(IdleLock.Due(limit, TimeSpan.FromMinutes(14), busy: false));
        Assert.True(IdleLock.Due(limit, TimeSpan.FromMinutes(15), busy: false));
        Assert.False(IdleLock.Due(limit, TimeSpan.FromMinutes(20), busy: true));
        Assert.True(IdleLock.Due(limit, TimeSpan.FromMinutes(30), busy: true));
        Assert.Contains(IdleLock.DefaultMinutes, IdleLock.Choices);
        Assert.Equal(15, new DogAI.Core.Settings.AppSettings().IdleLockMinutes);
    }

    [Fact]
    public void New_settings_take_part_in_equality()
    {
        var a = new DogAI.Core.Settings.AppSettings();
        Assert.NotEqual(a, a with { IdleLockMinutes = 5 });
        Assert.NotEqual(a, a with { PlainExportNoted = true });
        Assert.Equal(a with { IdleLockMinutes = 5 }, a with { IdleLockMinutes = 5 });
    }
}

public sealed class EngineEnvironmentTests
{
    [Fact]
    public void Inherited_llama_settings_that_could_write_text_to_disk_are_dropped()
    {
        var start = new System.Diagnostics.ProcessStartInfo("llama-server");
        start.Environment["LLAMA_ARG_SLOT_SAVE_PATH"] = @"C:\slots";
        start.Environment["llama_arg_log_file"] = @"C:\log.txt";
        start.Environment["LLAMA_LOG_VERBOSITY"] = "10";
        start.Environment["PATH_KEEP_ME"] = "x";
        var removed = DogAI.Engine.EngineEnvironment.Clean(start);
        Assert.Equal(3, removed.Count);
        Assert.False(start.Environment.ContainsKey("LLAMA_ARG_SLOT_SAVE_PATH"));
        Assert.False(start.Environment.ContainsKey("LLAMA_LOG_VERBOSITY"));
        Assert.Equal("x", start.Environment["PATH_KEEP_ME"]);
    }
}
