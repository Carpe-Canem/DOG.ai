using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DogAI.Core.Characters;

/// <summary>Where a lore entry goes in the prompt, relative to the character's description.</summary>
public enum LorePosition
{
    BeforeCard = 0,
    AfterCard = 1,
}

/// <summary>
/// One piece of world info: text that is added to the prompt when one of its keys shows up in recent messages.
/// Follows the Character Card V2 "character_book" entry. Fields DOG AI does not edit are kept in <see cref="Raw"/>.
/// </summary>
public sealed class LoreEntry
{
    /// <summary>A label for people; never sent to the model.</summary>
    public string Name { get; set; } = "";

    public List<string> Keys { get; set; } = [];

    /// <summary>With <see cref="Selective"/>, at least one of these must also appear.</summary>
    public List<string> SecondaryKeys { get; set; } = [];

    public string Content { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Always added, whether or not a key appears.</summary>
    public bool Constant { get; set; }

    public bool Selective { get; set; }
    public bool CaseSensitive { get; set; }

    /// <summary>Lower numbers are placed earlier in the prompt.</summary>
    public int InsertionOrder { get; set; } = 100;

    /// <summary>When the lore budget runs out, entries with lower priority are left out first.</summary>
    public int Priority { get; set; } = 10;

    public LorePosition Position { get; set; } = LorePosition.AfterCard;

    /// <summary>The entry as it was read, so unknown fields survive editing and export.</summary>
    public JsonObject Raw { get; set; } = [];
}

/// <summary>
/// A character's lorebook (the card's embedded "character_book"). Read from and written back to the
/// card's <see cref="Character.ExtraJson"/>, so other apps get it back unchanged apart from DOG AI's edits.
/// </summary>
public sealed class Lorebook
{
    public string Name { get; set; } = "";

    /// <summary>How many of the latest messages are searched for keys. Null uses <see cref="LoreMatcher.DefaultScanDepth"/>.</summary>
    public int? ScanDepth { get; set; }

    /// <summary>Most tokens lore may take. Null uses a quarter of the context window.</summary>
    public int? TokenBudget { get; set; }

    /// <summary>When on, the text of entries that fired is searched too, so one entry can bring in another.</summary>
    public bool RecursiveScanning { get; set; }

    public List<LoreEntry> Entries { get; set; } = [];

    public JsonObject Raw { get; set; } = [];

    /// <summary>The character's lorebook, or an empty one when the card has none.</summary>
    public static Lorebook For(Character character) =>
        CharacterCard.EmbeddedLorebookJson(character) is { } json ? Parse(json) : new Lorebook();

    public static Lorebook Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            root = [];
        }
        var book = new Lorebook
        {
            Raw = root,
            Name = Str(root, "name"),
            ScanDepth = Int(root, "scan_depth"),
            TokenBudget = Int(root, "token_budget"),
            RecursiveScanning = Bool(root, "recursive_scanning") ?? false,
        };
        if (root["entries"] is JsonArray entries)
        {
            foreach (var node in entries.OfType<JsonObject>())
            {
                var position = Str(node, "position");
                book.Entries.Add(new LoreEntry
                {
                    Raw = node,
                    Name = Str(node, "name") is { Length: > 0 } n ? n : Str(node, "comment"),
                    Keys = Strings(node, "keys"),
                    SecondaryKeys = Strings(node, "secondary_keys"),
                    Content = Str(node, "content"),
                    Enabled = Bool(node, "enabled") ?? true,
                    Constant = Bool(node, "constant") ?? false,
                    Selective = Bool(node, "selective") ?? false,
                    CaseSensitive = Bool(node, "case_sensitive") ?? false,
                    InsertionOrder = Int(node, "insertion_order") ?? 100,
                    Priority = Int(node, "priority") ?? 10,
                    Position = position == "before_char" ? LorePosition.BeforeCard : LorePosition.AfterCard,
                });
            }
        }
        return book;
    }

    /// <summary>
    /// Reads lore from a file: a character card (PNG or JSON) with an embedded book, a Character Card V2
    /// "character_book", or a SillyTavern World Info file (whose entries are an object keyed by number).
    /// </summary>
    public static Lorebook Import(byte[] file)
    {
        if (file.Length > CharacterCard.MaxFileBytes) throw new CardFormatException("That file is too large.");
        if (PngText.IsPng(file))
        {
            var book = For(CharacterCard.Read(file));
            return book.Entries.Count > 0 ? book : throw new CardFormatException("That card has no lore in it.");
        }
        var start = file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF ? 3 : 0;
        var json = System.Text.Encoding.UTF8.GetString(file, start, file.Length - start);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new CardFormatException("That file has no lore in it.");
        }
        catch (JsonException)
        {
            throw new CardFormatException("That file is not lore (it is not valid JSON).");
        }

        if (root["data"] is JsonObject { } data && data["character_book"] is JsonObject embedded) return Parse(embedded.ToJsonString());
        if (root["entries"] is JsonArray) return Parse(json);
        if (root["entries"] is JsonObject worldInfo) return FromWorldInfo(root, worldInfo);
        throw new CardFormatException("That file has no lore in it.");
    }

    /// <summary>Converts SillyTavern World Info entries to the card format.</summary>
    private static Lorebook FromWorldInfo(JsonObject root, JsonObject entries)
    {
        var book = new Lorebook { Name = Str(root, "name") };
        foreach (var (_, node) in entries)
        {
            if (node is not JsonObject e) continue;
            book.Entries.Add(new LoreEntry
            {
                Name = Str(e, "comment"),
                Keys = Strings(e, "key"),
                SecondaryKeys = Strings(e, "keysecondary"),
                Content = Str(e, "content"),
                Enabled = !(Bool(e, "disable") ?? false),
                Constant = Bool(e, "constant") ?? false,
                Selective = Bool(e, "selective") ?? false,
                CaseSensitive = Bool(e, "caseSensitive") ?? false,
                InsertionOrder = Int(e, "order") ?? 100,
                // World Info position 0 is "before the character's description"; others go after it.
                Position = Int(e, "position") == 0 ? LorePosition.BeforeCard : LorePosition.AfterCard,
            });
        }
        return book;
    }

    /// <summary>The book as Character Card V2 "character_book" JSON, keeping fields DOG AI does not edit.</summary>
    public JsonObject ToJson()
    {
        var root = (JsonObject)Raw.DeepClone();
        root["name"] = Name;
        SetOrRemove(root, "scan_depth", ScanDepth);
        SetOrRemove(root, "token_budget", TokenBudget);
        root["recursive_scanning"] = RecursiveScanning;
        root["extensions"] ??= new JsonObject();
        var entries = new JsonArray();
        var nextId = 1;
        foreach (var e in Entries)
        {
            var o = (JsonObject)e.Raw.DeepClone();
            o["keys"] = Array(e.Keys);
            o["secondary_keys"] = Array(e.SecondaryKeys);
            o["content"] = e.Content;
            o["enabled"] = e.Enabled;
            o["constant"] = e.Constant;
            o["selective"] = e.Selective;
            o["case_sensitive"] = e.CaseSensitive;
            o["insertion_order"] = e.InsertionOrder;
            o["priority"] = e.Priority;
            o["position"] = e.Position == LorePosition.BeforeCard ? "before_char" : "after_char";
            o["name"] = e.Name;
            o["comment"] = e.Name;
            o["extensions"] ??= new JsonObject();
            if (o["id"] is null) o["id"] = nextId;
            nextId++;
            entries.Add(o);
        }
        root["entries"] = entries;
        return root;
    }

    /// <summary>Stores this book in the character's card (or removes it when it has no entries).</summary>
    public void SaveTo(Character character) =>
        character.ExtraJson = CharacterCard.WithEmbeddedLorebook(character, Entries.Count == 0 ? null : ToJson());

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());

    private static void SetOrRemove(JsonObject o, string key, int? value)
    {
        if (value is { } v) o[key] = v;
        else o.Remove(key);
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int? Int(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out i)) return i;
        return null;
    }

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static List<string> Strings(JsonObject o, string key) =>
        o[key] is JsonArray a
            ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s.Trim() : null).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()
            : [];
}

/// <summary>Finds the lore entries whose keys appear in the latest messages.</summary>
public static class LoreMatcher
{
    public const int DefaultScanDepth = 4;
    private const int MaxRecursion = 3;

    /// <summary>Card text is untrusted, so a pattern key gets little time; a pattern that takes longer simply does not match.</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    /// <param name="recent">Message texts, oldest first. Only the last <see cref="Lorebook.ScanDepth"/> are searched.</param>
    /// <returns>The entries that fired, most important first (constant entries, then by priority).</returns>
    public static IReadOnlyList<LoreEntry> Match(Lorebook book, IReadOnlyList<string> recent)
    {
        var depth = Math.Max(0, book.ScanDepth ?? DefaultScanDepth);
        var scan = string.Join("\n", recent.Skip(Math.Max(0, recent.Count - depth)));
        var fired = new List<LoreEntry>();
        var candidates = book.Entries.Where(e => e.Enabled && e.Content.Trim().Length > 0).ToList();

        foreach (var entry in candidates.Where(e => e.Constant)) fired.Add(entry);
        var pass = 0;
        while (true)
        {
            var added = candidates.Where(e => !fired.Contains(e) && Fires(e, scan)).ToList();
            if (added.Count == 0) break;
            fired.AddRange(added);
            if (!book.RecursiveScanning || ++pass >= MaxRecursion) break;
            scan = string.Join("\n", added.Select(e => e.Content));
        }
        return fired
            .OrderByDescending(e => e.Constant)
            .ThenByDescending(e => e.Priority)
            .ThenBy(e => e.InsertionOrder)
            .ToList();
    }

    public static bool Fires(LoreEntry entry, string text)
    {
        if (!entry.Keys.Any(k => Contains(text, k, entry.CaseSensitive))) return false;
        if (entry.Selective && entry.SecondaryKeys.Count > 0)
            return entry.SecondaryKeys.Any(k => Contains(text, k, entry.CaseSensitive));
        return true;
    }

    /// <summary>
    /// True when <paramref name="key"/> appears in <paramref name="text"/>. Keys written as /pattern/flags are
    /// regular expressions. Other keys match whole words, so "cat" does not fire on "category".
    /// </summary>
    public static bool Contains(string text, string key, bool caseSensitive)
    {
        if (key.Length == 0 || text.Length == 0) return false;
        var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        string pattern;
        if (key.Length > 2 && key[0] == '/' && key.LastIndexOf('/') is var end and > 0)
        {
            pattern = key[1..end];
            if (key[(end + 1)..].Contains('i')) options |= RegexOptions.IgnoreCase;
        }
        else
        {
            var escaped = Regex.Escape(key);
            pattern = (char.IsLetterOrDigit(key[0]) ? @"(?<![\p{L}\p{N}_])" : "") + escaped
                      + (char.IsLetterOrDigit(key[^1]) ? @"(?![\p{L}\p{N}_])" : "");
        }
        try
        {
            return Regex.IsMatch(text, pattern, options | RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
