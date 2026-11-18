// Fills a portable DOG.ai folder with the user guide's made-up content, so every picture in docs/guide can be made
// again on any PC from nothing but this program, its assets and the models the shot list links in.
//
//   GuideData fill <home>     <home> is a portable DOG.ai folder (app\, portable.txt); its data\ must hold no database
//                             yet. Writes settings, characters, a persona, chats, and every kept picture and clip under
//                             assets\ into it.
//   GuideData export <home>   the reverse for pictures and clips: writes every picture kept in Images and every clip
//                             kept in Video in <home> into this program's assets folder (tools\GuideData\assets), as
//                             the bytes plus a JSON of the row (picture-01…, clip-01…; rename them to say what they
//                             show: "biscuit-portrait" is the character's picture). Used once, after the pictures were
//                             drawn and looked at.
//
// Everything here is made up. Nothing from the owner's own data may be put in: the guide's pictures are public.
// Every date is today between 09:00 and 09:30, so every list shows the same times in the same order on every run.
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Documents;
using DogAI.Core.Stories;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Core.Video;
using DogAI.Data;
using DogAI.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 2 || args[0] is not ("fill" or "export"))
{
    Console.Error.WriteLine("GuideData fill|export <portable DOG.ai folder>");
    return 2;
}
var home = Path.GetFullPath(args[1]);
var paths = AppPaths.Resolve(Path.Combine(home, "app"), null);
if (!paths.IsPortable) { Console.Error.WriteLine($"{home} is not a portable DOG.ai folder (no portable.txt)."); return 2; }

var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
var assets = Assets.Folder();

if (args[0] == "export") return await Export.RunAsync(services, assets);
if (File.Exists(paths.DatabaseFile)) { Console.Error.WriteLine($"{paths.DatabaseFile} exists already: fill an empty data folder."); return 2; }
Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabaseFile)!);
await services.MigrateDogDatabaseAsync();
await Fill.RunAsync(services, paths, assets);
Console.WriteLine($"filled {home}");
return 0;

static class Assets
{
    /// <summary>tools\GuideData\assets in the repository this program was built from.</summary>
    public static string Folder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DogAI.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("DogAI.sln was not found above the program.");
        return Path.Combine(dir.FullName, "tools", "GuideData", "assets");
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}

static class Fill
{
    public static async Task RunAsync(IServiceProvider services, AppPaths paths, string assets)
    {
        var models = Path.Combine(paths.PortableHome!, "models");
        var chat = Path.Combine(models, Content.ChatModel);
        var search = Path.Combine(models, Content.SearchModel);

        // Settings: the look a first start shows, the guide's chat model as the default of every chat mode.
        var store = services.GetRequiredService<ISettingsStore>();
        var settings = await store.LoadAsync();
        foreach (var mode in ModelDefaults.ChatModes) settings = ModelDefaults.Set(settings, mode, chat).Settings;
        settings = ModelDefaults.Set(settings, ModelDefaults.DocumentSearch, search).Settings;
        await store.SaveAsync(settings);
        await services.GetRequiredService<IModelRecordStore>().SaveAsync(new ModelRecord
        {
            Path = chat, Repo = Content.ChatRepo, Quant = "Q8_0", UseCase = "general", AddedAt = DateTime.UtcNow.AddMinutes(-90),
        });
        var catalog = new ImageModelCatalog(services.GetRequiredService<IDocumentStore>());
        await catalog.LoadAsync();
        await catalog.ChooseAsync(Content.Checkpoint);

        // People.
        var characters = services.GetRequiredService<ICharacterStore>();
        await characters.SavePersonaAsync(Content.Sam());
        var portrait = Picture(assets, "biscuit-portrait");
        var biscuit = await characters.SaveAsync(Content.Biscuit(portrait?.Image, portrait is null ? null : SquarePng(portrait.Image!)));
        var mara = await characters.SaveAsync(Content.Mara());

        // Chats, oldest first so the newest is at the top of each list.
        var chats = services.GetRequiredService<IChatStore>();
        var folder = await chats.CreateFolderAsync("Kitchen");
        foreach (var (title, pinned, inFolder, turns) in Content.GeneralChats)
        {
            var c = await chats.CreateAsync(new Conversation { Mode = "general", Title = title });
            await AddTurnsAsync(chats, c.Id, turns.Select(t => (t.Role, t.Text, (long?)null)));
            if (pinned) await chats.SetPinnedAsync(c.Id, true);
            if (inFolder) await chats.SetFolderAsync(c.Id, folder.Id);
        }

        var withBiscuit = await chats.CreateAsync(new Conversation { Mode = "characters", Title = "Biscuit", CharacterId = biscuit.Id });
        await AddTurnsAsync(chats, withBiscuit.Id, Content.BiscuitChat.Select(t => (t.Role, t.Text, (long?)null)));

        var group = await chats.CreateAsync(new Conversation { Mode = Conversation.GroupMode, Title = "Biscuit and Mara" });
        await chats.SetMembersAsync(group.Id, [new ChatMember { CharacterId = biscuit.Id }, new ChatMember { CharacterId = mara.Id }]);
        await AddTurnsAsync(chats, group.Id, Content.GroupChat.Select(t => (t.Role, t.Text,
            t.Speaker switch { "Biscuit" => biscuit.Id, "Mara" => (long?)mara.Id, _ => null })));

        // Kept pictures and clips, drawn once and kept with this program.
        var pictures = services.GetRequiredService<IPictureStore>();
        foreach (var p in AllPictures(assets)) await pictures.AddAsync(p);
        var clips = services.GetRequiredService<IClipStore>();
        foreach (var c in AllClips(assets)) await clips.AddAsync(c);

        // Everything below was added for the chat pages (M23 step 2). It gets its times after everything above, so the
        // pictures made before it keep theirs.
        var first = await FirstNewAsync(services);
        await ChatPagesAsync(services, assets, chats, characters, biscuit, mara, pictures);

        // The picture, video, voice and models pages (M23 step 3): Biscuit's Looks, the words of his portrait, so his
        // Looks and voice tab shows his own words. Nothing else is added: those pages use what is already here.
        new Looks(Content.BiscuitLooks).SaveTo(biscuit);
        await characters.SaveAsync(biscuit);

        await SettleTimesAsync(services, first);
    }

    private static async Task ChatPagesAsync(IServiceProvider services, string assets, IChatStore chats, ICharacterStore characters,
        Character biscuit, Character mara, IPictureStore pictures)
    {
        // Biscuit's lore.
        var lore = Lorebook.For(biscuit);
        foreach (var (name, keys, words) in Content.BiscuitLore)
            lore.Entries.Add(new LoreEntry { Name = name, Keys = [.. keys], Content = words });
        lore.SaveTo(biscuit);
        await characters.SaveAsync(biscuit);

        // A second version of the coastal walk's reply ("2 of 2").
        var coastal = (await chats.ListAsync("general")).Single(c => c.Title == Content.GeneralChats[1].Title);
        var question = (await chats.GetMessagesAsync(coastal.Id)).First(m => m.Role == ChatRole.User);
        var again = await chats.AddMessageAsync(new ChatMessage
        {
            ConversationId = coastal.Id, ParentId = question.Id, Role = ChatRole.Assistant, Content = Content.CoastalWalkAgain,
            ModelName = ModelName,
        });
        await chats.SetActiveLeafAsync(coastal.Id, again.Id);

        // A chat with Mara: a summary of its start, a picture on her last reply.
        var withMara = await chats.CreateAsync(new Conversation { Mode = "characters", Title = "Mara", CharacterId = mara.Id });
        var maraIds = await AddTurnsAsync(chats, withMara.Id, Content.MaraChat.Select(t => (t.Role, t.Text, (long?)null)));
        await chats.SetSummaryAsync(withMara.Id, Content.MaraSummary, maraIds[1]);
        await IllustrateAsync(chats, pictures, assets, "harbour-sunrise", maraIds[^1], new StoryTurn());

        // A question with a picture, and a reply that thought first.
        var photo = await chats.CreateAsync(new Conversation { Mode = "general", Title = Content.PhotoTitle });
        var asked = await chats.AddMessageAsync(new ChatMessage { ConversationId = photo.Id, Role = ChatRole.User, Content = Content.PhotoQuestion });
        await services.GetRequiredService<IAttachedPictureStore>().AddAsync(asked.Id, [ChatJpeg(Path.Combine(assets, "pictures", "harbour-sunrise.webp"))]);
        var answered = await chats.AddMessageAsync(new ChatMessage
        {
            ConversationId = photo.Id, ParentId = asked.Id, Role = ChatRole.Assistant, Content = Content.PhotoReply,
            Reasoning = Content.PhotoThinking, ModelName = ModelName,
        });
        await chats.SetActiveLeafAsync(photo.Id, answered.Id);

        // A document collection with one file, and a chat that searches it and cites it.
        var collections = services.GetRequiredService<ICollectionStore>();
        var collection = await collections.CreateAsync(new DocumentCollection { Name = Content.CollectionName, Description = Content.CollectionNotes });
        var bytes = System.Text.Encoding.UTF8.GetBytes(Content.DocumentText.Replace("\r\n", "\n"));
        var text = TextExtractor.Read(Content.DocumentName, bytes);
        var pieces = Chunker.Split(text);
        var document = await collections.AddDocumentAsync(new SourceDocument
        {
            CollectionId = collection.Id, FileName = Content.DocumentName, Kind = text.Kind, SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            Text = text.Text, PageCount = text.PageStarts.Count, Status = DocumentStatus.Waiting, AddedAt = DateTime.UtcNow,
        }, pieces.Select((c, i) => new DocumentChunk { Ordinal = i, Start = c.Start, Length = c.Length, Page = c.Page, Heading = c.Heading, Text = c.Text }).ToList());
        // Indexed here with the real document search model (the engine and the model are linked in already), so the app
        // does not load that model at start to index it: that would change every picture of Models' side panel.
        using (var embedder = new EmbeddingServer(new LoopbackHttp(), new LlamaServerLocator(services.GetRequiredService<AppPaths>()),
            services.GetRequiredService<ISettingsStore>(), services.GetRequiredService<IModelRecordStore>()))
        {
            var library = new DocumentLibrary(collections, embedder);
            library.StartIndexing();
            await library.WaitForIndexingAsync();
            if ((await collections.DocumentsAsync(collection.Id)).Any(d => d.Status != DocumentStatus.Ready))
                throw new InvalidOperationException("The made-up document was not indexed: " + library.Problem);
            await embedder.StopAsync();
        }
        var stored = await collections.ChunksToIndexAsync(document.Id, "guide");
        foreach (var c in stored) Console.WriteLine($"passage {c.Id}: {c.Heading} ({c.Text.Length} characters)");
        DocumentChunk Passage(string words) => stored.First(c => c.Text.Contains(words, StringComparison.Ordinal));

        var ferry = await chats.CreateAsync(new Conversation { Mode = "general", Title = Content.FerryTitle });
        await collections.SetAttachedAsync(ferry.Id, [collection.Id]);
        var ferryQuestion = await chats.AddMessageAsync(new ChatMessage { ConversationId = ferry.Id, Role = ChatRole.User, Content = Content.FerryQuestion });
        var ferries = Passage("19:00");
        var tickets = Passage("checked again");
        var sources = new ReplySources
        {
            Passages =
            [
                new SourcePassage(1, ferries.Id, document.Id, Content.DocumentName, ferries.Page, ferries.Heading, 0.71f),
                new SourcePassage(2, tickets.Id, document.Id, Content.DocumentName, tickets.Page, tickets.Heading, 0.58f),
            ],
        };
        var ferryReply = await chats.AddMessageAsync(new ChatMessage
        {
            ConversationId = ferry.Id, ParentId = ferryQuestion.Id, Role = ChatRole.Assistant, Content = Content.FerryReply,
            ModelName = ModelName, SourcesJson = sources.ToJson(),
        });
        await chats.SetActiveLeafAsync(ferry.Id, ferryReply.Id);

        // An adventure: three passages, a path not taken at the first choice, choices after the last, a picture.
        var stories = services.GetRequiredService<IStoryStore>();
        var adventure = await chats.CreateAsync(new Conversation { Mode = Story.Mode, Title = Content.AdventureTitle });
        await stories.SaveAsync(new Story
        {
            ConversationId = adventure.Id, Kind = StoryKind.Interactive, Bible = Content.AdventureBible, TrackState = true, Dice = true,
        });
        var opening = await AddAsync(chats, adventure.Id, null, ChatRole.Assistant, Content.AdventureOpening, new StoryTurn
        {
            Choices = [.. Content.AdventureFirstChoices.Select(c => new StoryChoice { Text = c })], State = Content.AdventureState(0),
        });
        var keep = await AddAsync(chats, adventure.Id, opening.Id, ChatRole.User, Content.AdventureOtherPath);
        await AddAsync(chats, adventure.Id, keep.Id, ChatRole.Assistant, Content.AdventureOtherPassage, new StoryTurn { State = Content.AdventureState(1) });
        var ask = await AddAsync(chats, adventure.Id, opening.Id, ChatRole.User, Content.AdventureAsk);
        var second = await AddAsync(chats, adventure.Id, ask.Id, ChatRole.Assistant, Content.AdventureSecond, new StoryTurn { State = Content.AdventureState(1) });
        var walk = await AddAsync(chats, adventure.Id, second.Id, ChatRole.User, Content.AdventureWalk);
        var third = await AddAsync(chats, adventure.Id, walk.Id, ChatRole.Assistant, Content.AdventureThird);
        await IllustrateAsync(chats, pictures, assets, "cliff-path", third.Id, new StoryTurn
        {
            Choices = [.. Content.AdventureLastChoices.Select(c => new StoryChoice { Text = c.Text, Risky = c.Risky })],
            State = Content.AdventureState(2),
        });
        await chats.SetActiveLeafAsync(adventure.Id, third.Id);

        // A written story along a three-chapter outline, two chapters in.
        var written = await chats.CreateAsync(new Conversation { Mode = Story.Mode, Title = Content.WrittenTitle });
        await stories.SaveAsync(new Story
        {
            ConversationId = written.Id, Kind = StoryKind.Authored, Bible = Content.WrittenBible,
            Outline = [.. Content.WrittenOutline.Select(c => new StoryChapter { Title = c.Title, Plan = c.Plan })],
        });
        // Each passage of a written story answers a direction (empty when none was given), which carries its chapter.
        var d1 = await AddAsync(chats, written.Id, null, ChatRole.User, "", new StoryTurn { Chapter = 0 });
        var p1 = await AddAsync(chats, written.Id, d1.Id, ChatRole.Assistant, Content.WrittenFirst);
        var d2 = await AddAsync(chats, written.Id, p1.Id, ChatRole.User, Content.WrittenDirection, new StoryTurn { Chapter = 0 });
        var p2 = await AddAsync(chats, written.Id, d2.Id, ChatRole.Assistant, Content.WrittenSecond);
        var d3 = await AddAsync(chats, written.Id, p2.Id, ChatRole.User, "", new StoryTurn { Chapter = 1 });
        var p3 = await AddAsync(chats, written.Id, d3.Id, ChatRole.Assistant, Content.WrittenThird);
        await chats.SetActiveLeafAsync(written.Id, p3.Id);
    }

    private static readonly string ModelName = Path.GetFileNameWithoutExtension(Content.ChatModel);

    private static async Task<ChatMessage> AddAsync(IChatStore chats, long conversation, long? parent, ChatRole role, string text, StoryTurn? turn = null) =>
        await chats.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation, ParentId = parent, Role = role, Content = text, ExtraJson = turn?.ToJson(),
            ModelName = role == ChatRole.Assistant ? ModelName : null,
        });

    /// <summary>Keeps one of the assets' pictures as a reply's or passage's picture, with the rest of its story details.</summary>
    private static async Task IllustrateAsync(IChatStore chats, IPictureStore pictures, string assets, string name, long messageId, StoryTurn turn)
    {
        var p = Load(Path.Combine(assets, "pictures", name));
        p.OwnerKind = PictureOwners.Message;
        p.OwnerId = messageId;
        var kept = await pictures.AddAsync(p);
        turn.Picture = kept.Id;
        await chats.SetMessageExtraAsync(messageId, turn.ToJson());
    }

    /// <summary>A picture as the chat box makes it: a plain JPEG at most 1,024 px on its longest side.</summary>
    private static byte[] ChatJpeg(string webp)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(File.ReadAllBytes(webp)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var (w, h) = AttachedPictures.SizeFor(frame.PixelWidth, frame.PixelHeight);
        var scaled = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform((double)w / frame.PixelWidth, (double)h / frame.PixelHeight));
        var jpeg = new JpegBitmapEncoder { QualityLevel = AttachedPictures.Quality };
        jpeg.Frames.Add(BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        jpeg.Save(ms);
        var plain = AttachedPictures.Plain(ms.ToArray());
        if (AttachedPictures.Problem(plain) is { } problem) throw new InvalidOperationException("The chat picture is refused: " + problem);
        return plain;
    }

    /// <summary>The highest ids before the chat pages' content was added.</summary>
    private static async Task<(long Conversation, long Message)> FirstNewAsync(IServiceProvider services)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
        return (await db.Conversations.MaxAsync(x => (long?)x.Id) ?? 0, await db.Messages.MaxAsync(x => (long?)x.Id) ?? 0);
    }

    /// <summary>
    /// Every date today between 09:00 and 09:30, a minute apart in the order things were made: lists then show the same
    /// times on every run on every day, so the pictures do not change from one run to the next.
    /// </summary>
    /// <remarks>
    /// Content added later (<paramref name="first"/>: the highest conversation and message ids before it) gets its minutes
    /// after everything that was there before, so adding to the made-up folder never moves a time already in a picture.
    /// </remarks>
    private static async Task SettleTimesAsync(IServiceProvider services, (long Conversation, long Message) first)
    {
        var start = DateTime.Today.AddHours(9).ToUniversalTime();
        await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
        var minute = 0;
        DateTime Next() => start.AddMinutes(minute++);
        foreach (var x in await db.Characters.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = x.UpdatedAt = Next();
        foreach (var x in await db.Personas.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();
        foreach (var x in await db.ChatFolders.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();
        async Task SettleAsync(IEnumerable<Conversation> conversations, Func<ChatMessage, bool> which)
        {
            foreach (var c in conversations)
            {
                c.CreatedAt = Next();
                foreach (var m in await db.Messages.Where(m => m.ConversationId == c.Id).OrderBy(m => m.Id).ToListAsync())
                    if (which(m)) m.CreatedAt = Next();
                c.UpdatedAt = start.AddMinutes(minute - 1);
            }
        }
        var conversations = await db.Conversations.OrderBy(x => x.Id).ToListAsync();
        await SettleAsync(conversations.Where(c => c.Id <= first.Conversation), m => m.Id <= first.Message);
        foreach (var x in await db.ModelRecords.ToListAsync()) x.AddedAt = start;
        var pictures = await db.Pictures.OrderBy(x => x.Id).ToListAsync();
        foreach (var x in pictures.Where(p => p.OwnerKind != PictureOwners.Message)) x.CreatedAt = Next();
        foreach (var x in await db.Clips.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();

        // The chat pages' content (M23 step 2).
        foreach (var m in await db.Messages.Where(m => m.Id > first.Message && m.ConversationId <= first.Conversation).OrderBy(m => m.Id).ToListAsync())
            m.CreatedAt = Next();
        await SettleAsync(conversations.Where(c => c.Id > first.Conversation), _ => true);
        foreach (var x in pictures.Where(p => p.OwnerKind == PictureOwners.Message)) x.CreatedAt = Next();
        foreach (var x in await db.AttachedPictures.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();
        foreach (var x in await db.Collections.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = x.UpdatedAt = Next();
        foreach (var x in await db.Documents.OrderBy(x => x.Id).ToListAsync()) x.AddedAt = Next();
        await db.SaveChangesAsync();
    }

    private static async Task<List<long>> AddTurnsAsync(IChatStore chats, long id, IEnumerable<(ChatRole Role, string Text, long? Speaker)> turns)
    {
        var ids = new List<long>();
        long? parent = null;
        foreach (var (role, text, speaker) in turns)
        {
            var m = await chats.AddMessageAsync(new ChatMessage
            {
                ConversationId = id, ParentId = parent, Role = role, Content = text, SpeakerId = speaker,
                ModelName = role == ChatRole.Assistant ? Path.GetFileNameWithoutExtension(Content.ChatModel) : null,
            });
            parent = m.Id;
            ids.Add(m.Id);
        }
        await chats.SetActiveLeafAsync(id, parent);
        return ids;
    }

    private static Picture? Picture(string assets, string name) =>
        File.Exists(Path.Combine(assets, "pictures", name + ".json")) ? Load(Path.Combine(assets, "pictures", name)) : null;

    private static IEnumerable<Picture> AllPictures(string assets)
    {
        var folder = Path.Combine(assets, "pictures");
        if (!Directory.Exists(folder)) yield break;
        foreach (var json in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
            yield return Load(json[..^5]);
    }

    private static Picture Load(string stem)
    {
        var p = JsonSerializer.Deserialize<Picture>(File.ReadAllText(stem + ".json"))!;
        p.Id = 0;
        p.CreatedAt = default;
        p.Image = File.ReadAllBytes(stem + ".webp");
        p.Preview = File.Exists(stem + ".preview.webp") ? File.ReadAllBytes(stem + ".preview.webp") : null;
        return p;
    }

    private static IEnumerable<Clip> AllClips(string assets)
    {
        var folder = Path.Combine(assets, "clips");
        if (!Directory.Exists(folder)) yield break;
        foreach (var json in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var stem = json[..^5];
            var c = JsonSerializer.Deserialize<Clip>(File.ReadAllText(json))!;
            c.Id = 0;
            c.CreatedAt = default;
            c.Video = File.ReadAllBytes(stem + ".webm");
            c.Still = File.Exists(stem + ".still.webp") ? File.ReadAllBytes(stem + ".still.webp") : null;
            c.StartFrame = File.Exists(stem + ".frame.jpg") ? File.ReadAllBytes(stem + ".frame.jpg") : null;
            yield return c;
        }
    }

    /// <summary>The middle square of a picture as a 256 px PNG: what the app's cropper makes for a list avatar.</summary>
    private static byte[] SquarePng(byte[] image)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(image), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var side = Math.Min(frame.PixelWidth, frame.PixelHeight);
        var crop = new CroppedBitmap(frame, new System.Windows.Int32Rect((frame.PixelWidth - side) / 2, 0, side, side));
        var scaled = new TransformedBitmap(crop, new System.Windows.Media.ScaleTransform(256.0 / side, 256.0 / side));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        png.Save(ms);
        return ms.ToArray();
    }
}

static class Export
{
    public static async Task<int> RunAsync(IServiceProvider services, string assets)
    {
        var pictures = services.GetRequiredService<IPictureStore>();
        var page = await pictures.PageAsync(PictureQuery.Images(null, null, null, 0, 200));
        var folder = Path.Combine(assets, "pictures");
        Directory.CreateDirectory(folder);
        var n = 0;
        foreach (var listed in page.Pictures.OrderBy(p => p.Id))
        {
            var p = await pictures.GetAsync(listed.Id) ?? throw new InvalidOperationException($"picture {listed.Id} is gone");
            var stem = Path.Combine(folder, $"picture-{++n:00}");
            await File.WriteAllBytesAsync(stem + ".webp", p.Image!);
            if (listed.Preview is { } preview) await File.WriteAllBytesAsync(stem + ".preview.webp", preview);
            p.Image = null; p.Preview = null;
            await File.WriteAllTextAsync(stem + ".json", JsonSerializer.Serialize(p, Assets.Json));
            Console.WriteLine($"{stem}: {p.Prompt}");
        }

        var clips = services.GetRequiredService<IClipStore>();
        var cpage = await clips.PageAsync(ClipOwners.Video, 0, 0, 50);
        var cfolder = Path.Combine(assets, "clips");
        Directory.CreateDirectory(cfolder);
        n = 0;
        foreach (var listed in cpage.Clips.OrderBy(c => c.Id))
        {
            var c = await clips.GetAsync(listed.Id) ?? throw new InvalidOperationException($"clip {listed.Id} is gone");
            var stem = Path.Combine(cfolder, $"clip-{++n:00}");
            await File.WriteAllBytesAsync(stem + ".webm", c.Video!);
            if ((c.Still ?? listed.Still) is { } still) await File.WriteAllBytesAsync(stem + ".still.webp", still);
            if (c.StartFrame is { } frame) await File.WriteAllBytesAsync(stem + ".frame.jpg", frame);
            c.Video = null; c.Still = null; c.StartFrame = null;
            await File.WriteAllTextAsync(stem + ".json", JsonSerializer.Serialize(c, Assets.Json));
            Console.WriteLine($"{stem}: {c.Prompt}");
        }
        return 0;
    }
}
