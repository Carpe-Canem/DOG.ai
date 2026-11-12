using System.Text;
using System.Text.RegularExpressions;
using DogAI.Core.Chat;

namespace DogAI.Core.Characters;

/// <summary>Turns a character card and the user's persona into the instructions a model is given.</summary>
public static partial class CharacterPrompt
{
    public const string DefaultInstruction =
        "You are {{char}} in an ongoing fictional role-play with {{user}}. " +
        "Write {{char}}'s next reply only. Stay in character, keep replies vivid and in keeping with the story so far, " +
        "and never write {{user}}'s words or actions.";

    /// <summary>
    /// The user turn put before a greeting when the prompt would otherwise open with the character speaking,
    /// because many chat templates refuse a conversation that starts with a reply.
    /// </summary>
    public const string ChatStart = "[Start a new chat]";

    /// <summary>Asks the model to write the user's next turn instead of the character's (impersonate).</summary>
    public const string ImpersonateDirective =
        "[Write {{user}}'s next message in this role-play, as {{user}} would write it and in the same style as " +
        "{{user}}'s earlier messages. Write only {{user}}'s own words and actions, nothing from {{char}}. " +
        "Reply with the message only.]";

    /// <summary>Added to the system message when drafting the user's turn, so the model knows whose voice to use.</summary>
    public const string ImpersonateHint =
        "[This time, write {{user}}'s next message instead of {{char}}'s: {{user}}'s own words and actions, " +
        "written as {{user}} would write them, with nothing from {{char}}. Write a real message, never a placeholder.]";

    /// <summary>The system message for writing the rolling summary of older turns.</summary>
    public const string SummarySystem = "You summarise stories accurately and briefly. You never continue the story yourself.";

    /// <summary>
    /// The request for the rolling summary. It goes after the messages, where small models follow it best.
    /// {0} is "updated " when there is a summary so far, else empty.
    /// </summary>
    public const string SummaryTask =
        "[Task: write an {0}summary of the story so far between {{char}} and {{user}}, so it can be remembered after the " +
        "messages themselves are forgotten. Combine the summary so far (if any) with the messages above. Write in the past " +
        "tense, under 300 words. Keep names, facts about the characters and the world, what happened, how the characters " +
        "feel about each other, promises, and anything left unresolved. Leave out small talk. Reply with the summary only.]";

    /// <summary>
    /// False for text that should not become a chat's memory: empty, the request repeated back, or stuck repeating
    /// itself. Small models do all three now and then; a bad summary would be sent with every later reply.
    /// </summary>
    public static bool IsUsableSummary(string text)
    {
        if (text.Trim().Length == 0) return false;
        if (text.Contains("[Task:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("summary of the story so far between", StringComparison.OrdinalIgnoreCase)
            || text.Contains("summarise stories accurately", StringComparison.OrdinalIgnoreCase))
            return false;
        return !LooksRepetitive(text);
    }

    /// <summary>True when some three-word run repeats far more often than ordinary prose allows.</summary>
    public static bool LooksRepetitive(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 30) return false;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var worst = 0;
        for (var i = 0; i + 2 < words.Length; i++)
        {
            var key = words[i] + " " + words[i + 1] + " " + words[i + 2];
            worst = Math.Max(worst, counts[key] = counts.GetValueOrDefault(key) + 1);
        }
        return worst >= Math.Max(5, words.Length / 20);
    }

    /// <summary>Fills in {{char}} and {{user}} (any letter case, also the old &lt;BOT&gt; and &lt;USER&gt; forms).</summary>
    public static string Fill(string text, string characterName, string userName)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = CharMacro().Replace(text, characterName);
        return UserMacro().Replace(text, userName);
    }

    /// <summary>
    /// The whole card as one block of text (system message followed by the final instruction), for token counts.
    /// Chats use <see cref="Layout"/>, which places the parts separately.
    /// </summary>
    public static string Build(Character character, Persona? persona, string chatInstructions = "")
    {
        var layout = Layout(character, persona, chatInstructions);
        var text = layout.SystemText();
        return layout.FinalInstruction.Length == 0 ? text : text + "\n\n" + layout.FinalInstruction;
    }

    /// <summary>
    /// The prompt parts for a one-to-one chat: instruction, card, persona, examples and the chat's own instructions
    /// in the system message; lore before or after the card; the card's final instruction and the chat's author's
    /// note placed inside the history. Names are filled in everywhere. Parts that are empty are left out.
    /// </summary>
    public static PromptLayout Layout(Character character, Persona? persona, string chatInstructions = "",
        IReadOnlyList<LoreEntry>? lore = null, string authorsNote = "", int authorsNoteDepth = PromptLayout.DefaultAuthorsNoteDepth,
        string memory = "")
    {
        var user = persona?.Name is { Length: > 0 } n ? n : "User";
        string F(string text) => Fill(text, character.Name, user);
        static string Labelled(string label, string text) => text.Trim().Length == 0 ? "" : label + "\n" + text.Trim();

        var layout = new PromptLayout { AuthorsNoteDepth = authorsNoteDepth };
        // Cards may write {{original}} to keep the default instruction inside their own.
        layout.Add(PromptPartKind.Instruction, F(string.IsNullOrWhiteSpace(character.SystemPrompt)
            ? DefaultInstruction
            : character.SystemPrompt.Replace("{{original}}", DefaultInstruction, StringComparison.OrdinalIgnoreCase)));

        var card = new StringBuilder();
        foreach (var part in new[]
                 {
                     Labelled("[{{char}}]", character.Description),
                     Labelled("[{{char}}'s personality]", character.Personality),
                     Labelled("[Scenario]", character.Scenario),
                     Labelled("[{{user}}]", persona?.Description ?? ""),
                 })
        {
            if (part.Length == 0) continue;
            if (card.Length > 0) card.Append("\n\n");
            card.Append(part);
        }
        layout.Add(PromptPartKind.Card, F(card.ToString()));

        if (lore is { Count: > 0 })
        {
            // Placed in the card's insertion order; which ones made it in was decided by priority.
            var ordered = lore.OrderBy(e => e.InsertionOrder).ToList();
            foreach (var position in new[] { LorePosition.BeforeCard, LorePosition.AfterCard })
            {
                var text = string.Join("\n\n", ordered.Where(e => e.Position == position).Select(e => e.Content.Trim()));
                if (text.Length > 0)
                    layout.Add(position == LorePosition.BeforeCard ? PromptPartKind.LoreBefore : PromptPartKind.LoreAfter, F(Labelled("[World info]", text)));
            }
            layout.LoreNames.AddRange(ordered.Select(LoreLabel));
        }

        layout.Add(PromptPartKind.Examples, F(Labelled("[Examples of how {{char}} talks]", character.ExampleMessages)));
        layout.Add(PromptPartKind.ChatInstructions, F(Labelled("[Instructions for this chat]", chatInstructions)));
        layout.Add(PromptPartKind.Memory, F(Labelled("[What happened earlier in this chat]", memory)));
        layout.FinalInstruction = F(character.PostHistoryInstructions).Trim();
        layout.AuthorsNote = F(authorsNote).Trim();
        return layout;
    }

    /// <summary>How a lore entry is named to people: its name, else its first keys.</summary>
    public static string LoreLabel(LoreEntry entry) =>
        entry.Name.Trim() is { Length: > 0 } name ? name
        : entry.Keys.Count > 0 ? string.Join(", ", entry.Keys.Take(3))
        : "(always on)";

    [GeneratedRegex(@"\{\{char\}\}|<BOT>", RegexOptions.IgnoreCase)]
    private static partial Regex CharMacro();

    [GeneratedRegex(@"\{\{user\}\}|<USER>", RegexOptions.IgnoreCase)]
    private static partial Regex UserMacro();
}
