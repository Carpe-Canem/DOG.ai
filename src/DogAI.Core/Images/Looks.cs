using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core.Characters;

namespace DogAI.Core.Images;

/// <summary>
/// How a character or persona looks in pictures: appearance words that go into every picture of them, and optionally a
/// LoRA trained on them. This is what keeps someone recognisable from one picture to the next: the chat model plans a
/// scene, but a person's looks always come from here.
/// </summary>
/// <param name="Tags">Appearance in the checkpoint's prompt style, usually tags ("1girl, silver hair, green eyes, scar on cheek").</param>
/// <param name="Lora">A character LoRA, as its path inside the loras folder; null for none.</param>
/// <param name="LoraStrength">How strongly the LoRA is used; null for the LoRA's own default.</param>
/// <remarks>
/// A character's looks live in the card's extensions (<c>extensions.dogai.looks</c> inside <see cref="Character.ExtraJson"/>),
/// so export and import keep them and other apps pass them through. A persona's are JSON in <see cref="Persona.LooksJson"/>.
/// Looks on an imported card are untrusted like the rest of it: <see cref="Clean"/> is applied on every read.
/// </remarks>
public sealed record Looks(string Tags = "", string? Lora = null, double? LoraStrength = null)
{
    public const int MaxTagsLength = 1000;
    public const double MinStrength = -2;
    public const double MaxStrength = 2;

    public static Looks None { get; } = new();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Tags) && Lora is null;

    /// <summary>
    /// The same looks made safe to use: text trimmed, put on one line and cut to <see cref="MaxTagsLength"/>; a LoRA
    /// only as a path that cannot leave the loras folder; the strength kept within sensible bounds.
    /// </summary>
    public Looks Clean()
    {
        var tags = string.Join(' ', (Tags ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim().Trim(',').Trim();
        if (tags.Length > MaxTagsLength) tags = tags[..MaxTagsLength];
        var lora = Lora is { Length: > 0 } path && path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) && ImageRequest.IsSafeRelativePath(path)
            ? ImageModelCatalog.Normalise(path)
            : null;
        double? strength = lora is null || LoraStrength is not { } s || double.IsNaN(s) ? null : Math.Clamp(s, MinStrength, MaxStrength);
        return new Looks(tags, lora, strength);
    }

    /// <summary>The words as separate pieces ("silver hair", "green eyes"), split at commas.</summary>
    public IReadOnlyList<string> Pieces() =>
        Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ---- Cards from someone else ----

    /// <summary>
    /// These looks as they are taken from an imported card: without any piece that is a tag DOG.ai leaves out of its own
    /// list, or contains one (<see cref="TagList.LeavesOut"/>), and how many pieces went. Everything else stays as
    /// written, known tag or not. Looks are otherwise used in prompts exactly as typed and never checked, which is right
    /// for words the owner typed and wrong for words that came with a card. With the full tag list added (Models › On this PC › Pictures
    /// → Extra tags) nothing is left out, as for the planner's own tags.
    /// </summary>
    public (Looks Looks, int LeftOut) ForImport(TagList? tags = null)
    {
        var list = tags ?? TagList.Current;
        var pieces = Pieces();
        var kept = pieces.Where(p => !list.LeavesOut(p)).ToList();
        return kept.Count == pieces.Count ? (this, 0) : (this with { Tags = string.Join(", ", kept) }, pieces.Count - kept.Count);
    }

    /// <summary>
    /// Call once on a character read from an imported card, before it is saved: takes left-out tags out of its Looks
    /// (<see cref="ForImport"/>) and returns how many went, so the owner can be told. Never call it on a character the
    /// owner is editing: what the owner types in the Looks box is theirs.
    /// </summary>
    public static int CheckImported(Character character, TagList? tags = null)
    {
        var (looks, leftOut) = Of(character).ForImport(tags);
        if (leftOut > 0) looks.SaveTo(character);
        return leftOut;
    }

    /// <summary>What the owner is told when <see cref="CheckImported"/> took something out. Says how many, never which.</summary>
    public static string ImportNote(int leftOut) =>
        $"{leftOut} of this card’s Looks {(leftOut == 1 ? "tag was" : "tags were")} left out, because DOG.ai leaves {(leftOut == 1 ? "it" : "them")} out of its own tag list. " +
        "Cards keep such tags once the full tag list is added in " + DogAI.Core.Models.ModelsPlaces.TagsName + ".";

    // ---- Characters: inside the card ----

    public static Looks Of(Character character) => FromJson(CharacterCard.LooksJson(character));

    /// <summary>Puts the looks into the card's kept-aside JSON (or takes them out when empty), keeping every other field.</summary>
    public void SaveTo(Character character) => character.ExtraJson = CharacterCard.WithLooks(character, ToJson());

    // ---- Personas: their own column ----

    public static Looks Of(Persona persona)
    {
        if (string.IsNullOrWhiteSpace(persona.LooksJson)) return None;
        try
        {
            return FromJson(JsonNode.Parse(persona.LooksJson) as JsonObject);
        }
        catch (JsonException)
        {
            return None;
        }
    }

    public void SaveTo(Persona persona) => persona.LooksJson = ToJson()?.ToJsonString();

    // ---- JSON ----

    public static Looks FromJson(JsonObject? o)
    {
        if (o is null) return None;
        return new Looks(
            o["tags"] is JsonValue t && t.TryGetValue<string>(out var tags) ? tags : "",
            o["lora"] is JsonValue l && l.TryGetValue<string>(out var lora) ? lora : null,
            o["lora_strength"] is JsonValue s && s.TryGetValue<double>(out var strength) ? strength : null).Clean();
    }

    /// <summary>The looks as JSON, or null when there is nothing to keep.</summary>
    public JsonObject? ToJson()
    {
        var clean = Clean();
        if (clean.IsEmpty) return null;
        var o = new JsonObject { ["tags"] = clean.Tags };
        if (clean.Lora is not null) o["lora"] = clean.Lora;
        if (clean.LoraStrength is { } strength) o["lora_strength"] = strength;
        return o;
    }
}
