using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>Fills in {{char}} and {{user}} (any letter case, also the old &lt;BOT&gt; and &lt;USER&gt; forms).</summary>
    public static string Fill(string text, string characterName, string userName)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = CharMacro().Replace(text, characterName);
        return UserMacro().Replace(text, userName);
    }

    /// <summary>The system message for a one-to-one chat. Parts that are empty are left out.</summary>
    public static string Build(Character character, Persona? persona, string chatInstructions = "")
    {
        var user = persona?.Name is { Length: > 0 } n ? n : "User";
        var sb = new StringBuilder();
        void Part(string? label, string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            if (sb.Length > 0) sb.Append("\n\n");
            if (label is not null) sb.Append(label).Append('\n');
            sb.Append(text);
        }

        // Cards may write {{original}} to keep the default instruction inside their own.
        Part(null, string.IsNullOrWhiteSpace(character.SystemPrompt)
            ? DefaultInstruction
            : character.SystemPrompt.Replace("{{original}}", DefaultInstruction, StringComparison.OrdinalIgnoreCase));
        Part("[{{char}}]", character.Description);
        Part("[{{char}}'s personality]", character.Personality);
        Part("[Scenario]", character.Scenario);
        Part("[{{user}}]", persona?.Description ?? "");
        Part("[Examples of how {{char}} talks]", character.ExampleMessages);
        Part("[Instructions for this chat]", chatInstructions);
        Part(null, character.PostHistoryInstructions);
        return Fill(sb.ToString(), character.Name, user);
    }

    [GeneratedRegex(@"\{\{char\}\}|<BOT>", RegexOptions.IgnoreCase)]
    private static partial Regex CharMacro();

    [GeneratedRegex(@"\{\{user\}\}|<USER>", RegexOptions.IgnoreCase)]
    private static partial Regex UserMacro();
}
