using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DogAI.Core;
using DogAI.Core.Backup;
using DogAI.Core.Chat;
using DogAI.Core.Models;
using DogAI.Core.Privacy;
using DogAI.Core.Search;
using DogAI.Core.Settings;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// Locked data (M8): the encrypted database, turning the lock on and off, backups of locked data and the four restore cases.
/// Every test works in its own throwaway folder.
/// </summary>
public sealed class LockedDataTests : IDisposable
{
    /// <summary>A word that appears nowhere by chance. If it can be found in a file's bytes, that file is readable.</summary>
    private const string Marker = "XYLOPHONEQUAGGA";
    private const int Rounds = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-locked-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private AppPaths Install(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.EnsureCreated();
        return paths;
    }

    /// <summary>An install whose lock is on: a lock file with this password, and the key that goes with it.</summary>
    private (AppPaths Paths, byte[] Key, NewLock Lock) LockedInstall(string name, string password)
    {
        var paths = Install(name);
        var made = LockFile.Create(password, Rounds);
        made.File.Write(paths.LockFile);
        return (paths, made.DatabaseKey, made);
    }

    private static ServiceProvider Open(AppPaths paths, byte[]? key)
    {
        var services = new ServiceCollection().AddSingleton(paths);
        if (key is not null) services.AddSingleton(new DatabaseKey(key));
        return services.AddDogData().BuildServiceProvider();
    }

    /// <summary>Opens the install the way the app does (migrations included), does the work, and lets go of the file.</summary>
    private static async Task Use(AppPaths paths, byte[]? key, Func<IServiceProvider, Task> work)
    {
        await using (var services = Open(paths, key))
        {
            await services.MigrateDogDatabaseAsync();
            await work(services);
        }
        SqliteConnection.ClearAllPools();
    }

    private static async Task AddChat(IServiceProvider services, string title, string question, string answer)
    {
        var chats = services.GetRequiredService<IChatStore>();
        var chat = await chats.CreateAsync(new Conversation { Title = title, Mode = "general" });
        var q = await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.User, Content = question });
        var a = await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, ParentId = q.Id, Role = ChatRole.Assistant, Content = answer });
        await chats.SetActiveLeafAsync(chat.Id, a.Id);
    }

    private static async Task<string[]> ChatTitles(IServiceProvider services) =>
        [.. (await services.GetRequiredService<IChatStore>().ListAsync("general")).Select(c => c.Title).Order()];

    private static async Task<int> Hits(IServiceProvider services, string word) =>
        (await services.GetRequiredService<ISearchStore>().SearchAsync(word)).Count;

    /// <summary>True if the word can be read in the bytes of the file, or of any file under the folder (as UTF-8 or UTF-16 text).</summary>
    private static bool Holds(string path, string word)
    {
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [path];
        return files.Any(file =>
        {
            // SQLite removes its log files when the last connection closes; a file that is gone holds nothing.
            if (!File.Exists(file)) return false;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return Holds(bytes, word);
        });
    }

    private static bool Holds(byte[] bytes, string word) =>
        bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(word)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(word)) >= 0;

    private static string JournalMode(string file, byte[]? key)
    {
        try
        {
            using var db = new SqliteConnection(DatabaseLock.ConnectionString(file, key, readOnly: true));
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA journal_mode";
            return (string)command.ExecuteScalar()!;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string[] FilesIn(string folder) => [.. Directory.GetFiles(folder).Select(Path.GetFileName).Order()!];

    // ---- The encrypted database ----

    [Fact]
    public async Task A_plain_database_is_encrypted_and_decrypted_back_with_every_row_intact()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            await AddChat(s, "First chat", $"what is a {Marker}", "an animal with a tune");
            await AddChat(s, "Second chat", "and a heron", "a bird");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { Skin = "fireside", ModelsDirectory = @"Z:\models" });
            await s.GetRequiredService<IModelRecordStore>().SaveAsync(new ModelRecord { Path = @"Z:\models\q.gguf", Repo = "x/q", Quant = "Q4", UseCase = "general" });
        });
        var key = LockFile.Create("pw", Rounds).DatabaseKey;
        var file = paths.DatabaseFile;
        var before = DatabaseLock.RowCounts(file, null);
        Assert.Equal(4, before["Messages"]);
        Assert.Equal(DatabaseFileKind.Plain, DatabaseLock.KindOf(file));
        Assert.Equal("wal", JournalMode(file, null));

        DatabaseLock.Encrypt(file, key);

        // Only the database is left: no old plain file, no half-made copy, no log files from before.
        Assert.Equal([AppPaths.DatabaseName], FilesIn(paths.Root));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(file));
        Assert.Equal(before, DatabaseLock.RowCounts(file, key));
        Assert.Equal("wal", JournalMode(file, key));
        await Use(paths, key, async s =>
        {
            Assert.Equal(["First chat", "Second chat"], await ChatTitles(s));
            Assert.Equal(1, await Hits(s, Marker));
            Assert.Equal("fireside", (await s.GetRequiredService<ISettingsStore>().LoadAsync()).Skin);
            Assert.Single(await s.GetRequiredService<IModelRecordStore>().AllAsync());
        });

        DatabaseLock.Decrypt(file, key);

        Assert.Equal([AppPaths.DatabaseName], FilesIn(paths.Root));
        Assert.Equal(DatabaseFileKind.Plain, DatabaseLock.KindOf(file));
        Assert.Equal(before, DatabaseLock.RowCounts(file, null));
        Assert.Equal("wal", JournalMode(file, null));
        await Use(paths, null, async s =>
        {
            Assert.Equal(["First chat", "Second chat"], await ChatTitles(s));
            Assert.Equal(1, await Hits(s, Marker));
        });
    }

    [Fact]
    public async Task A_database_from_m7_is_encrypted_then_brought_up_to_date_and_decrypted_back()
    {
        var paths = Install("m7");
        await using (var services = Open(paths, null))
        {
            await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261112205103_Polish");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'M7 chat', '', 'balanced', '', 4, '', 1, 0, 1, 0, '2026-11-12 10:00:00', '2026-11-12 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, {0}, '2026-11-12 10:00:00')", $"an old message about {Marker}");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, ParentId, Role, Content, CreatedAt) VALUES (1, 1, 1, 'an old answer', '2026-11-12 10:00:01')");
        }
        SqliteConnection.ClearAllPools();
        var key = LockFile.Create("pw", Rounds).DatabaseKey;
        var before = DatabaseLock.RowCounts(paths.DatabaseFile, null);

        DatabaseLock.Encrypt(paths.DatabaseFile, key);

        Assert.Equal(before, DatabaseLock.RowCounts(paths.DatabaseFile, key));
        Assert.False(Holds(paths.Root, Marker));
        // The migrations added since M7 run on the encrypted database.
        await Use(paths, key, async s =>
        {
            Assert.Equal(["M7 chat"], await ChatTitles(s));
            Assert.Equal(1, await Hits(s, Marker));
            await AddChat(s, "Added while locked", "new question", "new answer");
        });

        DatabaseLock.Decrypt(paths.DatabaseFile, key);

        await Use(paths, null, async s =>
        {
            Assert.Equal(["Added while locked", "M7 chat"], await ChatTitles(s));
            Assert.Equal(1, await Hits(s, Marker));
            Assert.Equal(4, DatabaseLock.RowCounts(paths.DatabaseFile, null)["Messages"]);
        });
    }

    [Fact]
    public async Task The_encrypted_file_holds_no_marker_word_in_its_bytes()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            await AddChat(s, $"Title {Marker}", $"question {Marker}", $"answer {Marker}");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { ModelsDirectory = @"Z:\" + Marker });
        });
        // The search itself works: the plain file is readable.
        Assert.True(Holds(paths.DatabaseFile, Marker));
        var key = LockFile.Create("pw", Rounds).DatabaseKey;

        DatabaseLock.Encrypt(paths.DatabaseFile, key);

        Assert.False(Holds(paths.Root, Marker));

        // While the app is writing, the log file beside the database is encrypted too.
        await using (var services = Open(paths, key))
        {
            await AddChat(services, $"Later {Marker}", $"later question {Marker}", $"later answer {Marker}");
            Assert.True(File.Exists(paths.DatabaseFile + "-wal"));
            Assert.False(Holds(paths.Root, Marker));
            // Search finds all four messages (and the chats' titles) in the encrypted database.
            Assert.True(await Hits(services, Marker) >= 4);
        }
        SqliteConnection.ClearAllPools();
        Assert.False(Holds(paths.Root, Marker));
    }

    [Fact]
    public async Task Search_works_on_a_database_that_was_encrypted_from_its_first_day()
    {
        var (paths, key, _) = LockedInstall("a", "pw");

        await Use(paths, key, async s =>
        {
            await AddChat(s, "Garden", "how do I grow rhubarb", $"plant the {Marker} crowns in autumn");
            await AddChat(s, "Kitchen", "a rhubarb crumble", "bake it");
            Assert.Equal(2, await Hits(s, "rhubarb"));
            Assert.Equal(1, await Hits(s, Marker));

            // The triggers keep the index in step: a deleted chat is no longer found.
            var chats = s.GetRequiredService<IChatStore>();
            await chats.DeleteAsync((await chats.ListAsync("general")).Single(c => c.Title == "Garden").Id);
            Assert.Equal(1, await Hits(s, "rhubarb"));
            Assert.Equal(0, await Hits(s, Marker));
        });

        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(paths.DatabaseFile));
        Assert.Equal("wal", JournalMode(paths.DatabaseFile, key));
        Assert.False(Holds(paths.Root, "rhubarb"));
    }

    [Fact]
    public async Task An_encrypted_database_does_not_open_without_its_key()
    {
        var (paths, key, _) = LockedInstall("a", "pw");
        await Use(paths, key, s => AddChat(s, "Private", "q", "a"));
        var other = LockFile.Create("pw", Rounds).DatabaseKey;

        Assert.True(DatabaseLock.Opens(paths.DatabaseFile, key));
        Assert.False(DatabaseLock.Opens(paths.DatabaseFile, other));
        Assert.False(DatabaseLock.Opens(paths.DatabaseFile, null));
        await Assert.ThrowsAsync<SqliteException>(() => Use(paths, null, ChatTitles));
        await Assert.ThrowsAsync<SqliteException>(() => Use(paths, other, ChatTitles));
        // And still opens with the right one afterwards.
        await Use(paths, key, async s => Assert.Equal(["Private"], await ChatTitles(s)));
    }

    [Fact]
    public async Task A_database_that_is_in_use_is_left_exactly_as_it_was()
    {
        var paths = Install("a");
        await Use(paths, null, s => AddChat(s, "Keep", $"q {Marker}", "a"));
        var key = LockFile.Create("pw", Rounds).DatabaseKey;
        var before = DatabaseLock.RowCounts(paths.DatabaseFile, null);

        using (var holder = new SqliteConnection(DatabaseLock.ConnectionString(paths.DatabaseFile, null)))
        {
            holder.Open();
            Assert.Throws<DataLockException>(() => DatabaseLock.Encrypt(paths.DatabaseFile, key));
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(DatabaseFileKind.Plain, DatabaseLock.KindOf(paths.DatabaseFile));
        Assert.Equal(before, DatabaseLock.RowCounts(paths.DatabaseFile, null));
        Assert.DoesNotContain(FilesIn(paths.Root), f => f.Contains(".converting") || f.Contains(".old"));
        await Use(paths, null, async s => Assert.Equal(["Keep"], await ChatTitles(s)));
    }

    [Fact]
    public async Task A_conversion_cut_short_leaves_a_whole_database_at_the_next_start()
    {
        var paths = Install("a");
        await Use(paths, null, s => AddChat(s, "Keep", "q", "a"));
        var file = paths.DatabaseFile;

        // Stopped while the copy was being written: the half-made copy is thrown away.
        File.WriteAllText(file + ".converting", "half a copy");
        File.WriteAllText(file + ".converting-journal", "its journal");
        DatabaseLock.Recover(file);
        Assert.Equal([AppPaths.DatabaseName], FilesIn(paths.Root));

        // Stopped in the middle of the swap: the old database was moved aside and the new one never arrived.
        File.Move(file, file + ".old");
        DatabaseLock.Recover(file);
        Assert.Equal([AppPaths.DatabaseName], FilesIn(paths.Root));
        await Use(paths, null, async s => Assert.Equal(["Keep"], await ChatTitles(s)));

        // Stopped just after the swap: the new database is in place and the old one is removed.
        File.WriteAllText(file + ".old", "the old one");
        DatabaseLock.Recover(file);
        Assert.Equal([AppPaths.DatabaseName], FilesIn(paths.Root));
        await Use(paths, null, async s => Assert.Equal(["Keep"], await ChatTitles(s)));
    }

    [Fact]
    public async Task A_folder_with_awkward_characters_in_its_name_can_be_encrypted()
    {
        var paths = Install("odd name #1 50% (new) & more");
        await Use(paths, null, s => AddChat(s, "Keep", "q", "a"));
        var key = LockFile.Create("pw", Rounds).DatabaseKey;

        DatabaseLock.Encrypt(paths.DatabaseFile, key);
        await Use(paths, key, async s => Assert.Equal(["Keep"], await ChatTitles(s)));
        DatabaseLock.Decrypt(paths.DatabaseFile, key);
        await Use(paths, null, async s => Assert.Equal(["Keep"], await ChatTitles(s)));
        Assert.Equal([AppPaths.DatabaseName], FilesIn(paths.Root));
    }

    // ---- Turning the lock on and off at start-up ----

    [Fact]
    public async Task Turning_the_lock_on_encrypts_at_the_next_unlock_and_turning_it_off_decrypts()
    {
        var paths = Install("a");
        await Use(paths, null, s => AddChat(s, "Mine", $"q {Marker}", "a"));
        Assert.Equal(DataLockState.Off, DataLock.Look(paths, out var none));
        Assert.Null(none);

        // The owner chooses a password: only the lock file is written. The database is still plain until the password is entered again.
        var made = LockFile.Create("correct horse", Rounds);
        made.File.Write(paths.LockFile);
        Assert.Equal(DataLockState.TurningOn, DataLock.Look(paths, out var file));
        var key = file!.OpenWithPassword("correct horse")!;

        Assert.Equal(key, DataLock.Settle(paths, file, key));

        Assert.Equal(DataLockState.Locked, DataLock.Look(paths, out file));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(paths.DatabaseFile));
        Assert.False(Holds(paths.DatabaseFile, Marker));
        await Use(paths, key, async s => Assert.Equal(["Mine"], await ChatTitles(s)));
        // An ordinary start while locked changes nothing.
        Assert.Equal(key, DataLock.Settle(paths, file!, key));
        Assert.Equal(DataLockState.Locked, DataLock.Look(paths, out file));

        // The owner asks to turn the lock off: carried out at the next unlock.
        file!.Pending = LockPending.TurnOff;
        file.Write(paths.LockFile);
        Assert.Equal(DataLockState.TurningOff, DataLock.Look(paths, out file));

        Assert.Null(DataLock.Settle(paths, file!, file!.OpenWithPassword("correct horse")!));

        Assert.Equal(DataLockState.Off, DataLock.Look(paths, out file));
        Assert.Null(file);
        Assert.False(File.Exists(paths.LockFile));
        Assert.Equal(DatabaseFileKind.Plain, DatabaseLock.KindOf(paths.DatabaseFile));
        await Use(paths, null, async s => Assert.Equal(["Mine"], await ChatTitles(s)));
    }

    [Fact]
    public async Task Turning_the_lock_on_can_be_backed_out_of_while_the_database_is_still_plain()
    {
        var paths = Install("a");
        await Use(paths, null, s => AddChat(s, "Mine", "q", "a"));
        LockFile.Create("a password I will forget", Rounds).File.Write(paths.LockFile);

        Assert.True(DataLock.CancelTurningOn(paths));

        Assert.Equal(DataLockState.Off, DataLock.Look(paths, out _));
        await Use(paths, null, async s => Assert.Equal(["Mine"], await ChatTitles(s)));

        // Once the data is encrypted there is no backing out without the key.
        var (locked, key, _) = LockedInstall("b", "pw");
        await Use(locked, key, s => AddChat(s, "Private", "q", "a"));
        Assert.False(DataLock.CancelTurningOn(locked));
        Assert.True(File.Exists(locked.LockFile));
    }

    [Fact]
    public async Task A_lock_that_was_turned_off_but_whose_file_was_left_behind_is_tidied_away()
    {
        var paths = Install("a");
        await Use(paths, null, s => AddChat(s, "Mine", "q", "a"));
        var made = LockFile.Create("pw", Rounds);
        made.File.Pending = LockPending.TurnOff;
        made.File.Write(paths.LockFile);

        // The database is already plain, so nothing is protected by the leftover file and no password is needed to drop it.
        Assert.Equal(DataLockState.Off, DataLock.Look(paths, out var file));
        Assert.Null(file);
        Assert.False(File.Exists(paths.LockFile));
    }

    [Fact]
    public async Task An_encrypted_database_without_its_lock_file_or_with_another_ones_says_so()
    {
        var (paths, key, _) = LockedInstall("a", "pw");
        await Use(paths, key, s => AddChat(s, "Private", "q", "a"));

        // A lock file from another install: the password opens the file, but its key is not this database's.
        var other = LockFile.Create("pw", Rounds);
        other.File.Write(paths.LockFile);
        Assert.Equal(DataLockState.Locked, DataLock.Look(paths, out var file));
        var error = Assert.Throws<DataLockException>(() => DataLock.Settle(paths, file!, other.DatabaseKey));
        Assert.Contains("does not belong", error.Message);

        File.Delete(paths.LockFile);
        var missing = Assert.Throws<LockFileException>(() => DataLock.Look(paths, out _));
        Assert.Contains("missing", missing.Message);

        File.WriteAllText(paths.LockFile, "{ not a lock file");
        Assert.Throws<LockFileException>(() => DataLock.Look(paths, out _));
        // The database itself was never touched.
        Assert.True(DatabaseLock.Opens(paths.DatabaseFile, key));
    }

    [Fact]
    public async Task A_moved_portable_folder_has_its_saved_paths_updated_in_the_encrypted_database()
    {
        var (paths, key, _) = LockedInstall("a", "pw");
        await Use(paths, key, s => s.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { ModelsDirectory = @"E:\old place\models" }));

        Assert.Equal(1, PortableMove.Rebase(paths.DatabaseFile, @"E:\old place", @"F:\new place", key));

        await Use(paths, key, async s => Assert.Equal(@"F:\new place\models", (await s.GetRequiredService<ISettingsStore>().LoadAsync()).ModelsDirectory));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(paths.DatabaseFile));
    }

    // ---- Backups of locked data ----

    [Fact]
    public async Task A_backup_of_locked_data_is_encrypted_and_carries_the_lock_file_without_hello_entries()
    {
        var (paths, key, made) = LockedInstall("a", "correct horse");
        await made.File.AddHelloAsync(new PrivacyTests.FakeHello("this-pc-id"), key);
        made.File.Write(paths.LockFile);
        await Use(paths, key, async s =>
        {
            await AddChat(s, $"Title {Marker}", $"question {Marker}", $"answer {Marker}");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { HfTokenProtected = "token-" + Marker, Skin = "fireside" });
        });
        var zip = Path.Combine(_root, "locked.zip");

        var manifest = await DatabaseBackup.CreateAsync(paths, zip, "9.9.9", key: key);

        Assert.True(manifest.Locked);
        Assert.Equal(1, manifest.Chats);
        Assert.Equal(2, manifest.Messages);
        // The label and the names inside are the ones every earlier backup used.
        Assert.Equal("DOG AI", manifest.App);
        var parts = new Dictionary<string, byte[]>();
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                parts[entry.FullName] = memory.ToArray();
            }
        }
        Assert.Equal(["backup.json", "dogai.db", "lock.json"], parts.Keys.Order());

        // No plain copy: not in the zip's own bytes, and not in anything unpacked from it.
        Assert.False(Holds(zip, Marker));
        Assert.All(parts.Values, bytes => Assert.False(Holds(bytes, Marker)));
        Assert.NotEqual("SQLite format 3", Encoding.ASCII.GetString(parts["dogai.db"], 0, 15));

        // What the zip says about itself in the open gives nothing away.
        var inZip = JsonSerializer.Deserialize<BackupManifest>(parts["backup.json"])!;
        Assert.True(inZip.Locked);
        Assert.Equal(0, inZip.Chats);
        Assert.Equal(0, inZip.Messages);

        // The lock file in it opens with the password and the recovery key, and has no Windows Hello entry.
        var lockText = Encoding.UTF8.GetString(parts["lock.json"]);
        Assert.DoesNotContain("this-pc-id", lockText);
        var carried = LockFile.Parse(lockText);
        Assert.Equal(0, carried.HelloCount);
        Assert.Equal(key, carried.OpenWithPassword("correct horse"));
        Assert.Equal(key, carried.OpenWithRecoveryKey(made.RecoveryKey));
        // This PC's own lock file keeps its Hello entry.
        Assert.Equal(1, LockFile.Read(paths.LockFile)!.HelloCount);

        // The database in it opens with the key, and the download token was left out as always.
        var unpacked = Path.Combine(_root, "unpacked.db");
        File.WriteAllBytes(unpacked, parts["dogai.db"]);
        Assert.True(DatabaseLock.Opens(unpacked, key));
        Assert.False(DatabaseLock.Opens(unpacked, null));
        using (var db = new SqliteConnection(DatabaseLock.ConnectionString(unpacked, key, readOnly: true)))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Json FROM Settings WHERE Key = 'app'";
            var json = (string)command.ExecuteScalar()!;
            Assert.DoesNotContain("token-", json);
            Assert.Contains("fireside", json);
        }
        SqliteConnection.ClearAllPools();
        // Nothing was left beside the backup or in the data folder.
        Assert.Equal(["locked.zip", "unpacked.db"], FilesIn(_root));
    }

    [Fact]
    public async Task A_backup_of_plain_data_is_readable_which_shows_the_marker_search_can_find_things()
    {
        var paths = Install("a");
        await Use(paths, null, s => AddChat(s, "Plain", $"question {Marker}", "answer"));
        var zip = Path.Combine(_root, "plain.zip");

        var manifest = await DatabaseBackup.CreateAsync(paths, zip, "9.9.9");

        Assert.False(manifest.Locked);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["backup.json", "dogai.db"], archive.Entries.Select(e => e.FullName).Order());
        using var stream = archive.GetEntry("dogai.db")!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.True(Holds(memory.ToArray(), Marker));
    }

    [Fact]
    public async Task Locked_data_cannot_be_backed_up_without_the_lock_file_that_would_open_the_backup()
    {
        var (paths, key, _) = LockedInstall("a", "pw");
        await Use(paths, key, s => AddChat(s, "Private", "q", "a"));
        File.Delete(paths.LockFile);
        var zip = Path.Combine(_root, "never.zip");

        await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.CreateAsync(paths, zip, "1.0.0", key: key));

        Assert.False(File.Exists(zip));
    }

    // ---- The four restore cases ----

    private static readonly AppSettings ThisPc = new() { ModelsDirectory = @"Y:\this-pc\models", HfTokenProtected = "this-pcs-token", Skin = "playhouse" };

    private static async Task Fill(IServiceProvider services, string title)
    {
        await AddChat(services, title, $"question {Marker}", "answer");
        await services.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { ModelsDirectory = @"Z:\backup-pc\models", HfTokenProtected = "backup-pcs-token", Skin = "fireside" });
    }

    private static async Task AssertRestored(IServiceProvider services, string title)
    {
        Assert.Equal([title], await ChatTitles(services));
        Assert.Equal(1, await Hits(services, Marker));
        var settings = await services.GetRequiredService<ISettingsStore>().LoadAsync();
        Assert.Equal("fireside", settings.Skin);                      // what the backup held
        Assert.Equal(@"Y:\this-pc\models", settings.ModelsDirectory); // what belongs to this PC
        Assert.Equal("this-pcs-token", settings.HfTokenProtected);
    }

    [Fact]
    public async Task Restore_case_1_a_plain_backup_into_a_plain_dog_ai()
    {
        var source = Install("source");
        await Use(source, null, s => Fill(s, "From the backup"));
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(source, zip, "1.0.0");
        var here = Install("here");
        await Use(here, null, async s =>
        {
            await AddChat(s, "Here before", "q", "a");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(ThisPc);
        });

        var staged = await DatabaseBackup.StageAsync(here, zip);
        Assert.False(staged.Locked);
        Assert.NotNull(DatabaseBackup.ApplyPending(here));
        DatabaseBackup.FinishRestore(here, null);

        Assert.Equal(DataLockState.Off, DataLock.Look(here, out _));
        Assert.Equal("wal", JournalMode(here.DatabaseFile, null));
        await Use(here, null, s => AssertRestored(s, "From the backup"));
    }

    [Fact]
    public async Task Restore_case_2_a_plain_backup_into_a_locked_dog_ai_is_encrypted_while_it_waits()
    {
        var source = Install("source");
        await Use(source, null, s => Fill(s, "From the backup"));
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(source, zip, "1.0.0");
        var (here, key, made) = LockedInstall("here", "my password");
        await Use(here, key, async s =>
        {
            await AddChat(s, "Here before", "q", "a");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(ThisPc);
        });

        var staged = await DatabaseBackup.StageAsync(here, zip, key: key);

        // Nothing readable waits on disk for the restart.
        Assert.False(staged.Locked);
        Assert.True(DatabaseBackup.HasPending(here));
        Assert.False(Holds(here.Root, Marker));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(Path.Combine(here.Root, "restore", "dogai.db")));

        // The swap happens before the unlock and needs no key.
        Assert.NotNull(DatabaseBackup.ApplyPending(here));
        Assert.False(Holds(here.Root, Marker));
        Assert.Equal(DataLockState.Locked, DataLock.Look(here, out var file));
        var unlocked = file!.OpenWithPassword("my password")!;
        Assert.Equal(key, unlocked);
        Assert.Equal(key, DataLock.Settle(here, file, unlocked));
        DatabaseBackup.FinishRestore(here, unlocked);

        await Use(here, key, s => AssertRestored(s, "From the backup"));
        Assert.Equal("wal", JournalMode(here.DatabaseFile, key));
        Assert.Equal(key, LockFile.Read(here.LockFile)!.OpenWithRecoveryKey(made.RecoveryKey));
        // What was replaced is kept, still encrypted, with the lock file that opens it.
        var safety = DatabaseBackup.SafetyCopy(here)!;
        Assert.True(DatabaseLock.Opens(safety, key));
        Assert.False(DatabaseLock.Opens(safety, null));
        Assert.Equal(key, LockFile.Read(Path.Combine(Path.GetDirectoryName(safety)!, "lock.json"))!.OpenWithPassword("my password"));
        Assert.False(Holds(here.Root, Marker));
    }

    [Fact]
    public async Task Restore_case_3_a_locked_backup_into_a_plain_dog_ai_asks_for_its_password_and_brings_its_lock()
    {
        var (source, sourceKey, made) = LockedInstall("source", "backup password");
        await made.File.AddHelloAsync(new PrivacyTests.FakeHello("backup-pc"), sourceKey);
        made.File.Write(source.LockFile);
        await Use(source, sourceKey, s => Fill(s, "From the locked backup"));
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(source, zip, "1.0.0", key: sourceKey);
        var here = Install("here");
        await Use(here, null, async s =>
        {
            await AddChat(s, "Here before", "q", "a");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(ThisPc);
        });

        // Without its password the backup is not even looked into.
        var asked = await Assert.ThrowsAsync<BackupLockedException>(() => DatabaseBackup.StageAsync(here, zip));
        Assert.False(asked.Wrong);
        var refused = await Assert.ThrowsAsync<BackupLockedException>(() => DatabaseBackup.StageAsync(here, zip, backupPassword: "not the password"));
        Assert.True(refused.Wrong);
        Assert.False(DatabaseBackup.HasPending(here));
        Assert.Equal(["Here before"], await TitlesOf(here, null));

        var staged = await DatabaseBackup.StageAsync(here, zip, backupPassword: "backup password");
        Assert.True(staged.Locked);
        Assert.Equal(1, staged.Chats);
        Assert.Equal(2, staged.Messages);
        Assert.False(Holds(Path.Combine(here.Root, "restore"), Marker));

        Assert.NotNull(DatabaseBackup.ApplyPending(here));

        // DOG.ai is now locked with the backup's password; Windows Hello from the other PC did not come along.
        Assert.Equal(DataLockState.Locked, DataLock.Look(here, out var file));
        Assert.Equal(0, file!.HelloCount);
        Assert.Null(file.OpenWithPassword("not the password"));
        var unlocked = file.OpenWithPassword("backup password")!;
        Assert.Equal(sourceKey, unlocked);
        Assert.Equal(unlocked, DataLock.Settle(here, file, unlocked));
        DatabaseBackup.FinishRestore(here, unlocked);

        await Use(here, unlocked, s => AssertRestored(s, "From the locked backup"));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(here.DatabaseFile));
        // The plain database it replaced is kept as before.
        Assert.True(DatabaseLock.Opens(DatabaseBackup.SafetyCopy(here)!, null));
    }

    [Fact]
    public async Task A_locked_backup_also_opens_with_its_recovery_key()
    {
        var (source, sourceKey, made) = LockedInstall("source", "backup password");
        await Use(source, sourceKey, s => Fill(s, "From the locked backup"));
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(source, zip, "1.0.0", key: sourceKey);
        var here = Install("here");
        await Use(here, null, s => s.GetRequiredService<ISettingsStore>().SaveAsync(ThisPc));

        await DatabaseBackup.StageAsync(here, zip, backupPassword: RecoveryKey.Format(made.RecoveryKey).ToLowerInvariant());
        DatabaseBackup.ApplyPending(here);
        DataLock.Look(here, out var file);
        var unlocked = file!.OpenWithRecoveryKey(made.RecoveryKey)!;
        DataLock.Settle(here, file, unlocked);
        DatabaseBackup.FinishRestore(here, unlocked);

        await Use(here, unlocked, s => AssertRestored(s, "From the locked backup"));
    }

    [Fact]
    public async Task Restore_case_4_a_locked_backup_into_a_locked_dog_ai_ends_up_under_this_dog_ais_password()
    {
        var (source, sourceKey, _) = LockedInstall("source", "backup password");
        await Use(source, sourceKey, s => Fill(s, "From the locked backup"));
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(source, zip, "1.0.0", key: sourceKey);
        var (here, key, _) = LockedInstall("here", "my password");
        await here.Let(async paths =>
        {
            var file = LockFile.Read(paths.LockFile)!;
            await file.AddHelloAsync(new PrivacyTests.FakeHello("this-pc"), key);
            file.Write(paths.LockFile);
        });
        await Use(here, key, async s =>
        {
            await AddChat(s, "Here before", "q", "a");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(ThisPc);
        });

        await Assert.ThrowsAsync<BackupLockedException>(() => DatabaseBackup.StageAsync(here, zip, key: key));
        await Assert.ThrowsAsync<BackupLockedException>(() => DatabaseBackup.StageAsync(here, zip, key: key, backupPassword: "my password"));
        var staged = await DatabaseBackup.StageAsync(here, zip, key: key, backupPassword: "backup password");

        // What waits is already under this DOG.ai's key, not the backup's.
        Assert.True(staged.Locked);
        var waiting = Path.Combine(here.Root, "restore", "dogai.db");
        Assert.True(DatabaseLock.Opens(waiting, key));
        Assert.False(DatabaseLock.Opens(waiting, sourceKey));
        Assert.Equal(["dogai.db"], FilesIn(Path.Combine(here.Root, "restore")));

        Assert.NotNull(DatabaseBackup.ApplyPending(here));
        Assert.Equal(DataLockState.Locked, DataLock.Look(here, out var lockFile));
        // This DOG.ai's own password and Windows Hello still stand; the backup's password does not open it.
        Assert.Null(lockFile!.OpenWithPassword("backup password"));
        Assert.Equal(1, lockFile.HelloCount);
        var unlocked = lockFile.OpenWithPassword("my password")!;
        Assert.Equal(key, DataLock.Settle(here, lockFile, unlocked));
        DatabaseBackup.FinishRestore(here, unlocked);

        await Use(here, key, s => AssertRestored(s, "From the locked backup"));
        Assert.False(Holds(here.Root, Marker));
    }

    [Fact]
    public async Task A_locked_dog_ais_own_backup_restores_into_it()
    {
        var (here, key, _) = LockedInstall("here", "my password");
        await Use(here, key, async s =>
        {
            await AddChat(s, "Backed up", $"question {Marker}", "answer");
            await s.GetRequiredService<ISettingsStore>().SaveAsync(ThisPc with { Skin = "fireside" });
        });
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(here, zip, "1.0.0", key: key);
        await Use(here, key, async s =>
        {
            var chats = s.GetRequiredService<IChatStore>();
            await chats.DeleteAsync((await chats.ListAsync("general")).Single().Id);
            await AddChat(s, "Made later", "q", "a");
        });

        await DatabaseBackup.StageAsync(here, zip, key: key, backupPassword: "my password");
        Assert.NotNull(DatabaseBackup.ApplyPending(here));
        DataLock.Look(here, out var file);
        var unlocked = file!.OpenWithPassword("my password")!;
        DataLock.Settle(here, file, unlocked);
        DatabaseBackup.FinishRestore(here, unlocked);

        await Use(here, key, s => AssertRestored(s, "Backed up"));
    }

    [Fact]
    public async Task A_locked_backup_that_was_tampered_with_is_refused_and_nothing_changes()
    {
        var (source, sourceKey, _) = LockedInstall("source", "backup password");
        await Use(source, sourceKey, s => Fill(s, "From the locked backup"));
        var zip = Path.Combine(_root, "backup.zip");
        await DatabaseBackup.CreateAsync(source, zip, "1.0.0", key: sourceKey);
        var here = Install("here");
        await Use(here, null, s => AddChat(s, "Here before", "q", "a"));

        // The lock file taken out: the backup says it is locked but cannot be opened.
        var noLock = Path.Combine(_root, "no-lock.zip");
        File.Copy(zip, noLock);
        using (var archive = ZipFile.Open(noLock, ZipArchiveMode.Update)) archive.GetEntry("lock.json")!.Delete();
        var error = await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(here, noLock, backupPassword: "backup password"));
        Assert.Contains("missing", error.Message);

        // The database swapped for a readable one while the lock file stays: what it holds does not match what it claims.
        var plainSource = Install("plain-source");
        await Use(plainSource, null, s => AddChat(s, "Planted", "q", "a"));
        var swapped = Path.Combine(_root, "swapped.zip");
        File.Copy(zip, swapped);
        using (var archive = ZipFile.Open(swapped, ZipArchiveMode.Update))
        {
            archive.GetEntry("dogai.db")!.Delete();
            archive.CreateEntryFromFile(plainSource.DatabaseFile, "dogai.db");
        }
        await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(here, swapped, backupPassword: "backup password"));

        // A damaged lock file.
        var damaged = Path.Combine(_root, "damaged.zip");
        File.Copy(zip, damaged);
        using (var archive = ZipFile.Open(damaged, ZipArchiveMode.Update))
        {
            archive.GetEntry("lock.json")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("lock.json").Open());
            writer.Write("{ broken");
        }
        await Assert.ThrowsAsync<BackupException>(() => DatabaseBackup.StageAsync(here, damaged, backupPassword: "backup password"));

        Assert.False(DatabaseBackup.HasPending(here));
        Assert.False(File.Exists(here.LockFile));
        Assert.Equal(["Here before"], await TitlesOf(here, null));
    }

    private static async Task<string[]> TitlesOf(AppPaths paths, byte[]? key)
    {
        string[] titles = [];
        await Use(paths, key, async s => titles = await ChatTitles(s));
        return titles;
    }
}

internal static class LetExtensions
{
    /// <summary>Runs a few lines against a value in the middle of a test's setup.</summary>
    public static Task Let<T>(this T value, Func<T, Task> work) => work(value);
}
