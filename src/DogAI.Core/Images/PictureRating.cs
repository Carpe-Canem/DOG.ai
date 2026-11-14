namespace DogAI.Core.Images;

/// <summary>
/// How far pictures may go, chosen once in Settings → Pictures (the owner's decision, 2026-11-13: a setting, not the chat
/// model's choice). The four levels are Danbooru's; tag checkpoints were trained on them under each family's own words
/// (<see cref="RatingWords"/>).
/// </summary>
public enum PictureRating
{
    /// <summary>Safe for anyone.</summary>
    General,

    /// <summary>The second level.</summary>
    Sensitive,

    /// <summary>The third level.</summary>
    Questionable,

    /// <summary>The highest level.</summary>
    Explicit,
}

/// <summary>
/// The words a checkpoint knows for each rating. The chosen rating's words go into the prompt (after the quality words,
/// where NoobAI's card puts "safe"); the words of every rating above it go into the negative prompt. They sit on the
/// checkpoint's settings card, because the families differ. An empty level adds nothing.
/// </summary>
public sealed record RatingWords(string General = "", string Sensitive = "", string Questionable = "", string Explicit = "")
{
    public static RatingWords None { get; } = new();

    /// <summary>
    /// Starting words per family. NoobAI: from Laxhar's model card (Hugging Face, read 2026-11-14) and Danbooru's own
    /// rating names. Pony: the three its author's page names; Pony has no middle level, so Sensitive adds
    /// nothing. Illustrious: its model cards name no rating words, so these are Danbooru's own rating names, which its
    /// training data used; not checked against the checkpoint. Plain SDXL was not trained with rating words.
    /// </summary>
    public static RatingWords ForFamily(ImageFamily family) => family switch
    {
        ImageFamily.Illustrious => new("general", "sensitive", "questionable, nsfw", "explicit, nsfw"),
        ImageFamily.NoobAI => new("safe", "sensitive", "nsfw", "explicit, nsfw"),
        ImageFamily.Pony => new("rating_safe", "", "rating_questionable", "rating_explicit"),
        _ => None,
    };

    public string For(PictureRating rating) => rating switch
    {
        PictureRating.Sensitive => Sensitive,
        PictureRating.Questionable => Questionable,
        PictureRating.Explicit => Explicit,
        _ => General,
    };

    public RatingWords With(PictureRating rating, string words) => rating switch
    {
        PictureRating.Sensitive => this with { Sensitive = words },
        PictureRating.Questionable => this with { Questionable = words },
        PictureRating.Explicit => this with { Explicit = words },
        _ => this with { General = words },
    };

    /// <summary>The words of every rating above <paramref name="rating"/>, one by one, without repeats.</summary>
    public IReadOnlyList<string> Above(PictureRating rating) =>
        Enum.GetValues<PictureRating>().Where(r => r > rating).SelectMany(r => PictureRatings.Pieces(For(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Each level on one line, trimmed, at most 200 characters.</summary>
    public RatingWords Clean()
    {
        static string One(string? s)
        {
            var text = string.Join(", ", PictureRatings.Pieces(s ?? ""));
            return text.Length > 200 ? text[..200] : text;
        }
        return new(One(General), One(Sensitive), One(Questionable), One(Explicit));
    }
}

public static class PictureRatings
{
    public static string Title(PictureRating rating) => rating switch
    {
        PictureRating.Sensitive => "Sensitive",
        PictureRating.Questionable => "Questionable",
        PictureRating.Explicit => "Explicit",
        _ => "General",
    };

    public static string Blurb(PictureRating rating) => rating switch
    {
        PictureRating.Sensitive => "Up to what the tag sites rate “sensitive”.",
        PictureRating.Questionable => "Up to what the tag sites rate “questionable”.",
        PictureRating.Explicit => "The highest level: DOG.ai sets no limit.",
        _ => "Safe for anyone.",
    };

    /// <summary>What the chat model is told when it plans a picture, so it picks a moment that fits.</summary>
    public static string ForPlanner(PictureRating rating) => rating switch
    {
        PictureRating.Sensitive => "The picture may go as far as Danbooru's \"sensitive\" rating and no further: everyone stays dressed.",
        PictureRating.Questionable => "The picture may go as far as Danbooru's \"questionable\" rating and no further. Everyone in it is an adult.",
        PictureRating.Explicit => "The picture may go as far as the text calls for. Everyone in it is an adult.",
        _ => "The picture must suit anyone: everyone is fully clothed, and nothing in it is suggestive or gory.",
    };

    /// <summary>The words of the chosen rating, the negative prompt with the words above it added, without repeats.</summary>
    public static (string Words, string Negative) Apply(CheckpointSettings settings, PictureRating rating)
    {
        var words = settings.RatingsOrDefault().For(rating).Trim();
        var negative = Pieces(settings.Negative).ToList();
        // A word the chosen level shares with a higher one stays out of the
        // negative prompt: asking for a word and against it at once cancels it.
        var own = Pieces(words);
        foreach (var piece in settings.RatingsOrDefault().Above(rating))
            if (!own.Contains(piece, StringComparer.OrdinalIgnoreCase) && !negative.Contains(piece, StringComparer.OrdinalIgnoreCase)) negative.Add(piece);
        return (words, string.Join(", ", negative));
    }

    /// <summary>
    /// The same request under another rating: the rating words after the checkpoint's quality words (of any level) are
    /// replaced by <paramref name="rating"/>'s, and the negative prompt is made again from the card with that rating. Used by
    /// Try again, so a picture drawn (or kept) under one rating never carries its words after the owner chose another.
    /// Nothing else in the prompt changes.
    /// </summary>
    public static ImageRequest Reapply(ImageRequest request, CheckpointSettings settings, PictureRating rating)
    {
        var ratings = settings.RatingsOrDefault();
        var known = Enum.GetValues<PictureRating>().SelectMany(r => Pieces(ratings.For(r))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (words, negative) = Apply(settings, rating);
        var pieces = Pieces(request.Prompt).ToList();
        var quality = Pieces(settings.QualityBefore);
        var at = quality.Count > 0 && pieces.Count >= quality.Count && pieces.Take(quality.Count).SequenceEqual(quality, StringComparer.OrdinalIgnoreCase)
            ? quality.Count
            : 0;
        var end = at;
        while (end < pieces.Count && known.Contains(pieces[end])) end++;
        var wanted = Pieces(words);
        var prompt = request.Prompt;
        if (!pieces.Skip(at).Take(end - at).SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
        {
            pieces.RemoveRange(at, end - at);
            pieces.InsertRange(at, wanted);
            prompt = string.Join(", ", pieces);
        }
        return request with { Prompt = prompt, NegativePrompt = negative };
    }

    /// <summary>Comma-separated words as pieces, on one line, trimmed, empty ones left out.</summary>
    public static IReadOnlyList<string> Pieces(string text) =>
        text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
