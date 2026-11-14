namespace DogAI.Core.Settings;

/// <summary>A Settings category: its id (the address <c>settings/&lt;id&gt;</c>), name and a few words on what is in it.</summary>
public sealed record SettingsCategory(string Id, string Title, string About);

/// <summary>One setting, findable by the search box: the element it scrolls to (<see cref="Anchor"/>) and extra words to match.</summary>
public sealed record SettingsEntry(string Category, string Anchor, string Title, string Words = "");

/// <summary>
/// The Settings screen's categories and what the search box finds in them. The screen shows one category at a time
/// and remembers the last one (<see cref="InterfaceMemory.SettingsCategory"/>). Every anchor is an element id on the
/// Settings page; when a setting moves, move its entry with it.
/// </summary>
public static class SettingsCatalog
{
    public const string Appearance = "appearance", Chats = "chats", Voice = "voice", Pictures = "pictures",
        Privacy = "privacy", Windows = "windows", Backup = "backup", About = "about";

    public static IReadOnlyList<SettingsCategory> Categories { get; } =
    [
        new(Appearance, "Appearance", "Look, light or dark"),
        new(Chats, "Chats and stories", "Speech colours, thinking"),
        new(Voice, "Voice", "Voices, speakers, microphone"),
        new(Pictures, "Pictures", "Checkpoint, quality, rating"),
        new(Privacy, "Privacy", "Password lock"),
        new(Windows, "Windows", "Tray icon, talk anywhere"),
        new(Backup, "Backup", "Back up and restore"),
        new(About, "About", "Version and folders"),
    ];

    public static IReadOnlyList<SettingsEntry> Entries { get; } =
    [
        new(Appearance, "look-label", "Look", "skin workbench fireside playhouse style layout"),
        new(Appearance, "theme-label", "Light or dark", "theme dark mode light mode windows colours"),
        new(Chats, "set-speech-colours", "Colour spoken lines and actions", "speech colour quotes dialogue roleplay group reading"),
        new(Chats, "set-story-thinking", "Let the model think in stories", "thinking reasoning story passage slow"),
        new(Voice, "set-voice", "Default voice", "voice text to speech tts kokoro piper speak"),
        new(Voice, "set-voice-speed", "Speaking speed", "voice speed rate fast slow"),
        new(Voice, "set-speakers", "Speakers", "audio output headphones sound device"),
        new(Voice, "set-microphone", "Microphone", "audio input mic recording device"),
        new(Voice, "set-auto-read", "Read replies aloud", "read aloud automatically speak replies"),
        new(Voice, "set-speech-language", "Language you speak", "language dictation speech recognition whisper"),
        new(Voice, "set-hands-free", "Hands-free listening", "hands free sensitivity noise quiet room"),
        new(Voice, "set-speech-model", "Speech recognition", "whisper dictation microphone try listening model"),
        new(Pictures, "pictures-checkpoint", "Checkpoint", "pictures images model draw sdxl"),
        new(Pictures, "pictures-quality", "Quality to start at", "pictures draft standard fine steps"),
        new(Pictures, "rating-label", "Rating pictures aim for", "pictures rating general sensitive questionable explicit tags"),
        new(Pictures, "pictures-show-prompt", "Show me the prompt before drawing", "pictures prompt review edit"),
        new(Privacy, "set-lock", "Lock with a password", "password encrypt encryption lock recovery key windows hello idle ctrl+l"),
        new(Windows, "set-tray", "Show DOG.ai in the notification area", "tray icon clock taskbar"),
        new(Windows, "set-close-to-tray", "Closing the window keeps DOG.ai running", "tray close minimise background"),
        new(Windows, "set-dictate-anywhere", "Talk into any program", "ctrl+alt+space dictation global key type anywhere"),
        new(Backup, "set-backup", "Back up and restore", "backup restore zip copy move pc"),
        new(About, "set-about", "About this install", "version data folder models folder portable network offline blocked"),
    ];

    /// <summary>A known category: the id itself when known, else Appearance.</summary>
    public static string Category(string? id) =>
        Categories.Any(c => c.Id == id) ? id! : Appearance;

    public static SettingsCategory Find(string? id) => Categories.First(c => c.Id == Category(id));

    /// <summary>
    /// The settings whose name, category or words have a word starting with every typed word (any case), best first:
    /// those found by their name before those found only by their other words; then in the order of the screen.
    /// </summary>
    public static IReadOnlyList<SettingsEntry> Search(string? query, int max = 12)
    {
        var typed = Words(query ?? "");
        if (typed.Count == 0) return [];
        return Entries
            .Select((e, order) => (e, order, title: Words(e.Title), all: Words($"{e.Title} {Find(e.Category).Title} {e.Words}")))
            .Where(x => typed.All(t => x.all.Any(w => w.StartsWith(t, StringComparison.Ordinal))))
            .OrderBy(x => typed.All(t => x.title.Any(w => w.StartsWith(t, StringComparison.Ordinal))) ? 0 : 1)
            .ThenBy(x => x.order)
            .Take(max)
            .Select(x => x.e)
            .ToList();
    }

    private static List<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', ',', '.', '-', '/', '(', ')', '?', '!', ':', ';', '\t', '\n', '\'', '’', '"'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();
}
