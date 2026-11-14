using DogAI.Core.Stories;

namespace DogAI.Core.Characters;

/// <summary>
/// The tabs of the character editor and the story editor (M12). Every tab stays in one form, only hidden, so
/// switching never loses typing; the Save bar stays visible below them. A problem found on saving opens the tab its
/// field is on (<see cref="ForCharacterProblem"/>).
/// </summary>
public static class EditorTabs
{
    // Character editor.
    public const string Basics = "basics", Personality = "personality", Messages = "messages", LooksAndVoice = "looks", Advanced = "advanced";

    public static readonly IReadOnlyList<(string Id, string Title)> Character =
    [
        (Basics, "Basics"),
        (Personality, "Personality and scenario"),
        (Messages, "Messages"),
        (LooksAndVoice, "Looks and voice"),
        (Advanced, "Advanced"),
    ];

    // Story editor. "plot" is the outline of a written story or the rules of an adventure: the same tab, so changing
    // the kind of story never takes away the tab that is open.
    public const string Cast = "cast", WorldAndStyle = "world", Plot = "plot";

    public static IReadOnlyList<(string Id, string Title)> Story(StoryKind kind) =>
    [
        (Basics, "Basics"),
        (Cast, "Cast"),
        (WorldAndStyle, "World and style"),
        (Plot, kind == StoryKind.Authored ? "Outline" : "Adventure rules"),
    ];

    /// <summary>The problems saving a character can find, by the field they are about.</summary>
    public enum CharacterProblem { NoName, Picture }

    /// <summary>The tab holding the field a problem is about, so saving can open it.</summary>
    public static string ForCharacterProblem(CharacterProblem problem) => problem switch
    {
        CharacterProblem.NoName or CharacterProblem.Picture => Basics,
        _ => Basics,
    };

    /// <summary>A known tab id of the list, else the first tab (an address or a remembered tab can be stale).</summary>
    public static string Known(IReadOnlyList<(string Id, string Title)> tabs, string? id) =>
        tabs.Any(t => t.Id == id) ? id! : tabs[0].Id;
}
