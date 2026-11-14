using System.IO.Compression;
using System.Text;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
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
/// What the owner removes is really gone from the database files, also in a plain (unlocked) database and while DOG.ai is
/// still running: not in the database, not in the log file beside it, not in the search index. Each test searches the
/// bytes of every file in its throwaway data folder.
/// </summary>
public sealed class RemovedDataTests : IDisposable
{
    private const string Removed = "WOMBATGLOCKENSPIEL";
    private const string Stays = "PANGOLINHARMONICA";
    private static readonly byte[] RemovedImage = Encoding.ASCII.GetBytes("\u0001REMOVED-PICTURE-BYTES-CASSOWARY\u0002");
    private static readonly byte[] KeptImage = Encoding.ASCII.GetBytes("\u0001KEPT-PICTURE-BYTES-TAMARIN\u0002");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-removed-" + Guid.NewGuid().ToString("N"));

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

    private static ServiceProvider Open(AppPaths paths) => new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();

    /// <summary>Waits until the tidying that follows a delete has finished, as it would within a second or two in the app.</summary>
    private static Task Settled(IServiceProvider services) => services.GetRequiredService<DatabaseTidy>().SettledAsync();

    private static byte[] Bytes(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException)
        {
            // SQLite removes its log files when the last connection closes; a file that is gone holds nothing.
            return [];
        }
    }

    /// <summary>The size of the log file beside the database; nothing when SQLite has removed it.</summary>
    private static long LogFileSize(AppPaths paths) =>
        File.Exists(paths.DatabaseFile + "-wal") ? new FileInfo(paths.DatabaseFile + "-wal").Length : 0;

    private static bool Holds(string folder, byte[] bytes) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Any(file => Bytes(file).AsSpan().IndexOf(bytes) >= 0);

    /// <summary>True if the word can be read in any file under the folder, in capitals or small letters, as UTF-8 or UTF-16.</summary>
    private static bool HoldsWord(string folder, string word) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Any(file => HoldsWord(Bytes(file), word));

    private static bool HoldsWord(byte[] bytes, string word)
    {
        // Small letters throughout: the search index keeps words that way.
        var lower = (byte[])bytes.Clone();
        for (var i = 0; i < lower.Length; i++)
            if (lower[i] is >= (byte)'A' and <= (byte)'Z') lower[i] += 32;
        var small = word.ToLowerInvariant();
        return lower.AsSpan().IndexOf(Encoding.UTF8.GetBytes(small)) >= 0 || lower.AsSpan().IndexOf(Encoding.Unicode.GetBytes(small)) >= 0;
    }

    private static Picture PictureWith(byte[] marker, string word, string ownerKind = PictureOwners.Test, long ownerId = 0)
    {
        var image = KeptPictureTests.Webp(832, 1216, filler: 6000, seed: marker[5]);
        marker.CopyTo(image.AsSpan(200));
        return new Picture
        {
            OwnerKind = ownerKind, OwnerId = ownerId, Image = image, ImageWidth = 832, ImageHeight = 1216,
            Checkpoint = "nova.safetensors", Prompt = $"1girl, {word}, forest", NegativePrompt = "lowres",
            Width = 832, Height = 1216, Seed = 5, Steps = 20, Sampler = "euler_a", Guidance = 5.5,
        };
    }

    private static async Task<long> AddChat(IServiceProvider services, string word)
    {
        var chats = services.GetRequiredService<IChatStore>();
        var chat = await chats.CreateAsync(new Conversation { Title = $"About {word}", Mode = "general" });
        var q = await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.User, Content = $"what is a {word}" });
        var a = await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, ParentId = q.Id, Role = ChatRole.Assistant, Content = $"a {word} is an animal with a tune, and {word.ToLowerInvariant()} is how it is written small" });
        await chats.SetActiveLeafAsync(chat.Id, a.Id);
        return chat.Id;
    }

    [Fact]
    public async Task This_sqlite_build_overwrites_deleted_content_with_zeros()
    {
        // The SQLite build DOG.ai ships (SQLite3 Multiple Ciphers) is compiled with secure delete on. If a package update
        // ever changes that, this fails and the setting has to be switched on by hand for every connection.
        var paths = Install("a");
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        // The search index has its own switch, set by a migration: deleted messages' words are taken out of it at once.
        command.CommandText = "SELECT v FROM MessageSearch_config WHERE k = 'secure-delete'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_removed_picture_is_gone_from_every_database_file_while_dog_ai_is_still_running()
    {
        var paths = Install("a");
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var removed = await store.AddAsync(PictureWith(RemovedImage, Removed));
        var kept = await store.AddAsync(PictureWith(KeptImage, Stays));
        // Both are on disk now (in the database or the log file beside it): the search finds things.
        Assert.True(Holds(paths.Root, RemovedImage));
        Assert.True(HoldsWord(paths.Root, Removed));

        await store.DeleteAsync(removed.Id);
        await Settled(services);

        Assert.False(Holds(paths.Root, RemovedImage));
        Assert.False(HoldsWord(paths.Root, Removed));
        // The one that was not removed is untouched.
        Assert.True(Holds(paths.Root, KeptImage));
        Assert.Equal(PictureWith(KeptImage, Stays).Image, await store.ImageAsync(kept.Id));
    }

    [Fact]
    public async Task A_deleted_chat_is_gone_from_the_database_its_log_file_and_the_search_index()
    {
        var paths = Install("a");
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var gone = await AddChat(services, Removed);
        await AddChat(services, Stays);
        var search = services.GetRequiredService<ISearchStore>();
        Assert.NotEmpty(await search.SearchAsync(Removed));
        Assert.True(HoldsWord(paths.Root, Removed));

        await services.GetRequiredService<IChatStore>().DeleteAsync(gone);
        await Settled(services);

        Assert.Empty(await search.SearchAsync(Removed));
        Assert.False(HoldsWord(paths.Root, Removed));
        // The other chat is still there and still found.
        Assert.NotEmpty(await search.SearchAsync(Stays));
        Assert.True(HoldsWord(paths.Root, Stays));
    }

    [Fact]
    public async Task A_deleted_character_takes_its_text_its_chats_and_its_pictures_off_the_disk()
    {
        var paths = Install("a");
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var cards = services.GetRequiredService<ICharacterStore>();
        var chats = services.GetRequiredService<IChatStore>();
        var ann = await cards.SaveAsync(new Character { Name = "Ann", Description = $"A baker who keeps a {Removed}" });
        var bea = await cards.SaveAsync(new Character { Name = "Bea", Description = $"A smith who keeps a {Stays}" });
        var chat = await chats.CreateAsync(new Conversation { Title = "With Ann", Mode = "characters", CharacterId = ann.Id });
        await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.Assistant, Content = $"Ann shows you her {Removed}" });
        await services.GetRequiredService<IPictureStore>().AddAsync(PictureWith(RemovedImage, Removed, PictureOwners.Character, ann.Id));
        await services.GetRequiredService<IPictureStore>().AddAsync(PictureWith(KeptImage, Stays, PictureOwners.Character, bea.Id));
        Assert.True(Holds(paths.Root, RemovedImage));

        await cards.DeleteAsync(ann.Id);
        await Settled(services);

        Assert.False(HoldsWord(paths.Root, Removed));
        Assert.False(Holds(paths.Root, RemovedImage));
        Assert.True(HoldsWord(paths.Root, Stays));
        Assert.True(Holds(paths.Root, KeptImage));
    }

    [Fact]
    public async Task Messages_written_before_this_change_are_wiped_from_the_search_index_when_their_chat_is_deleted()
    {
        var paths = Install("old");
        await using (var old = Open(paths))
        {
            await using var db = await old.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261114094935_Pictures");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'Old chat', '', 'balanced', '', 4, '', 1, 0, 1, 0, '2026-11-13 10:00:00', '2026-11-13 10:00:00')");
            for (var i = 0; i < 40; i++)
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, {0}, '2026-11-13 10:00:00')", $"old message {i} about the {Removed} and other things");
        }
        SqliteConnection.ClearAllPools();
        Assert.True(HoldsWord(paths.Root, Removed));

        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        await AddChat(services, Stays);
        Assert.NotEmpty(await services.GetRequiredService<ISearchStore>().SearchAsync(Removed));

        await services.GetRequiredService<IChatStore>().DeleteAsync(1);
        await Settled(services);

        Assert.False(HoldsWord(paths.Root, Removed));
        Assert.NotEmpty(await services.GetRequiredService<ISearchStore>().SearchAsync(Stays));
    }

    [Fact]
    public async Task Words_of_a_chat_deleted_before_this_change_leave_the_search_index_when_dog_ai_is_updated()
    {
        var paths = Install("old");
        await using (var old = Open(paths))
        {
            await using var db = await old.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261114094935_Pictures");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'Old chat', '', 'balanced', '', 4, '', 1, 0, 1, 0, '2026-11-13 10:00:00', '2026-11-13 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, {0}, '2026-11-13 10:00:00')", $"an old message about the {Removed}");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, {0}, '2026-11-13 10:00:00')", $"one that stays, about the {Stays}");
            // Deleted the way the older DOG.ai did it: the row goes, its words stay behind in the search index.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Messages WHERE Id = 1");
        }
        SqliteConnection.ClearAllPools();
        Assert.True(HoldsWord(paths.Root, Removed));

        await using (var services = Open(paths))
        {
            await services.MigrateDogDatabaseAsync();
            await Settled(services);
            // Gone as soon as the update has run, while DOG.ai is open.
            Assert.False(HoldsWord(paths.Root, Removed));
            Assert.NotEmpty(await services.GetRequiredService<ISearchStore>().SearchAsync(Stays));
            Assert.Empty(await services.GetRequiredService<ISearchStore>().SearchAsync(Removed));
        }
    }

    [Fact]
    public async Task The_log_file_beside_the_database_is_emptied_after_a_delete_and_filled_again_by_later_work()
    {
        var paths = Install("a");
        await using var services = Open(paths);
        await services.MigrateDogDatabaseAsync();
        var chat = await AddChat(services, Removed);

        // Several deletes in a row are tidied once, shortly after the last.
        var store = services.GetRequiredService<IPictureStore>();
        var first = await store.AddAsync(PictureWith(RemovedImage, Removed));
        await store.DeleteAsync(first.Id);
        await services.GetRequiredService<IChatStore>().DeleteAsync(chat);
        await Settled(services);
        Assert.Equal(0, LogFileSize(paths));
        Assert.False(HoldsWord(paths.Root, Removed));

        // DOG.ai goes on working as before.
        await AddChat(services, Stays);
        Assert.NotEmpty(await services.GetRequiredService<ISearchStore>().SearchAsync(Stays));
    }

    [Fact]
    public async Task The_log_file_of_a_locked_database_is_emptied_after_a_delete_too()
    {
        var paths = Install("locked");
        var made = DogAI.Core.Privacy.LockFile.Create("pw", 1000);
        made.File.Write(paths.LockFile);
        await using var services = new ServiceCollection().AddSingleton(paths)
            .AddSingleton(new DogAI.Core.Privacy.DatabaseKey(made.DatabaseKey)).AddDogData().BuildServiceProvider();
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var removed = await store.AddAsync(PictureWith(RemovedImage, Removed));
        var kept = await store.AddAsync(PictureWith(KeptImage, Stays));

        await store.DeleteAsync(removed.Id);
        await Settled(services);

        // The tidying opened the database with its key: the log file is empty and the data is whole and still encrypted.
        Assert.Equal(0, LogFileSize(paths));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(paths.DatabaseFile));
        Assert.Null(await store.GetAsync(removed.Id));
        Assert.Equal(PictureWith(KeptImage, Stays).Image, await store.ImageAsync(kept.Id));
        Assert.False(Holds(paths.Root, KeptImage));
    }

    [Fact]
    public async Task A_delete_just_before_dog_ai_closes_leaves_nothing_behind_either()
    {
        var paths = Install("a");
        await using (var services = Open(paths))
        {
            await services.MigrateDogDatabaseAsync();
            var store = services.GetRequiredService<IPictureStore>();
            var removed = await store.AddAsync(PictureWith(RemovedImage, Removed));
            var chat = await AddChat(services, Removed);
            await store.DeleteAsync(removed.Id);
            await services.GetRequiredService<IChatStore>().DeleteAsync(chat);
            // Closed at once, without waiting for the tidying.
        }
        SqliteConnection.ClearAllPools();

        Assert.False(Holds(paths.Root, RemovedImage));
        Assert.False(HoldsWord(paths.Root, Removed));
    }

    [Fact]
    public async Task The_download_token_taken_out_of_a_backup_is_not_left_in_the_backups_bytes()
    {
        var paths = Install("a");
        await using (var services = Open(paths))
        {
            await services.MigrateDogDatabaseAsync();
            await services.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { HfTokenProtected = "token-" + Removed, Skin = "fireside" });
        }
        SqliteConnection.ClearAllPools();
        var zip = Path.Combine(_root, "backup.zip");

        await DatabaseBackup.CreateAsync(paths, zip, "1.0.0");

        using var archive = ZipFile.OpenRead(zip);
        using var stream = archive.GetEntry("dogai.db")!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.False(HoldsWord(memory.ToArray(), Removed));
        Assert.True(HoldsWord(memory.ToArray(), "fireside"));
    }
}
