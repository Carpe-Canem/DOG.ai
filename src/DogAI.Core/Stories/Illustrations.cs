using DogAI.Core.Chat;
using DogAI.Core.Images;

namespace DogAI.Core.Stories;

/// <summary>Someone who may be in a story picture: their name, their looks (used exactly as typed) and a few words on who they are.</summary>
/// <param name="Setting">In a chat, their card's Scenario (filled in): the chat's setting, which has a block of its own
/// (<see cref="ChatPictures.Setting"/>). Null in stories, whose setting is the World box.</param>
public sealed record IllustrationPerson(string Name, Looks Looks, string? About, string? Setting = null)
{
    /// <summary>True when their looks come from their card, so the chat model must not describe them.</summary>
    public bool HasLooks => !string.IsNullOrWhiteSpace(Looks.Tags);
}

/// <summary>
/// Story illustrations (M9 step 8): a passage can show one kept picture above it. The picture is a <see cref="Picture"/> row
/// owned by the passage's message (<see cref="PictureOwners.Message"/>), so the database removes it with the passage or the
/// story; <see cref="StoryTurn.Picture"/> records which row is shown. A passage keeps one row: choosing another replaces it.
/// Nothing reaches the disk until the owner presses Use in the picture maker.
/// </summary>
public static class Illustrations
{
    /// <summary>At most this many people in a story picture (the owner's choice: two, unless asked for three).</summary>
    public const int MaxPeople = 2;

    /// <summary>The picture a passage shows, or null.</summary>
    public static long? Shown(ChatMessage passage) => passage.Role == ChatRole.Assistant ? StoryTurn.Of(passage).Picture : null;

    /// <summary>
    /// Who may be in a picture of this story: the linked cards in cast order (with their Looks and what their card says
    /// about them), then the names in the bible's Cast box, which have no Looks; the rest of their Cast line says who they are.
    /// Same order as <see cref="StoryPrompt.People"/>.
    /// </summary>
    public static IReadOnlyList<IllustrationPerson> People(Story story, IReadOnlyList<StoryCastMember> cast)
    {
        var people = StoryPrompt.People(story, cast);
        var list = new List<IllustrationPerson>();
        for (var i = 0; i < people.Count; i++)
        {
            var name = people[i].Name.Trim();
            if (name.Length == 0) continue;
            if (i < cast.Count)
            {
                var card = cast[i].Character;
                // Without the card's Scenario: a story's place is its World box (M18 step 3).
                list.Add(new IllustrationPerson(name, Looks.Of(card), PictureWriter.About(card, withSetting: false) is { Length: > 0 } about ? about : null));
            }
            else
            {
                list.Add(new IllustrationPerson(name, Looks.None, CastBoxAbout(story.Bible.Cast, name)));
            }
        }
        return list;
    }

    /// <summary>What a Cast box line says after the name ("Elara Kaine: a cartographer…" gives "a cartographer…"), or null.</summary>
    public static string? CastBoxAbout(string? castBox, string name)
    {
        foreach (var raw in (castBox ?? "").Split('\n'))
        {
            if (StoryPrompt.CastBoxNames(raw) is not [var found] || found != name) continue;
            var line = raw.Trim().TrimStart('-', '*', '•', ' ', '\t').Trim('*', '_');
            var at = line.IndexOf(name, StringComparison.Ordinal);
            if (at < 0) return null;
            var rest = line[(at + name.Length)..].Trim().TrimStart('*', '_', ':', '–', '—', '-', ',', '.').Trim();
            if (rest.StartsWith('(') && rest.EndsWith(')')) rest = rest[1..^1].Trim();
            return rest.Length > 0 ? rest : null;
        }
        return null;
    }

    /// <summary>
    /// The story's world and premise: the setting, which the planner is told to draw unless the passage says this moment is
    /// somewhere else (M18 step 3; before, it was "for understanding only"). World first: it is the place.
    /// </summary>
    public static string Setting(Story story)
    {
        var parts = new List<string>();
        if (story.Bible.World.Trim() is { Length: > 0 } world) parts.Add("World: " + world);
        if (story.Bible.Premise.Trim() is { Length: > 0 } premise) parts.Add("Premise: " + premise);
        return string.Join("\n", parts);
    }

    /// <summary>The longest the reader's action or the author's direction before a passage may be in "what happened just before".</summary>
    public const int TurnLength = 400;

    /// <summary>
    /// What happened just before <paramref name="passage"/> on <paramref name="path"/>: the end of the passage before it,
    /// then the action or direction that asked for this one (as typed). It tells the planner where they are when the place
    /// was set only earlier (the trial: a talk-only passage got the forest campfire 3 of 3 with it, a tavern without).
    /// Empty for the opening passage.
    /// </summary>
    public static string Before(Story story, IReadOnlyList<ChatMessage> path, ChatMessage passage)
    {
        var at = IndexOf(path, passage);
        if (at <= 0) return "";
        string? turn = null, previous = null;
        for (var i = at - 1; i >= 0; i--)
        {
            var text = path[i].Content.Trim();
            if (path[i].Role == ChatRole.User && turn is null && previous is null && text.Length > 0) turn = text;
            if (path[i].Role == ChatRole.Assistant && text.Length > 0)
            {
                previous = text;
                break;
            }
        }
        var room = PictureWriter.MaxBeforeLength;
        var parts = new List<string>();
        if (turn is not null)
        {
            if (turn.Length > TurnLength) turn = turn[..TurnLength] + "…";
            turn = (story.Kind == StoryKind.Interactive ? "The reader's action: " : "The author's direction: ") + turn.ReplaceLineEndings(" ");
            room -= turn.Length + 2;
        }
        if (previous is not null) parts.Add(PictureWriter.CutStart(previous, room));
        if (turn is not null) parts.Add(turn);
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Where the nearest picture before <paramref name="message"/> on <paramref name="path"/> took place (kept with it, see
    /// <see cref="StoryTurn.Where"/>); null when no earlier picture has one. The message's own picture never counts.
    /// </summary>
    public static PicturePlace? LastPlace(IReadOnlyList<ChatMessage> path, ChatMessage message)
    {
        var at = IndexOf(path, message);
        for (var i = (at < 0 ? path.Count : at) - 1; i >= 0; i--)
            if (path[i].Role == ChatRole.Assistant && StoryTurn.Of(path[i]).Where is { } where) return where;
        return null;
    }

    /// <summary>What the planner is told besides the passage and the people.</summary>
    public static SceneContext Context(Story story, IReadOnlyList<ChatMessage> path, ChatMessage passage) =>
        new(SceneSource.Story, Setting(story), Before(story, path, passage), LastPlace(path, passage));

    private static int IndexOf(IReadOnlyList<ChatMessage> path, ChatMessage message)
    {
        for (var i = 0; i < path.Count; i++)
            if (path[i].Id == message.Id) return i;
        return -1;
    }

    /// <summary>The people as the planner is told about them.</summary>
    public static IReadOnlyList<CastEntry> CastFor(IReadOnlyList<IllustrationPerson> people) =>
        people.Select(p => new CastEntry(p.Name, p.HasLooks, p.About)).ToList();

    /// <summary>
    /// The chat model plans a picture of one moment of <paramref name="passage"/>: at most <see cref="MaxPeople"/> people,
    /// with the story's setting, what happened just before and the last picture's place (<see cref="Context"/>).
    /// </summary>
    public static Task<PictureScene> PlanAsync(PictureWriter writer, Story story, IReadOnlyList<IllustrationPerson> people, IReadOnlyList<ChatMessage> path,
        ChatMessage passage, PromptStyle style, PictureRating rating, CancellationToken ct = default) =>
        writer.SceneAsync(passage.Content, CastFor(people), style, MaxPeople, Context(story, path, passage), rating, ct);

    /// <summary>
    /// Makes <paramref name="chosen"/> the passage's picture: the row is added (unless it is the one already kept), the passage
    /// points at it, then any older row of the passage goes. Returns the kept row's id.
    /// </summary>
    /// <exception cref="InvalidOperationException">The picture could not be kept, or the passage is gone (then nothing is left behind).</exception>
    public static async Task<long> KeepAsync(IPictureStore pictures, ChatService chat, long conversationId, long passageId, PictureTry chosen,
        CancellationToken ct = default)
    {
        var existing = await pictures.ListAsync(PictureOwners.Message, passageId, ct).ConfigureAwait(false);
        var keep = chosen.KeptId is { } same && existing.Any(p => p.Id == same)
            ? same
            : (await pictures.AddAsync(chosen.ToKeep(PictureOwners.Message, passageId), ct).ConfigureAwait(false)).Id;
        try
        {
            await chat.SetPassagePictureAsync(conversationId, passageId, keep, chosen.Place, ct).ConfigureAwait(false);
        }
        catch
        {
            if (existing.All(p => p.Id != keep)) await pictures.DeleteAsync(keep, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        foreach (var old in existing)
            if (old.Id != keep) await pictures.DeleteAsync(old.Id, ct).ConfigureAwait(false);
        return keep;
    }

    /// <summary>Takes the picture off the passage and deletes it.</summary>
    public static async Task RemoveAsync(IPictureStore pictures, ChatService chat, long conversationId, long passageId, CancellationToken ct = default)
    {
        await chat.SetPassagePictureAsync(conversationId, passageId, null, null, ct).ConfigureAwait(false);
        await pictures.DeleteForAsync(PictureOwners.Message, passageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The pictures shown on <paramref name="path"/>, by picture id, as WebP (for exports). A passage that points at a row
    /// that is gone, or at a row of another message, gets none.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, byte[]>> LoadAsync(IPictureStore pictures, IReadOnlyList<ChatMessage> path, CancellationToken ct = default)
    {
        var found = new Dictionary<long, byte[]>();
        foreach (var passage in path)
        {
            if (Shown(passage) is not { } id || found.ContainsKey(id)) continue;
            if (await pictures.GetAsync(id, ct).ConfigureAwait(false) is { Image: { } image, OwnerKind: PictureOwners.Message } row && row.OwnerId == passage.Id)
                found[id] = image;
        }
        return found;
    }
}
