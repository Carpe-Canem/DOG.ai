using System.Text;
using DogAI.Core.Characters;
using DogAI.Core.Images;
using DogAI.Core.Stories;

namespace DogAI.Core.Chat;

/// <summary>
/// Pictures in character chats and group chats: a reply can show one kept picture above it, exactly as a story passage
/// does. The row, the pointer on the message and their removal are <see cref="Illustrations"/>' (<c>KeepAsync</c>,
/// <c>RemoveAsync</c>, <c>Shown</c>); this class says who may be in a chat's picture and what the planner is given.
/// A regenerated reply is a new message, so it starts with no picture; the earlier version keeps its own.
/// </summary>
public static class ChatPictures
{
    /// <summary>At most this many people in a chat picture, as in stories.</summary>
    public const int MaxPeople = Illustrations.MaxPeople;

    /// <summary>How many messages before the reply the planner reads to understand it (they are not drawn).</summary>
    public const int BackgroundMessages = 6;

    /// <summary>The longest one earlier message may be in the background, and the background as a whole
    /// (the planner cuts its background at 1,500 characters from the end, which would lose the newest message).</summary>
    public const int BackgroundMessageLength = 500, BackgroundLength = 1400;

    /// <summary>
    /// Who may be in a picture of this chat: the characters in the order given (one for a character chat, the members
    /// for a group), then the persona. A persona named like a character is left out: the planner tells people apart by name.
    /// </summary>
    public static IReadOnlyList<IllustrationPerson> People(IReadOnlyList<Character> characters, Persona? persona)
    {
        var list = new List<IllustrationPerson>();
        foreach (var card in characters)
        {
            var name = card.Name.Trim();
            if (name.Length == 0 || list.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new IllustrationPerson(name, Looks.Of(card), PictureWriter.About(card) is { Length: > 0 } about ? about : null));
        }
        if (persona?.Name.Trim() is { Length: > 0 } personaName && !list.Any(p => p.Name.Equals(personaName, StringComparison.OrdinalIgnoreCase)))
        {
            var about = persona.Description.Trim();
            list.Add(new IllustrationPerson(personaName, Looks.Of(persona), about.Length > 0 ? about : null));
        }
        return list;
    }

    /// <summary>
    /// The messages just before <paramref name="reply"/> on <paramref name="path"/>, oldest first, each as "Name: text".
    /// The newest are kept when it gets too long. Empty when the reply opens the chat.
    /// </summary>
    public static string Background(IReadOnlyList<ChatMessage> path, ChatMessage reply, Func<ChatMessage, string> nameOf)
    {
        var at = -1;
        for (var i = 0; i < path.Count; i++)
            if (path[i].Id == reply.Id) at = i;
        if (at <= 0) return "";
        var lines = new List<string>();
        var length = 0;
        for (var i = at - 1; i >= 0 && lines.Count < BackgroundMessages; i--)
        {
            if (path[i].Role is not (ChatRole.User or ChatRole.Assistant)) continue;
            var text = path[i].Content.Trim().ReplaceLineEndings(" ");
            if (text.Length == 0) continue;
            if (text.Length > BackgroundMessageLength) text = text[..BackgroundMessageLength] + "…";
            var line = Line(nameOf(path[i]), text);
            if (length + line.Length + 1 > BackgroundLength) break;
            length += line.Length + 1;
            lines.Add(line);
        }
        lines.Reverse();
        return string.Join("\n", lines);
    }

    /// <summary>The reply as the planner reads it: who wrote it, then the text, so "I" has an owner.</summary>
    public static string Passage(ChatMessage reply, Func<ChatMessage, string> nameOf) => Line(nameOf(reply), reply.Content.Trim());

    /// <summary>
    /// The chat model plans a picture of one moment of <paramref name="reply"/>, with at most <see cref="MaxPeople"/>
    /// people. <paramref name="nameOf"/> gives the name a message was written under (the persona, the character, the group member).
    /// </summary>
    public static Task<PictureScene> PlanAsync(PictureWriter writer, IReadOnlyList<IllustrationPerson> people, IReadOnlyList<ChatMessage> path,
        ChatMessage reply, Func<ChatMessage, string> nameOf, PromptStyle style, PictureRating rating, CancellationToken ct = default)
    {
        if (reply.Content.Trim().Length == 0) throw new InvalidOperationException("There is nothing to illustrate yet.");
        var background = Background(path, reply, nameOf);
        return writer.SceneAsync(Passage(reply, nameOf), Illustrations.CastFor(people), style, MaxPeople,
            background.Length > 0 ? "What was said just before:\n" + background : null, rating, ct);
    }

    private static string Line(string? name, string text)
    {
        name = (name ?? "").Trim();
        return name.Length > 0 ? new StringBuilder(name).Append(": ").Append(text).ToString() : text;
    }
}
