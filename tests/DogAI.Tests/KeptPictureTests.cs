using System.IO.Compression;
using System.Text;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Privacy;
using DogAI.Core.Settings;
using DogAI.Data;
using DogAI.Engine.Images;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// Kept pictures (M9 step 5): the Pictures table and its store, and that the lock, backups, restore and a moved portable
/// folder cover pictures with nothing made specially for them. Every test works in its own throwaway folder.
/// </summary>
public sealed class KeptPictureTests : IDisposable
{
    /// <summary>A word that appears nowhere by chance, put in prompts. If a file's bytes hold it, the file is readable.</summary>
    private const string Marker = "ZEBRAFINCHOCARINA";

    /// <summary>Bytes that appear nowhere by chance, put inside the test pictures themselves.</summary>
    private static readonly byte[] ImageMarker = Encoding.ASCII.GetBytes("\u0001PICTURE-BYTES-KOOKABURRA\u0002");

    private const int Rounds = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-pictures-" + Guid.NewGuid().ToString("N"));

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
        }
        SqliteConnection.ClearAllPools();
    }

    private static IPictureStore Store(IServiceProvider services) => services.GetRequiredService<IPictureStore>();

    /// <summary>
    /// A WebP file as far as its header goes (the lossy kind), followed by <paramref name="filler"/> bytes that hold
    /// <see cref="ImageMarker"/>. Enough for the store, which reads only the header.
    /// </summary>
    internal static byte[] Webp(int width, int height, int filler = 4000, int seed = 1)
    {
        var body = new byte[10 + filler];
        body[3] = 0x9D; body[4] = 0x01; body[5] = 0x2A;
        body[6] = (byte)width; body[7] = (byte)(width >> 8);
        body[8] = (byte)height; body[9] = (byte)(height >> 8);
        new Random(seed).NextBytes(body.AsSpan(10));
        ImageMarker.CopyTo(body.AsSpan(10 + filler / 2));
        return Riff(("VP8 ", body));
    }

    private static byte[] Riff(params (string Name, byte[] Body)[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WEBP"u8);
        foreach (var (name, body) in chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((uint)body.Length);
            writer.Write(body);
            if (body.Length % 2 == 1) writer.Write((byte)0);
        }
        writer.Flush();
        var bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static Picture Sample(string ownerKind = PictureOwners.Test, long ownerId = 0, int seed = 1, string word = Marker) => new()
    {
        OwnerKind = ownerKind,
        OwnerId = ownerId,
        Image = Webp(832, 1216, seed: seed),
        ImageWidth = 832,
        ImageHeight = 1216,
        Checkpoint = "illustrious/nova.safetensors",
        Vae = "sdxl-fix.safetensors",
        Prediction = ImagePrediction.V,
        Prompt = $"masterpiece, 1girl, {word}, forest",
        NegativePrompt = $"lowres, {word}NOT",
        Width = 832,
        Height = 1216,
        Seed = 4_000_000_000 + seed,
        Steps = 28,
        Sampler = "euler_a",
        Scheduler = "karras",
        Guidance = 5.5,
        ClipSkip = 2,
        Loras = [new LoraUse("style/ink.safetensors", 0.8), new LoraUse("marian.safetensors", 1)],
        Hires = new HiresFix(1.5, 14, 0.5),
    };

    private static void AssertSame(Picture expected, Picture actual, bool withImage = true)
    {
        Assert.Equal(expected.OwnerKind, actual.OwnerKind);
        Assert.Equal(expected.OwnerId, actual.OwnerId);
        if (withImage) Assert.Equal(expected.Image, actual.Image);
        else Assert.Null(actual.Image);
        Assert.Equal(expected.Image!.Length, actual.ImageBytes);
        Assert.Equal((expected.ImageWidth, expected.ImageHeight), (actual.ImageWidth, actual.ImageHeight));
        Assert.Equal(expected.Checkpoint, actual.Checkpoint);
        Assert.Equal(expected.Vae, actual.Vae);
        Assert.Equal(expected.Prediction, actual.Prediction);
        Assert.Equal(expected.Prompt, actual.Prompt);
        Assert.Equal(expected.NegativePrompt, actual.NegativePrompt);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(expected.Steps, actual.Steps);
        Assert.Equal(expected.Sampler, actual.Sampler);
        Assert.Equal(expected.Scheduler, actual.Scheduler);
        Assert.Equal(expected.Guidance, actual.Guidance);
        Assert.Equal(expected.ClipSkip, actual.ClipSkip);
        Assert.Equal(expected.Loras, actual.Loras);
        Assert.Equal(expected.Hires, actual.Hires);
        // The request that would draw it again is the same, down to what the engine is sent.
        Assert.Equal(SdApi.Body(expected.ToRequest(), expected.Seed).ToJsonString(), SdApi.Body(actual.ToRequest(), actual.Seed).ToJsonString());
    }

    /// <summary>True if the bytes can be found in the file, or in any file under the folder.</summary>
    private static bool Holds(string path, byte[] bytes)
    {
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [path];
        return files.Any(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var all = new byte[stream.Length];
            stream.ReadExactly(all);
            return all.AsSpan().IndexOf(bytes) >= 0;
        });
    }

    /// <summary>True if the word (as UTF-8 or UTF-16) or the picture marker can be read anywhere in the file or folder.</summary>
    private static bool HoldsPicture(string path) =>
        Holds(path, Encoding.UTF8.GetBytes(Marker)) || Holds(path, Encoding.Unicode.GetBytes(Marker)) || Holds(path, ImageMarker);

    // ---- The WebP header ----

    [Fact]
    public void A_webp_header_gives_the_size_the_kind_and_whether_it_carries_metadata()
    {
        Assert.Equal(new WebpInfo(832, 1216, Lossless: false, HasMetadata: false), WebpInfo.Read(Webp(832, 1216)));

        // Lossless: 2F, then width less one and height less one in 14 bits each.
        var bits = (uint)(640 - 1) | (uint)(480 - 1) << 14;
        var lossless = Riff(("VP8L", [0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), .. new byte[16]]));
        Assert.Equal(new WebpInfo(640, 480, Lossless: true, HasMetadata: false), WebpInfo.Read(lossless));

        // The extended kind, with an EXIF part: where a prompt could be written into a picture.
        byte[] canvas = [0x08, 0, 0, 0, 0xFF, 0x04, 0, 0xFF, 0x06, 0];   // 1280 × 1792, less one each
        var lossy = new byte[10];
        lossy[3] = 0x9D; lossy[4] = 0x01; lossy[5] = 0x2A;
        var extended = Riff(("VP8X", canvas), ("VP8 ", lossy), ("EXIF", [1, 2, 3]));
        Assert.Equal(new WebpInfo(1280, 1792, Lossless: false, HasMetadata: true), WebpInfo.Read(extended));
        // The same without the flag but with the part itself still counts.
        canvas[0] = 0;
        Assert.True(WebpInfo.Read(Riff(("VP8X", canvas), ("VP8 ", lossy), ("XMP ", [1, 2])))!.Value.HasMetadata);
    }

    [Fact]
    public void Bytes_that_are_not_a_whole_webp_file_are_refused()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[64]];
        Assert.Null(WebpInfo.Read(png));
        Assert.Null(WebpInfo.Read([]));
        Assert.Null(WebpInfo.Read(Encoding.ASCII.GetBytes("RIFF....WEBP")));
        var whole = Webp(512, 512);
        // Cut short: the header says more bytes than there are.
        Assert.Null(WebpInfo.Read(whole.AsSpan(0, whole.Length - 100)));
        // A lossy part without its start code.
        var broken = (byte[])whole.Clone();
        broken[23] = 0;
        Assert.Null(WebpInfo.Read(broken));
        // A container with no picture part.
        Assert.Null(WebpInfo.Read(Riff(("EXIF", new byte[40]))));
    }

    // ---- The store ----

    [Fact]
    public async Task A_kept_picture_comes_back_with_its_image_and_everything_needed_to_draw_it_again()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var kept = await Store(s).AddAsync(Sample());
            Assert.True(kept.Id > 0);
            Assert.InRange(kept.CreatedAt, before, DateTime.UtcNow.AddSeconds(1));

            var read = await Store(s).GetAsync(kept.Id);
            AssertSame(Sample(), read!);
            Assert.Equal(kept.CreatedAt, read!.CreatedAt);
            Assert.Equal(Sample().Image, await Store(s).ImageAsync(kept.Id));

            // What it says about redrawing: WebP at the kept quality, same seed, and the model by today's folder.
            var request = read.ToRequest();
            Assert.Equal((ImageFormat.Webp, PictureFormat.Quality, 1), (request.Format, request.Quality, request.Count));
            Assert.Equal(read.Seed, request.Seed);
            Assert.Empty(request.Problems());
            var model = read.ModelIn(@"E:\models\image");
            Assert.Equal(@"E:\models\image\checkpoints\illustrious\nova.safetensors", model.CheckpointPath);
            Assert.Equal(@"E:\models\image\loras", model.LoraFolder);
            Assert.Equal(@"E:\models\image\vae\sdxl-fix.safetensors", model.VaePath);
            Assert.Equal(ImagePrediction.V, model.Prediction);

            // The plainest picture: no LoRA, no hires fix, no VAE, engine defaults.
            var plain = Sample(seed: 2);
            plain.Loras = [];
            plain.Hires = null;
            plain.Vae = null;
            plain.Scheduler = null;
            plain.ClipSkip = null;
            plain.Prediction = ImagePrediction.Auto;
            var keptPlain = await Store(s).AddAsync(plain);
            var readPlain = await Store(s).GetAsync(keptPlain.Id);
            Assert.Null(readPlain!.Hires);
            Assert.Empty(readPlain.Loras);
            Assert.Null(readPlain.ModelIn(@"E:\m").VaePath);
            AssertSame(plain, readPlain);
        });
    }

    [Fact]
    public async Task Pictures_are_listed_by_owner_newest_first_and_without_their_images()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var store = Store(s);
            var first = await store.AddAsync(Sample(PictureOwners.Character, 7, seed: 1));
            var second = await store.AddAsync(Sample(PictureOwners.Character, 7, seed: 2));
            var other = await store.AddAsync(Sample(PictureOwners.Character, 8, seed: 3));
            var persona = await store.AddAsync(Sample(PictureOwners.Persona, 7, seed: 4));

            var list = await store.ListAsync(PictureOwners.Character, 7);
            Assert.Equal([second.Id, first.Id], list.Select(p => p.Id));
            AssertSame(Sample(PictureOwners.Character, 7, seed: 2), list[0], withImage: false);
            Assert.Equal(second.CreatedAt, list[0].CreatedAt);
            Assert.Equal([persona.Id], (await store.ListAsync(PictureOwners.Persona, 7)).Select(p => p.Id));
            Assert.Empty(await store.ListAsync(PictureOwners.Message, 7));

            await store.DeleteAsync(first.Id);
            await store.DeleteAsync(first.Id);   // already gone: nothing happens
            Assert.Null(await store.GetAsync(first.Id));
            Assert.Null(await store.ImageAsync(first.Id));
            Assert.Equal([second.Id], (await store.ListAsync(PictureOwners.Character, 7)).Select(p => p.Id));

            Assert.Equal(1, await store.DeleteForAsync(PictureOwners.Character, 7));
            Assert.Equal(0, await store.DeleteForAsync(PictureOwners.Character, 7));
            Assert.NotNull(await store.GetAsync(other.Id));
            Assert.NotNull(await store.GetAsync(persona.Id));
        });
    }

    [Fact]
    public async Task A_picture_that_is_not_webp_or_names_a_file_by_its_full_path_is_not_kept()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var store = Store(s);
            async Task Refused(Action<Picture> change, string expected)
            {
                var picture = Sample();
                change(picture);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(picture));
                Assert.Contains(expected, error.Message);
            }

            await Refused(p => p.Image = [0x89, (byte)'P', (byte)'N', (byte)'G', .. new byte[100]], "not a WebP");
            await Refused(p => p.Image = null, "not a WebP");
            await Refused(p => p.ImageWidth = 1024, "size does not match");
            await Refused(p => p.Checkpoint = @"D:\DOG.ai\Models\image\checkpoints\nova.safetensors", "checkpoints folder");
            await Refused(p => p.Checkpoint = @"..\..\secret.safetensors", "checkpoints folder");
            await Refused(p => p.Checkpoint = "", "checkpoints folder");
            await Refused(p => p.Vae = @"\\server\share\vae.safetensors", "vae folder");
            await Refused(p => p.Loras = [new LoraUse(@"C:\loras\ink.safetensors")], "LoRA folder");
            await Refused(p => p.OwnerKind = "", "belongs to");
            await Refused(p => p.OwnerKind = new string('x', 40), "belongs to");
            await Refused(p => p.Seed = -1, "seed");
            await Refused(p => p.Prompt = " ", "prompt is empty");
            await Refused(p => p.Steps = 0, "Steps");

            Assert.Empty(await store.ListAsync(PictureOwners.Test, 0));
        });
        // Nothing of the refused pictures reached the file.
        Assert.False(Holds(paths.Root, ImageMarker));
    }

    [Fact]
    public void A_drawn_picture_becomes_a_kept_one_that_draws_the_same_request_again()
    {
        var settings = CheckpointSettings.ForFamily(ImageFamily.Illustrious) with { Vae = @"fixes\sdxl.safetensors", Prediction = ImagePrediction.V };
        var request = ImagePrompt.Compose(settings, "1girl, forest", [(@"style\ink.safetensors", new LoraSettings("inkstyle", 0.7))], 832, 1216)
            .ForKeeping() with { Hires = new HiresFix(1.5, 14, 0.5), Count = 2, Preview = true };
        var result = new ImageResult([Webp(1280, 1856, seed: 1), Webp(1280, 1856, seed: 2)], ImageFormat.Webp, Seed: 500);

        var second = Picture.Of(PictureOwners.Message, 12, @"illustrious\nova.safetensors", settings, request, result, index: 1);

        Assert.Empty(second.Problems());
        Assert.Equal((PictureOwners.Message, 12L), (second.OwnerKind, second.OwnerId));
        Assert.Equal(result.Images[1], second.Image);
        Assert.Equal((1280, 1856), (second.ImageWidth, second.ImageHeight));
        Assert.Equal((832, 1216), (second.Width, second.Height));
        // Folders are written one way whatever the PC wrote, and the second picture of a request has the next seed.
        Assert.Equal("illustrious/nova.safetensors", second.Checkpoint);
        Assert.Equal("fixes/sdxl.safetensors", second.Vae);
        Assert.Equal("style/ink.safetensors", Assert.Single(second.Loras).Path);
        Assert.Equal(501, second.Seed);

        // Drawing it again sends the engine what was sent the first time, for one picture and without previews.
        var again = SdApi.Body(second.ToRequest(), second.Seed).ToJsonString();
        var original = SdApi.Body(request with { Count = 1, Preview = false }, 501).ToJsonString();
        Assert.Equal(original, again);
    }

    // ---- Pictures go when their owner goes ----

    [Fact]
    public async Task Deleting_a_chat_a_character_or_a_persona_takes_its_pictures_along_and_no_others()
    {
        var paths = Install("a");
        await Use(paths, null, async s =>
        {
            var store = Store(s);
            var chats = s.GetRequiredService<IChatStore>();
            var cards = s.GetRequiredService<ICharacterStore>();

            var story = await chats.CreateAsync(new Conversation { Title = "Story", Mode = "story" });
            var passage = await chats.AddMessageAsync(new ChatMessage { ConversationId = story.Id, Role = ChatRole.Assistant, Content = "Once" });
            var kept = await chats.CreateAsync(new Conversation { Title = "Kept", Mode = "story" });
            var keptPassage = await chats.AddMessageAsync(new ChatMessage { ConversationId = kept.Id, Role = ChatRole.Assistant, Content = "Twice" });
            var ann = await cards.SaveAsync(new Character { Name = "Ann" });
            var bea = await cards.SaveAsync(new Character { Name = "Bea" });
            var me = await cards.SavePersonaAsync(new Persona { Name = "Me" });

            var ofPassage = await store.AddAsync(Sample(PictureOwners.Message, passage.Id, 1));
            var ofKeptPassage = await store.AddAsync(Sample(PictureOwners.Message, keptPassage.Id, 2));
            var ofAnn = await store.AddAsync(Sample(PictureOwners.Character, ann.Id, 3));
            var ofBea = await store.AddAsync(Sample(PictureOwners.Character, bea.Id, 4));
            var ofMe = await store.AddAsync(Sample(PictureOwners.Persona, me.Id, 5));
            // Same number, another kind: must not be mistaken for the message's or the character's.
            var test = await store.AddAsync(Sample(PictureOwners.Test, passage.Id, 6));

            // The whole story is deleted: its messages go with it, and their pictures with them.
            await chats.DeleteAsync(story.Id);
            Assert.Null(await store.GetAsync(ofPassage.Id));
            Assert.NotNull(await store.GetAsync(ofKeptPassage.Id));

            await cards.DeleteAsync(ann.Id);
            Assert.Null(await store.GetAsync(ofAnn.Id));
            Assert.NotNull(await store.GetAsync(ofBea.Id));

            await cards.DeletePersonaAsync(me.Id);
            Assert.Null(await store.GetAsync(ofMe.Id));

            Assert.NotNull(await store.GetAsync(test.Id));
            Assert.NotNull(await store.GetAsync(ofKeptPassage.Id));
        });
    }

    // ---- Upgrading ----

    [Fact]
    public async Task A_database_from_before_pictures_is_upgraded_with_everything_intact_and_can_keep_pictures()
    {
        var paths = Install("old");
        await using (var services = Open(paths, null))
        {
            await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261113201950_ChatFolders");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Conversations (Mode, Title, SystemPrompt, PresetId, AuthorsNote, AuthorsNoteDepth, Summary, MemoryEnabled, TurnOrder, RecallEnabled, Pinned, CreatedAt, UpdatedAt) " +
                "VALUES ('story', 'Old story', '', 'creative', '', 4, '', 1, 0, 1, 0, '2026-11-13 10:00:00', '2026-11-13 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Messages (ConversationId, Role, Content, CreatedAt) VALUES (1, 1, 'An old passage', '2026-11-13 10:00:00')");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Characters (Name, Description, Personality, Scenario, FirstMessage, AlternateGreetings, ExampleMessages, SystemPrompt, " +
                "PostHistoryInstructions, CreatorNotes, Creator, CharacterVersion, Tags, ExtraJson, CreatedAt, UpdatedAt) " +
                "VALUES ('Ann', 'A baker', '', '', 'Hi', '[]', '', '', '', '', '', '', '[]', '', '2026-11-13 10:00:00', '2026-11-13 10:00:00')");
            // The table does not exist yet.
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("SELECT COUNT(*) FROM Pictures"));
        }
        SqliteConnection.ClearAllPools();

        await Use(paths, null, async s =>
        {
            var chats = s.GetRequiredService<IChatStore>();
            var story = Assert.Single(await chats.ListAsync("story"));
            Assert.Equal("Old story", story.Title);
            var passage = Assert.Single(await chats.GetMessagesAsync(story.Id));
            Assert.Equal("An old passage", passage.Content);
            var ann = Assert.Single(await s.GetRequiredService<ICharacterStore>().ListAsync());
            Assert.Equal("Ann", ann.Name);

            var store = Store(s);
            Assert.Empty(await store.ListAsync(PictureOwners.Message, passage.Id));
            var kept = await store.AddAsync(Sample(PictureOwners.Message, passage.Id));
            AssertSame(Sample(PictureOwners.Message, passage.Id), (await store.GetAsync(kept.Id))!);
            var portrait = await store.AddAsync(Sample(PictureOwners.Character, ann.Id, 2));

            // The old tables got the rule too: deleting the old story removes the picture kept for its passage.
            await chats.DeleteAsync(story.Id);
            Assert.Null(await store.GetAsync(kept.Id));
            Assert.NotNull(await store.GetAsync(portrait.Id));
        });
    }

    // ---- The lock ----

    [Fact]
    public async Task A_locked_databases_pictures_are_encrypted_with_no_picture_bytes_or_prompt_in_the_file()
    {
        var paths = Install("a");
        var original = Sample();
        long id = 0;
        await Use(paths, null, async s => id = (await Store(s).AddAsync(Sample())).Id);
        // The search itself works: in the plain file both the picture's bytes and its prompt can be found.
        Assert.True(Holds(paths.DatabaseFile, ImageMarker));
        Assert.True(Holds(paths.DatabaseFile, Encoding.UTF8.GetBytes(Marker)));
        var key = LockFile.Create("pw", Rounds).DatabaseKey;

        DatabaseLock.Encrypt(paths.DatabaseFile, key);

        Assert.False(HoldsPicture(paths.Root));
        Assert.Equal([AppPaths.DatabaseName], Directory.GetFiles(paths.Root).Select(Path.GetFileName));

        // While the app is running and writing, the log file beside the database is encrypted too.
        await using (var services = Open(paths, key))
        {
            AssertSame(original, (await Store(services).GetAsync(id))!);
            var later = await Store(services).AddAsync(Sample(PictureOwners.Character, 3, seed: 9));
            Assert.True(File.Exists(paths.DatabaseFile + "-wal"));
            Assert.False(HoldsPicture(paths.Root));
            Assert.Equal(Sample(seed: 9).Image, await Store(services).ImageAsync(later.Id));
            await Store(services).DeleteAsync(later.Id);
            Assert.False(HoldsPicture(paths.Root));
        }
        SqliteConnection.ClearAllPools();
        Assert.False(HoldsPicture(paths.Root));

        // Turning the lock off gives the same picture back, byte for byte.
        DatabaseLock.Decrypt(paths.DatabaseFile, key);
        await Use(paths, null, async s => AssertSame(original, (await Store(s).GetAsync(id))!));
    }

    [Fact]
    public async Task Pictures_kept_in_a_database_that_was_locked_from_its_first_day_never_touch_the_disk_in_the_clear()
    {
        var (paths, key) = LockedInstall("a", "pw");
        await Use(paths, key, async s =>
        {
            var kept = await Store(s).AddAsync(Sample());
            AssertSame(Sample(), (await Store(s).GetAsync(kept.Id))!);
            Assert.False(HoldsPicture(paths.Root));
        });
        Assert.False(HoldsPicture(paths.Root));
        Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(paths.DatabaseFile));
        // Without the key there is no reading them.
        await Assert.ThrowsAsync<SqliteException>(() => Use(paths, null, s => Store(s).ListAsync(PictureOwners.Test, 0)));
    }

    // ---- Backup and restore: all four cases ----

    [Theory]
    [InlineData(false, false)]   // a plain backup into a plain DOG.ai
    [InlineData(false, true)]    // a plain backup into a locked DOG.ai
    [InlineData(true, false)]    // a locked backup into a plain DOG.ai
    [InlineData(true, true)]     // a locked backup into a locked DOG.ai
    public async Task A_backup_and_restore_bring_kept_pictures_back(bool backupLocked, bool hereLocked)
    {
        // The DOG.ai the backup is made from: a story passage with a picture, and a character with one.
        var (source, sourceKey) = backupLocked ? LockedInstall("source", "backup password") : (Install("source"), null);
        long passageId = 0, characterId = 0;
        await Use(source, sourceKey, async s =>
        {
            var chats = s.GetRequiredService<IChatStore>();
            var story = await chats.CreateAsync(new Conversation { Title = "Story", Mode = "story" });
            passageId = (await chats.AddMessageAsync(new ChatMessage { ConversationId = story.Id, Role = ChatRole.Assistant, Content = "Once" })).Id;
            characterId = (await s.GetRequiredService<ICharacterStore>().SaveAsync(new Character { Name = "Ann" })).Id;
            await Store(s).AddAsync(Sample(PictureOwners.Message, passageId, 1));
            await Store(s).AddAsync(Sample(PictureOwners.Character, characterId, 2));
            await s.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { ModelsDirectory = @"Z:\backup-pc\models" });
        });
        var zip = Path.Combine(_root, "backup.zip");

        await DatabaseBackup.CreateAsync(source, zip, "1.0.0", key: sourceKey);

        // A locked backup shows nothing of the pictures, packed or unpacked; a plain one does (so the search finds things).
        var unpacked = Path.Combine(_root, "unpacked");
        ZipFile.ExtractToDirectory(zip, unpacked);
        Assert.Equal(!backupLocked, HoldsPicture(unpacked));
        if (backupLocked) Assert.False(HoldsPicture(zip));
        Directory.Delete(unpacked, true);

        // The DOG.ai it is restored into has a picture of its own, which the restore replaces.
        var (here, hereKey) = hereLocked ? LockedInstall("here", "my password") : (Install("here"), null);
        await Use(here, hereKey, async s =>
        {
            await Store(s).AddAsync(Sample(PictureOwners.Test, 0, seed: 77, word: "HEREBEFORE"));
            await s.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { ModelsDirectory = @"Y:\this-pc\models" });
        });

        await DatabaseBackup.StageAsync(here, zip, key: hereKey, backupPassword: backupLocked ? "backup password" : null);

        // Unless both are plain, nothing readable waits on disk for the restart.
        if (backupLocked || hereLocked) Assert.False(HoldsPicture(Path.Combine(here.Root, "restore")));
        Assert.NotNull(DatabaseBackup.ApplyPending(here));
        byte[]? key = null;
        if (backupLocked || hereLocked)
        {
            Assert.Equal(DataLockState.Locked, DataLock.Look(here, out var file));
            // A locked DOG.ai keeps its own password; a plain one takes over the backup's lock.
            key = file!.OpenWithPassword(hereLocked ? "my password" : "backup password")!;
            Assert.NotNull(key);
            DataLock.Settle(here, file, key);
        }
        else
        {
            Assert.Equal(DataLockState.Off, DataLock.Look(here, out _));
        }
        DatabaseBackup.FinishRestore(here, key);

        await Use(here, key, async s =>
        {
            var store = Store(s);
            var ofPassage = Assert.Single(await store.ListAsync(PictureOwners.Message, passageId));
            AssertSame(Sample(PictureOwners.Message, passageId, 1), (await store.GetAsync(ofPassage.Id))!);
            var ofCharacter = Assert.Single(await store.ListAsync(PictureOwners.Character, characterId));
            AssertSame(Sample(PictureOwners.Character, characterId, 2), (await store.GetAsync(ofCharacter.Id))!);
            // What was here before is gone from the app (it is in the safety copy), and the owners came along too.
            Assert.Empty(await store.ListAsync(PictureOwners.Test, 0));
            Assert.Equal("Ann", (await s.GetRequiredService<ICharacterStore>().GetAsync(characterId))!.Name);
            Assert.Equal(@"Y:\this-pc\models", (await s.GetRequiredService<ISettingsStore>().LoadAsync()).ModelsDirectory);
        });

        if (hereLocked)
        {
            // A locked DOG.ai never had a readable picture on disk at any point: not the restored ones, not its own old one.
            Assert.False(HoldsPicture(here.Root));
            Assert.False(Holds(here.Root, Encoding.UTF8.GetBytes("HEREBEFORE")));
        }
        else if (backupLocked)
        {
            // A plain DOG.ai that took a locked backup: the data it now uses is encrypted. Only its own earlier
            // database, kept aside as before, is still a plain file.
            Assert.Equal(DatabaseFileKind.Encrypted, DatabaseLock.KindOf(here.DatabaseFile));
            Assert.False(HoldsPicture(here.DatabaseFile));
        }
    }

    [Fact]
    public async Task A_locked_backups_description_says_nothing_about_pictures()
    {
        var (paths, key) = LockedInstall("a", "pw");
        await Use(paths, key, s => Store(s).AddAsync(Sample()));
        var zip = Path.Combine(_root, "locked.zip");

        await DatabaseBackup.CreateAsync(paths, zip, "1.0.0", key: key);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["backup.json", "dogai.db", "lock.json"], archive.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(archive.GetEntry("backup.json")!.Open());
        var manifest = reader.ReadToEnd();
        Assert.DoesNotContain(Marker, manifest);
        // No count of pictures, or anything else about them, is written in the open.
        using var json = System.Text.Json.JsonDocument.Parse(manifest);
        Assert.DoesNotContain(json.RootElement.EnumerateObject(), p => p.Name.Contains("icture", StringComparison.OrdinalIgnoreCase));
        Assert.All(json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Number && p.Name != "Format"),
            p => Assert.Equal(0, p.Value.GetInt32()));
    }

    // ---- Portable mode ----

    [Fact]
    public async Task A_moved_portable_folder_needs_nothing_done_for_pictures()
    {
        var paths = Install("a");
        long id = 0;
        await Use(paths, null, async s =>
        {
            id = (await Store(s).AddAsync(Sample())).Id;
            await s.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { ModelsDirectory = @"E:\old place\models" });
        });
        var before = PictureRow(paths.DatabaseFile, id);
        // No column of a picture holds a full path: nothing with a drive letter or a leading slash.
        Assert.DoesNotContain(before, v => v is string text && (text.Contains(@":\") || text.Contains(@"E:") || text.StartsWith('\\') || text.StartsWith('/')));

        // Only the settings row is rewritten.
        Assert.Equal(1, PortableMove.Rebase(paths.DatabaseFile, @"E:\old place", @"F:\new place"));

        Assert.Equal(before, PictureRow(paths.DatabaseFile, id));
        await Use(paths, null, async s =>
        {
            var picture = (await Store(s).GetAsync(id))!;
            AssertSame(Sample(), picture);
            // The picture finds its files under wherever the models folder is now.
            var settings = await s.GetRequiredService<ISettingsStore>().LoadAsync();
            Assert.Equal(@"F:\new place\models", settings.ModelsDirectory);
            Assert.Equal(@"F:\new place\models\image\checkpoints\illustrious\nova.safetensors", picture.ModelIn(settings.ImageFolder()!).CheckpointPath);
        });
    }

    /// <summary>Every column of a picture's row except the image, as SQLite holds it.</summary>
    private static object?[] PictureRow(string databaseFile, long id)
    {
        try
        {
            using var db = new SqliteConnection(DatabaseLock.ConnectionString(databaseFile, null, readOnly: true));
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT * FROM Pictures WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return [.. Enumerable.Range(0, reader.FieldCount).Where(i => reader.GetName(i) != "Image")
                .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))];
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
