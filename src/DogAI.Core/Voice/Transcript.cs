using System.Text.RegularExpressions;

namespace DogAI.Core.Voice;

/// <summary>
/// Tidies what the speech recognition model heard. Whisper marks silence and noise in brackets ("[BLANK_AUDIO]",
/// "(wind blowing)") and, given silence or noise, tends to invent stock phrases it saw often in training
/// ("Thank you.", "Thanks for watching!"). Those are dropped rather than typed into the message box.
/// </summary>
public static partial class Transcript
{
    private static readonly HashSet<string> StockPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "you", "thank you", "thank you very much", "thanks", "thanks for watching", "thank you for watching",
        "thanks for watching and see you next time", "please subscribe", "subscribe", "bye", "okay", "so",
        "the end", "music", "applause", "silence", "subtitles by the amara.org community",
    };

    /// <summary>Joins the recognised pieces and removes noise markers. Empty when nothing was said.</summary>
    public static string Clean(IEnumerable<string> pieces)
    {
        var text = string.Join(" ", pieces.Select(p => p.Trim()).Where(p => p.Length > 0));
        text = Markers().Replace(text, " ");
        text = Spaces().Replace(text, " ").Trim();
        return text.Trim('-', ' ', '>').Trim();
    }

    /// <summary>
    /// True when the text is probably made up: a stock phrase from a short or quiet clip, or the model itself
    /// thought there was no speech.
    /// </summary>
    /// <param name="noSpeech">The model's own estimate (0–1) that the clip held no speech; null when unknown.</param>
    public static bool IsLikelyInvented(string text, TimeSpan audio, double? noSpeech)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (noSpeech is > 0.6) return true;
        var bare = Punctuation().Replace(text, "").Trim();
        if (bare.Length == 0) return true;
        return StockPhrases.Contains(bare) && (audio < TimeSpan.FromSeconds(3) || noSpeech is > 0.2);
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|♪+")]
    private static partial Regex Markers();

    [GeneratedRegex(@"[\p{P}\p{S}]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
