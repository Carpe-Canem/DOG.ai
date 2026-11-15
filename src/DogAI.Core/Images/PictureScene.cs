using System.Text.Json;
using System.Text.Json.Nodes;

namespace DogAI.Core.Images;

/// <summary>One person in a planned picture, as the chat model filled the slots.</summary>
/// <param name="Name">One of the names the model was given, or <see cref="PictureScene.SomeoneElse"/>.</param>
/// <param name="Kind">"girl", "boy" or "other": becomes the count tag ("1girl") unless the person's own looks say.</param>
/// <param name="Looks">Appearance, used only for a person who has no looks of their own.</param>
public sealed record ScenePerson(string Name, string Kind, IReadOnlyList<string> Looks, IReadOnlyList<string> Clothing,
    IReadOnlyList<string> Expression, IReadOnlyList<string> Pose);

/// <summary>
/// A planned picture: one moment and the named slots that describe it. The chat model fills this in (as JSON matching
/// <see cref="Schema"/>); code turns it into the prompt (<see cref="PicturePrompt.Assemble"/>), so the model never
/// writes the prompt itself and cannot change a character's looks, the quality words or the negative prompt.
/// </summary>
/// <param name="Moment">One sentence saying what the picture shows. For the owner to read; never sent to the picture engine.</param>
public sealed record PictureScene(string Moment, IReadOnlyList<ScenePerson> People, IReadOnlyList<string> Place,
    IReadOnlyList<string> Light, IReadOnlyList<string> Framing)
{
    /// <summary>A person in the picture who is not one of the named cast.</summary>
    public const string SomeoneElse = "someone else";

    /// <summary>The most people a picture may show. SDXL mixes up more (one person's hair on another).</summary>
    public const int MaxPeople = 3;

    public static IReadOnlyList<string> Kinds { get; } = ["girl", "boy", "other"];

    /// <summary>
    /// How much is shown: a short closed list, so the model cannot invent one. These are real Danbooru tags; for
    /// sentence prompts they read well enough as they are. A picture gets one of them.
    /// </summary>
    public static IReadOnlyList<string> Sizes { get; } = ["portrait", "upper body", "cowboy shot", "full body", "close-up", "wide shot"];

    /// <summary>Where the picture is seen from; a picture gets at most one. <see cref="StraightOn"/> adds nothing to the prompt.</summary>
    public static IReadOnlyList<string> Views { get; } = ["looking at viewer", "from side", "from above", "from below", "from behind"];

    /// <summary>The view the model picks when none of the others applies.</summary>
    public const string StraightOn = "straight on";

    /// <summary>Everything that may stand in <see cref="Framing"/>: the sizes, then the views.</summary>
    public static IReadOnlyList<string> Framings { get; } = [.. Sizes, .. Views];

    /// <summary>The framing that reaches the prompt: the first size and the first view, nothing else.</summary>
    public IReadOnlyList<string> FramingForPrompt()
    {
        var normal = Framing.Select(TagList.Normalise).ToList();
        return new[] { normal.FirstOrDefault(Sizes.Contains), normal.FirstOrDefault(Views.Contains) }.OfType<string>().ToList();
    }

    /// <summary>True when there is something to draw: a person, a place or light.</summary>
    public bool HasContent => People.Count > 0 || Place.Count > 0 || Light.Count > 0;

    /// <summary>
    /// The JSON shape the model must produce. The moment comes first, so it is decided before the slots are filled.
    /// <paramref name="names"/> are the people who may appear; with none, any name is accepted.
    /// </summary>
    public static string Schema(IReadOnlyList<string> names, int maxPeople, bool onePersonExactly = false)
    {
        static JsonObject Tags(int max) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = max };
        static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

        var name = new JsonObject { ["type"] = "string" };
        if (names.Count > 0) name["enum"] = Strings(onePersonExactly ? names : [.. names, SomeoneElse]);
        var person = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = name,
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(Kinds) },
                ["looks"] = Tags(8),
                ["clothing"] = Tags(6),
                ["expression"] = Tags(2),
                ["pose"] = Tags(3),
            },
            ["required"] = new JsonArray("name", "kind", "looks", "clothing", "expression", "pose"),
        };
        var people = new JsonObject { ["type"] = "array", ["items"] = person, ["maxItems"] = onePersonExactly ? 1 : Math.Clamp(maxPeople, 1, MaxPeople) };
        if (onePersonExactly) people["minItems"] = 1;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["moment"] = new JsonObject { ["type"] = "string" },
                ["people"] = people,
                ["place"] = Tags(PicturePlace.MaxWritten),
                ["light"] = Tags(3),
                ["framing"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(Sizes) },
                ["view"] = new JsonObject { ["type"] = "string", ["enum"] = Strings([StraightOn, .. Views]) },
            },
            ["required"] = new JsonArray("moment", "people", "place", "light", "framing", "view"),
        }.ToJsonString();
    }

    /// <summary>
    /// Reads the model's JSON. Tolerates text around the object, a list given as one comma-separated string, missing
    /// fields and too many people (the first <see cref="MaxPeople"/> are kept). Refuses an answer that is not an object.
    /// </summary>
    /// <exception cref="InvalidOperationException">The answer could not be read.</exception>
    public static PictureScene Parse(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model did not describe a picture. Try again, or try another model.");
        JsonObject o;
        try
        {
            o = JsonNode.Parse(answer[start..(end + 1)]) as JsonObject ?? throw new InvalidOperationException("The model did not describe a picture.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The model's picture description was cut off or garbled. Try again, or try another model.");
        }

        var people = (o["people"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => new ScenePerson(
                Text(p, "name"),
                Kinds.Contains(Text(p, "kind").ToLowerInvariant()) ? Text(p, "kind").ToLowerInvariant() : "other",
                List(p, "looks"), List(p, "clothing"), List(p, "expression"), List(p, "pose")))
            .Take(MaxPeople)
            .ToList();
        var moment = Text(o, "moment");
        return new PictureScene(moment.Length > 400 ? moment[..400] : moment, people, List(o, "place"), List(o, "light"), [.. List(o, "framing"), .. List(o, "view")]);
    }

    /// <summary>
    /// The scene's place and light as they should reach the prompt (M18 step 3): a place entry that is a light word
    /// ("night", "moonlight", or anything also in the light list) leaves the place, and goes to the light when it is not
    /// there yet, so the place's room goes to real place words. Nothing is checked against the tag list here.
    /// </summary>
    public (IReadOnlyList<string> Place, IReadOnlyList<string> Light) PlaceAndLight()
    {
        var light = Light.ToList();
        var lightKeys = new HashSet<string>(light.Select(TagList.Normalise), StringComparer.Ordinal);
        var place = new List<string>();
        foreach (var p in Place)
        {
            var key = TagList.Normalise(p);
            if (lightKeys.Contains(key)) continue;
            if (PicturePlace.IsLightWord(key))
            {
                light.Add(p);
                lightKeys.Add(key);
                continue;
            }
            place.Add(p);
        }
        return (place, light);
    }

    private static string Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    /// <summary>A slot's entries: a list of strings, or one string with commas. Each cut to a sane length, at most twelve.</summary>
    private static IReadOnlyList<string> List(JsonObject o, string key)
    {
        IEnumerable<string> items = o[key] switch
        {
            JsonArray a => a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : ""),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split(','),
            _ => [],
        };
        return items.SelectMany(i => i.Split(','))
            .Select(i => i.Trim())
            .Where(i => i.Length > 0)
            .Select(i => i.Length > 80 ? i[..80] : i)
            .Take(12)
            .ToList();
    }
}

/// <summary>
/// Where a picture is: its place words and its light words as they went into the prompt (unescaped). Kept with a story
/// passage's or a chat reply's picture (<see cref="Stories.StoryTurn"/>), so the next picture can be told "the last
/// picture took place in …" (M18 step 3). Never logged.
/// </summary>
public sealed record PicturePlace(IReadOnlyList<string> Place, IReadOnlyList<string> Light)
{
    /// <summary>Place words the planner may write, and the most kept in the prompt (the trial: six written gave
    /// "indoors"/"outdoors" and real detail; five kept leaves room for the people).</summary>
    public const int MaxWritten = 6, MaxPlace = 5, MaxLight = 3;

    /// <summary>The longest one word may be when kept with a picture.</summary>
    public const int MaxWordLength = 60;

    public static PicturePlace None { get; } = new([], []);

    public bool IsEmpty => Place.Count == 0 && Light.Count == 0;

    /// <summary>"forest, outdoors, campfire, night": place then light, as the planner is told.</summary>
    public string Line => string.Join(", ", Place.Concat(Light));

    /// <summary>
    /// The words cleaned for keeping or for the planner (they came from a model, or from JSON on a message): one line each,
    /// at most <see cref="MaxWordLength"/> characters, no repeats, at most <see cref="MaxPlace"/> and <see cref="MaxLight"/>.
    /// Null when nothing is left.
    /// </summary>
    public static PicturePlace? Of(IEnumerable<string>? place, IEnumerable<string>? light)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> Clean(IEnumerable<string>? words, int max) => (words ?? [])
            .Select(w => string.Join(' ', (w ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ',', '.'))
            .Where(w => w.Length is > 0 and <= MaxWordLength && seen.Add(w))
            .Take(max)
            .ToList();
        var p = Clean(place, MaxPlace);
        var l = Clean(light, MaxLight);
        return p.Count == 0 && l.Count == 0 ? null : new PicturePlace(p, l);
    }

    /// <summary>Words typed by the owner in a box ("forest, outdoors, moss"): split at commas, used as written.</summary>
    public static IReadOnlyList<string> Words(string? typed) =>
        (typed ?? "").Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length > 0).ToList();

    /// <summary>True when every word is still in <paramref name="prompt"/> (as written or escaped for the prompt), so a prompt
    /// changed by hand still has this place.</summary>
    public bool StillIn(string prompt) =>
        Place.Concat(Light).All(w => prompt.Contains(w, StringComparison.OrdinalIgnoreCase) || prompt.Contains(TagList.ForPrompt(w), StringComparison.OrdinalIgnoreCase));

    /// <summary>Time of day, light and weather words: a place entry that is one of these is moved to the light.</summary>
    private static readonly HashSet<string> LightWords = new(StringComparer.Ordinal)
    {
        "night", "day", "daytime", "morning", "evening", "afternoon", "dawn", "dusk", "sunset", "sunrise", "twilight", "midnight",
        "sunlight", "moonlight", "candlelight", "firelight", "lamplight", "starlight", "dappled sunlight", "sunbeam", "light rays",
        "backlighting", "dim lighting", "dark", "darkness", "rain", "raining", "snow", "snowing", "fog", "foggy", "mist", "misty",
        "overcast", "storm", "thunderstorm", "lightning", "golden hour", "blue hour", "night sky", "cloudy",
    };

    /// <summary>True for a word of time of day, light or weather (normalised as <see cref="TagList.Normalise"/> does).</summary>
    public static bool IsLightWord(string normalised) => LightWords.Contains(normalised);
}
