using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DogAI.Core.Characters;

public sealed class CardFormatException(string message) : Exception(message);

/// <summary>
/// Reads and writes character cards: Character Card V1, V2 and V3, as JSON files or embedded in PNG pictures
/// (base64 JSON in a "chara" text entry for V2 and "ccv3" for V3).
/// </summary>
public static class CharacterCard
{
    /// <summary>Card files bigger than this are refused, so a wrong file cannot exhaust memory.</summary>
    public const int MaxFileBytes = 40 * 1024 * 1024;

    private static readonly string[] KnownFields =
    [
        "name", "description", "personality", "scenario", "first_mes", "mes_example", "creator_notes", "system_prompt",
        "post_history_instructions", "alternate_greetings", "tags", "creator", "character_version",
    ];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Reads a card from a PNG picture or a JSON file. A PNG's picture is kept as the character's image.</summary>
    public static Character Read(byte[] file)
    {
        if (file.Length > MaxFileBytes) throw new CardFormatException("That file is too large to be a character card.");
        if (PngText.IsPng(file))
        {
            Dictionary<string, string> text;
            try
            {
                text = PngText.ReadText(file);
            }
            catch (InvalidDataException)
            {
                throw new CardFormatException("That picture could not be read.");
            }
            var payload = text.GetValueOrDefault("ccv3") ?? text.GetValueOrDefault("chara")
                ?? throw new CardFormatException("That picture has no character card inside it.");
            string json;
            try
            {
                json = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Trim()));
            }
            catch (FormatException)
            {
                throw new CardFormatException("The card inside that picture is damaged.");
            }
            var character = FromJson(json);
            character.Image = file;
            return character;
        }

        // Skip a UTF-8 byte order mark, which some editors add.
        var start = file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF ? 3 : 0;
        return FromJson(Encoding.UTF8.GetString(file, start, file.Length - start));
    }

    public static Character FromJson(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new CardFormatException("That file is not a character card.");
        }
        catch (JsonException)
        {
            throw new CardFormatException("That file is not a character card (it is not valid JSON).");
        }

        // V2 and V3 put the fields under "data"; V1 cards have them at the top.
        var data = root["data"] as JsonObject ?? root;
        var name = Str(data, "name").Trim();
        if (name.Length == 0) name = Str(data, "char_name").Trim();
        if (name.Length == 0) throw new CardFormatException("That card has no character name.");

        var extra = new JsonObject();
        if (data != root)
        {
            foreach (var (key, value) in data)
                if (!KnownFields.Contains(key) && value is not null)
                    extra[key] = value.DeepClone();
        }

        return new Character
        {
            Name = name,
            Description = Str(data, "description"),
            Personality = Str(data, "personality"),
            Scenario = Str(data, "scenario"),
            FirstMessage = Str(data, "first_mes"),
            ExampleMessages = Str(data, "mes_example"),
            CreatorNotes = Str(data, "creator_notes"),
            SystemPrompt = Str(data, "system_prompt"),
            PostHistoryInstructions = Str(data, "post_history_instructions"),
            AlternateGreetings = Strings(data, "alternate_greetings"),
            Tags = Strings(data, "tags"),
            Creator = Str(data, "creator"),
            CharacterVersion = Str(data, "character_version"),
            ExtraJson = extra.Count == 0 ? "" : extra.ToJsonString(),
        };
    }

    /// <summary>The card as Character Card V2 JSON: the standard fields plus extensions and the embedded lorebook.</summary>
    public static string ToV2Json(Character c, bool indented = false)
    {
        var data = Fields(c);
        var extra = Extra(c);
        data["extensions"] = extra["extensions"]?.DeepClone() ?? new JsonObject();
        if (extra["character_book"] is { } book) data["character_book"] = book.DeepClone();
        var root = new JsonObject { ["spec"] = "chara_card_v2", ["spec_version"] = "2.0", ["data"] = data };
        return root.ToJsonString(indented ? Pretty : null);
    }

    /// <summary>The card as Character Card V3 JSON, keeping every field DOG.ai does not edit.</summary>
    public static string ToV3Json(Character c, bool indented = false)
    {
        var data = Fields(c);
        foreach (var (key, value) in Extra(c))
            if (value is not null) data[key] = value.DeepClone();
        data["extensions"] ??= new JsonObject();
        data["group_only_greetings"] ??= new JsonArray();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        data["creation_date"] ??= new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAt == default ? DateTime.UtcNow : c.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
        data["modification_date"] = now;
        var root = new JsonObject { ["spec"] = "chara_card_v3", ["spec_version"] = "3.0", ["data"] = data };
        return root.ToJsonString(indented ? Pretty : null);
    }

    /// <summary>
    /// A PNG card: the character's picture (or a plain square when there is none) with the card stored
    /// both as V2 ("chara") and V3 ("ccv3"), which is what other apps expect.
    /// </summary>
    public static byte[] ToPng(Character c)
    {
        var picture = c.Image is { } image && PngText.IsPng(image) ? image : PngText.SolidSquare(400, 0x8a, 0x6f, 0x55);
        return PngText.WithText(picture, new Dictionary<string, string>
        {
            ["chara"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ToV2Json(c))),
            ["ccv3"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ToV3Json(c))),
        });
    }

    /// <summary>The card's V3 group-only greetings: openings meant for group chats only. Empty when it has none.</summary>
    public static IReadOnlyList<string> GroupGreetings(Character c) =>
        Strings(Extra(c), "group_only_greetings").Where(g => !string.IsNullOrWhiteSpace(g)).ToList();

    /// <summary>The card's kept-aside JSON with the group-only greetings replaced, keeping every other field.</summary>
    public static string WithGroupGreetings(Character c, IEnumerable<string> greetings)
    {
        var extra = Extra(c);
        var list = greetings.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (list.Count == 0) extra.Remove("group_only_greetings");
        else extra["group_only_greetings"] = new JsonArray(list.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray());
        return extra.Count == 0 ? "" : extra.ToJsonString();
    }

    /// <summary>The lorebook embedded in the card, as JSON, or null when it has none.</summary>
    public static string? EmbeddedLorebookJson(Character c) => Extra(c)["character_book"]?.ToJsonString();

    /// <summary>The card's kept-aside JSON with the embedded lorebook replaced (or removed when <paramref name="book"/> is null).</summary>
    public static string WithEmbeddedLorebook(Character c, JsonObject? book)
    {
        var extra = Extra(c);
        if (book is null) extra.Remove("character_book");
        else extra["character_book"] = book.DeepClone();
        return extra.Count == 0 ? "" : extra.ToJsonString();
    }

    private static JsonObject Fields(Character c) => new()
    {
        ["name"] = c.Name,
        ["description"] = c.Description,
        ["personality"] = c.Personality,
        ["scenario"] = c.Scenario,
        ["first_mes"] = c.FirstMessage,
        ["mes_example"] = c.ExampleMessages,
        ["creator_notes"] = c.CreatorNotes,
        ["system_prompt"] = c.SystemPrompt,
        ["post_history_instructions"] = c.PostHistoryInstructions,
        ["alternate_greetings"] = new JsonArray(c.AlternateGreetings.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
        ["tags"] = new JsonArray(c.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
        ["creator"] = c.Creator,
        ["character_version"] = c.CharacterVersion,
    };

    private static JsonObject Extra(Character c)
    {
        if (string.IsNullOrWhiteSpace(c.ExtraJson)) return [];
        try
        {
            return JsonNode.Parse(c.ExtraJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static List<string> Strings(JsonObject o, string key) =>
        o[key] is JsonArray a
            ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];
}
