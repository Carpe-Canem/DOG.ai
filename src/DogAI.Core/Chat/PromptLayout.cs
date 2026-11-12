using System.Text;

namespace DogAI.Core.Chat;

/// <summary>The kinds of standing text a prompt can carry, in the order they appear in the system message.</summary>
public enum PromptPartKind
{
    Instruction = 0,
    LoreBefore = 1,
    Card = 2,

    /// <summary>Group chats: who else is in the scene.</summary>
    Cast = 3,
    LoreAfter = 4,
    Examples = 5,
    ChatInstructions = 6,
    Memory = 7,

    /// <summary>Passages found in the chat's documents. Placed with the newest user turn, not in the system message.</summary>
    Documents = 8,

    /// <summary>Earlier messages of a long chat that match the newest one. Placed with the newest user turn.</summary>
    Recall = 9,
}

/// <summary>One piece of standing text, e.g. the card's description or the lore that fired.</summary>
public sealed record PromptPart(PromptPartKind Kind, string Text)
{
    /// <summary>
    /// Text that changes with every message (found passages, recalled messages) goes with the newest user turn rather
    /// than in the system message: the engine reuses the prompt it processed last time up to the first change, so a
    /// system message that changed every turn would mean reading the whole chat again for every reply.
    /// </summary>
    public bool InUserTurn => Kind is PromptPartKind.Documents or PromptPartKind.Recall;

    /// <summary>
    /// When even the newest message does not fit, parts are left out in this order (lowest first) after all
    /// older turns are gone. Zero means never: the instruction, the card and the chat's own instructions stay.
    /// The order follows the plan's priority: system &gt; card &gt; others in a group &gt; documents &gt; lore &gt; memory &gt;
    /// recalled messages &gt; older turns.
    /// </summary>
    public int DropOrder => Kind switch
    {
        PromptPartKind.Examples => 1,
        PromptPartKind.Recall => 2,
        PromptPartKind.Memory => 3,
        PromptPartKind.LoreBefore or PromptPartKind.LoreAfter => 4,
        PromptPartKind.Documents => 5,
        PromptPartKind.Cast => 6,
        _ => 0,
    };
}

/// <summary>
/// Everything that goes into a prompt besides the chat history: the parts of the system message, plus the
/// author's note and the final instruction that are placed inside the history.
/// </summary>
public sealed class PromptLayout
{
    public List<PromptPart> Parts { get; } = [];

    /// <summary>A reminder placed <see cref="AuthorsNoteDepth"/> messages from the end of the history.</summary>
    public string AuthorsNote { get; set; } = "";

    /// <summary>0 puts the note with the newest message; 4 puts it four messages back.</summary>
    public int AuthorsNoteDepth { get; set; } = DefaultAuthorsNoteDepth;

    /// <summary>The card's post-history instruction, placed with the newest user message.</summary>
    public string FinalInstruction { get; set; } = "";

    /// <summary>
    /// A one-off request added as the last user turn (or to it, when the history already ends with the user),
    /// such as "write the user's next message". Empty for normal replies.
    /// </summary>
    public string Directive { get; set; } = "";

    /// <summary>
    /// The user turn put first when the history would otherwise open with a reply (many chat templates refuse that).
    /// Null uses the character-chat wording.
    /// </summary>
    public string? OpeningTurn { get; set; }

    /// <summary>Names of the lore entries included, for the prompt inspector.</summary>
    public List<string> LoreNames { get; } = [];

    /// <summary>Which messages the memory summary covers, for the prompt inspector. Null when there is none.</summary>
    public string? MemoryCovers { get; set; }

    public const int DefaultAuthorsNoteDepth = 4;

    /// <summary>A layout that is just the given instructions, as General chats use.</summary>
    public static PromptLayout Plain(string instructions)
    {
        var layout = new PromptLayout();
        layout.Add(PromptPartKind.ChatInstructions, instructions);
        return layout;
    }

    public void Add(PromptPartKind kind, string text)
    {
        text = text.Trim();
        if (text.Length > 0) Parts.Add(new PromptPart(kind, text));
    }

    /// <summary>The system message made from <paramref name="parts"/> (all parts when null), in standard order.</summary>
    public string SystemText(IEnumerable<PromptPart>? parts = null) =>
        string.Join("\n\n", (parts ?? Parts).Where(p => !p.InUserTurn).OrderBy(p => p.Kind).Select(p => p.Text));

    /// <summary>
    /// The full message list: the system message, then the history with the author's note and final instruction
    /// added to user turns. They go inside user turns rather than as extra system messages, because many chat
    /// templates refuse a system message anywhere but first.
    /// </summary>
    /// <param name="history">The chat turns, oldest first, already trimmed to fit. Roles are "user" and "assistant".</param>
    /// <param name="directive">False leaves <see cref="Directive"/> out.</param>
    /// <param name="parts">The parts still in the prompt (all when null); those placed in user turns go before the newest user turn's text.</param>
    public List<PromptMessage> Arrange(string system, IReadOnlyList<PromptMessage> history, bool directive = true, IEnumerable<PromptPart>? parts = null)
    {
        var result = new List<PromptMessage>(history.Count + 1);
        if (system.Length > 0) result.Add(new PromptMessage("system", system));
        var turns = history.Select(m => m.Content).ToArray();

        var note = AuthorsNote.Trim();
        if (note.Length > 0 && UserTurnAt(history, history.Count - 1 - Math.Max(0, AuthorsNoteDepth)) is { } at)
            turns[at] = Join(turns[at], "[Author's note: " + note + "]");

        var final = FinalInstruction.Trim();
        if (final.Length > 0 && UserTurnAt(history, history.Count - 1) is { } last)
            turns[last] = Join(turns[last], final);

        // Found passages and recalled messages come first in the newest user turn, so the message itself is read last.
        var reference = string.Join("\n\n", (parts ?? Parts).Where(p => p.InUserTurn).OrderBy(p => p.Kind).Select(p => p.Text));
        if (reference.Length > 0 && UserTurnAt(history, history.Count - 1) is { } newest)
            turns[newest] = turns[newest].Trim().Length == 0 ? reference : reference + "\n\n" + turns[newest];

        var request = directive ? Directive.Trim() : "";
        if (request.Length > 0 && history.Count > 0 && history[^1].Role == "user")
        {
            turns[^1] = Join(turns[^1], request);
            request = "";
        }

        for (var i = 0; i < history.Count; i++) result.Add(history[i] with { Content = turns[i] });
        if (request.Length > 0) result.Add(new PromptMessage("user", request));
        return result;
    }

    /// <summary>Text that <see cref="Arrange"/> adds to the history, for token counting.</summary>
    public string InsertedText() => AuthorsNote.Trim() + "\n" + FinalInstruction.Trim();

    /// <summary>
    /// Where a model writing a user's turn should stop: given the text a chat template puts after a user's message
    /// (such as the ChatML end-of-turn and assistant header, or Mistral's " [/INST]"), its first line and the whole of it.
    /// </summary>
    public static IReadOnlyList<string> TurnEnds(string afterUserText)
    {
        var whole = afterUserText.Trim();
        if (whole.Length == 0) return [];
        var firstLine = whole.Split('\n')[0].Trim();
        return new[] { firstLine, whole }.Where(s => s.Length > 0).Distinct().ToList();
    }

    /// <summary><paramref name="text"/> up to the first of <paramref name="stops"/>, or all of it when none appears.</summary>
    public static string CutAtFirst(string text, IEnumerable<string> stops)
    {
        var cut = text.Length;
        foreach (var stop in stops)
        {
            var at = text.IndexOf(stop, StringComparison.Ordinal);
            if (at >= 0 && at < cut) cut = at;
        }
        return text[..cut];
    }

    /// <summary>The newest user turn at or before <paramref name="index"/>, else the oldest one after it.</summary>
    private static int? UserTurnAt(IReadOnlyList<PromptMessage> history, int index)
    {
        for (var i = Math.Min(index, history.Count - 1); i >= 0; i--)
            if (history[i].Role == "user") return i;
        for (var i = Math.Max(0, index + 1); i < history.Count; i++)
            if (history[i].Role == "user") return i;
        return null;
    }

    private static string Join(string text, string addition) =>
        new StringBuilder(text.TrimEnd()).Append("\n\n").Append(addition).ToString();
}
