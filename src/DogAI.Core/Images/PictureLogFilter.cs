using System.Text.RegularExpressions;

namespace DogAI.Core.Images;

/// <summary>
/// Decides which of the picture engine's output lines may go to DOG.ai's log. A prompt must never get there, whatever a
/// future engine version prints, so a line has to pass two separate checks once any prompt has been sent:
/// <list type="number">
/// <item><b>Its kind.</b> Only status lines pass: those the engine marks as information, a warning or an error
/// (<c>[I]</c>, <c>[W]</c>, <c>[E]</c>, or the words spelled out), and graphics-library messages (<c>ggml_…</c>,
/// <c>CUDA…</c>: out of memory and the like). Detail lines (<c>[D]</c>, where the engine prints prompts and tokens),
/// progress bars and anything unrecognised are dropped.</item>
/// <item><b>Its words.</b> A status line that shares a word with a prompt sent in the last ten minutes is held back, also
/// when the word sits inside a longer one or is cut short. The engine forgets a finished picture after a minute, so it
/// has nothing older to print.</item>
/// </list>
/// Before the first prompt of a run every line passes: the engine cannot print what it was never given. Holding back too
/// much only makes the log shorter; <see cref="TakeHeldBack"/> says how many status lines were held back for their
/// words, never what they said.
/// </summary>
public sealed partial class PictureLogFilter(Func<DateTime>? clock = null)
{
    /// <summary>How long a prompt's words are remembered after it was last sent. The engine keeps a finished picture for one minute.</summary>
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);

    /// <summary>Words of two or three plain letters match only as whole words; shorter ones are not matched at all.</summary>
    private const int LongWord = 4;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly object _lock = new();
    private readonly Dictionary<string, Sent> _sent = [];
    private bool _anySent;
    private int _heldBack;

    private sealed record Sent(HashSet<string> Short, string[] Long, DateTime At);

    /// <summary>Call before a request goes to the engine, so no line about it can arrive first.</summary>
    public void Sending(ImageRequest request) => Sending(request.Prompt, request.NegativePrompt);

    public void Sending(params string[] texts)
    {
        var words = texts.SelectMany(Words).Distinct().ToList();
        var entry = new Sent(
            [.. words.Where(w => !IsLong(w) && w.Length >= 2)],
            [.. words.Where(IsLong)],
            _clock());
        lock (_lock)
        {
            _anySent = true;
            // The same prompts again (a redraw, another seed) only renew the time.
            _sent[string.Join('\n', texts)] = entry;
        }
    }

    /// <summary>True when the line may be written to the log.</summary>
    public bool Allows(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        lock (_lock)
        {
            if (!_anySent) return true;
            if (!IsStatusLine(line)) return false;
            if (!SharesWords(line)) return true;
            _heldBack++;
            return false;
        }
    }

    /// <summary>How many status lines were held back for sharing a word with a prompt since the last call.</summary>
    public int TakeHeldBack()
    {
        lock (_lock)
        {
            var count = _heldBack;
            _heldBack = 0;
            return count;
        }
    }

    private static bool IsStatusLine(string line)
    {
        var level = LevelTag().Match(line);
        if (level.Success)
            return level.Groups[1].Value.ToUpperInvariant() is "I" or "INFO" or "W" or "WARN" or "WARNING" or "E" or "ERR" or "ERROR";
        return LibraryLine().IsMatch(line);
    }

    private bool SharesWords(string line)
    {
        var now = _clock();
        foreach (var key in _sent.Where(s => now - s.Value.At > Remember).Select(s => s.Key).ToList())
            _sent.Remove(key);
        if (_sent.Count == 0) return false;

        var lower = line.ToLowerInvariant();
        var lineWords = Words(lower).ToList();
        foreach (var sent in _sent.Values)
        {
            if (lineWords.Any(sent.Short.Contains)) return true;
            foreach (var word in sent.Long)
            {
                if (lower.Contains(word, StringComparison.Ordinal)) return true;
                // A prompt word cut short by the engine ("zqxmark…") still gives the line away.
                if (lineWords.Any(w => w.Length >= LongWord && word.StartsWith(w, StringComparison.Ordinal))) return true;
            }
        }
        return false;
    }

    /// <summary>Words are matched inside longer text when they have four characters or more, or any letter outside plain ASCII
    /// (languages written without spaces between words).</summary>
    private static bool IsLong(string word) => word.Length >= LongWord || word.Any(c => c > 0x7F);

    /// <summary>The runs of letters and digits in a text, in small letters.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0) start = i;
            else if (!inWord && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    [GeneratedRegex(@"^\s*\[\s*([A-Za-z]+)\s*\]")]
    private static partial Regex LevelTag();

    [GeneratedRegex(@"^\s*(ggml_|gguf_|load_backend:|Device \d+:|cuda|CUDA|cublas|nvidia|NVIDIA)", RegexOptions.CultureInvariant)]
    private static partial Regex LibraryLine();
}
