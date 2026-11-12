namespace DogAI.Core.Voice;

/// <summary>
/// Kokoro names its voices like "af_heart": the first letter is the language, the second is f(emale) or m(ale),
/// and the rest is the voice's own name. The names themselves are read from the model file.
/// </summary>
public static class KokoroVoices
{
    /// <summary>The voice used when a Kokoro pack is the default and nothing else was picked.</summary>
    public const string Preferred = "af_heart";

    private static readonly Dictionary<char, (string Label, string Lang)> Languages = new()
    {
        ['a'] = ("American English", "en-us"),
        ['b'] = ("British English", "en"), // espeak-ng's plain "en" is British; it has no "en-gb"
        ['e'] = ("Spanish", "es"),
        ['f'] = ("French", "fr"),
        ['h'] = ("Hindi", "hi"),
        ['i'] = ("Italian", "it"),
        ['j'] = ("Japanese", "ja"),
        ['p'] = ("Brazilian Portuguese", "pt-br"),
        ['z'] = ("Mandarin Chinese", "cmn"),
    };

    /// <summary>"af_heart" → "Heart (American English, female)".</summary>
    public static string Describe(string name)
    {
        var (label, _, gender, own) = Split(name);
        if (own is null) return name;
        var details = string.Join(", ", new[] { label, gender }.Where(s => s is not null));
        return details.Length == 0 ? own : $"{own} ({details})";
    }

    /// <summary>The espeak-ng language this voice pronounces with ("en-us", "en" for British, "cmn"…), or null when unknown.
    /// It must be a name in the pack's espeak-ng-data/lang folder, or nothing can be said.</summary>
    public static string? Language(string name) => Split(name).Lang;

    /// <summary>The language name shown in pickers ("American English"), or null when unknown.</summary>
    public static string? LanguageLabel(string name) => Split(name).Label;

    private static (string? Label, string? Lang, string? Gender, string? Own) Split(string name)
    {
        var underscore = name.IndexOf('_');
        if (underscore != 2 || name.Length <= 3) return (null, null, null, null);
        var own = name[3..];
        own = char.ToUpperInvariant(own[0]) + own[1..];
        var gender = name[1] switch { 'f' => "female", 'm' => "male", _ => null };
        return Languages.TryGetValue(name[0], out var lang) ? (lang.Label, lang.Lang, gender, own) : (null, null, gender, own);
    }
}
