using System.Reflection;
using System.Text;

namespace DogAI.Core.Images;

/// <summary>What a tag check did, for showing the owner ("Show me the prompt") and for tests.</summary>
/// <param name="Corrected">Tags that were not real but close to one: what was written and what is used.</param>
/// <param name="Dropped">Tags that matched nothing and were left out.</param>
public sealed record TagNotes(IReadOnlyList<(string Written, string Used)> Corrected, IReadOnlyList<string> Dropped)
{
    public static TagNotes None { get; } = new([], []);

    public TagNotes Plus(TagNotes other) => new([.. Corrected, .. other.Corrected], [.. Dropped, .. other.Dropped]);
}

/// <summary>
/// A list of real tags, shipped inside DOG.ai (nothing is looked up online): the general tags of Danbooru and e621
/// and e621's species tags. Tag-style checkpoints (Illustrious, NoobAI, Pony) were trained on these exact words, so a
/// tag the chat model made up does nothing or does the wrong thing. Every tag the model writes is put through
/// <see cref="Resolve"/>: a real tag is kept, a near miss becomes the real tag, anything else is dropped. Artist,
/// character, series and meta tags are left out on purpose, so the model cannot bring in someone else's character or
/// style; what the owner types in a card's Looks is never checked. Where the list comes from and its licence:
/// <c>Images/Tags/README.md</c>; it is rebuilt with <c>tools/make-taglist.ps1</c>.
/// <para>
/// Some tags are left out of the bundled list by the owner's choice (2026-11-14), and known only by fingerprint
/// (<see cref="TagFingerprints"/>): such a tag, and anything written that contains one, is dropped, never corrected into
/// a tag nearby. The owner may add the full list in Models › On this PC › Pictures › Tags (<see cref="With"/>); with it, every tag in it
/// is real again and nothing is dropped for its fingerprint.
/// </para>
/// </summary>
/// <remarks>
/// Tags are compared in the form prompts use: lower case, spaces instead of underscores ("silver hair"). Emoticon tags
/// of up to three characters keep their underscore ("^_^").
/// </remarks>
public sealed class TagList
{
    public const string ResourceName = "DogAI.Core.Images.Tags.tag-list.txt";

    private readonly Dictionary<string, int> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<string>> _byLength = [];
    private TagFingerprints _excluded = TagFingerprints.None;

    private static readonly Lazy<TagList> BundledList = new(LoadBundled);
    private static volatile TagList? _extra;

    /// <summary>Words that start a phrase without being part of the tag ("wearing a red dress" is "red dress").</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "wearing", "wears", "with", "in", "has", "having", "her", "his", "their", "its", "some", "is", "of", "and",
    };

    /// <summary>A describing word in front of these turns the meaning round, so the last word alone would be wrong ("no shoes").</summary>
    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "no", "not", "without", "non", "missing", "removed", "removing", "off",
    };

    /// <summary>Words that show a written "tag" is really a sentence, which is dropped whole.</summary>
    private static readonly HashSet<string> Sentence = new(StringComparer.Ordinal)
    {
        "she", "he", "they", "it", "i", "you", "we", "who", "that", "which", "is", "are", "was", "were", "be", "been", "as", "while",
        "because", "when", "to", "from", "by", "for", "at", "on", "into", "over", "under", "after", "before", "about", "like",
    };

    /// <summary>A near miss may not swap one of these for another ("grey cloak" must never become "green cloak").</summary>
    private static readonly HashSet<string> Colours = new(StringComparer.Ordinal)
    {
        "red", "blue", "green", "yellow", "orange", "purple", "pink", "brown", "black", "white", "grey", "gray", "silver", "gold", "golden",
        "blonde", "blond", "aqua", "teal", "cyan", "violet", "tan", "dark", "light", "pale", "long", "short", "medium", "large", "small",
        "big", "huge", "left", "right", "one", "two", "three", "single", "multiple", "male", "female", "day", "night",
    };

    /// <summary>
    /// Other spellings in the source data that are wrong for pictures, found by reading what a chat model wrote:
    /// "lean" (a build) is listed as another spelling of "leaning" (a pose); "orange" and "oranges" (most often the colour) of
    /// "orange (fruit)" (seen in M16 step 4: autumn leaves became fruit); "canopy" (of a tree or a bed) of
    /// "canopy (aircraft)" (seen in M16 step 6: a fox under a tree got an aircraft's canopy). They are not used.
    /// </summary>
    private static readonly HashSet<string> NotASpelling = new(StringComparer.Ordinal) { "lean", "orange", "oranges", "canopy" };

    public static TagList Empty { get; } = new();

    /// <summary>The list shipped with DOG.ai. Empty when this build has none: tags are then used as written.</summary>
    public static TagList Bundled => BundledList.Value;

    /// <summary>The list pictures are checked against: the bundled one, or the bundled one with the owner's full list added.</summary>
    public static TagList Current => _extra ?? Bundled;

    /// <summary>Puts the bundled list merged with the owner's full list into use (<see cref="Current"/>), or goes back to the bundled list with null.</summary>
    public static void UseExtra(TagList? merged) => _extra = merged;

    /// <summary>The tags left out of this list on purpose, which <see cref="Resolve"/> drops.</summary>
    public TagFingerprints Excluded => _excluded;

    public int Count => _tags.Count;
    public bool IsEmpty => _tags.Count == 0;

    private static TagList LoadBundled()
    {
        using var stream = typeof(TagList).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return Empty;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var list = Read(reader);
        list._excluded = TagFingerprints.Bundled;
        return list;
    }

    /// <summary>
    /// Reads a list: one tag a line as Danbooru writes it, then a tab and how many posts have it, then optionally a tab
    /// and other spellings that mean the same tag, separated by spaces. Lines starting with # are comments.
    /// </summary>
    public static TagList Read(TextReader reader)
    {
        var list = new TagList();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            var tag = Normalise(parts[0]);
            if (tag.Length == 0) continue;
            var count = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
            if (!list._tags.TryAdd(tag, count)) continue;
            if (!list._byLength.TryGetValue(tag.Length, out var bucket)) list._byLength[tag.Length] = bucket = [];
            bucket.Add(tag);
            if (parts.Length > 2)
                foreach (var alias in parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (Normalise(alias) is { Length: > 0 } a && !NotASpelling.Contains(a)) list._aliases.TryAdd(a, tag);
        }
        // An alias never hides a real tag of the same spelling.
        foreach (var real in list._tags.Keys) list._aliases.Remove(real);
        return list;
    }

    /// <summary>A list made in code, for tests.</summary>
    public static TagList Of(params string[] tags) => Read(new StringReader(string.Join('\n', tags.Select(t => t.Contains('\t') ? t : t + "\t1000"))));

    /// <summary>The same list, dropping the tags these fingerprints stand for. For tests; the bundled list has its own.</summary>
    public TagList Excluding(TagFingerprints excluded)
    {
        var copy = Copy();
        copy._excluded = excluded;
        return copy;
    }

    /// <summary>
    /// This list with the tags of a full tag file added (Models › On this PC › Pictures › Tags). The result drops nothing for its
    /// fingerprint: the owner chose the full list.
    /// </summary>
    public TagList With(IEnumerable<TagRow> rows)
    {
        var merged = Copy();
        foreach (var row in rows)
        {
            var tag = Normalise(row.Name);
            if (tag.Length == 0) continue;
            if (merged._tags.TryAdd(tag, row.Posts))
            {
                if (!merged._byLength.TryGetValue(tag.Length, out var bucket)) merged._byLength[tag.Length] = bucket = [];
                bucket.Add(tag);
            }
            foreach (var alias in row.Spellings)
                if (Normalise(alias) is { Length: > 0 } a && !NotASpelling.Contains(a)) merged._aliases.TryAdd(a, tag);
        }
        foreach (var real in merged._tags.Keys) merged._aliases.Remove(real);
        return merged;
    }

    private TagList Copy()
    {
        var copy = new TagList();
        foreach (var (tag, count) in _tags) copy._tags[tag] = count;
        foreach (var (alias, tag) in _aliases) copy._aliases[alias] = tag;
        foreach (var (length, bucket) in _byLength) copy._byLength[length] = [.. bucket];
        return copy;
    }

    /// <summary>
    /// A tag in the form prompts use: trimmed, lower case, spaces for underscores, no weighting brackets or quotes
    /// around it. Emoticons of up to three characters keep their underscore.
    /// </summary>
    public static string Normalise(string? written)
    {
        var tag = (written ?? "").Trim().Trim('"', '\'', '`', '.', ';', ':', '!', '*').Trim();
        tag = tag.Replace("\\(", "(").Replace("\\)", ")");
        // "(red dress:1.2)" or "(red dress)": the model was asked for plain tags; weights are not its to set.
        if (tag.Length > 2 && tag[0] == '(' && tag[^1] == ')' && tag.IndexOf('(', 1) < 0)
        {
            tag = tag[1..^1];
            var colon = tag.LastIndexOf(':');
            if (colon > 0 && double.TryParse(tag[(colon + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) tag = tag[..colon];
        }
        tag = tag.Trim().ToLowerInvariant();
        if (tag.Length > 3) tag = tag.Replace('_', ' ');
        return string.Join(' ', tag.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A tag as it is written into a prompt: brackets escaped, because the engine reads bare ones as emphasis.</summary>
    public static string ForPrompt(string tag) => tag.Replace("(", "\\(").Replace(")", "\\)");

    public bool Contains(string tag) => _tags.ContainsKey(Normalise(tag));

    /// <summary>
    /// The real tags for something the model wrote: itself when it is a tag; the tag it is another spelling of; a near
    /// miss put right ("pony tail" → "ponytail", "glove" → "gloves", "wearing a red dress" → "red dress"); two tags when
    /// it packed two into one ("long silver hair" → "long hair", "silver hair"); the thing itself when only its
    /// describing word is unknown ("tattered cloak" → "cloak"). Empty when nothing real is close.
    /// An empty list knows nothing and keeps the tag as written.
    /// </summary>
    public IReadOnlyList<string> Resolve(string written) => Resolve(written, out _, out _);

    /// <summary>
    /// <see cref="Resolve(string)"/>, saying also when only the thing itself was kept because its describing words are
    /// unknown ("silvr hair" → "hair"). Right for the chat model's tags; for the owner's own words that is not a
    /// correction, since it loses what they described ("Check my tags" says so instead of offering it).
    /// </summary>
    public IReadOnlyList<string> Resolve(string written, out bool thingOnly) => Resolve(written, out _, out thingOnly);

    /// <summary>
    /// True when something written is a tag this list leaves out on purpose, in any spelling, or contains one among its
    /// words: exactly the cases <see cref="Resolve(string)"/> drops for that reason, and no others (an unknown word is not
    /// one). Always false for a list that leaves nothing out, such as the full list the owner added. For words DOG.ai did
    /// not write and the owner did not type: the Looks on an imported card (<see cref="Looks.ForImport"/>).
    /// </summary>
    public bool LeavesOut(string written)
    {
        var real = Resolve(written, out var leftOut, out _);
        if (leftOut) return true;
        if (real.Count > 0 || _excluded.Count == 0) return false;
        // Unknown to the list. The tag check drops such a word anyway, but here it would stay: a left-out tag written
        // as one word ("hiddenthing") is still that tag.
        return Normalise(written).Split(' ').Any(JoinedLeftOut);
    }

    /// <summary>Whether a single word is a left-out tag of two or three words written without its spaces.</summary>
    private bool JoinedLeftOut(string word)
    {
        if (word.Length is < 4 or > 40) return false;
        for (var i = 1; i < word.Length; i++)
        {
            if (_excluded.Contains(word[..i] + " " + word[i..])) return true;
            for (var j = i + 1; j < word.Length; j++)
                if (_excluded.Contains(word[..i] + " " + word[i..j] + " " + word[j..])) return true;
        }
        return false;
    }

    private IReadOnlyList<string> Resolve(string written, out bool leftOut, out bool thingOnly)
    {
        leftOut = false;
        thingOnly = false;
        var tag = Normalise(written);
        if (tag.Length == 0) return [];
        if (IsEmpty) return [tag];
        if (Known(tag) is { } exact) return [exact];

        var words = tag.Split(' ').ToList();
        while (words.Count > 1 && Fillers.Contains(words[0])) words.RemoveAt(0);
        words.RemoveAll(w => w is "a" or "an" or "the");
        if (words.Count == 0) return [];
        var plain = string.Join(' ', words);
        if (plain != tag && Known(plain) is { } unfilled) return [unfilled];

        // A tag left out on purpose, in any spelling: dropped, never put right into a clean tag nearby.
        if (IsLeftOut(tag, plain))
        {
            leftOut = true;
            return [];
        }

        foreach (var variant in Variants(plain))
            if (Known(variant) is { } found) return [found];

        // Anything else that contains a left-out tag among its words is dropped too (a left-out tag followed by "dress"
        // must not become "dress"). After the plural and singular above, so a clean tag's plural is not lost because a
        // word in it was left out on its own.
        if (ContainsLeftOut(words))
        {
            leftOut = true;
            return [];
        }

        // Two tags packed into one: every describing word with the thing described.
        if (words.Count >= 3)
        {
            var head = words[^1];
            var split = words.Take(words.Count - 1).Select(w => Known(w + " " + head)).ToList();
            if (split.All(s => s is not null)) return split.Select(s => s!).Distinct().ToList();
        }

        if (Near(plain) is { } near) return [near];

        // Only the describing words are unknown: keep the thing itself. Not when they turn its meaning round, not for
        // a sentence, and only when the thing is a tag in its own right: another spelling is too weak a link here.
        if (words.Count is 2 or 3 && !words.Any(Sentence.Contains) && !words.Take(words.Count - 1).Any(Negations.Contains))
        {
            thingOnly = true;
            if (words.Count == 3 && _tags.ContainsKey(words[^2] + " " + words[^1])) return [words[^2] + " " + words[^1]];
            if (words[^1].Length >= 3 && _tags.ContainsKey(words[^1])) return [words[^1]];
            thingOnly = false;
        }
        return [];
    }

    /// <summary>
    /// Checks a slot's tags: real ones kept, near misses corrected, the rest dropped, no tag twice, at most
    /// <paramref name="max"/>. <paramref name="already"/> are tags the prompt has from elsewhere (a person's looks).
    /// </summary>
    public (IReadOnlyList<string> Tags, TagNotes Notes) Check(IEnumerable<string> written, int max = int.MaxValue, ISet<string>? already = null)
    {
        var kept = new List<string>();
        var corrected = new List<(string, string)>();
        var dropped = new List<string>();
        var seen = new HashSet<string>(already ?? new HashSet<string>(), StringComparer.Ordinal);
        foreach (var raw in written.SelectMany(w => (w ?? "").Split(',')))
        {
            var tag = Normalise(raw);
            if (tag.Length == 0) continue;
            var real = Resolve(tag);
            if (real.Count == 0)
            {
                if (!dropped.Contains(tag)) dropped.Add(tag);
                continue;
            }
            if (real.Count != 1 || real[0] != tag) corrected.Add((tag, string.Join(", ", real)));
            foreach (var r in real)
                if (kept.Count < max && seen.Add(r)) kept.Add(r);
        }
        return (kept, new TagNotes(corrected, dropped));
    }

    private string? Known(string tag) => _tags.ContainsKey(tag) ? tag : _aliases.GetValueOrDefault(tag);

    /// <summary>Whether the written tag is a left-out tag: as written, without filler words, or in another spelling worth trying (joined, singular, plural).</summary>
    private bool IsLeftOut(string tag, string plain) =>
        _excluded.Count > 0 && (_excluded.Contains(tag) || _excluded.Contains(plain) || Variants(plain).Any(_excluded.Contains));

    /// <summary>
    /// Whether a left-out tag is among the written words, other than inside a longer run of them that is itself a clean
    /// tag (a clean tag may share a word with a left-out one). Runs are compared only as written, so a clean word whose
    /// plural happens to be left out is not lost inside a longer tag.
    /// </summary>
    private bool ContainsLeftOut(List<string> words)
    {
        if (_excluded.Count == 0) return false;
        var clean = new List<(int Start, int Length)>();
        for (var length = words.Count - 1; length >= 2; length--)
            for (var start = 0; start + length <= words.Count; start++)
                if (Known(string.Join(' ', words.Skip(start).Take(length))) is not null) clean.Add((start, length));
        for (var length = words.Count - 1; length >= 1; length--)
            for (var start = 0; start + length <= words.Count; start++)
                if (_excluded.Contains(string.Join(' ', words.Skip(start).Take(length)))
                    && !clean.Any(c => c.Length > length && c.Start <= start && start + length <= c.Start + c.Length))
                    return true;
        return false;
    }

    /// <summary>Other spellings worth trying: joined or hyphenated words, and singular or plural.</summary>
    private static IEnumerable<string> Variants(string tag)
    {
        if (tag.Contains(' '))
        {
            yield return tag.Replace(" ", "");
            yield return tag.Replace(' ', '-');
        }
        if (tag.Contains('-'))
        {
            yield return tag.Replace('-', ' ');
            yield return tag.Replace("-", "");
        }
        if (tag.EndsWith("es", StringComparison.Ordinal) && tag.Length > 4) yield return tag[..^2];
        if (tag.EndsWith('s') && tag.Length > 3) yield return tag[..^1];
        else
        {
            yield return tag + "s";
            yield return tag + "es";
        }
    }

    /// <summary>
    /// The most used real tag one slip away (two for long tags), or null. Never one that starts with another letter or
    /// swaps a colour, size or side word, or changes a whole short word: a wrong correction is worse than a dropped tag.
    /// </summary>
    /// <summary>A tag with fewer posts than this is too rare to be what a short word one slip away meant (<see cref="Near"/>).</summary>
    public const int RareBelow = 1000;

    private string? Near(string tag)
    {
        var allowed = tag.Length >= 12 ? 2 : tag.Length >= 6 ? 1 : 0;
        if (allowed == 0) return null;
        var words = tag.Split(' ');
        string? best = null;
        var bestDistance = int.MaxValue;
        var bestCount = -1;
        for (var length = tag.Length - allowed; length <= tag.Length + allowed; length++)
        {
            if (!_byLength.TryGetValue(length, out var bucket)) continue;
            foreach (var candidate in bucket)
            {
                if (candidate[0] != tag[0]) continue;
                var distance = Distance(tag, candidate, allowed);
                if (distance > allowed) continue;
                var count = _tags[candidate];
                if (distance > bestDistance || (distance == bestDistance && count <= bestCount)) continue;
                // A short single word one slip from a rare tag is more likely another word than a typo ("center" is
                // not "censer", 195 posts).
                if (words.Length == 1 && tag.Length <= 7 && count < RareBelow) continue;
                var other = candidate.Split(' ');
                if (other.Length != words.Length) continue;
                if (words.Where(Colours.Contains).Any(w => !other.Contains(w)) || other.Where(Colours.Contains).Any(w => !words.Contains(w))) continue;
                // A slip is inside a word. Where a whole short word differs it is another word, not a slip
                // ("glowing aura" is not "glowing fur").
                if (!words.Zip(other).All(p => p.First == p.Second || (Math.Min(p.First.Length, p.Second.Length) >= 5 && Distance(p.First, p.Second, 2) <= (p.First.Length >= 10 ? 2 : 1)))) continue;
                (best, bestDistance, bestCount) = (candidate, distance, count);
            }
        }
        return best;
    }

    /// <summary>Edit distance (insert, delete, replace), stopping early once it must exceed <paramref name="limit"/>.</summary>
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }
            if (rowBest > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
