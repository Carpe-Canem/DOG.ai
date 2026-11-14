using DogAI.Core.Chat;
using DogAI.Core.Images;

namespace DogAI.Core.Stories;

/// <summary>Someone who may be in a story picture: their name, their looks (used exactly as typed) and a few words on who they are.</summary>
public sealed record IllustrationPerson(string Name, Looks Looks, string? About)
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

    /// <summary>The longest the story's premise and world may be when given to the planner as background.</summary>
    public const int BackgroundLength = 1500;

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
                list.Add(new IllustrationPerson(name, Looks.Of(card), PictureWriter.About(card) is { Length: > 0 } about ? about : null));
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

    /// <summary>The story's premise and world, for the planner to understand a passage (not to draw).</summary>
    public static string Background(Story story)
    {
        var parts = new List<string>();
        if (story.Bible.Premise.Trim() is { Length: > 0 } premise) parts.Add("Premise: " + premise);
        if (story.Bible.World.Trim() is { Length: > 0 } world) parts.Add("World: " + world);
        var text = string.Join("\n", parts);
        return text.Length <= BackgroundLength ? text : text[..BackgroundLength];
    }

    /// <summary>The people as the planner is told about them.</summary>
    public static IReadOnlyList<CastEntry> CastFor(IReadOnlyList<IllustrationPerson> people) =>
        people.Select(p => new CastEntry(p.Name, p.HasLooks, p.About)).ToList();

    /// <summary>The chat model plans a picture of one moment of <paramref name="passage"/>: at most <see cref="MaxPeople"/> people.</summary>
    public static Task<PictureScene> PlanAsync(PictureWriter writer, Story story, IReadOnlyList<IllustrationPerson> people, ChatMessage passage,
        PromptStyle style, PictureRating rating, CancellationToken ct = default) =>
        writer.SceneAsync(passage.Content, CastFor(people), style, MaxPeople, Background(story), rating, ct);

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
            await chat.SetPassagePictureAsync(conversationId, passageId, keep, ct).ConfigureAwait(false);
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
        await chat.SetPassagePictureAsync(conversationId, passageId, null, ct).ConfigureAwait(false);
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
