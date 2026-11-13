namespace DogAI.Core.Text;

/// <summary>Which third-person pronoun a character goes by, as far as their card tells.</summary>
public enum Pronoun { Unknown, She, He }

/// <summary>Someone who may speak in a story: a card character, or a name from the story bible's Cast box.</summary>
/// <param name="Pronoun">Unknown when the card does not make it clear, and for names without a card.</param>
public sealed record Person(string Name, Pronoun Pronoun = Pronoun.Unknown);

public static class Pronouns
{
    private static readonly HashSet<string> SheWords = new(StringComparer.OrdinalIgnoreCase) { "she", "her", "hers", "herself" };
    private static readonly HashSet<string> HeWords = new(StringComparer.OrdinalIgnoreCase) { "he", "him", "his", "himself" };

    /// <summary>She or He for those words, else Unknown ("they", "I", "you"…).</summary>
    public static Pronoun Of(string word) => SheWords.Contains(word) ? Pronoun.She : HeWords.Contains(word) ? Pronoun.He : Pronoun.Unknown;

    /// <summary>
    /// The pronoun a card's own text uses for its character: one kind must be used at least twice and three times as
    /// often as the other. Cards also talk about {{user}}, which a clear majority outweighs; anything less clear is Unknown.
    /// </summary>
    public static Pronoun Guess(params string?[] texts)
    {
        int she = 0, he = 0;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var bare = word.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '“', '”', '’', '(', ')', '*', '_');
                switch (Of(bare))
                {
                    case Pronoun.She: she++; break;
                    case Pronoun.He: he++; break;
                }
            }
        }
        if (she >= 2 && she >= 3 * he) return Pronoun.She;
        if (he >= 2 && he >= 3 * she) return Pronoun.He;
        return Pronoun.Unknown;
    }
}

/// <summary>
/// Guesses who says each quotation in a passage, paragraph by paragraph, from the names written next to it:
/// a dialogue tag after the line ("…," said Mara / Mara whispered), else the sentence just before it (Mara turned.
/// "…"), else the speaker of the line before in the same paragraph when nothing in between brings in someone else.
/// A "she said" or "He shrugged." counts when exactly one person named nearby (in this paragraph or the one before,
/// or who spoke there) can be that "she" or "he", and their card says so. A line in which someone gives their own
/// name ("I am Suckbot," the object said.) is theirs unless its tag names someone else. A line that cannot be pinned to one person
/// gets no speaker: a wrong colour or voice is worse than the plain one. Names come from character cards, which are
/// untrusted, so they are matched as plain text, never as patterns. One instance reads one passage, in order.
/// </summary>
public sealed class SpeakerGuess
{
    /// <summary>A word this short or shorter is not used on its own as a name (a first name taken from a full name).</summary>
    private const int ShortestFirstName = 3;

    /// <summary>A name counts as the subject of a tag or sentence only within its first few words.</summary>
    private const int SubjectWords = 3;

    private static readonly HashSet<string> PronounWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "he", "she", "they", "i", "we", "you", "it", "his", "her", "their", "my", "our", "your", "someone", "somebody", "everyone",
    };

    /// <summary>A name after one of these words is who is spoken to or about, not who speaks ("she said to Mara").</summary>
    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "at", "with", "toward", "towards", "for", "from", "of", "on", "beside", "behind", "past", "about", "and", "than", "like",
    };

    private readonly List<(string Alias, int Speaker)> _aliases = [];
    private readonly List<Pronoun> _pronouns = [];

    // What the paragraph before said, for "she said" at the start of the next one.
    private string _previousNarration = "";
    private List<int> _previousSpeakers = [];

    /// <param name="names">The characters' names, by speaker number. Empty or one-letter names are ignored.</param>
    public SpeakerGuess(IReadOnlyList<string> names) : this(names.Select(n => new Person(n)).ToList())
    {
    }

    /// <param name="people">Who may speak, by speaker number. Empty or one-letter names are ignored.</param>
    public SpeakerGuess(IReadOnlyList<Person> people)
    {
        var claimed = new Dictionary<string, int>(StringComparer.Ordinal);
        void Claim(string alias, int speaker)
        {
            if (claimed.TryGetValue(alias, out var other)) claimed[alias] = other == speaker ? speaker : -1;
            else claimed[alias] = speaker;
        }
        for (var s = 0; s < people.Count; s++)
        {
            _pronouns.Add(people[s].Pronoun);
            var name = (people[s].Name ?? "").Trim();
            if (name.Length < 2) continue;
            Claim(name, s);
            var first = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (first.Length >= ShortestFirstName && first != name) Claim(first, s);
        }
        // A first name two characters share tells nothing about which one speaks.
        foreach (var (alias, speaker) in claimed)
            if (speaker >= 0) _aliases.Add((alias, speaker));
        _aliases.Sort((a, b) => b.Alias.Length.CompareTo(a.Alias.Length));
    }

    public bool IsEmpty => _aliases.Count == 0;

    /// <summary>
    /// The speaker number of each quotation in the next paragraph of the passage, in order; null where unsure.
    /// Paragraphs must be given in order: a "she said" may refer to someone named in the paragraph before.
    /// </summary>
    public IReadOnlyList<int?> Assign(IReadOnlyList<QuotePart> parts)
    {
        var speakers = new List<int?>();
        var narration = new System.Text.StringBuilder();
        int? last = null;
        for (var k = 0; k < parts.Count; k++)
        {
            if (!parts[k].Quoted)
            {
                narration.Append(parts[k].Text).Append(' ');
                continue;
            }
            var before = k > 0 && !parts[k - 1].Quoted ? parts[k - 1].Text : "";
            var after = k + 1 < parts.Count && !parts[k + 1].Quoted ? parts[k + 1].Text : "";
            // Who is "nearby" for a pronoun: everyone named in the narration so far (this paragraph and the one before,
            // quotations left out, since a name inside a line is usually who is spoken to) and who has spoken there.
            var window = _previousNarration + " " + narration;
            var spoke = _previousSpeakers.Concat(speakers.OfType<int>());
            var who = Guess(parts[k].Text, before, after, last, speakers.Count > 0, window, spoke);
            speakers.Add(who);
            last = who;
        }
        // A paragraph with no narration of its own ("…" alone) keeps the context of the one before.
        var own = narration.ToString();
        if (own.Any(char.IsLetter))
        {
            _previousNarration = own;
            _previousSpeakers = speakers.OfType<int>().ToList();
        }
        else
        {
            _previousSpeakers = [.. _previousSpeakers, .. speakers.OfType<int>()];
        }
        return speakers;
    }

    private int? Guess(string quote, string before, string after, int? last, bool hadLine, string window, IEnumerable<int> spoke)
    {
        // Someone saying who they are ("I am Suckbot," the object said.) is that person, unless a tag names another.
        var introduced = Introduces(quote);
        // A tag after the line: "…," said Mara. / "…!" Mara shouted. / "…," she said.
        var tag = after.TrimStart();
        Pronoun? newSentence = null;
        if (tag.Length > 0 && char.IsLetter(tag[0]))
        {
            var clause = FirstClause(tag);
            var subject = Subject(clause, out var pronoun);
            if (subject is not null) return subject;
            if (introduced is not null) return introduced;
            // A lower-case tag belongs to the line: "she said", or a tag without a name ("came a voice").
            if (char.IsLower(tag[0])) return pronoun is { } p ? Resolve(p, window, spoke, Mentions(clause)) : null;
            // A new sentence ("She smiled.") is not a tag: the action before the line comes first.
            newSentence = pronoun;
        }
        if (introduced is not null) return introduced;
        // An action just before the line: Mara turned. "…" / She turned. "…"
        var beat = LastSentence(before);
        if (beat.Length > 0)
        {
            var subject = Subject(beat, out var pronoun);
            if (subject is not null) return subject;
            if (pronoun is { } p) return Resolve(p, window, spoke, Mentions(beat));
        }
        // The same speaker carries on when nothing in between brings in anyone else.
        if (hadLine && last is not null && Mentions(before).All(s => s == last)) return last;
        // Only an action after the line is left: "…" She turned away.
        if (newSentence is { } q && beat.Length == 0) return Resolve(q, window, spoke, []);
        return null;
    }

    /// <summary>
    /// The one person nearby that <paramref name="pronoun"/> can mean: named in <paramref name="window"/> or among
    /// those who <paramref name="spoke"/>, not named in the tag itself ("she said to Mara"), and whose card uses that
    /// pronoun. Someone nearby whose pronoun is not known could be meant too, so they make it unsure.
    /// </summary>
    private int? Resolve(Pronoun pronoun, string window, IEnumerable<int> spoke, IEnumerable<int> exclude)
    {
        if (pronoun == Pronoun.Unknown) return null;
        var excluded = exclude.ToHashSet();
        var candidates = Mentions(window).Concat(spoke).Distinct()
            .Where(s => !excluded.Contains(s) && (_pronouns[s] == pronoun || _pronouns[s] == Pronoun.Unknown))
            .ToList();
        return candidates.Count == 1 && _pronouns[candidates[0]] == pronoun ? candidates[0] : null;
    }

    /// <summary>
    /// The speaker named as the subject of <paramref name="text"/>: within its first words, not after "to" or "at".
    /// When the text starts with a pronoun instead, <paramref name="pronoun"/> says which (Unknown for "they", "I"…).
    /// </summary>
    private int? Subject(string text, out Pronoun? pronoun)
    {
        pronoun = null;
        var words = Words(text);
        if (words.Count == 0) return null;
        var first = text.Substring(words[0].Start, words[0].Length);
        if (PronounWords.Contains(first) && Match(text, words[0].Start) is null)
        {
            pronoun = Pronouns.Of(first);
            return null;
        }
        for (var w = 0; w < Math.Min(SubjectWords, words.Count); w++)
        {
            if (Match(text, words[w].Start) is not { } hit) continue;
            if (w > 0 && Prepositions.Contains(text.Substring(words[w - 1].Start, words[w - 1].Length))) continue;
            return hit;
        }
        return null;
    }

    /// <summary>What a speaker says right before giving their own name.</summary>
    private static readonly string[] Introductions = ["I am ", "I'm ", "I’m ", "I&#39;m ", "my name is ", "my name's ", "my name’s ", "call me "];

    /// <summary>
    /// The one person a line introduces as its speaker: "I am Suckbot", "I'm Mara", "My name is Ben", "Call me Ben".
    /// Not "I am Ben's sister", and nobody when the line gives two different names this way.
    /// </summary>
    private int? Introduces(string quote)
    {
        int? found = null;
        foreach (var phrase in Introductions)
        {
            for (var i = quote.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = quote.IndexOf(phrase, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                // Whole words only: not "Miami am ".
                if (i > 0 && char.IsLetterOrDigit(quote[i - 1])) continue;
                var at = i + phrase.Length;
                if (at >= quote.Length || MatchAlias(quote, at) is not { } hit) continue;
                var (who, end) = (hit.Speaker, at + hit.Length);
                if (end + 1 < quote.Length && quote[end] is '\'' or '’' && quote[end + 1] is 's' or 'S') continue;
                if (found is not null && found != who) return null;
                found = who;
            }
        }
        return found;
    }

    /// <summary>Everyone named in <paramref name="text"/>.</summary>
    private IEnumerable<int> Mentions(string text)
    {
        foreach (var (start, _) in Words(text))
            if (Match(text, start) is { } speaker) yield return speaker;
    }

    /// <summary>The speaker whose name starts at <paramref name="i"/> and ends at a word boundary, if any.</summary>
    private int? Match(string text, int i) => MatchAlias(text, i)?.Speaker;

    /// <summary>The speaker whose name starts at <paramref name="i"/>, and how long that name is there.</summary>
    private (int Speaker, int Length)? MatchAlias(string text, int i)
    {
        foreach (var (alias, speaker) in _aliases)
        {
            if (string.CompareOrdinal(text, i, alias, 0, alias.Length) != 0) continue;
            var end = i + alias.Length;
            if (end < text.Length && char.IsLetterOrDigit(text[end])) continue;
            return (speaker, alias.Length);
        }
        return null;
    }

    private static List<(int Start, int Length)> Words(string text)
    {
        var words = new List<(int, int)>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || (text[i] is '\'' or '’' or '-' && i + 1 < text.Length && char.IsLetter(text[i + 1])))) i++;
            if (i > start) words.Add((start, i - start));
        }
        return words;
    }

    private static bool EndsSentence(char c) => c is '.' or '!' or '?' or '…' or ';' or ':' or '\n';

    /// <summary>A tag up to the end of its clause: "said Mara, turning away." → "said Mara".</summary>
    private static string FirstClause(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (EndsSentence(text[i]) || text[i] is ',' or '—' or '–') return text[..i];
        return text;
    }

    /// <summary>The last sentence of <paramref name="text"/>, finished or not: "She sat. Mara turned to him:" → "Mara turned to him:".</summary>
    private static string LastSentence(string text)
    {
        var end = text.Length;
        while (end > 0 && (char.IsWhiteSpace(text[end - 1]) || EndsSentence(text[end - 1]) || text[end - 1] is ',' or '—' or '–' or '*' or '_')) end--;
        var start = end;
        while (start > 0 && !EndsSentence(text[start - 1])) start--;
        return text[start..end].Trim();
    }
}
