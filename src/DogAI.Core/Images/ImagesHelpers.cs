using DogAI.Core.Chat;
using DogAI.Core.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DogAI.Core.Images;

/// <summary>
/// One comma- or line-separated piece of what the owner typed into a prompt box, with where it is. The core is the
/// piece without weighting brackets around it or a weight after it ("(red dress:1.2)" has the core "red dress"), so a
/// tag can be looked up and changed while the owner's brackets and weight stay exactly as typed.
/// </summary>
/// <param name="Start">Where the piece starts, spaces around it left out (brackets included).</param>
/// <param name="CoreStart">Where the core starts.</param>
/// <param name="OwnBrackets">True when every bracket the piece opens it also closes (or it has none), so it can be taken out whole.</param>
/// <param name="Checkable">False for what is not a tag to look up: LoRA or embedding syntax (&lt;…&gt;), BREAK, nested brackets, a colon left inside.</param>
public sealed record PromptPiece(int Start, int Length, int CoreStart, int CoreLength, string Core, bool OwnBrackets, bool Checkable)
{
    public int End => Start + Length;
    public int CoreEnd => CoreStart + CoreLength;
}

/// <summary>Reading a prompt box as pieces without changing a character of it (M16 step 4).</summary>
public static partial class PromptPieces
{
    [GeneratedRegex(@":\s*-?(\d+(\.\d*)?|\.\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Weight();

    /// <summary>Every piece of <paramref name="text"/> in order. Empty pieces (",,", blank lines) are left out.</summary>
    public static IReadOnlyList<PromptPiece> Of(string? text)
    {
        text ??= "";
        var pieces = new List<PromptPiece>();
        var from = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not (',' or '\n' or '\r')) continue;
            if (Read(text, from, i) is { } piece) pieces.Add(piece);
            from = i + 1;
        }
        return pieces;
    }

    private static PromptPiece? Read(string text, int from, int to)
    {
        var (s, e) = (from, to);
        while (s < e && char.IsWhiteSpace(text[s])) s++;
        while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
        if (s == e) return null;
        var (start, end) = (s, e);

        var open = 0;
        while (s < e && text[s] is '(' or '[' or '{')
        {
            s++;
            open++;
            while (s < e && char.IsWhiteSpace(text[s])) s++;
        }
        var close = 0;
        while (true)
        {
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            if (e > s && text[e - 1] is ')' or ']' or '}' && !(e - 2 >= s && text[e - 2] == '\\'))
            {
                e--;
                close++;
                continue;
            }
            // "blue eyes:1.2)" — a weight goes with a closing bracket, here or in the piece that closes the group.
            if (close > 0 && Weight().Match(text, s, e - s) is { Success: true } w && w.Index + w.Length == e)
            {
                e = w.Index;
                continue;
            }
            break;
        }
        if (s >= e) return null;
        var core = text[s..e];
        var checkable = core.IndexOfAny(['<', '>', ':']) < 0 && core != "BREAK" && !Unescaped(core);
        return new PromptPiece(start, end - start, s, e - s, core, open == close, checkable);
    }

    /// <summary>True when the core still has a bracket that is not escaped ("a (b) c"): nested emphasis, left alone.</summary>
    private static bool Unescaped(string core)
    {
        for (var i = 0; i < core.Length; i++)
            if (core[i] is '(' or ')' or '[' or ']' or '{' or '}' && (i == 0 || core[i - 1] != '\\')) return true;
        return false;
    }

    /// <summary>True when one of the pieces is this word, compared as tags are (any case, underscores as spaces).</summary>
    public static bool Has(string? text, string word)
    {
        var wanted = TagList.Normalise(word);
        return wanted.Length > 0 && Of(text).Any(p => TagList.Normalise(p.Core) == wanted);
    }

    /// <summary>
    /// A word put after what is typed (a LoRA's trigger word): with a comma between, and not at all when a piece of the
    /// box already is that word. What was typed stays as typed.
    /// </summary>
    public static string Insert(string? text, string word) =>
        Has(text, word) ? text ?? "" : SavedPicturePrompts.AddTo(text, word.Trim());
}

/// <summary>Which box a tag-check offer is about.</summary>
public enum PromptBox { Prompt, Negative }

/// <summary>
/// One change "Check my tags" offers. <see cref="Use"/> is the real tag (or tags) to write instead, exactly as it would be
/// written (brackets escaped, underscores when the owner wrote them); null when nothing in
/// the tag list is close, and the offer is to take the piece out. <see cref="Written"/> is exactly what is in the box.
/// </summary>
/// <param name="Key">The piece as a tag ("red dres"): the same word written twice is one offer.</param>
/// <param name="Count">How many times it is written in the box.</param>
/// <param name="Only">For a piece offered for taking out whose last word or two are a tag on their own ("silvr hair": "hair"):
/// that tag, said to the owner instead of offered, because writing it would lose what they described.</param>
public sealed record TagOffer(PromptBox Box, string Key, string Written, string? Use, int Count, string? Only = null)
{
    public bool IsCorrection => Use is not null;
}

/// <summary>
/// "Check my tags" on the Images section's Make tab (M16 step 4): looks up every piece of the two boxes in the tag list
/// in use (<see cref="TagList.Current"/>) and offers a change for each piece that is not a tag: the real tag when one is
/// close, else taking it out. It never changes anything by itself: <see cref="Accept"/> applies one offer the owner
/// accepted and leaves every other character of the box as typed (brackets and weights around the piece too).
/// <para>
/// Not looked at: the checkpoint's own quality words, negative prompt and rating words, the ticked LoRAs' trigger words
/// (<see cref="Fine"/>), well-known quality words (<see cref="QualityWords"/>), and what is not a plain tag (LoRA or
/// embedding syntax, BREAK, nested brackets).
/// </para>
/// </summary>
public static class TagCheck
{
    /// <summary>
    /// Quality and age words the SDXL anime checkpoints were trained with that are not Danbooru tags, so the tag list does
    /// not know them. A box holding them is not offered to change them, whichever checkpoint is chosen.
    /// </summary>
    public static IReadOnlyList<string> QualityWords { get; } =
    [
        "masterpiece", "best quality", "high quality", "good quality", "normal quality", "medium quality", "low quality", "bad quality",
        "worst quality", "amazing quality", "great quality", "very aesthetic", "aesthetic", "displeasing", "very displeasing",
        "absurdres", "highres", "lowres", "incredibly absurdres", "newest", "recent", "mid", "early", "old", "oldest",
        "score_9", "score_8_up", "score_7_up", "score_6_up", "score_5_up", "score_4_up", "score_9_up",
        "source_anime", "source_cartoon", "source_furry", "source_pony", "rating_safe", "rating_questionable", "rating_explicit",
        "safe", "general", "sensitive", "questionable", "explicit", "nsfw", "sfw",
        // Common words of negative prompts that the list may not hold (meta tags are not in it).
        "jpeg artifacts", "signature", "username", "logo", "watermark", "text", "artist name", "bad anatomy", "bad hands",
        "mutated hands", "extra digits", "fewer digits", "missing fingers", "extra fingers", "blurry", "detailed", "ugly",
    ];

    /// <summary>
    /// The words the check leaves alone for this checkpoint: <see cref="QualityWords"/>, the card's quality words before
    /// and after, its negative prompt, the rating words of every level, and the given LoRA trigger words. Compared as tags.
    /// </summary>
    public static ISet<string> Fine(CheckpointSettings? card, IEnumerable<string>? triggerWords = null)
    {
        var fine = new HashSet<string>(QualityWords.Select(TagList.Normalise), StringComparer.Ordinal);
        void Add(string? text)
        {
            foreach (var piece in PromptPieces.Of(text)) if (TagList.Normalise(piece.Core) is { Length: > 0 } t) fine.Add(t);
        }
        if (card is not null)
        {
            Add(card.QualityBefore);
            Add(card.QualityAfter);
            Add(card.Negative);
            var ratings = card.RatingsOrDefault();
            foreach (var level in Enum.GetValues<PictureRating>()) Add(ratings.For(level));
        }
        foreach (var words in triggerWords ?? []) Add(words);
        return fine;
    }

    /// <summary>The offers for one box, in the order the pieces are written; a piece written twice is offered once.</summary>
    public static IReadOnlyList<TagOffer> Offers(string? text, PromptBox box, TagList tags, ISet<string> fine)
    {
        var offers = new List<TagOffer>();
        if (tags.IsEmpty) return offers;
        foreach (var (key, use, only, piece) in Findings(text, tags, fine))
        {
            var at = offers.FindIndex(o => o.Key == key);
            if (at >= 0) offers[at] = offers[at] with { Count = offers[at].Count + 1 };
            else offers.Add(new TagOffer(box, key, piece.Core, use, 1, only));
        }
        return offers;
    }

    /// <summary>
    /// Applies one accepted offer to the box it is about, everywhere that word is written: the real tag written in place
    /// of the core (brackets and weight stay), or, for an offer to take it out, the piece and one comma beside it removed.
    /// Every other character stays as typed. Returns the box unchanged when the word is no longer in it.
    /// </summary>
    /// <param name="change">True to write <see cref="TagOffer.Use"/> in its place; false to take the piece out.</param>
    public static string Accept(string? text, TagOffer offer, bool change, TagList tags, ISet<string> fine)
    {
        text ??= "";
        if (change && offer.Use is null) return text;
        // One place at a time, reading the box again after each: a removal moves everything behind it.
        for (var guard = 0; guard < 200; guard++)
        {
            var found = Findings(text, tags, fine).FirstOrDefault(f => f.Key == offer.Key);
            if (found.Piece is not { } piece) break;
            text = change
                ? text[..piece.CoreStart] + found.Use! + text[piece.CoreEnd..]
                : Remove(text, piece);
            if (change && found.Use is null) break;
        }
        return text;
    }

    private static IEnumerable<(string Key, string? Use, string? Only, PromptPiece Piece)> Findings(string? text, TagList tags, ISet<string> fine)
    {
        foreach (var piece in PromptPieces.Of(text))
        {
            if (!piece.Checkable) continue;
            var key = TagList.Normalise(piece.Core);
            if (key.Length == 0 || fine.Contains(key)) continue;
            var real = tags.Resolve(piece.Core, out var thingOnly);
            if (real.Count == 1 && real[0] == key) continue;
            // "silvr hair" → "hair" would lose the colour the owner asked for: said, not offered.
            if (thingOnly) yield return (key, null, Written(real, piece.Core), piece);
            else yield return (key, real.Count == 0 ? null : Written(real, piece.Core), null, piece);
        }
    }

    /// <summary>The real tag as the owner would write it: brackets escaped, and with underscores when the owner used them.</summary>
    private static string Written(IEnumerable<string> real, string core)
    {
        var tags = real.Select(TagList.ForPrompt);
        if (core.Contains('_') && !core.Contains(' ')) tags = tags.Select(t => t.Replace(' ', '_'));
        return string.Join(", ", tags);
    }

    /// <summary>Takes a piece out with the comma after it (or, for the last piece, the comma before it).</summary>
    private static string Remove(string text, PromptPiece piece)
    {
        var (s, e) = piece.OwnBrackets ? (piece.Start, piece.End) : (piece.CoreStart, piece.CoreEnd);
        var after = e;
        while (after < text.Length && text[after] is ' ' or '\t') after++;
        if (after < text.Length && text[after] == ',')
        {
            after++;
            while (after < text.Length && text[after] is ' ' or '\t') after++;
            return text[..s] + text[after..];
        }
        var before = s;
        while (before > 0 && text[before - 1] is ' ' or '\t') before--;
        if (before > 0 && text[before - 1] == ',')
        {
            before--;
            while (before > 0 && text[before - 1] is ' ' or '\t') before--;
            return text[..before] + text[e..];
        }
        return text[..s] + text[e..];
    }

    /// <summary>What the dialog says about one offer: "red dres → red dress", "frobnicate: not in the tag list".</summary>
    public static string Describe(TagOffer offer) =>
        (offer.Use is { } use ? $"{offer.Written} → {use}" : $"{offer.Written}: not in the tag list" + (offer.Only is { } only ? $" (only “{only}” is)" : "")) +
        (offer.Count > 1 ? $" (written {offer.Count.ToString(CultureInfo.InvariantCulture)} times)" : "");
}

/// <summary>
/// A ticked LoRA's trigger words as chips in the Images section's LoRAs fold (M16 step 4). A click puts one into the
/// prompt (<see cref="PromptPieces.Insert"/>); nothing is ever added by itself. Words found in the file come from the
/// file's maker, not the owner, so they are cleaned first: one short line each, nothing that looks like a path or an
/// address, and (as for an imported card's Looks) nothing the tag list leaves out on purpose. Words the owner typed on
/// the LoRA's card are theirs and show as typed.
/// </summary>
public static class LoraChips
{
    public const int Max = 8;
    public const int MaxLength = 60;

    /// <summary>
    /// The chips of a LoRA, and how many words from the file were not shown. <paramref name="cardWords"/> are the trigger
    /// words in use (the card's, else the file's); <paramref name="cardSaved"/> says the owner saved its card. A word is
    /// the owner's only when the card was saved and the word is not one the file gave: the card's editor starts filled
    /// with the file's words, so a card saved for its strength alone still holds them, and they are cleaned as the file's.
    /// </summary>
    /// <param name="max">How many words at most. The chips take <see cref="Max"/>; a LoRA's row in Models shows them all.</param>
    public static (IReadOnlyList<string> Chips, int Hidden) ForLora(string? cardWords, bool cardSaved, IReadOnlyList<string>? fileWords, TagList? tags = null, int max = Max)
    {
        var fromFile = (fileWords ?? []).Select(TagList.Normalise).ToHashSet(StringComparer.Ordinal);
        return For(Words(cardWords), word => cardSaved && !fromFile.Contains(TagList.Normalise(word)), tags, max);
    }

    /// <summary>The chips, and how many words from the file were not shown.</summary>
    /// <param name="owners">True when the words were typed by the owner on the LoRA's card (Models › On this PC › Pictures › LoRAs).</param>
    public static (IReadOnlyList<string> Chips, int Hidden) For(string? triggerWords, bool owners, TagList? tags = null) =>
        For(Words(triggerWords), _ => owners, tags, Max);

    /// <summary>
    /// Words read from a LoRA file (its training tags), cleaned for showing: all that may be shown, and how many were not.
    /// A word is taken whole, commas included.
    /// </summary>
    public static (IReadOnlyList<string> Words, int Hidden) FromFile(IEnumerable<string>? fileWords, TagList? tags = null) =>
        For(fileWords ?? [], _ => false, tags, int.MaxValue);

    private static IEnumerable<string> Words(string? triggerWords) =>
        (triggerWords ?? "").Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (IReadOnlyList<string> Chips, int Hidden) For(IEnumerable<string> words, Func<string, bool> ownersWord, TagList? tags, int max)
    {
        var chips = new List<string>();
        var hidden = 0;
        foreach (var raw in words)
        {
            var word = raw.Trim();
            if (word.Length == 0 || chips.Contains(word, StringComparer.OrdinalIgnoreCase)) continue;
            if (ownersWord(word))
            {
                // The owner's own words, as typed; only what could not be shown on one chip is skipped.
                if (word.Length > 200 || word.Any(char.IsControl)) continue;
            }
            else if (!FromFileIsShown(word, tags ?? TagList.Current))
            {
                hidden++;
                continue;
            }
            if (chips.Count < max) chips.Add(word);
        }
        return (chips, hidden);
    }

    private static bool FromFileIsShown(string word, TagList tags) =>
        word.Length <= MaxLength
        && !word.Any(char.IsControl)
        && word.IndexOfAny(['\\', '/', '<', '>']) < 0
        && !word.Contains("http", StringComparison.OrdinalIgnoreCase)
        && !(word.Length > 1 && word[1] == ':')
        && !tags.LeavesOut(word);

    /// <summary>The line under a LoRA's chips when some words from its file are not shown. Says how many, never which.</summary>
    public static string HiddenNote(int hidden) => hidden == 1
        ? "One word from the file is not shown."
        : $"{hidden.ToString(CultureInfo.InvariantCulture)} words from the file are not shown.";
}

/// <summary>What "Write it for me…" wrote: the prompt for the owner to put in the box and change, and what the tag check did to it.</summary>
/// <param name="Moment">One sentence on what the picture shows, as the model understood it. Shown, never sent to the picture engine.</param>
public sealed record PromptDraft(string Moment, string Prompt, TagNotes Notes);

/// <summary>
/// The picture "Write it for me… › From a picture" looks at (M17 step 5): the JPEG the page made from the picture shown on
/// the Make tab, a kept picture or a file (<see cref="AttachedPictures.MaxSide"/> pixels, quality
/// <see cref="AttachedPictures.Quality"/>: the chat engine does not read WebP). Held in <see cref="ImagesSession"/> only:
/// never a file, a cache or a row. It is only looked at; nothing is drawn from it.
/// </summary>
/// <param name="From">Where it came from, in words ("Try 3", "A kept picture", "A file").</param>
public sealed record HelperPicture(byte[] Jpeg, int Width, int Height, string From)
{
    // Kept beside the bytes, not in the record (a record's fields count when two are compared).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> Addresses = new();

    /// <summary>The picture as a data: address (from memory; never a file), made once.</summary>
    public string Src => Addresses.GetValue(Jpeg, static jpeg => "data:image/jpeg;base64," + Convert.ToBase64String(jpeg));

    /// <summary>
    /// The page's JPEG, made ready the way a chat picture is (<see cref="AttachedPictures.Plain"/> takes out the colour
    /// profile the browser's encoder adds) and checked (<see cref="AttachedPictures.Problem"/>). Returns the picture, or
    /// why it cannot be used.
    /// </summary>
    public static (HelperPicture? Picture, string? Problem) Of(byte[]? jpeg, string from)
    {
        if (jpeg is not { Length: > 0 }) return (null, FromPicture.NotAPicture);
        var plain = AttachedPictures.Plain(jpeg);
        if (AttachedPictures.Problem(plain) is { } problem) return (null, problem);
        var (width, height) = AttachedPictures.SizeOf(plain);
        return (new HelperPicture(plain, width, height, from), null);
    }
}

/// <summary>The words of "Write it for me… › From a picture" (M17 step 5).</summary>
public static class FromPicture
{
    public const string Shown = "The picture shown";
    public const string Kept = "A kept picture";
    public const string File = "A file";

    /// <summary>The source of a try: "Try 3".</summary>
    public static string Try(int number) => "Try " + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The one line under the choice of picture.</summary>
    public const string About =
        "The chat model looks at the picture at 1,024 pixels and writes a prompt for a picture like it. Nothing is drawn from it, and it is kept nowhere: it stays in memory until DOG.ai closes.";

    public const string NotAPicture = "That file could not be used: only " + ComposerPictures.Kinds + " pictures can be.";

    /// <summary>Why Write cannot be pressed in picture mode with no picture chosen.</summary>
    public const string Choose = "Choose a picture first.";

    /// <summary>
    /// Said before Write is pressed when the model that would be used cannot see: the one in memory, or the default for
    /// General when none is (<paramref name="defaultName"/>). Ends before the place, which the page makes a link.
    /// </summary>
    public static PlaceLine CannotSee(string? defaultName) => defaultName is null
        ? ComposerPictures.CannotSee(loaded: true)
        : new($"The default model for General ({defaultName}) cannot see pictures, and it is the one used when none is loaded. Models that can are marked “{Projectors.Label}” in",
            ModelsPlaces.LibraryName, ModelsPlaces.Address(ModelsPlaces.Library));

}
