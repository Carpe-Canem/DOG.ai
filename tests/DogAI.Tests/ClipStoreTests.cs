using System.IO.Compression;
using System.Text;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Privacy;
using DogAI.Core.Video;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// Kept clips (M20 step 2): what may be kept (a plain WebM, a small still, a checked first frame, file names without
/// folders), the table and its store, and that the lock, backups, restore, a moved portable folder and "removed is
/// gone" cover them with nothing made specially. Every test works in its own throwaway folder.
/// </summary>
public sealed class ClipStoreTests : IDisposable
{
    private const int Rounds = 1000;
    private const string Word = "zqxclipword";
    private static readonly byte[] StillMarker = Encoding.ASCII.GetBytes("\u0001CLIP-STILL-BYTES-NUMBAT\u0002");
    private static readonly byte[] FrameMarker = Encoding.ASCII.GetBytes("\u0001CLIP-FIRST-FRAME-BYTES-DUNNART\u0002");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-clips-" + Guid.NewGuid().ToString("N"));

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

    private static IClipStore Store(IServiceProvider services) => services.GetRequiredService<IClipStore>();

    /// <summary>
    /// A clip as the Video section keeps it: made from a sentence and a first frame, with a still. Each of its three
    /// large values holds a marker of its own, so a search of a data folder's bytes can tell them apart.
    /// </summary>
    private static Clip Sample(int seed = 1, bool withFrame = true, int filler = 400, byte[]? marker = null, string kind = ClipOwners.Video, long owner = ClipOwners.VideoOwner)
    {
        var frame = withFrame ? KeptPictureTests.Webp(960, 640, seed: seed, marker: FrameMarker) : null;
        var request = VideoRequest.For($"A fox runs through the snow, {Word}", VideoQuality.Quick, PictureShape.Wide, startFrame: frame);
        var result = new VideoResult(TestWebm.Make(960, 640, 121, seed, filler, marker), 960, 640, 121, 24, 4_000_000_000 + seed);
        return Clip.Of(kind, owner, VideoFiles.Wan22, request, result, KeptPictureTests.Webp(384, 256, 2000, seed, StillMarker));
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

    /// <summary>True when the folder holds any piece of a sample clip: its video, its still, its first frame or its sentence.</summary>
    private static bool HoldsClip(string path) =>
        Holds(path, TestWebm.Marker) || Holds(path, StillMarker) || Holds(path, FrameMarker) || Holds(path, Encoding.UTF8.GetBytes(Word));

    private static List<string> Columns(AppPaths paths, string where = "")
    {
        using var db = new SqliteConnection(DatabaseLock.ConnectionString(paths.DatabaseFile, null));
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('Clips') {where} ORDER BY cid";
        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    // ---- The store ----

    [Fact]
    public async Task A_kept_clip_comes_back_byte_for_byte_with_what_made_it()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var sample = Sample();
            var (video, still, frame) = (sample.Video!, sample.Still!, sample.StartFrame!);

            var kept = await Store(s).AddAsync(sample);

            Assert.True(kept.Id > 0);
            Assert.True(kept.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
            var read = (await Store(s).GetAsync(kept.Id))!;
            Assert.Equal(video, read.Video);
            Assert.Equal(still, read.Still);
            Assert.Equal(frame, read.StartFrame);
            Assert.Equal((video.Length, frame.Length, true), (read.VideoBytes, read.StartFrameBytes, read.HasStartFrame));
            Assert.Equal((ClipOwners.Video, 0L), (read.OwnerKind, read.OwnerId));
            Assert.Equal(VideoFiles.Wan22, read.Files);
            Assert.Equal(4_000_000_001, read.Seed);

            // The request that makes it again is the one that made it, with the seed that was used.
            var again = read.ToRequest();
            Assert.Empty(again.Problems());
            Assert.Equal(($"A fox runs through the snow, {Word}", 960, 640, 4_000_000_001L, VideoDecoder.Fast, VideoRequest.FixedNegative),
                (again.Prompt, again.Width, again.Height, again.Seed, again.Decoder, again.NegativePrompt));
            Assert.Equal((121, 24, 20, "euler", 5.0, 5.0, 70), (again.Frames, again.Fps, again.Steps, again.Sampler, again.Guidance, again.FlowShift, again.Compression));
            Assert.Equal(frame, again.StartFrame);
            Assert.Equal(@"D:\m\video\diffusion_models\wan2.2_ti2v_5B_fp16.safetensors", read.ModelIn(@"D:\m\video").DiffusionPath);

            Assert.Equal(video, await Store(s).VideoAsync(kept.Id));
            Assert.Equal(frame, await Store(s).StartFrameAsync(kept.Id));
            Assert.Null(await Store(s).GetAsync(kept.Id + 1));
            Assert.Null(await Store(s).VideoAsync(kept.Id + 1));

            // A clip from the sentence alone, at Better's size with the model's own decoder.
            var better = VideoRequest.For("A lake at dawn.", VideoQuality.Better, PictureShape.Tall);
            var second = await Store(s).AddAsync(Clip.Of(ClipOwners.Video, 0, VideoFiles.Wan22, better,
                new VideoResult(TestWebm.Make(832, 1216, 121, 5), 832, 1216, 121, 24, 55)));
            var plain = (await Store(s).GetAsync(second.Id))!;
            Assert.Null(plain.StartFrame);
            Assert.Null(plain.Still);
            Assert.Equal((0, false, VideoDecoder.Own), (plain.StartFrameBytes, plain.HasStartFrame, plain.Decoder));
            Assert.Null(await Store(s).StartFrameAsync(second.Id));
        });
    }

    [Fact]
    public async Task Lists_come_with_the_still_and_without_the_video_or_the_first_frame()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            for (var i = 1; i <= 5; i++) await Store(s).AddAsync(Sample(i));
            await Store(s).AddAsync(Sample(9, kind: ClipOwners.Message, owner: 77));

            var page = await Store(s).PageAsync(ClipOwners.Video, ClipOwners.VideoOwner, 0, 2);

            Assert.Equal((5, 0, true, 2), (page.Total, page.Skip, page.HasMore, page.Next));
            Assert.Equal([5L, 4L], page.Clips.Select(c => c.Id));
            Assert.All(page.Clips, c =>
            {
                Assert.Null(c.Video);
                Assert.Null(c.StartFrame);
                Assert.NotNull(c.Still);
                Assert.True(c.VideoBytes > 0 && c.HasStartFrame);
                Assert.Contains(Word, c.Prompt);
            });
            var rest = await Store(s).PageAsync(ClipOwners.Video, ClipOwners.VideoOwner, page.Next, 10);
            Assert.Equal([3L, 2L, 1L], rest.Clips.Select(c => c.Id));
            Assert.False(rest.HasMore);
            Assert.Empty((await Store(s).PageAsync(ClipOwners.Video, ClipOwners.VideoOwner, 5)).Clips);
            Assert.Equal(6L, Assert.Single((await Store(s).PageAsync(ClipOwners.Message, 77)).Clips).Id);

            var facts = (await Store(s).FactsAsync(3))!;
            Assert.Null(facts.Video);
            Assert.Null(facts.StartFrame);
            Assert.Equal(KeptPictureTests.Webp(384, 256, 2000, 3, StillMarker), facts.Still);
            Assert.Null(await Store(s).FactsAsync(99));
        });
    }

    [Fact]
    public async Task Only_a_plain_webm_with_checked_pictures_and_file_names_is_kept()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            async Task<string> Refused(Action<Clip> change)
            {
                var clip = Sample();
                change(clip);
                return (await Assert.ThrowsAsync<InvalidOperationException>(() => Store(s).AddAsync(clip))).Message;
            }

            // The video: a WebM by its structure, of the clip's own size and length, with nothing written into it.
            Assert.Contains("The video is not a plain WebM file: it carries tags", await Refused(c => c.Video = TestWebm.Make(960, 640, 121, inSegment: TestWebm.TagsWith("a fox"))));
            Assert.Contains("it carries an attached file", await Refused(c => c.Video = TestWebm.Make(960, 640, 121, inSegment: TestWebm.El(TestWebm.Attachments))));
            Assert.Contains("not a plain WebM file", await Refused(c => c.Video = KeptPictureTests.Webp(960, 640)));
            Assert.Contains("The video is empty.", await Refused(c => c.Video = null));
            Assert.Contains("640 × 960, not the 960 × 640", await Refused(c => c.Video = TestWebm.Make(640, 960)));
            Assert.Contains("117 frames, not the 121", await Refused(c => c.Video = TestWebm.Make(960, 640, 117)));
            // The still: a small WebP of the clip's shape and nothing else.
            const string Still = "The still is not a small WebP of the video’s first frame.";
            Assert.Contains(Still, await Refused(c => c.Still = KeptPictureTests.Webp(960, 640)));
            Assert.Contains(Still, await Refused(c => c.Still = KeptPictureTests.Webp(256, 384)));
            Assert.Contains(Still, await Refused(c => c.Still = TestJpegs.Make(384, 256)));
            // The first frame: as it was checked for the request.
            Assert.Contains("not the shape of the video", await Refused(c => c.StartFrame = KeptPictureTests.Webp(640, 960)));
            Assert.Contains("extra information besides the picture", await Refused(c => c.StartFrame = TestJpegs.Make(960, 640, extra: TestJpegs.Exif())));
            // File names: a path inside the file's own folder, never a full path, and only the two formats.
            Assert.Contains("named by its path inside its own folder", await Refused(c => c.Model = @"D:\DOG.ai\Models\video\diffusion_models\wan.safetensors"));
            Assert.Contains("named by its path inside its own folder", await Refused(c => c.TextEncoder = @"..\umt5.gguf"));
            Assert.Contains("named by its path inside its own folder", await Refused(c => c.FastDecoder = ""));
            Assert.Contains(".safetensors or .gguf", await Refused(c => c.Vae = "wan.ckpt"));
            // And the rest.
            Assert.Contains("does not say what it belongs to", await Refused(c => c.OwnerKind = ""));
            Assert.Contains("seed is not known", await Refused(c => c.Seed = -1));
            Assert.Contains("Say in a sentence", await Refused(c => c.Prompt = ""));
            Assert.Contains("multiple of four", await Refused(c => c.Frames = 120));

            Assert.Equal(0, (await Store(s).PageAsync(ClipOwners.Video, 0)).Total);
            Assert.False(HoldsClip(paths.Root));
            // One spelling per file.
            var kept = await Store(s).AddAsync(Sample().Also(c => c.Model = @"wan\wan2.2.safetensors"));
            Assert.Equal("wan/wan2.2.safetensors", (await Store(s).FactsAsync(kept.Id))!.Model);
        });
    }

    [Fact]
    public async Task A_clip_kept_without_a_still_can_be_given_one_and_a_replaced_still_leaves_the_disk()
    {
        var paths = Install("a");
        await using var services = Open(paths, null);
        await services.MigrateDogDatabaseAsync();
        var sample = Sample();
        sample.Still = null;
        var kept = await Store(services).AddAsync(sample);
        Assert.False(Holds(paths.Root, StillMarker));

        Assert.True(await Store(services).SetStillAsync(kept.Id, KeptPictureTests.Webp(384, 256, 2000, 1, StillMarker)));
        Assert.True(Holds(paths.Root, StillMarker));
        Assert.False(await Store(services).SetStillAsync(kept.Id + 1, KeptPictureTests.Webp(384, 256)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(services).SetStillAsync(kept.Id, KeptPictureTests.Webp(960, 640)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(services).SetStillAsync(kept.Id, KeptPictureTests.Webp(256, 384)));

        var other = Encoding.ASCII.GetBytes("\u0001SECOND-STILL-BYTES-POTOROO\u0002");
        Assert.True(await Store(services).SetStillAsync(kept.Id, KeptPictureTests.Webp(384, 256, 2000, 2, other)));
        await services.GetRequiredService<DatabaseTidy>().SettledAsync();

        Assert.True(Holds(paths.Root, other));
        Assert.False(Holds(paths.Root, StillMarker));
        // The video was not rewritten for it.
        Assert.Equal(sample.Video, await Store(services).VideoAsync(kept.Id));
    }

    // ---- The table ----

    [Fact]
    public async Task The_three_large_values_are_the_last_columns_and_the_video_the_last_of_all()
    {
        var paths = Install("a");
        await Use(paths, null, _ => Task.CompletedTask);

        var columns = Columns(paths);

        // SQLite walks a large value's pages to reach any column behind it: nothing may come after the video, and
        // nothing a list reads may come after the first frame.
        Assert.Equal(["Still", "StartFrame", "Video"], columns[^3..]);
        Assert.Equal(
        [
            "Id", "OwnerKind", "OwnerId", "Prompt", "NegativePrompt", "Width", "Height", "Frames", "Fps", "Seed", "Steps", "Sampler", "Guidance",
            "FlowShift", "Decoder", "Compression", "Model", "Vae", "TextEncoder", "FastDecoder", "VideoBytes", "StartFrameBytes", "CreatedAt",
            "Still", "StartFrame", "Video",
        ], columns);
    }

    [Fact]
    public async Task A_moved_portable_folder_needs_nothing_done_for_clips()
    {
        var paths = Install("a");
        var sample = Sample();
        var video = sample.Video!;
        long id = 0;
        await Use(paths, null, async s =>
        {
            id = (await Store(s).AddAsync(sample)).Id;
            await s.GetRequiredService<DogAI.Core.Settings.ISettingsStore>().SaveAsync(new DogAI.Core.Settings.AppSettings { ModelsDirectory = @"E:\old place\models" });
        });

        // Only the settings row is rewritten: a clip names its model's files without any folder.
        Assert.Equal(1, PortableMove.Rebase(paths.DatabaseFile, @"E:\old place", @"F:\new place"));

        await Use(paths, null, async s =>
        {
            var read = (await Store(s).GetAsync(id))!;
            Assert.Equal(video, read.Video);
            Assert.Equal(VideoFiles.Wan22, read.Files);
            Assert.Equal(@"F:\new place\models\video", (await s.GetRequiredService<DogAI.Core.Settings.ISettingsStore>().LoadAsync()).VideoFolder());
        });
        // The text columns: the owner's kind, the two prompts, the sampler, the four file names and the date. The
        // store refuses a file name that is a full path, so there is nowhere a path could be.
        Assert.Equal(["OwnerKind", "Prompt", "NegativePrompt", "Sampler", "Model", "Vae", "TextEncoder", "FastDecoder", "CreatedAt"], Columns(paths, "WHERE type = 'TEXT'"));
    }

    [Fact]
    public async Task A_database_from_before_m20_is_upgraded_with_everything_intact_and_can_keep_clips()
    {
        var paths = Install("a");
        await using (var services = Open(paths, null))
        {
            var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261116103646_TunedWithProjector");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, RecallEnabled, TurnOrder, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('general', 'Old chat', '', 'balanced', '', 4, '', 1, 1, 0, 0, '2026-11-16 10:00:00', '2026-11-16 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 0, 'An old question', '2026-11-16 10:00:00')");
        }
        SqliteConnection.ClearAllPools();
        // The build before M20 kept pictures; one is in the old database.
        var old = KeptPictureTests.Webp(832, 1216);
        long picture = 0;
        await using (var services = Open(paths, null))
        {
            // Through the store of today on the old tables: the Pictures table did not change in M20.
            picture = (await services.GetRequiredService<IPictureStore>().AddAsync(new Picture
            {
                OwnerKind = PictureOwners.Images, Image = old, ImageWidth = 832, ImageHeight = 1216, Checkpoint = "nova.safetensors",
                Prompt = "1girl", Width = 832, Height = 1216, Seed = 5, Steps = 28, Sampler = "euler", Guidance = 5.5,
            })).Id;
        }
        SqliteConnection.ClearAllPools();

        await Use(paths, null, async s =>
        {
            var chats = s.GetRequiredService<IChatStore>();
            var chat = Assert.Single(await chats.ListAsync("general"));
            Assert.Equal("An old question", Assert.Single(await chats.GetMessagesAsync(chat.Id)).Content);
            Assert.Equal(old, await s.GetRequiredService<IPictureStore>().ImageAsync(picture));
            Assert.Equal(0, (await Store(s).PageAsync(ClipOwners.Video, 0)).Total);

            var kept = await Store(s).AddAsync(Sample());
            Assert.Equal(1, (await Store(s).PageAsync(ClipOwners.Video, 0)).Total);
            Assert.NotNull(await Store(s).VideoAsync(kept.Id));
        });
    }

    [Fact]
    public async Task Going_back_a_version_takes_the_table_and_its_triggers_away_cleanly()
    {
        var paths = Install("a");
        await using var services = Open(paths, null);
        await services.MigrateDogDatabaseAsync();
        var chats = services.GetRequiredService<IChatStore>();
        var chat = await chats.CreateAsync(new Conversation { Title = "A chat" });
        await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.User, Content = "Hello" });
        await Store(services).AddAsync(Sample());
        var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();

        await using (var db = await factory.CreateDbContextAsync())
            await db.GetService<IMigrator>().MigrateAsync("20261116103646_TunedWithProjector");

        // A trigger left behind would name a table that is gone, and every delete of a message would fail.
        await chats.DeleteAsync(chat.Id);
        Assert.Null(await chats.GetAsync(chat.Id));
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(0, await db.Clips.CountAsync());
        }
        await Store(services).AddAsync(Sample());
    }

    // ---- Removed is gone ----

    [Fact]
    public async Task A_removed_clip_is_gone_from_the_data_folder_and_no_other()
    {
        var paths = Install("a");
        var staying = Encoding.ASCII.GetBytes("\u0001STAYING-CLIP-BYTES-BETTONG\u0002");
        await using var services = Open(paths, null);
        await services.MigrateDogDatabaseAsync();
        // A clip the size of a real one (about 3 MB), so its pages are spread through the file.
        var removed = await Store(services).AddAsync(Sample(1, filler: 25_000));
        var kept = await Store(services).AddAsync(Sample(2, withFrame: false, marker: staying).Also(c => c.Still = null).Also(c => c.Prompt = "A lake at dawn."));
        // The search itself works: while the clip exists every piece of it can be found in the folder.
        Assert.True(Holds(paths.Root, TestWebm.Marker));
        Assert.True(Holds(paths.Root, StillMarker));
        Assert.True(Holds(paths.Root, FrameMarker));
        Assert.True(Holds(paths.Root, Encoding.UTF8.GetBytes(Word)));

        await Store(services).DeleteAsync(removed.Id);
        await Store(services).DeleteAsync(removed.Id);
        await services.GetRequiredService<DatabaseTidy>().SettledAsync();

        Assert.False(HoldsClip(paths.Root));
        Assert.Null(await Store(services).GetAsync(removed.Id));
        Assert.True(Holds(paths.Root, staying));
        Assert.Equal(TestWebm.Make(960, 640, 121, 2, 400, staying), await Store(services).VideoAsync(kept.Id));

        // All of an owner's clips at once.
        await Store(services).AddAsync(Sample(3));
        await Store(services).AddAsync(Sample(4));
        Assert.Equal(3, await Store(services).DeleteForAsync(ClipOwners.Video, ClipOwners.VideoOwner));
        await services.GetRequiredService<DatabaseTidy>().SettledAsync();
        Assert.False(HoldsClip(paths.Root));
        Assert.False(Holds(paths.Root, staying));
    }

    [Fact]
    public async Task A_clip_goes_with_the_message_character_or_persona_it_belongs_to_whichever_code_deleted_it()
    {
        var paths = Install("a");
        await using var services = Open(paths, null);
        await services.MigrateDogDatabaseAsync();
        var chats = services.GetRequiredService<IChatStore>();
        var characters = services.GetRequiredService<ICharacterStore>();
        var chat = await chats.CreateAsync(new Conversation { Title = "A chat" });
        var message = await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.Assistant, Content = "A reply" });
        var other = await chats.AddMessageAsync(new ChatMessage { ConversationId = chat.Id, Role = ChatRole.Assistant, Content = "Another" });
        var character = await characters.SaveAsync(new Character { Name = "Garm" });
        var persona = await characters.SavePersonaAsync(new Persona { Name = "Tomas" });
        var ofMessage = await Store(services).AddAsync(Sample(1, kind: ClipOwners.Message, owner: message.Id));
        var ofOther = await Store(services).AddAsync(Sample(2, kind: ClipOwners.Message, owner: other.Id));
        var ofCharacter = await Store(services).AddAsync(Sample(3, kind: ClipOwners.Character, owner: character.Id));
        var ofPersona = await Store(services).AddAsync(Sample(4, kind: ClipOwners.Persona, owner: persona.Id));
        // A clip kept in the Video section belongs to nothing that can be deleted, whatever ids those things have.
        var section = await Store(services).AddAsync(Sample(5).Also(c => c.OwnerId = message.Id));

        // Not through the clip store: the database itself removes the clip with its message.
        var factory = services.GetRequiredService<IDbContextFactory<DogDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            await db.Messages.Where(m => m.Id == message.Id).ExecuteDeleteAsync();
        Assert.Null(await Store(services).FactsAsync(ofMessage.Id));
        Assert.NotNull(await Store(services).FactsAsync(ofOther.Id));

        await characters.DeleteAsync(character.Id);
        await characters.DeletePersonaAsync(persona.Id);
        Assert.Null(await Store(services).FactsAsync(ofCharacter.Id));
        Assert.Null(await Store(services).FactsAsync(ofPersona.Id));

        // A whole chat deleted takes its messages' clips.
        await chats.DeleteAsync(chat.Id);
        Assert.Null(await Store(services).FactsAsync(ofOther.Id));
        Assert.NotNull(await Store(services).FactsAsync(section.Id));
        await services.GetRequiredService<DatabaseTidy>().SettledAsync();
        Assert.Equal(1, (await Store(services).PageAsync(ClipOwners.Video, message.Id)).Total);
    }

    // ---- The lock ----

    [Fact]
    public async Task Clips_in_a_locked_database_never_touch_the_disk_in_the_clear()
    {
        var (paths, key) = LockedInstall("a", "pw");
        var sample = Sample(filler: 8000);
        var video = sample.Video!;
        long id = 0;
        await Use(paths, key, async s =>
        {
            id = (await Store(s).AddAsync(sample)).Id;
            Assert.False(HoldsClip(paths.Root));
            await Store(s).SetStillAsync(id, KeptPictureTests.Webp(384, 256, 2000, 7, StillMarker));
            Assert.False(HoldsClip(paths.Root));
        });
        Assert.False(HoldsClip(paths.Root));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(paths.DatabaseFile));
        await Use(paths, key, async s => Assert.Equal(video, await Store(s).VideoAsync(id)));
        // Without the key there is no reading them.
        await Assert.ThrowsAsync<SqliteException>(() => Use(paths, null, s => Store(s).GetAsync(id)));
    }

    [Fact]
    public async Task Turning_the_lock_on_and_off_keeps_the_clips_byte_for_byte()
    {
        var paths = Install("a");
        var sample = Sample(filler: 8000);
        var (video, still, frame) = (sample.Video!, sample.Still!, sample.StartFrame!);
        long id = 0;
        await Use(paths, null, async s => id = (await Store(s).AddAsync(sample)).Id);
        Assert.True(HoldsClip(paths.DatabaseFile));
        var key = LockFile.Create("pw", Rounds).DatabaseKey;

        // The conversion compares every table's rows, the new one among them.
        DatabaseLock.Encrypt(paths.DatabaseFile, key);

        Assert.False(HoldsClip(paths.Root));
        await Use(paths, key, async s =>
        {
            var read = (await Store(s).GetAsync(id))!;
            Assert.Equal(video, read.Video);
            Assert.Equal(still, read.Still);
            Assert.Equal(frame, read.StartFrame);
        });
        DatabaseLock.Decrypt(paths.DatabaseFile, key);
        await Use(paths, null, async s =>
        {
            var read = (await Store(s).GetAsync(id))!;
            Assert.Equal(video, read.Video);
            Assert.Equal(still, read.Still);
            Assert.Equal(frame, read.StartFrame);
        });
    }

    // ---- Backup and restore: all four cases ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_backup_and_restore_bring_the_kept_clips_back(bool backupLocked, bool hereLocked)
    {
        var (source, sourceKey) = backupLocked ? LockedInstall("source", "backup password") : (Install("source"), null);
        var samples = new[] { Sample(1, filler: 6000), Sample(2, withFrame: false) };
        var videos = samples.Select(c => c.Video!).ToList();
        var frame = samples[0].StartFrame!;
        await Use(source, sourceKey, async s =>
        {
            foreach (var sample in samples) await Store(s).AddAsync(sample);
        });
        var zip = Path.Combine(_root, "backup.zip");

        await DatabaseBackup.CreateAsync(source, zip, "1.0.0", key: sourceKey);

        var unpacked = Path.Combine(_root, "unpacked");
        ZipFile.ExtractToDirectory(zip, unpacked);
        Assert.Equal(!backupLocked, HoldsClip(unpacked));
        if (backupLocked) Assert.False(HoldsClip(zip));
        Directory.Delete(unpacked, true);

        // The DOG.ai it is restored into has a clip of its own, which the restore replaces.
        var hereBefore = Encoding.ASCII.GetBytes("\u0001HERE-BEFORE-CLIP-BYTES-QUOLL\u0002");
        var (here, hereKey) = hereLocked ? LockedInstall("here", "my password") : (Install("here"), null);
        await Use(here, hereKey, async s =>
        {
            for (var i = 0; i < 3; i++) await Store(s).AddAsync(Sample(10 + i, withFrame: false, marker: hereBefore).Also(c => c.Still = null).Also(c => c.Prompt = "Mine"));
        });

        await DatabaseBackup.StageAsync(here, zip, key: hereKey, backupPassword: backupLocked ? "backup password" : null);

        if (backupLocked || hereLocked) Assert.False(HoldsClip(Path.Combine(here.Root, "restore")));
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
            var page = await Store(s).PageAsync(ClipOwners.Video, ClipOwners.VideoOwner);
            Assert.Equal([2L, 1L], page.Clips.Select(c => c.Id));
            Assert.Equal(videos[0], await Store(s).VideoAsync(1));
            Assert.Equal(videos[1], await Store(s).VideoAsync(2));
            Assert.Equal(frame, await Store(s).StartFrameAsync(1));
            Assert.Equal(KeptPictureTests.Webp(384, 256, 2000, 1, StillMarker), page.Clips[1].Still);
            Assert.Contains(Word, page.Clips[0].Prompt);
        });
        if (hereLocked)
        {
            Assert.False(HoldsClip(here.Root));
            Assert.False(Holds(here.Root, hereBefore));
        }
        else if (backupLocked)
        {
            Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(here.DatabaseFile));
            Assert.False(HoldsClip(here.DatabaseFile));
        }
    }
}

internal static class ClipTestExtensions
{
    public static Clip Also(this Clip clip, Action<Clip> change)
    {
        change(clip);
        return clip;
    }
}
