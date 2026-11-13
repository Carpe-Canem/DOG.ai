using System.Text;
using DogAI.Core.Characters;
using DogAI.Core.Chat;

namespace DogAI.Core.Stories;

/// <summary>A card linked to a story, together with the character it names, as the prompt needs it.</summary>
public sealed record StoryCastMember(Character Character, bool Main);

// Character cards in a story. A card is not copied into the bible: its text is read from the card each time a
// passage is written, so the story follows later changes to the card, and the card's lore can fire.
public static partial class StoryPrompt
{
    /// <summary>What {{user}} in a card becomes when no card is the story's main character.</summary>
    public const string MainCharacter = "the main character";

    /// <summary>What {{user}} becomes in the main character's own card when there is no other card.</summary>
    public const string OtherCharacter = "the other character";

    /// <summary>With several cards, the longest each one's description gets, in characters. A single card is sent whole.</summary>
    public const int CastCardLength = 2400;

    /// <summary>With several cards, the longest each one's personality line gets.</summary>
    public const int CastPersonalityLength = 600;

    /// <summary>With several cards, the longest each one's example dialogue gets.</summary>
    public const int CastExamplesLength = 1200;

    /// <summary>
    /// Who {{user}} is in <paramref name="member"/>'s card. Cards are written for a chat between the character and
    /// one other person; in a story that person is the main character (or, in the main character's own card, the
    /// first other card).
    /// </summary>
    public static string UserNameFor(StoryCastMember member, IReadOnlyList<StoryCastMember> cast)
    {
        var other = member.Main ? cast.FirstOrDefault(m => !m.Main) : cast.FirstOrDefault(m => m.Main);
        if (other is not null && other.Character.Name.Trim().Length > 0) return other.Character.Name.Trim();
        return member.Main ? OtherCharacter : MainCharacter;
    }

    /// <summary>The linked cards as the model is shown them. Empty when there are none.</summary>
    public static string CastText(Story story, IReadOnlyList<StoryCastMember> cast)
    {
        if (cast.Count == 0) return "";
        var several = cast.Count > 1;
        var text = new StringBuilder("[Characters]\nThese people are in the story. Keep each one true to the description here: how they look, talk and behave.");
        foreach (var member in cast)
        {
            var c = member.Character;
            var user = UserNameFor(member, cast);
            var description = CharacterPrompt.Fill(c.Description, c.Name, user).Trim();
            var personality = CharacterPrompt.Fill(c.Personality, c.Name, user).Trim();
            if (several)
            {
                description = CharacterPrompt.Brief(description, CastCardLength);
                personality = CharacterPrompt.Brief(personality, CastPersonalityLength);
            }
            text.Append("\n\n[").Append(c.Name.Trim()).Append(']');
            if (RoleNote(story, member) is { Length: > 0 } note) text.Append(' ').Append(note);
            if (description.Length > 0) text.Append('\n').Append(description);
            if (personality.Length > 0) text.Append("\nPersonality: ").Append(personality);
        }
        return text.ToString();
    }

    /// <summary>What the model is told about who writes a character.</summary>
    public static string RoleNote(Story story, StoryCastMember member)
    {
        var name = member.Character.Name.Trim();
        if (story.Kind == StoryKind.Interactive)
            return member.Main
                ? $"The main character, played by the player. The narrator never decides what {name} says, does or chooses."
                : "A character the narrator writes. Not the main character.";
        return member.Main ? "The main character." : "";
    }

    /// <summary>What people are told about who writes a character, on the setup page and beside the story.</summary>
    public static string RoleLabel(StoryKind kind, bool main)
    {
        if (kind == StoryKind.Interactive) return main ? "You play them; the narrator never decides for them" : "Played by the narrator";
        return main ? "The main character" : "In the story";
    }

    /// <summary>
    /// The cards' example dialogue, so the model hears how each character talks. The player's own character is left
    /// out: the narrator never speaks for them.
    /// </summary>
    public static string ExamplesText(Story story, IReadOnlyList<StoryCastMember> cast)
    {
        var text = new StringBuilder();
        foreach (var member in cast)
        {
            if (member.Main && story.Kind == StoryKind.Interactive) continue;
            var c = member.Character;
            var examples = CharacterPrompt.Fill(c.ExampleMessages, c.Name, UserNameFor(member, cast)).Trim();
            if (examples.Length == 0) continue;
            if (cast.Count > 1) examples = CharacterPrompt.Brief(examples, CastExamplesLength);
            if (text.Length > 0) text.Append("\n\n");
            text.Append("[Examples of how ").Append(c.Name.Trim()).Append(" talks]\n").Append(examples);
        }
        return text.ToString();
    }

    /// <summary>A card's scenario as a starting premise for a story about its character.</summary>
    public static string PremiseFrom(Character character)
    {
        var text = CharacterPrompt.Fill(character.Scenario, character.Name, MainCharacter).Trim();
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : "";
    }

    /// <summary>
    /// A card's first message as the opening passage of an interactive story. Greetings speak to {{user}}: the
    /// player's own card by name when the story has one, otherwise the reader, "you". Without a name, being
    /// addressed as {{user}} at the end of a phrase ("You're late, {{user}}.") is dropped, since "You're late, you."
    /// reads badly.
    /// </summary>
    public static string OpeningFrom(Character character, string? playerName = null)
    {
        var greeting = character.Greetings().FirstOrDefault() ?? "";
        if (playerName?.Trim() is { Length: > 0 } name) return CharacterPrompt.Fill(greeting, character.Name, name).Trim();
        return CharacterPrompt.Fill(AddressedUser().Replace(greeting, ""), character.Name, "you").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@",\s*(\{\{user\}\}|<USER>)(?=\s*([.!?,;:""”*]|$))", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AddressedUser();

    /// <summary>
    /// Who may speak in the story, for telling whose line is whose: the linked cards first, in <paramref name="cast"/>
    /// order (so speaker number n is cast[n]), each with the pronoun its card uses, then the names written in the
    /// bible's Cast box, which have no card. Those extra names get no colour or voice; they are there so a line of
    /// theirs is not taken for a card character's, and so a "she said" near them stays unsure.
    /// </summary>
    public static IReadOnlyList<DogAI.Core.Text.Person> People(Story story, IReadOnlyList<StoryCastMember> cast)
    {
        var people = cast.Select(m => new DogAI.Core.Text.Person(m.Character.Name,
            DogAI.Core.Text.Pronouns.Guess(m.Character.Description, m.Character.Personality))).ToList();
        foreach (var name in CastBoxNames(story.Bible.Cast))
            if (people.All(p => !p.Name.Trim().Equals(name, StringComparison.Ordinal))) people.Add(new DogAI.Core.Text.Person(name));
        return people;
    }

    /// <summary>
    /// The names at the start of the Cast box's lines: "Elara Kaine: a cartographer…" or "- Silas (the commissioner)"
    /// give "Elara Kaine" and "Silas". A line that does not start with a short capitalised name gives nothing.
    /// </summary>
    public static IReadOnlyList<string> CastBoxNames(string? castBox)
    {
        var names = new List<string>();
        foreach (var raw in (castBox ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', ' ', '\t');
            var end = line.Length;
            foreach (var stop in new[] { ":", " – ", " — ", " - ", ",", "(", "." })
            {
                var at = line.IndexOf(stop, StringComparison.Ordinal);
                if (at > 0 && at < end) end = at;
            }
            var name = line[..end].Trim().Trim('*', '_').Trim();
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (name.Length < 2 || words.Length > 4 || !char.IsUpper(name[0])) continue;
            if (!words.All(w => char.IsUpper(w[0]) || w is "de" or "van" or "von" or "of" or "the")) continue;
            if (!names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// The Cast line for the person the user played in a character chat that becomes a story: the story's main
    /// character, so the narrator keeps calling them by the name the chat used.
    /// </summary>
    public static string PlayerFromPersona(string name, string description, string characterName)
    {
        name = name.Trim();
        var about = CharacterPrompt.Fill(description, characterName, name).Trim();
        if (name.Length == 0 || name.Equals("You", StringComparison.OrdinalIgnoreCase))
            return about.Length > 0 ? "The main character, whom you play: " + about : "";
        return about.Length > 0 ? $"{name}, the main character, whom you play: {about}" : $"{name}, the main character, whom you play.";
    }

    /// <summary>Tells the model drafting a story bible which characters the story already has.</summary>
    public static string CastForDraft(StoryKind kind, IReadOnlyList<StoryCastMember> cast)
    {
        if (cast.Count == 0) return "";
        var text = new StringBuilder(
            "The story already has these characters. Build the premise, the world and the plot around them, and keep them as described. " +
            "Do not describe them again in the cast field: list only the other people there.");
        foreach (var member in cast)
        {
            var c = member.Character;
            var about = CharacterPrompt.Fill(c.Description.Trim().Length > 0 ? c.Description : c.Personality, c.Name, UserNameFor(member, cast));
            text.Append("\n\n[").Append(c.Name.Trim()).Append(']');
            if (member.Main) text.Append(kind == StoryKind.Interactive ? " The main character, whom the player plays." : " The main character.");
            if (CharacterPrompt.Brief(about) is { Length: > 0 } brief) text.Append('\n').Append(brief);
            if (PremiseFrom(c) is { Length: > 0 } scenario) text.Append("\nSituation: ").Append(CharacterPrompt.Brief(scenario));
        }
        return text.ToString();
    }
}
