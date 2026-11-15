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
    /// (under the planner's own limit for "what was said just before", <see cref="PictureWriter.MaxBeforeLength"/>).</summary>
    public const int BackgroundMessageLength = 500, BackgroundLength = 1400;

    /// <summary>
    /// Who may be in a picture of this chat: the characters in the order given (one for a character chat, the members
    /// for a group), then the persona. A persona named like a character is left out: the planner tells people apart by name.
    /// </summary>
    public static IReadOnlyList<IllustrationPerson> People(IReadOnlyList<Character> characters, Persona? persona)
    {
        var list = new List<IllustrationPerson>();
        var user = persona?.Name.Trim() is { Length: > 0 } you ? you : "the user";
        foreach (var card in characters)
        {
            var name = card.Name.Trim();
            if (name.Length == 0 || list.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            // The Scenario has a block of its own, in full (M18 step 3): inside "about" it was cut off after a long description.
            var scenario = CharacterPrompt.Fill(card.Scenario, name, user).Trim();
            list.Add(new IllustrationPerson(name, Looks.Of(card), PictureWriter.About(card, withSetting: false) is { Length: > 0 } about ? about : null,
                scenario.Length > 0 ? scenario : null));
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

    /// <summary>
    /// The chat's setting (M18 step 3): each character's Scenario in full (under their name in a group; the same text
    /// once), then the chat's summary when it covers messages before <paramref name="reply"/> on the branch shown.
    /// Empty when there is neither.
    /// </summary>
    public static string Setting(IReadOnlyList<IllustrationPerson> people, Conversation? conversation, IReadOnlyList<ChatMessage> path, ChatMessage reply)
    {
        var scenarios = people.Where(p => !string.IsNullOrWhiteSpace(p.Setting))
            .GroupBy(p => p.Setting!.Trim(), StringComparer.Ordinal)
            .ToList();
        var parts = new List<string>();
        if (people.Count(p => p.Setting is not null) == 1)
            parts.Add(scenarios[0].Key);
        else
            parts.AddRange(scenarios.Select(g => string.Join(" and ", g.Select(p => p.Name)) + ": " + g.Key));
        if (SummaryBefore(conversation, path, reply) is { } summary)
            parts.Add("What has happened in the chat so far: " + summary);
        return string.Join("\n\n", parts);
    }

    /// <summary>The chat's summary, only while the last message it covers is on the branch before the reply.</summary>
    private static string? SummaryBefore(Conversation? conversation, IReadOnlyList<ChatMessage> path, ChatMessage reply)
    {
        if (conversation is not { SummaryThroughId: { } through } || conversation.Summary.Trim() is not { Length: > 0 } summary) return null;
        for (var i = 0; i < path.Count && path[i].Id != reply.Id; i++)
            if (path[i].Id == through) return summary.ReplaceLineEndings(" ");
        return null;
    }

    /// <summary>What the planner is told besides the reply and the people: the setting, the messages before, the last picture's place.</summary>
    public static SceneContext Context(IReadOnlyList<IllustrationPerson> people, Conversation? conversation, IReadOnlyList<ChatMessage> path,
        ChatMessage reply, Func<ChatMessage, string> nameOf) =>
        new(SceneSource.Chat, Setting(people, conversation, path, reply), Background(path, reply, nameOf), Illustrations.LastPlace(path, reply));

    /// <summary>The reply as the planner reads it: who wrote it, then the text, so "I" has an owner.</summary>
    public static string Passage(ChatMessage reply, Func<ChatMessage, string> nameOf) => Line(nameOf(reply), reply.Content.Trim());

    /// <summary>
    /// The chat model plans a picture of one moment of <paramref name="reply"/>, with at most <see cref="MaxPeople"/>
    /// people. <paramref name="nameOf"/> gives the name a message was written under (the persona, the character, the group member).
    /// </summary>
    public static Task<PictureScene> PlanAsync(PictureWriter writer, IReadOnlyList<IllustrationPerson> people, Conversation? conversation,
        IReadOnlyList<ChatMessage> path, ChatMessage reply, Func<ChatMessage, string> nameOf, PromptStyle style, PictureRating rating, CancellationToken ct = default)
    {
        if (reply.Content.Trim().Length == 0) throw new InvalidOperationException("There is nothing to illustrate yet.");
        return writer.SceneAsync(Passage(reply, nameOf), Illustrations.CastFor(people), style, MaxPeople,
            Context(people, conversation, path, reply, nameOf), rating, ct);
    }

    private static string Line(string? name, string text)
    {
        name = (name ?? "").Trim();
        return name.Length > 0 ? new StringBuilder(name).Append(": ").Append(text).ToString() : text;
    }
}
