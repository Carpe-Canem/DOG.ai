namespace DogAI.Core.Voice;

/// <summary>
/// Cuts a reply into pieces to speak while it is still being written. Feed it the whole text so far each time;
/// it hands back the sentences completed since the last call. A piece ends at a sentence end followed by a space,
/// or at a line break; text inside an unfinished code block is held back. After the first piece (said as soon as
/// possible) short sentences are joined, since a voice sounds better and works faster on longer pieces.
/// </summary>
public sealed class SentenceSplitter
{
    /// <summary>Pieces after the first are at least this long, unless a paragraph ends first.</summary>
    public const int JoinBelow = 80;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "st", "jr", "sr", "prof", "vs", "etc", "e.g", "i.e", "no", "fig", "approx", "mt", "ft", "lt", "col", "gen", "sgt", "capt",
    };

    private int _consumed;
    private string _pending = "";
    private bool _first = true;

    /// <summary>The pieces of markdown completed in <paramref name="textSoFar"/> since the last call.</summary>
    public IReadOnlyList<string> Feed(string textSoFar)
    {
        var pieces = new List<string>();
        if (textSoFar.Length < _consumed) return pieces; // the text was replaced; keep going from where we were
        var start = _consumed;
        var inFence = InFence(textSoFar, start);
        for (var i = start; i < textSoFar.Length; i++)
        {
            if (FenceAt(textSoFar, i))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            var c = textSoFar[i];
            var paragraph = c == '\n';
            if (!paragraph && !EndsSentenceAt(textSoFar, i)) continue;

            var piece = textSoFar[_consumed..(i + 1)];
            _consumed = i + 1;
            Add(pieces, piece, paragraph && IsParagraphEnd(textSoFar, i));
        }
        return pieces;
    }

    /// <summary>Whatever is left once the reply is finished.</summary>
    public IReadOnlyList<string> Finish(string finalText)
    {
        var pieces = Feed(finalText).ToList();
        var rest = finalText.Length > _consumed ? finalText[_consumed..] : "";
        _consumed = finalText.Length;
        var last = _pending + rest;
        _pending = "";
        if (!string.IsNullOrWhiteSpace(last)) pieces.Add(last);
        return pieces;
    }

    private void Add(List<string> pieces, string piece, bool paragraphEnd)
    {
        _pending += piece;
        if (string.IsNullOrWhiteSpace(_pending))
        {
            _pending = "";
            return;
        }
        if (_first || paragraphEnd || _pending.Trim().Length >= JoinBelow)
        {
            pieces.Add(_pending);
            _pending = "";
            _first = false;
        }
    }

    private static bool EndsSentenceAt(string text, int i)
    {
        var c = text[i];
        if (c is not ('.' or '!' or '?' or '…' or '。' or '！' or '？')) return false;
        // Closing quotes and brackets belong to the sentence they end.
        var next = i + 1;
        while (next < text.Length && text[next] is '"' or '\'' or ')' or '”' or '’' or '*' or '_') next++;
        if (next >= text.Length) return false; // wait: the next character may still turn this into "3.5" or "...".
        if (!char.IsWhiteSpace(text[next])) return c is '。' or '！' or '？';
        if (c != '.') return true;
        if (i > 0 && text[i - 1] == '.') return true; // the end of "..."
        var word = WordBefore(text, i);
        if (word.Length == 0) return false;
        if (word.Length == 1 && char.IsUpper(word[0])) return false; // an initial: "J. R. R. Tolkien"
        if (word.All(char.IsDigit) && AtLineStart(text, i - word.Length)) return false; // "1. " starting a list item
        return !Abbreviations.Contains(word);
    }

    private static string WordBefore(string text, int dot)
    {
        var start = dot;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || (text[start - 1] == '.' && start - 1 > 0 && char.IsLetter(text[start - 2]))))
            start--;
        return text[start..dot];
    }

    private static bool IsParagraphEnd(string text, int newline) =>
        (newline > 0 && text[newline - 1] == '\n') || (newline + 1 < text.Length && text[newline + 1] == '\n') || LooksLikeBlockStart(text, newline + 1);

    /// <summary>A heading or list item after a line break ends the piece before it, like a paragraph does.</summary>
    private static bool LooksLikeBlockStart(string text, int i)
    {
        while (i < text.Length && text[i] == ' ') i++;
        if (i >= text.Length) return false;
        return text[i] is '#' or '-' or '*' or '>' || (char.IsDigit(text[i]) && text.IndexOf(". ", i, Math.Min(4, text.Length - i), StringComparison.Ordinal) > 0);
    }

    private static bool AtLineStart(string text, int i)
    {
        var j = i - 1;
        while (j >= 0 && text[j] is ' ' or '\t') j--;
        return j < 0 || text[j] == '\n';
    }

    /// <summary>True at the first character of a line that opens or closes a code block.</summary>
    private static bool FenceAt(string text, int i) =>
        text[i] is '`' or '~' && AtLineStart(text, i) &&
        (string.CompareOrdinal(text, i, "```", 0, 3) == 0 || string.CompareOrdinal(text, i, "~~~", 0, 3) == 0);

    private static bool InFence(string text, int end)
    {
        var inside = false;
        for (var i = 0; i < end; i++)
            if (FenceAt(text, i)) inside = !inside;
        return inside;
    }
}
