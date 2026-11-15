using System.Text.RegularExpressions;

namespace DogAI.Core.Search;

/// <summary>
/// The Settings and Search buttons go back (M14): clicking one while already there (any <c>settings/&lt;category&gt;</c>,
/// or Search) returns to the last screen that was neither; with nothing remembered it goes to the look's start page
/// (the empty address: Playhouse's home, a redirect to General in the other looks). <c>MainLayout</c> reports every
/// address with <see cref="Visited"/>; the buttons ask <see cref="Target"/> and <see cref="Title"/>. Memory only, never
/// saved: a restart starts afresh. Escape is not involved (dialogs and the drawer use it).
/// </summary>
public sealed class ReturnPlace
{
    public const string Settings = "settings", Search = "search";

    /// <summary>The last address that was neither Settings nor Search (without a leading slash), or null.</summary>
    public string? Remembered { get; private set; }

    /// <summary>Notes an address the app showed. Settings and Search are not remembered.</summary>
    public void Visited(string address)
    {
        var clean = Clean(address);
        if (IsIn(clean, Settings) || IsIn(clean, Search)) return;
        Remembered = clean;
    }

    /// <summary>True when <paramref name="address"/> is <paramref name="place"/> or a page inside it (<c>settings/voice</c>).</summary>
    public static bool IsIn(string address, string place)
    {
        var path = Path(Clean(address));
        return path.Equals(place, StringComparison.OrdinalIgnoreCase) || path.StartsWith(place + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the <paramref name="place"/> button goes from <paramref name="current"/>: back, or into the place.</summary>
    public string Target(string current, string place) => IsIn(current, place) ? Remembered ?? "" : place;

    /// <summary>The button's tooltip: "Back to …" while it would go back, else <paramref name="name"/>.</summary>
    public string Title(string current, string place, string name) =>
        !IsIn(current, place) ? name : Remembered is null ? "Back to the start page" : "Back to " + NameOf(Remembered);

    /// <summary>
    /// What an address is called after "Back to": a screen's name from <see cref="QuickJump.Screens"/> when it has one,
    /// else what kind of page it is ("the chat", "the character editor"). Never a chat's or character's own name: that
    /// would mean reading the database for a tooltip.
    /// </summary>
    public static string NameOf(string address)
    {
        var path = Path(Clean(address));
        if (path.Length == 0) return "Home"; // only Playhouse stays there; the other looks move on to General at once
        if (QuickJump.Screens.FirstOrDefault(s => s.Kind == JumpKind.Screen && s.Route.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } screen)
            return screen.Title;
        foreach (var (pattern, name) in Kinds)
            if (pattern.IsMatch(path)) return name;
        return "where you were";
    }

    private static readonly (Regex Pattern, string Name)[] Kinds =
    [
        (Shape(@"general/\d+"), "the chat"),
        (Shape(@"general/documents/\d+"), "Documents"),
        (Shape(@"characters/\d+"), "the character"),
        (Shape(@"characters/\d+/edit"), "the character editor"),
        (Shape(@"characters/\d+/lore"), "the lore editor"),
        (Shape(@"characters/chat/\d+"), "the chat"),
        (Shape(@"characters/group/\d+"), "the group chat"),
        (Shape(@"story/\d+"), "the story"),
        (Shape(@"story/\d+/setup"), "the story setup"),
        (Shape(@"models(/.*)?"), "Models"),
    ];

    private static Regex Shape(string pattern) => new("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Without a leading slash or a fragment (the query stays: <c>story/new?character=3</c> opens the same form).</summary>
    private static string Clean(string address)
    {
        var hash = address.IndexOf('#');
        return (hash >= 0 ? address[..hash] : address).TrimStart('/');
    }

    private static string Path(string address)
    {
        var query = address.IndexOf('?');
        return (query >= 0 ? address[..query] : address).TrimEnd('/');
    }
}
