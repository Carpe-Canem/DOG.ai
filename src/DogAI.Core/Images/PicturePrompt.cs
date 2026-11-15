using System.Text.RegularExpressions;

namespace DogAI.Core.Images;

/// <summary>A person who may be in a picture, with how they look.</summary>
/// <param name="Looks">From their card or persona; <see cref="Images.Looks.None"/> when they have none.</param>
/// <param name="Lora">The settings card of the LoRA their looks name, or null when they name none or the file is not installed.</param>
public sealed record PersonLooks(string Name, Looks Looks, LoraSettings? Lora = null);

/// <summary>Everything a picture needs besides the scene: the checkpoint's card, the people, LoRAs, shape, quality, seed, rating.</summary>
/// <param name="StyleLoras">The checkpoint's style LoRAs, which apply to every picture drawn with it.</param>
/// <param name="Rating">The rating from Settings → Pictures; null adds no rating words (see <see cref="ImagePrompt.Compose"/>).</param>
/// <param name="TypedPlace">The place and light as the owner typed them in the review ("Show me the prompt before drawing"):
/// when set, they replace the scene's and are used exactly as written, never checked or cut.</param>
public sealed record PictureOrder(
    CheckpointSettings Settings,
    IReadOnlyList<PersonLooks>? People = null,
    IReadOnlyList<(string Path, LoraSettings Settings)>? StyleLoras = null,
    PictureShape Shape = PictureShape.Tall,
    PictureQuality Quality = PictureQuality.Standard,
    long Seed = -1,
    PictureRating? Rating = null,
    PictureSize Size = PictureSize.Small,
    PicturePlace? TypedPlace = null);

/// <summary>A picture ready to draw.</summary>
/// <param name="Request">What goes to the picture engine.</param>
/// <param name="Subject">The part of the prompt that describes this picture, without quality words and LoRA triggers.</param>
/// <param name="Notes">What the tag check corrected and left out.</param>
/// <param name="Place">The place and light words that went into the prompt (unescaped); null when there are none.</param>
public sealed record PlannedPicture(ImageRequest Request, string Subject, TagNotes Notes, PicturePlace? Place = null);

/// <summary>
/// Builds the prompt from a planned scene. Code does this, not the chat model: the checkpoint's quality words, each
/// person's looks from their own card, the scene's slots in the order tag checkpoints expect, and the checkpoint's
/// negative prompt. Every tag the model wrote is checked against the bundled tag list first.
/// </summary>
public static partial class PicturePrompt
{
    /// <summary>Most tags kept per slot, for one or two people, and for three (the prompt must stay short).</summary>
    private static readonly (int Looks, int Clothing, int Expression, int Pose) Few = (8, 5, 2, 3), Many = (6, 3, 1, 2);
    private const int MaxPlace = PicturePlace.MaxPlace, MaxLight = PicturePlace.MaxLight;

    [GeneratedRegex(@"^1\s?(girl|boy|other)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountTag();

    /// <summary>True for "1girl", "1boy" and "1other": tags that say what a person is, which code counts and writes itself.</summary>
    public static bool IsCountTag(string tag) => CountTag().IsMatch(TagList.Normalise(tag));

    public static PlannedPicture Assemble(PictureScene scene, PictureOrder order, TagList? tags = null)
    {
        var settings = order.Settings;
        var known = order.People ?? [];
        var people = scene.People.Take(PictureScene.MaxPeople)
            .Select(p => (Scene: p, Own: known.FirstOrDefault(k => k.Name.Trim().Equals(p.Name.Trim(), StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var (subject, notes, place) = settings.Style == PromptStyle.Tags
            ? InTags(scene, people, tags ?? TagList.Current, order.TypedPlace)
            : InSentences(scene, people, order.TypedPlace);

        // The checkpoint's style LoRAs, then each person's own; a LoRA named twice is used once, at the person's strength.
        var loras = new List<(string Path, LoraSettings Settings)>();
        foreach (var (path, card) in (order.StyleLoras ?? []).Concat(people
                     .Where(p => p.Own is { Lora: not null, Looks.Lora: not null })
                     .Select(p => (p.Own!.Looks.Lora!, p.Own.Lora! with { Strength = p.Own.Looks.LoraStrength ?? p.Own.Lora!.Strength }))))
        {
            loras.RemoveAll(l => ImageModelCatalog.Normalise(l.Path).Equals(ImageModelCatalog.Normalise(path), StringComparison.OrdinalIgnoreCase));
            loras.Add((path, card));
        }

        var request = ImagePrompt.Compose(settings, subject, loras, seed: order.Seed, rating: order.Rating).At(order.Quality, order.Shape, order.Size);
        return new PlannedPicture(request, subject, notes, place);
    }

    private static (string, TagNotes, PicturePlace?) InTags(PictureScene scene, List<(ScenePerson Scene, PersonLooks? Own)> people, TagList list,
        PicturePlace? typed)
    {
        var limit = people.Count >= 3 ? Many : Few;
        var notes = TagNotes.None;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int> { ["girl"] = 0, ["boy"] = 0, ["other"] = 0 };
        var perPerson = new List<List<string>>();

        IReadOnlyList<string> Kept(IEnumerable<string> written, int max)
        {
            var (kept, n) = list.Check(written, max, seen);
            foreach (var tag in kept) seen.Add(tag);
            notes = notes.Plus(n);
            return kept;
        }

        // A checked tag gets its brackets escaped; the owner's own looks are left exactly as typed.
        IReadOnlyList<string> Checked(IEnumerable<string> written, int max) => Kept(written, max).Select(TagList.ForPrompt).ToList();

        foreach (var (person, own) in people)
        {
            var mine = new List<string>();
            var kind = person.Kind;
            if (own is { Looks.Tags.Length: > 0 })
            {
                // The owner's own words: used as written, never checked or cut. A count tag among them says what the
                // person is, and is counted with everyone else's.
                foreach (var piece in own.Looks.Pieces())
                {
                    if (CountTag().Match(piece) is { Success: true } m) kind = m.Groups[1].Value.ToLowerInvariant();
                    else if (seen.Add(TagList.Normalise(piece))) mine.Add(piece);
                }
            }
            else
            {
                mine.AddRange(Checked(person.Looks.Where(t => !IsCountTag(t)), limit.Looks));
            }
            counts[counts.ContainsKey(kind) ? kind : "other"]++;
            mine.AddRange(Checked(person.Clothing, limit.Clothing));
            mine.AddRange(Checked(person.Expression, limit.Expression));
            mine.AddRange(Checked(person.Pose, limit.Pose));
            perPerson.Add(mine);
        }

        var pieces = new List<string>();
        foreach (var kind in new[] { "girl", "boy", "other" })
            if (counts[kind] > 0) pieces.Add(counts[kind] == 1 ? $"1{kind}" : $"{Math.Min(counts[kind], 6)}{kind}s");
        if (people.Count == 1) pieces.Add("solo");
        if (people.Count == 0) pieces.Add("no humans");
        foreach (var mine in perPerson) pieces.AddRange(mine);
        PicturePlace? place;
        if (typed is not null)
        {
            // The owner's own words from the review: as written, like their Looks.
            place = typed.IsEmpty ? null : typed;
            if (people.Count == 0 && typed.Place.Count > 0) pieces.Add("scenery");
            foreach (var word in typed.Place.Concat(typed.Light))
                if (seen.Add(TagList.Normalise(word))) pieces.Add(word);
        }
        else
        {
            var (where, light) = scene.PlaceAndLight();
            if (people.Count == 0 && where.Count > 0) pieces.Add("scenery");
            var keptPlace = Kept(SplitPlaces(where, list), MaxPlace);
            var keptLight = Kept(light, MaxLight);
            pieces.AddRange(keptPlace.Concat(keptLight).Select(TagList.ForPrompt));
            place = keptPlace.Count + keptLight.Count > 0 ? new PicturePlace(keptPlace, keptLight) : null;
        }
        // Framing comes from a closed list; anything else the model wrote there is dropped without a second look.
        pieces.AddRange(scene.FramingForPrompt());
        return (string.Join(", ", pieces), notes, place);
    }

    /// <summary>
    /// Place words that are two tags in one ("forest floor", "kitchen table") become both: the tag check would keep only
    /// the last word as "the thing" and lose the place (seen in step 3: "forest floor" became "floor"). Only for the
    /// place, and only when the two words together are no tag and each is one.
    /// </summary>
    public static IEnumerable<string> SplitPlaces(IEnumerable<string> place, TagList list)
    {
        foreach (var written in place)
        {
            var tag = TagList.Normalise(written);
            if (tag.Split(' ') is [var first, var second] && !list.Contains(tag) && list.Contains(first) && list.Contains(second))
            {
                yield return first;
                yield return second;
            }
            else
            {
                yield return written;
            }
        }
    }

    /// <summary>
    /// For checkpoints that read sentences: the same slots as short phrases, strung together. Nothing is checked
    /// against the tag list, which means nothing to these checkpoints.
    /// </summary>
    private static (string, TagNotes, PicturePlace?) InSentences(PictureScene scene, List<(ScenePerson Scene, PersonLooks? Own)> people, PicturePlace? typed)
    {
        static string Phrase(string s) => s.Trim().Trim(',', '.').Trim();
        var pieces = new List<string>();
        pieces.AddRange(scene.FramingForPrompt());
        foreach (var (person, own) in people)
        {
            var looks = own is { Looks.Tags.Length: > 0 }
                ? own.Looks.Tags.Trim()
                : string.Join(", ", person.Looks.Select(Phrase).Where(p => p.Length > 0).Take(Few.Looks));
            if (looks.Length == 0) looks = person.Kind switch { "girl" => "a woman", "boy" => "a man", _ => "a person" };
            pieces.Add(looks);
            pieces.AddRange(person.Clothing.Concat(person.Expression).Concat(person.Pose).Select(Phrase).Where(p => p.Length > 0).Take(8));
        }
        PicturePlace? place;
        if (typed is not null)
        {
            place = typed.IsEmpty ? null : typed;
        }
        else
        {
            var (where, light) = scene.PlaceAndLight();
            var p = where.Select(Phrase).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPlace).ToList();
            var l = light.Select(Phrase).Where(x => x.Length > 0 && !p.Contains(x, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxLight).ToList();
            place = p.Count + l.Count > 0 ? new PicturePlace(p, l) : null;
        }
        if (place is not null) pieces.AddRange(place.Place.Concat(place.Light));
        return (string.Join(", ", pieces.Distinct(StringComparer.OrdinalIgnoreCase)), TagNotes.None, place);
    }
}
