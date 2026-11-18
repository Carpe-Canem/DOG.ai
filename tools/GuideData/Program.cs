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
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Core.Video;
using DogAI.Data;
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

        await SettleTimesAsync(services);
    }

    /// <summary>
    /// Every date today between 09:00 and 09:30, a minute apart in the order things were made: lists then show the same
    /// times on every run on every day, so the pictures do not change from one run to the next.
    /// </summary>
    private static async Task SettleTimesAsync(IServiceProvider services)
    {
        var start = DateTime.Today.AddHours(9).ToUniversalTime();
        await using var db = await services.GetRequiredService<IDbContextFactory<DogDbContext>>().CreateDbContextAsync();
        var minute = 0;
        DateTime Next() => start.AddMinutes(minute++);
        foreach (var x in await db.Characters.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = x.UpdatedAt = Next();
        foreach (var x in await db.Personas.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();
        foreach (var x in await db.ChatFolders.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();
        foreach (var c in await db.Conversations.OrderBy(x => x.Id).ToListAsync())
        {
            c.CreatedAt = Next();
            foreach (var m in await db.Messages.Where(m => m.ConversationId == c.Id).OrderBy(m => m.Id).ToListAsync()) m.CreatedAt = Next();
            c.UpdatedAt = start.AddMinutes(minute - 1);
        }
        foreach (var x in await db.ModelRecords.ToListAsync()) x.AddedAt = start;
        foreach (var x in await db.Pictures.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();
        foreach (var x in await db.Clips.OrderBy(x => x.Id).ToListAsync()) x.CreatedAt = Next();
        await db.SaveChangesAsync();
    }

    private static async Task AddTurnsAsync(IChatStore chats, long id, IEnumerable<(ChatRole Role, string Text, long? Speaker)> turns)
    {
        long? parent = null;
        foreach (var (role, text, speaker) in turns)
        {
            var m = await chats.AddMessageAsync(new ChatMessage
            {
                ConversationId = id, ParentId = parent, Role = role, Content = text, SpeakerId = speaker,
                ModelName = role == ChatRole.Assistant ? Path.GetFileNameWithoutExtension(Content.ChatModel) : null,
            });
            parent = m.Id;
        }
        await chats.SetActiveLeafAsync(id, parent);
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
