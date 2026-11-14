using DogAI.Core.Text;

namespace DogAI.Core.Voice;

/// <summary>
/// The people of a story (to find who says a line, with <see cref="SpeakerGuess"/>) and their voices, by the same
/// number. A null voice, or a number past the voices, reads in the narrator's voice.
/// </summary>
public sealed class VoiceCast(IReadOnlyList<Person> people, IReadOnlyList<VoiceId?> voices)
{
    public VoiceCast(IReadOnlyList<string> names, IReadOnlyList<VoiceId?> voices) : this(names.Select(n => new Person(n)).ToList(), voices)
    {
    }

    public IReadOnlyList<Person> People { get; } = people;
    public IReadOnlyList<VoiceId?> Voices { get; } = voices;
    public bool IsEmpty => NewGuess().IsEmpty;

    /// <summary>A guess for one passage (it remembers earlier paragraphs, so each passage needs its own).</summary>
    public SpeakerGuess NewGuess() => new(People);
}

/// <summary>A piece of text to say. <see cref="Speaker"/> is a number in a <see cref="VoiceCast"/>, null for the narrator.
/// <see cref="Plain"/> pieces are already the words to say; the others are markdown.</summary>
public readonly record struct SpokenPiece(string Text, int? Speaker = null, bool Plain = false);

/// <summary>Cuts text into pieces to say while it is still being written. Feed it the whole text so far each time.</summary>
public interface ISpokenSplitter
{
    IReadOnlyList<SpokenPiece> Feed(string textSoFar);
    IReadOnlyList<SpokenPiece> Finish(string finalText);
}

/// <summary>One voice: sentence by sentence (see <see cref="SentenceSplitter"/>).</summary>
public sealed class SentencePieces : ISpokenSplitter
{
    private readonly SentenceSplitter _splitter = new();
    public IReadOnlyList<SpokenPiece> Feed(string textSoFar) => _splitter.Feed(textSoFar).Select(p => new SpokenPiece(p)).ToList();
    public IReadOnlyList<SpokenPiece> Finish(string finalText) => _splitter.Finish(finalText).Select(p => new SpokenPiece(p)).ToList();
}

/// <summary>
/// A story passage read by several voices. Waits for each paragraph to be finished (who says a line can depend on
/// the words after it), then cuts it into narration and spoken lines and gives each line to its speaker.
/// </summary>
public sealed class VoicedPieces(VoiceCast cast) : ISpokenSplitter
{
    private readonly SpeakerGuess _guess = cast.NewGuess();
    private int _consumed;

    public IReadOnlyList<SpokenPiece> Feed(string textSoFar)
    {
        if (textSoFar.Length < _consumed) return [];
        var end = textSoFar.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (end < _consumed) return [];
        var done = textSoFar[_consumed..end];
        _consumed = end + 2;
        return Split(done, cast, _guess);
    }

    public IReadOnlyList<SpokenPiece> Finish(string finalText)
    {
        var pieces = Feed(finalText).ToList();
        if (finalText.Length > _consumed) pieces.AddRange(Split(finalText[_consumed..], cast, _guess));
        _consumed = finalText.Length;
        return pieces;
    }

    /// <summary>
    /// The words of <paramref name="markdown"/> as pieces, each line given to the speaker <see cref="SpeakerGuess"/>
    /// finds for it. Narration and lines nobody can be pinned to are the narrator's. Neighbouring pieces with the same
    /// voice are joined, and long ones are cut at sentence ends so reading can start sooner.
    /// </summary>
    /// <param name="guess">The guess reading this passage, when it goes on from paragraphs given before.</param>
    public static IReadOnlyList<SpokenPiece> Split(string markdown, VoiceCast cast, SpeakerGuess? guess = null)
    {
        guess ??= cast.NewGuess();
        var pieces = new List<SpokenPiece>();
        foreach (var paragraph in SpeechText.Paragraphs(markdown))
        {
            var parts = Quotations.Split(paragraph);
            var speakers = guess.Assign(parts);
            var q = 0;
            var runs = new List<(string Text, int? Speaker)>();
            foreach (var part in parts)
            {
                int? speaker = part.Quoted ? speakers[q++] : null;
                if (speaker is { } s && (s >= cast.Voices.Count || cast.Voices[s] is null)) speaker = null;
                if (runs.Count > 0 && runs[^1].Speaker == speaker) runs[^1] = (runs[^1].Text + part.Text, speaker);
                else runs.Add((part.Text, speaker));
            }
            foreach (var (text, speaker) in runs)
            {
                if (!text.Any(char.IsLetterOrDigit)) continue;
                foreach (var sentence in new SentenceSplitter().Finish(text))
                    if (sentence.Trim().Length > 0) pieces.Add(new SpokenPiece(sentence.Trim(), speaker, Plain: true));
            }
        }
        return pieces;
    }
}
