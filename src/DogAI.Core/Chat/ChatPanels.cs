using DogAI.Core.Settings;

namespace DogAI.Core.Chat;

/// <summary>
/// The side panels of General, character chats, group chats and stories (M12): each one folds away to a one-line
/// summary, and the whole side panel can be hidden. Both are remembered in <see cref="InterfaceMemory"/>, per place,
/// so closing Memory in a story does not close it in chats. This class holds the ids, which panels start open, and
/// the summary wording, so the screens stay consistent and the rules can be tested.
/// </summary>
public static class ChatPanels
{
    public const string General = "general", Character = "character", Group = "group", Story = "story";

    public static readonly IReadOnlyList<string> Places = [General, Character, Group, Story];

    // Panel names (the part after the place in an id).
    public const string Assistant = "assistant", Documents = "documents", Style = "style", Instructions = "instructions",
        ThisChat = "chat", Persona = "persona", AuthorsNote = "note", Memory = "memory", Members = "members", TurnOrder = "order",
        StoryInfo = "about", State = "state", Outline = "outline", SaveCopy = "export";

    /// <summary>Which panels each place has, in order, and whether each starts open. The ones used every turn start open; the rest show their summary.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Panel, bool Open)>> Layout =
        new Dictionary<string, IReadOnlyList<(string, bool)>>
        {
            [General] = [(Assistant, false), (Documents, false), (Style, true), (Instructions, true), (ThisChat, false)],
            [Character] = [(Persona, true), (Style, true), (Instructions, false), (AuthorsNote, false), (Memory, false), (Documents, false), (ThisChat, true)],
            [Group] = [(Members, true), (TurnOrder, false), (Persona, true), (Style, true), (Instructions, false), (AuthorsNote, false), (Memory, false), (Documents, false), (ThisChat, true)],
            [Story] = [(StoryInfo, true), (State, true), (Outline, true), (Style, false), (Memory, false), (Documents, false), (SaveCopy, false), (ThisChat, false)],
        };

    /// <summary>The remembered id of one panel, such as "character-memory". Its heading button is "#fold-" + this.</summary>
    public static string Id(string place, string panel)
    {
        if (!Layout.TryGetValue(place, out var panels) || !panels.Any(p => p.Panel == panel))
            throw new ArgumentException($"There is no {panel} panel in {place}.", nameof(panel));
        return $"{place}-{panel}";
    }

    /// <summary>Whether a panel starts open when the owner has not opened or closed it.</summary>
    public static bool OpensByDefault(string place, string panel) =>
        Layout.TryGetValue(place, out var panels) && panels.FirstOrDefault(p => p.Panel == panel) is { Panel: not null } found ? found.Open : true;

    /// <summary>The remembered id saying whether a place's whole side panel is shown (it is, by default).</summary>
    public static string AsideId(string place) =>
        Places.Contains(place) ? $"aside-{place}" : throw new ArgumentException($"Unknown place {place}.", nameof(place));

    /// <summary>Longest summary text before it is cut; the side panel is 250 to 290 pixels wide and cuts it again with "…".</summary>
    public const int SummaryLength = 60;

    /// <summary>A text box's summary: its first line, or "None" when it is empty.</summary>
    public static string TextSummary(string? text)
    {
        var line = (text ?? "").Trim().Split('\n', 2)[0].Trim();
        if (line.Length == 0) return "None";
        return line.Length <= SummaryLength ? line : line[..(SummaryLength - 1)].TrimEnd() + "…";
    }

    /// <summary>The documents panel: files added to this chat and shared collections searched.</summary>
    public static string DocumentsSummary(int files, int collections)
    {
        if (files == 0 && collections == 0) return "None";
        var parts = new List<string>();
        if (files > 0) parts.Add(Count(files, "file"));
        if (collections > 0) parts.Add(Count(collections, "collection"));
        return string.Join(", ", parts);
    }

    /// <summary>The memory panel: off, on, and whether a summary has been written.</summary>
    public static string MemorySummary(bool on, bool hasSummary) =>
        !on ? "Off" : hasSummary ? "On · summary written" : "On";

    /// <summary>"Chapter 2 of 5" for a written story's outline; null when there is no outline.</summary>
    public static string? OutlineSummary(int current, int chapters) =>
        chapters <= 0 ? null : $"Chapter {Math.Clamp(current, 0, chapters - 1) + 1} of {chapters}";

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
