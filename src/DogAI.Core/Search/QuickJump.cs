using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Documents;
using DogAI.Core.Models;
using DogAI.Core.Settings;

namespace DogAI.Core.Search;

/// <summary>What a quick-jump result is. The order is the order results of equal standing are listed in.</summary>
public enum JumpKind
{
    /// <summary>A screen of DOG.ai (General, Models › Engines, Settings › Voice…).</summary>
    Screen = 0,

    Character = 1,

    /// <summary>A chat, group chat or story, by its name.</summary>
    Chat = 2,

    /// <summary>A shared document collection.</summary>
    Documents = 3,

    /// <summary>One setting on the Settings page.</summary>
    Setting = 4,

    /// <summary>Words found inside a message (from <see cref="ISearchStore"/>).</summary>
    Message = 5,
}

/// <summary>
/// One place Ctrl+K can go: its name, a few words on where it is, and its address. <see cref="Anchor"/> is the
/// element to show on the Settings page; <see cref="MessageId"/> the message to open the chat at.
/// </summary>
public sealed record JumpTarget(JumpKind Kind, string Title, string Where, string Route)
{
    /// <summary>Extra words the box finds it by. Never shown.</summary>
    public string Words { get; init; } = "";

    /// <summary>Chats and stories: which area it belongs to ("general", "characters", "group", "story").</summary>
    public string? Mode { get; init; }
    public string? Anchor { get; init; }

    /// <summary>A message hit: the chat and the message in it.</summary>
    public long? ConversationId { get; init; }
    public long? MessageId { get; init; }

    /// <summary>A message hit's text, marked as <see cref="ChatSearch.Parts"/> reads it.</summary>
    public string? Snippet { get; init; }
    public DateTime? UpdatedAt { get; init; }

    /// <summary>Listed while nothing is typed yet.</summary>
    public bool Main { get; init; }

    /// <summary>The small label on a result: what kind of thing it is.</summary>
    public string Label => Kind switch
    {
        JumpKind.Chat => ChatSearch.ModeLabel(Mode ?? "general"),
        JumpKind.Character => "Character",
        JumpKind.Documents => "Documents",
        JumpKind.Setting => "Setting",
        JumpKind.Message => "In " + (Mode == Stories.Story.Mode ? "a story" : "a chat"),
        _ => "Go to",
    };
}

/// <summary>
/// Ctrl+K (M13): one box that jumps to any screen, chat, character, story, document collection or setting. The
/// screens and settings are fixed lists built from <see cref="ModelsPlaces"/> and <see cref="SettingsCatalog"/>, so
/// a place that moves is moved in one spot; the owner's own things come from <see cref="QuickJumpIndex"/>. Words
/// inside messages are found by the existing search (<see cref="ISearchStore"/>) and added with <see cref="FromHit"/>.
/// </summary>
public static class QuickJump
{
    /// <summary>How many results the box lists, and how many recent chats it offers before anything is typed.</summary>
    public const int Max = 12, Recent = 5, MessageHits = 5;

    /// <summary>Every screen that has an address of its own.</summary>
    public static IReadOnlyList<JumpTarget> Screens { get; } = BuildScreens();

    /// <summary>Every setting the Settings search knows, as a place to jump to.</summary>
    public static IReadOnlyList<JumpTarget> Settings { get; } = SettingsCatalog.Entries
        .Select(e => new JumpTarget(JumpKind.Setting, e.Title, $"Settings › {SettingsCatalog.Find(e.Category).Title}", $"settings/{e.Category}")
        {
            Anchor = e.Anchor,
            Words = e.Words,
        })
        .ToList();

    private static List<JumpTarget> BuildScreens()
    {
        var screens = new List<JumpTarget>
        {
            new(JumpKind.Screen, "General", "A new chat", "general") { Main = true, Words = "chat ask assistant question new" },
            new(JumpKind.Screen, "Documents", "General", "general/documents") { Words = "files pdf collections reference search library" },
            new(JumpKind.Screen, "Assistants and prompts", "General", "general/library") { Words = "library prompt instructions wording reuse" },
            new(JumpKind.Screen, "Characters", "Your cast", "characters") { Main = true, Words = "cards roleplay cast import" },
            new(JumpKind.Screen, "New character", "Characters", "characters/new") { Words = "create card add make" },
            new(JumpKind.Screen, "New group chat", "Characters", "characters/group/new") { Words = "create scene members several add make" },
            new(JumpKind.Screen, "Personas", "Characters", "characters/personas") { Words = "you are who am i me user" },
            new(JumpKind.Screen, "Story", "Your stories", "story") { Main = true, Words = "stories adventure write" },
            new(JumpKind.Screen, "New story", "Story", "story/new") { Words = "create adventure write add make" },
            new(JumpKind.Screen, "Images", "Make pictures from a prompt", Images.ImagesPanels.Address) { Main = true, Words = "pictures draw make prompt picture stable diffusion sdxl txt2img" },
            new(JumpKind.Screen, "Kept pictures", "Images", Images.ImagesPanels.KeptAddress) { Words = "images gallery saved pictures kept grid" },
            new(JumpKind.Screen, "Search", "Words inside chats and stories", "search") { Words = "find messages text" },
        };
        foreach (var place in ModelsPlaces.Groups.SelectMany(g => g.Places))
        {
            screens.Add(new JumpTarget(JumpKind.Screen, ModelsPlaces.Name(place.Id), place.Subtitle, ModelsPlaces.Address(place.Id))
            {
                Main = place.Id == ModelsPlaces.Library,
                Words = place.Id switch
                {
                    ModelsPlaces.Recommended or ModelsPlaces.Browse or ModelsPlaces.GetPictures or ModelsPlaces.GetVoice => "download gguf",
                    ModelsPlaces.Library => "library installed default models context size load benchmark folders gguf",
                    ModelsPlaces.Pictures => "images installed sdxl settings test picture",
                    ModelsPlaces.Voice => "installed whisper kokoro piper speech",
                    ModelsPlaces.GetVideo => "download wan clip clips movie animate",
                    ModelsPlaces.Video => "installed wan clip clips movie text encoder decoder",
                    _ => "llama.cpp llama-server stable-diffusion.cpp update install version",
                },
            });
            if (place.Id != ModelsPlaces.Pictures) continue;
            screens.Add(new JumpTarget(JumpKind.Screen, ModelsPlaces.LorasName, "LoRAs on this PC", ModelsPlaces.Address(place.Id, ModelsPlaces.Loras)) { Words = "lora style character trigger" });
            screens.Add(new JumpTarget(JumpKind.Screen, ModelsPlaces.TagsName, "The tag list picture prompts are checked against", ModelsPlaces.Address(place.Id, ModelsPlaces.Tags)) { Words = "tag list danbooru e621 full" });
        }
        screens.Add(new JumpTarget(JumpKind.Screen, "Downloads", "Models", $"models/{ModelsPlaces.OldDownloads}") { Words = "downloading progress paused failed" });
        screens.Add(new JumpTarget(JumpKind.Screen, "Settings", "The category shown last", "settings") { Main = true, Words = "options preferences" });
        screens.AddRange(SettingsCatalog.Categories.Select(c =>
            new JumpTarget(JumpKind.Screen, $"Settings › {c.Title}", c.About, $"settings/{c.Id}")));
        return screens;
    }

    /// <summary>
    /// What the box lists. With nothing typed: the chats and stories used last, then the main screens. Otherwise the
    /// places where every typed word starts a word of the name, of where it is, or of its extra words (any case):
    /// those found by their name first; then screens, characters, chats and stories (newest first), collections, settings.
    /// </summary>
    public static IReadOnlyList<JumpTarget> Find(IReadOnlyList<JumpTarget> yours, string? typed, int max = Max)
    {
        var words = Words(typed);
        if (words.Count == 0)
        {
            return yours.Where(t => t.Kind == JumpKind.Chat)
                .OrderByDescending(t => t.UpdatedAt ?? DateTime.MinValue)
                .Take(Recent)
                .Concat(Screens.Where(s => s.Main))
                .Take(max)
                .ToList();
        }
        return Screens.Concat(yours).Concat(Settings)
            .Select((t, order) => (t, order, title: Words(t.Title), all: Words($"{t.Title} {t.Where} {t.Words}")))
            .Where(x => words.All(w => x.all.Any(a => a.StartsWith(w, StringComparison.Ordinal))))
            .OrderBy(x => words.All(w => x.title.Any(a => a.StartsWith(w, StringComparison.Ordinal))) ? 0 : 1)
            .ThenBy(x => x.t.Kind)
            .ThenByDescending(x => x.t.UpdatedAt ?? DateTime.MinValue)
            .ThenBy(x => x.order)
            .Take(max)
            .Select(x => x.t)
            .ToList();
    }

    /// <summary>Whether enough is typed to look inside messages too (one letter would match nearly every chat).</summary>
    public static bool LooksInMessages(string? typed) => Words(typed).Sum(w => w.Length) >= 3;

    /// <summary>A message found by the search, as a place to jump to. Hits on a chat's name are left out: the box already lists chats by name.</summary>
    public static JumpTarget? FromHit(ChatHit hit) => hit.MessageId is null
        ? null
        : new JumpTarget(JumpKind.Message, hit.Title, ChatSearch.ModeLabel(hit.Mode), hit.Route)
        {
            Mode = hit.Mode,
            ConversationId = hit.ConversationId,
            MessageId = hit.MessageId,
            Snippet = (hit.Role == ChatRole.User ? "You: " : "") + hit.Snippet,
            UpdatedAt = hit.UpdatedAt,
        };

    /// <summary>The runs of letters and digits of a text, in lower case (all of them: <see cref="ChatSearch.Words"/> stops at twelve).</summary>
    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        var start = -1;
        var lower = (text ?? "").ToLowerInvariant();
        for (var i = 0; i <= lower.Length; i++)
        {
            var inWord = i < lower.Length && char.IsLetterOrDigit(lower[i]);
            if (inWord && start < 0) start = i;
            if (inWord || start < 0) continue;
            words.Add(lower[start..i]);
            start = -1;
        }
        return words;
    }
}

/// <summary>
/// Asks for the quick-jump box from outside the page: the window's own Ctrl+K (when the page has no focus) and the
/// button on the Search screen. The box listens to <see cref="Asked"/>.
/// </summary>
public sealed class JumpKey
{
    public event Action? Asked;
    public void Ask() => Asked?.Invoke();
}

/// <summary>The owner's own things for the quick-jump box, read fresh each time it opens: chats, group chats, stories, characters and shared document collections.</summary>
public sealed class QuickJumpIndex(IChatStore chats, ICharacterStore characters, ICollectionStore collections)
{
    private static readonly string[] Modes = ["general", "characters", Conversation.GroupMode, Stories.Story.Mode];

    public async Task<IReadOnlyList<JumpTarget>> LoadAsync(CancellationToken ct = default)
    {
        var cast = await characters.ListAsync(ct);
        var names = cast.ToDictionary(c => c.Id, c => c.Name);
        var targets = new List<JumpTarget>();
        foreach (var mode in Modes)
        {
            foreach (var chat in await chats.ListAsync(mode, ct))
            {
                var with = chat.CharacterId is { } id && names.TryGetValue(id, out var name) ? $"with {name}" : "";
                targets.Add(new JumpTarget(JumpKind.Chat, chat.Title, with, ChatSearch.RouteFor(mode, chat.Id)) { Mode = mode, UpdatedAt = chat.UpdatedAt });
            }
        }
        targets.AddRange(cast.Select(c => new JumpTarget(JumpKind.Character, c.Name, "", $"characters/{c.Id}")));
        targets.AddRange((await collections.ListAsync(ct)).Where(c => !c.IsPrivate)
            .Select(c => new JumpTarget(JumpKind.Documents, c.Name, "", $"general/documents/{c.Id}")));
        return targets;
    }
}
