using System.IO.Compression;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Privacy;
using DogAI.Core.Search;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// Pictures on chat messages (M17 step 2): what may be kept (a plain JPEG and nothing else), the table and its store,
/// and that the lock, backups, restore, a moved portable folder and "removed is gone" cover them with nothing made
/// specially. Every test works in its own throwaway folder.
/// </summary>
public sealed class AttachedPictureTests : IDisposable
{
    private const int Rounds = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-attached-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Helpers ----

    private AppPaths Install(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.EnsureCreated();
        return paths;
    }

    private (AppPaths Paths, byte[] Key) LockedInstall(string name, string password)
    {
        var paths = Install(name);
        var made = LockFile.Create(password, Rounds);
        made.File.Write(paths.LockFile);
        return (paths, made.DatabaseKey);
    }

    private static ServiceProvider Open(AppPaths paths, byte[]? key)
    {
        var services = new ServiceCollection().AddSingleton(paths);
        if (key is not null) services.AddSingleton(new DatabaseKey(key));
        return services.AddDogData().BuildServiceProvider();
    }

    private static async Task Use(AppPaths paths, byte[]? key, Func<IServiceProvider, Task> work)
    {
        await using (var services = Open(paths, key))
        {
            await services.MigrateDogDatabaseAsync();
            await work(services);
            await services.GetRequiredService<DatabaseTidy>().SettledAsync();
        }
        SqliteConnection.ClearAllPools();
    }

    private static IAttachedPictureStore Store(IServiceProvider services) => services.GetRequiredService<IAttachedPictureStore>();
    private static IChatStore Chats(IServiceProvider services) => services.GetRequiredService<IChatStore>();

    /// <summary>A chat with one question, and the question's id.</summary>
    private static async Task<(long Chat, long Message)> QuestionAsync(IServiceProvider services, string text = "What is this?")
    {
        var chat = await Chats(services).CreateAsync(new Conversation { Title = "Pictures" });
        var message = await Chats(services).AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.User, Content = text });
        return (chat.Id, message.Id);
    }

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
            return []; // SQLite removes its log files when the last connection closes
        }
    }

    /// <summary>True when a file, or any file under a folder, holds these bytes.</summary>
    private static bool Holds(string path, byte[] bytes) =>
        (Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [path]).Any(f => Bytes(f).AsSpan().IndexOf(bytes) >= 0);

    private static bool HoldsPicture(string path) => Holds(path, TestJpegs.Marker);

    // ---- What a JPEG's own structure says ----

    [Fact]
    public void A_jpegs_structure_gives_its_size_and_what_it_carries_besides_the_picture()
    {
        Assert.Equal(new JpegInfo(1024, 640, 3, false, false, false), JpegInfo.Read(TestJpegs.Make(1024, 640)));
        Assert.Equal(new JpegInfo(640, 960, 1, true, false, false), JpegInfo.Read(TestJpegs.Make(640, 960, frame: 0xC2, components: 1)));
        Assert.True(JpegInfo.Read(TestJpegs.Make(64, 64, extra: TestJpegs.Profile()))!.Value.HasProfile);
        Assert.False(JpegInfo.Read(TestJpegs.Make(64, 64, extra: TestJpegs.Profile()))!.Value.HasExtras);
        Assert.True(JpegInfo.Read(TestJpegs.Make(64, 64, extra: TestJpegs.Exif()))!.Value.HasExtras);
        Assert.True(JpegInfo.Read(TestJpegs.Make(64, 64, extra: TestJpegs.Comment("a prompt could be here")))!.Value.HasExtras);
        // A second JFIF header with a thumbnail, and JFIF's extension block, are a second picture inside the file.
        Assert.True(JpegInfo.Read(TestJpegs.Make(64, 64, extra: TestJpegs.Jfif(8, 8)))!.Value.HasExtras);
        Assert.True(JpegInfo.Read(TestJpegs.Make(64, 64, extra: TestJpegs.Segment(0xE0, "JFXX\0"u8.ToArray())))!.Value.HasExtras);
        // What says how the colours are stored is part of the picture.
        Assert.False(JpegInfo.Read(TestJpegs.Make(64, 64, extra: TestJpegs.Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1])))!.Value.HasExtras);

        // The real files Windows' encoder writes are plain pictures.
        Assert.Equal(new JpegInfo(256, 160, 3, false, false, false), JpegInfo.Read(TestJpegs.Real(TestJpegs.Red)));
        Assert.Equal(new JpegInfo(1024, 640, 3, false, false, false), JpegInfo.Read(TestJpegs.Real(TestJpegs.Sign)));
        Assert.Equal(new JpegInfo(1024, 1024, 3, false, false, false), JpegInfo.Read(TestJpegs.Real(TestJpegs.Yellow)));
    }

    [Fact]
    public void Bytes_that_are_not_a_whole_plain_jpeg_are_refused()
    {
        var jpeg = TestJpegs.Make(256, 160);
        Assert.NotNull(JpegInfo.Read(jpeg));
        Assert.Null(JpegInfo.Read([]));
        Assert.Null(JpegInfo.Read("not a picture at all, only words"u8));
        Assert.Null(JpegInfo.Read(KeptPictureTests.Webp(64, 64)));
        Assert.Null(JpegInfo.Read([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]));
        Assert.Null(JpegInfo.Read("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        // Cut short anywhere: in the header, in the picture data, just before its end.
        foreach (var length in new[] { 3, 20, 120, jpeg.Length / 2, jpeg.Length - 2, jpeg.Length - 1 })
            Assert.Null(JpegInfo.Read(jpeg.AsSpan(0, length)));
        // Anything after the picture's end is not part of a picture: a second file could ride there.
        Assert.Null(JpegInfo.Read(TestJpegs.Make(256, 160, after: [0])));
        Assert.Null(JpegInfo.Read(TestJpegs.Make(256, 160, after: jpeg)));
        // Kinds the chat engine does not read: 12 bits a sample, four colours, arithmetic coding, lossless.
        Assert.Null(JpegInfo.Read(TestJpegs.Make(256, 160, bits: 12)));
        Assert.Null(JpegInfo.Read(TestJpegs.Make(256, 160, components: 4)));
        Assert.Null(JpegInfo.Read(TestJpegs.Make(256, 160, frame: 0xC9)));
        Assert.Null(JpegInfo.Read(TestJpegs.Make(256, 160, frame: 0xC3)));
        Assert.Null(JpegInfo.Read(TestJpegs.Make(0, 160)));
        // A file with no picture in it, and one that starts a second picture.
        Assert.Null(JpegInfo.Read([0xFF, 0xD8, .. TestJpegs.Jfif(), 0xFF, 0xD9]));
        Assert.Null(JpegInfo.Read(TestJpegs.Make(256, 160, extra: [0xFF, 0xD8])));
    }

    [Fact]
    public void Only_a_colour_profile_is_taken_out_and_anything_else_is_refused()
    {
        var plain = TestJpegs.Make(800, 600);
        Assert.Null(AttachedPictures.Problem(plain));
        Assert.Same(plain, AttachedPictures.Plain(plain));
        Assert.Equal((800, 600), AttachedPictures.SizeOf(plain));

        // The browser's encoder adds a colour profile: without it the file is, byte for byte, the plain one.
        var profiled = TestJpegs.Make(800, 600, extra: TestJpegs.Profile());
        Assert.NotNull(AttachedPictures.Problem(profiled));
        Assert.Equal(plain, AttachedPictures.Plain(profiled));

        // A description of any kind is never taken out quietly: the file is refused, so nothing of it is kept or sent.
        foreach (var described in new[] { TestJpegs.Exif(), TestJpegs.Comment("where and when"), TestJpegs.Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0<x/>"u8.ToArray()) })
        {
            var file = TestJpegs.Make(800, 600, extra: [TestJpegs.Profile(), described]);
            Assert.Equal("The picture carries extra information besides the picture.", AttachedPictures.Problem(AttachedPictures.Plain(file)));
        }

        Assert.Equal("The picture is not a JPEG image.", AttachedPictures.Problem(null));
        Assert.Equal("The picture is not a JPEG image.", AttachedPictures.Problem(KeptPictureTests.Webp(64, 64)));
        Assert.Equal("A picture is at most 1,024 pixels on its longest side.", AttachedPictures.Problem(TestJpegs.Make(1025, 600)));
        Assert.Equal("A picture is at most 1,024 pixels on its longest side.", AttachedPictures.Problem(TestJpegs.Make(600, 1536)));
        Assert.Null(AttachedPictures.Problem(TestJpegs.Make(1024, 1024)));
        Assert.Equal("The picture is too large.", AttachedPictures.Problem(TestJpegs.Make(1024, 1024, filler: AttachedPictures.MaxBytes)));
    }

    [Theory]
    // Every size the trial saw with the engine (2026-11-16), and the two ends.
    [InlineData(256, 160, 42)]
    [InlineData(512, 320, 162)]
    [InlineData(640, 960, 602)]
    [InlineData(768, 768, 578)]
    [InlineData(832, 1216, 990)]
    [InlineData(1024, 640, 642)]
    [InlineData(1024, 1024, 1026)]
    [InlineData(1008, 1024, 1026)]  // 31.5 rounds to the even number, 32
    [InlineData(944, 1024, 962)]    // 29.5 rounds to 30
    [InlineData(8, 8, 11)]
    [InlineData(64, 64, 11)]
    [InlineData(2048, 2048, 1026)]
    [InlineData(0, 100, 0)]
    public void A_pictures_cost_by_the_rule_is_what_the_engine_counted_in_the_trial(int width, int height, int tokens) =>
        Assert.Equal(tokens, AttachedPictures.TokensByRule(width, height));

    [Fact]
    public void A_picture_is_reduced_to_1024_pixels_on_its_long_side_and_never_enlarged()
    {
        Assert.Equal((1024, 576), AttachedPictures.SizeFor(1920, 1080));
        Assert.Equal((683, 1024), AttachedPictures.SizeFor(4000, 6000));
        Assert.Equal((640, 960), AttachedPictures.SizeFor(640, 960));
        Assert.Equal((1024, 1), AttachedPictures.SizeFor(5000, 2));
        Assert.Equal((0, 0), AttachedPictures.SizeFor(0, 10));
    }

    [Fact]
    public void A_text_only_job_gets_a_note_where_a_picture_was()
    {
        var two = new ChatMessage { Content = "Which is better?", Pictures = [new AttachedPicture(), new AttachedPicture()] };
        Assert.Equal("[picture] [picture]\nWhich is better?", AttachedPictures.TextOf(two));
        Assert.Equal("[picture]", AttachedPictures.TextOf(new ChatMessage { Content = " ", Pictures = [new AttachedPicture()] }));
        Assert.Equal("Just words", AttachedPictures.TextOf(new ChatMessage { Content = "Just words" }));
        Assert.StartsWith(AttachedPictures.UnseenNote + " " + AttachedPictures.UnseenNote + "\nWhich", AttachedPictures.UnseenTextOf(two));

        var export = ChatExport.ToMarkdown(new Conversation { Title = "T" }, [two, new ChatMessage { Role = ChatRole.Assistant, Content = "The left one." }]);
        Assert.Contains("**You:**\n\n[picture] [picture]\nWhich is better?", export);
    }

    // ---- The store ----

    [Fact]
    public async Task Pictures_are_kept_on_their_message_in_order_and_come_back_byte_for_byte()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var (chat, message) = await QuestionAsync(s);
            var first = TestJpegs.Make(1024, 640, seed: 1);
            var second = TestJpegs.Make(640, 960, seed: 2);

            var added = await Store(s).AddAsync(message, [first, second]);

            Assert.All(added, p => Assert.Null(p.Image));
            var listed = Assert.Single(await Chats(s).GetMessagesAsync(chat)).Pictures;
            // The list has who, where and how large, never the image.
            Assert.Equal([(0, 1024, 640, first.Length), (1, 640, 960, second.Length)], listed.Select(p => (p.Position, p.Width, p.Height, p.Bytes)));
            Assert.All(listed, p => Assert.Null(p.Image));
            Assert.All(listed, p => Assert.Equal((message, chat), (p.MessageId, p.ConversationId)));

            var images = await Store(s).ImagesAsync([.. listed.Select(p => p.Id), 999]);
            Assert.Equal(2, images.Count);
            Assert.Equal(first, images[listed[0].Id]);
            Assert.Equal(second, images[listed[1].Id]);
            Assert.Equal(second, (await Store(s).GetAsync(listed[1].Id))!.Image);
            Assert.Null(await Store(s).GetAsync(999));

            // A third goes after them.
            await Store(s).AddAsync(message, [TestJpegs.Make(64, 64, seed: 3)]);
            Assert.Equal([0, 1, 2], (await Chats(s).GetMessagesAsync(chat))[0].Pictures.Select(p => p.Position));
            // Other messages have none, and their list is empty, not null.
            var reply = await Chats(s).AddMessageAsync(new ChatMessage { ConversationId = chat, ParentId = message, Role = ChatRole.Assistant, Content = "A dog." });
            Assert.Empty((await Chats(s).GetMessagesAsync(chat)).Single(m => m.Id == reply.Id).Pictures);
        });
    }

    [Fact]
    public async Task Nothing_but_a_plain_jpeg_is_kept_and_at_most_four_on_a_message()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var (chat, message) = await QuestionAsync(s);
            var good = TestJpegs.Make(64, 64);

            // One bad picture among good ones: nothing is kept.
            foreach (var bad in new[] { KeptPictureTests.Webp(64, 64), TestJpegs.Make(2048, 64), TestJpegs.Make(64, 64, extra: TestJpegs.Exif()), TestJpegs.Make(64, 64, extra: TestJpegs.Profile()), "words"u8.ToArray() })
                await Assert.ThrowsAsync<InvalidOperationException>(() => Store(s).AddAsync(message, [good, bad]));
            Assert.Empty((await Chats(s).GetMessagesAsync(chat))[0].Pictures);

            await Store(s).AddAsync(message, [good, good, good]);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Store(s).AddAsync(message, [good, good]));
            Assert.Equal("A message takes at most 4 pictures.", error.Message);
            await Store(s).AddAsync(message, [good]);
            Assert.Equal(4, (await Chats(s).GetMessagesAsync(chat))[0].Pictures.Count);

            await Assert.ThrowsAsync<InvalidOperationException>(() => Store(s).AddAsync(999, [good]));
        });
    }

    [Fact]
    public async Task A_copy_for_an_edited_question_has_the_very_same_bytes()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var (chat, message) = await QuestionAsync(s);
            var jpegs = Enumerable.Range(1, 3).Select(i => TestJpegs.Make(64 * i, 64, seed: i)).ToList();
            var originals = await Store(s).AddAsync(message, jpegs);
            var edited = await Chats(s).AddMessageAsync(new ChatMessage { ConversationId = chat, Role = ChatRole.User, Content = "What is this, exactly?" });
            var shorter = await Chats(s).AddMessageAsync(new ChatMessage { ConversationId = chat, Role = ChatRole.User, Content = "And this?" });

            var all = await Store(s).CopyAsync(message, edited.Id);
            var some = await Store(s).CopyAsync(message, shorter.Id, [originals[2].Id, originals[0].Id]);

            var images = await Store(s).ImagesAsync([.. all.Select(p => p.Id), .. some.Select(p => p.Id)]);
            Assert.Equal(jpegs, all.Select(p => images[p.Id]));
            Assert.Equal([jpegs[0], jpegs[2]], some.Select(p => images[p.Id]));
            Assert.Equal([0, 1], some.Select(p => p.Position));
            // The original keeps its own.
            Assert.Equal(3, (await Chats(s).GetMessagesAsync(chat)).Single(m => m.Id == message).Pictures.Count);
        });
    }

    [Fact]
    public async Task The_image_is_the_last_column_of_the_table()
    {
        var paths = Install("a");
        await Use(paths, null, _ => Task.CompletedTask);
        using var db = new SqliteConnection(DatabaseLock.ConnectionString(paths.DatabaseFile, null));
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('AttachedPictures') ORDER BY cid";
        var columns = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(0));
        // SQLite walks a large value's pages to reach any column behind it: nothing may come after the image.
        Assert.Equal("Image", columns[^1]);
        Assert.Equal(["Id", "MessageId", "ConversationId", "Position", "Width", "Height", "Bytes", "CreatedAt", "Image"], columns);
    }

    [Fact]
    public async Task A_database_from_before_m17_is_upgraded_with_everything_intact_and_can_keep_pictures()
    {
        var paths = Install("a");
        await using (var services = Open(paths, null))
        {
            var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261115133432_PicturePreviews");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, RecallEnabled, TurnOrder, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'Old chat', '', 'balanced', '', 4, '', 1, 1, 0, 0, '2026-11-15 10:00:00', '2026-11-15 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, 'An old question', '2026-11-15 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO ModelRecords (Path, AddedAt, MeasuredVramBytes, MeasuredVramContext) VALUES ('d:\\models\\old.gguf', '2026-11-15 10:00:00', 8900000000, 8192)");
        }
        SqliteConnection.ClearAllPools();

        await Use(paths, null, async s =>
        {
            var chat = Assert.Single(await Chats(s).ListAsync("general"));
            var question = Assert.Single(await Chats(s).GetMessagesAsync(chat.Id));
            Assert.Equal("An old question", question.Content);
            Assert.Empty(question.Pictures);
            // What was measured before M17 was measured without a projector, and is used only for a load without one.
            var record = Assert.Single(await s.GetRequiredService<IModelRecordStore>().AllAsync());
            Assert.False(record.MeasuredVramProjector);
            Assert.Equal(8_900_000_000, record.MeasuredAt(8192));
            Assert.Null(record.MeasuredAt(8192, withProjector: true));

            await Store(s).AddAsync(question.Id, [TestJpegs.Make(256, 160)]);
            Assert.Single((await Chats(s).GetMessagesAsync(chat.Id))[0].Pictures);
        });
    }

    // ---- Removed is gone ----

    [Fact]
    public async Task A_deleted_chats_pictures_are_gone_from_the_data_folder_and_no_others()
    {
        var paths = Install("a");
        var kept = System.Text.Encoding.ASCII.GetBytes("\u0001KEPT-ATTACHED-BYTES-PADEMELON\u0002");
        await using var services = Open(paths, null);
        await services.MigrateDogDatabaseAsync();
        var (chat, message) = await QuestionAsync(services);
        var (other, otherMessage) = await QuestionAsync(services);
        await Store(services).AddAsync(message, [TestJpegs.Make(1024, 640, filler: 200_000), TestJpegs.Make(640, 960, seed: 2, filler: 90_000)]);
        var staying = await Store(services).AddAsync(otherMessage, [TestJpegs.Make(256, 160, marker: kept)]);
        // The search itself works: while the chat exists its pictures can be found in the folder.
        Assert.True(HoldsPicture(paths.Root));

        await Chats(services).DeleteAsync(chat);
        await services.GetRequiredService<DatabaseTidy>().SettledAsync();

        Assert.False(HoldsPicture(paths.Root));
        Assert.True(Holds(paths.Root, kept));
        Assert.Equal(TestJpegs.Make(256, 160, marker: kept), (await Store(services).GetAsync(staying[0].Id))!.Image);
        Assert.Empty(await Store(services).ImagesAsync([1, 2]));
    }

    [Fact]
    public async Task A_message_deleted_by_any_code_takes_its_pictures_along()
    {
        var paths = Install("a");
        await using var services = Open(paths, null);
        await services.MigrateDogDatabaseAsync();
        var (chat, message) = await QuestionAsync(services);
        var added = await Store(services).AddAsync(message, [TestJpegs.Make(512, 512, filler: 50_000)]);
        Assert.True(HoldsPicture(paths.Root));

        // Not through the store: the database itself removes the picture with its message.
        var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            await db.Messages.Where(m => m.Id == message).ExecuteDeleteAsync();
        await services.GetRequiredService<DatabaseTidy>().SettledAsync();

        Assert.Null(await Store(services).GetAsync(added[0].Id));
        Assert.False(HoldsPicture(paths.Root));
        Assert.NotNull(await Chats(services).GetAsync(chat));
    }

    // ---- The lock ----

    [Fact]
    public async Task Pictures_in_a_locked_database_never_touch_the_disk_in_the_clear()
    {
        var (paths, key) = LockedInstall("a", "pw");
        var jpeg = TestJpegs.Make(1024, 1024, filler: 300_000);
        long id = 0;
        await Use(paths, key, async s =>
        {
            var (chat, message) = await QuestionAsync(s);
            id = (await Store(s).AddAsync(message, [jpeg]))[0].Id;
            Assert.False(HoldsPicture(paths.Root));
            var edited = await Chats(s).AddMessageAsync(new ChatMessage { ConversationId = chat, Role = ChatRole.User, Content = "Again" });
            await Store(s).CopyAsync(message, edited.Id);
            Assert.False(HoldsPicture(paths.Root));
        });
        Assert.False(HoldsPicture(paths.Root));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(paths.DatabaseFile));
        await Use(paths, key, async s => Assert.Equal(jpeg, (await Store(s).GetAsync(id))!.Image));
        // Without the key there is no reading them.
        await Assert.ThrowsAsync<SqliteException>(() => Use(paths, null, s => Store(s).GetAsync(id)));
    }

    [Fact]
    public async Task Turning_the_lock_on_and_off_keeps_the_pictures_byte_for_byte()
    {
        var paths = Install("a");
        var jpeg = TestJpegs.Make(832, 1024, filler: 120_000);
        long id = 0, chat = 0;
        await Use(paths, null, async s =>
        {
            (chat, var message) = await QuestionAsync(s);
            id = (await Store(s).AddAsync(message, [jpeg]))[0].Id;
        });
        Assert.True(HoldsPicture(paths.DatabaseFile));
        var key = LockFile.Create("pw", Rounds).DatabaseKey;

        // The conversion compares every table's rows, the new one among them.
        DatabaseLock.Encrypt(paths.DatabaseFile, key);

        Assert.False(HoldsPicture(paths.Root));
        await Use(paths, key, async s =>
        {
            Assert.Equal(jpeg, (await Store(s).GetAsync(id))!.Image);
            Assert.Single((await Chats(s).GetMessagesAsync(chat))[0].Pictures);
        });
        DatabaseLock.Decrypt(paths.DatabaseFile, key);
        await Use(paths, null, async s => Assert.Equal(jpeg, (await Store(s).GetAsync(id))!.Image));
    }

    // ---- Backup and restore: all four cases ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_backup_and_restore_bring_the_pictures_on_messages_back(bool backupLocked, bool hereLocked)
    {
        var (source, sourceKey) = backupLocked ? LockedInstall("source", "backup password") : (Install("source"), null);
        var jpegs = new[] { TestJpegs.Make(1024, 640, seed: 1, filler: 60_000), TestJpegs.Make(640, 960, seed: 2, filler: 40_000) };
        long chat = 0;
        await Use(source, sourceKey, async s =>
        {
            (chat, var message) = await QuestionAsync(s);
            await Store(s).AddAsync(message, jpegs);
        });
        var zip = Path.Combine(_root, "backup.zip");

        await DatabaseBackup.CreateAsync(source, zip, "1.0.0", key: sourceKey);

        var unpacked = Path.Combine(_root, "unpacked");
        ZipFile.ExtractToDirectory(zip, unpacked);
        Assert.Equal(!backupLocked, HoldsPicture(unpacked));
        if (backupLocked) Assert.False(HoldsPicture(zip));
        Directory.Delete(unpacked, true);

        // The DOG.ai it is restored into has a picture of its own, which the restore replaces.
        var hereBefore = System.Text.Encoding.ASCII.GetBytes("\u0001HERE-BEFORE-BYTES-BILBY\u0002");
        var (here, hereKey) = hereLocked ? LockedInstall("here", "my password") : (Install("here"), null);
        await Use(here, hereKey, async s =>
        {
            var (_, message) = await QuestionAsync(s, "Mine");
            await Store(s).AddAsync(message, [TestJpegs.Make(64, 64, marker: hereBefore)]);
        });

        await DatabaseBackup.StageAsync(here, zip, key: hereKey, backupPassword: backupLocked ? "backup password" : null);

        if (backupLocked || hereLocked) Assert.False(HoldsPicture(Path.Combine(here.Root, "restore")));
        Assert.NotNull(DatabaseBackup.ApplyPending(here));
        byte[]? key = null;
        if (backupLocked || hereLocked)
        {
            Assert.Equal(DataLockState.Locked, DataLock.Look(here, out var file));
            key = file!.OpenWithPassword(hereLocked ? "my password" : "backup password")!;
            Assert.NotNull(key);
            DataLock.Settle(here, file, key);
        }
        DatabaseBackup.FinishRestore(here, key);

        await Use(here, key, async s =>
        {
            var question = Assert.Single(await Chats(s).GetMessagesAsync(chat));
            Assert.Equal("What is this?", question.Content);
            Assert.Equal([(1024, 640), (640, 960)], question.Pictures.Select(p => (p.Width, p.Height)));
            var images = await Store(s).ImagesAsync([.. question.Pictures.Select(p => p.Id)]);
            Assert.Equal(jpegs, question.Pictures.Select(p => images[p.Id]));
        });
        if (hereLocked)
        {
            Assert.False(HoldsPicture(here.Root));
            Assert.False(Holds(here.Root, hereBefore));
        }
        else if (backupLocked)
        {
            Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(here.DatabaseFile));
            Assert.False(HoldsPicture(here.DatabaseFile));
        }
    }

    // ---- Portable mode ----

    [Fact]
    public async Task A_moved_portable_folder_needs_nothing_done_for_pictures_on_messages()
    {
        var paths = Install("a");
        var jpeg = TestJpegs.Make(512, 512);
        long id = 0;
        await Use(paths, null, async s =>
        {
            var (_, message) = await QuestionAsync(s);
            id = (await Store(s).AddAsync(message, [jpeg]))[0].Id;
            await s.GetRequiredService<DogAI.Core.Settings.ISettingsStore>().SaveAsync(new DogAI.Core.Settings.AppSettings { ModelsDirectory = @"E:\old place\models" });
        });

        // Only the settings row is rewritten: the table holds no path of any kind.
        Assert.Equal(1, PortableMove.Rebase(paths.DatabaseFile, @"E:\old place", @"F:\new place"));

        await Use(paths, null, async s => Assert.Equal(jpeg, (await Store(s).GetAsync(id))!.Image));
        using var db = new SqliteConnection(DatabaseLock.ConnectionString(paths.DatabaseFile, null));
        db.Open();
        using var command = db.CreateCommand();
        // Every column but the image and the date is a number: there is nowhere a path could be.
        command.CommandText = "SELECT group_concat(name) FROM pragma_table_info('AttachedPictures') WHERE type <> 'INTEGER'";
        Assert.Equal("CreatedAt,Image", (string)command.ExecuteScalar()!);
    }
}
