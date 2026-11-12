using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core.Chat;

namespace DogAI.Core.Stories;

/// <summary>What the model drafted from a one-line idea. Nothing is saved until the user creates the story.</summary>
public sealed record StoryDraft(string Title, StoryBible Bible, List<StoryChapter> Outline);

/// <summary>
/// Drafts a story bible (and, for an authored story, an outline) from a short idea. Like the card builder, the engine
/// is asked for JSON matching a schema, so the answer can be read reliably.
/// </summary>
public sealed class StoryBuilder(IChatEngine engine)
{
    public const int MaxTokens = 3000;

    public static string Schema(StoryKind kind)
    {
        static JsonObject Text() => new() { ["type"] = "string" };
        var properties = new JsonObject
        {
            ["title"] = Text(), ["premise"] = Text(), ["genre"] = Text(), ["tone"] = Text(),
            ["cast"] = Text(), ["world"] = Text(), ["style_guide"] = Text(),
        };
        var required = new JsonArray("title", "premise", "genre", "tone", "cast", "world", "style_guide");
        if (kind == StoryKind.Authored)
        {
            properties["chapters"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 3,
                ["maxItems"] = 8,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["title"] = Text(), ["plan"] = Text() },
                    ["required"] = new JsonArray("title", "plan"),
                },
            };
            required.Add("chapters");
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required }.ToJsonString();
    }

    public static string Instruction(StoryKind kind) =>
        "You plan stories. Given an idea, write a story bible that a writer can follow. Fill in every field:\n" +
        "- title: a short title.\n" +
        "- premise: two or three sentences on who the story is about, what they want and what stands in the way.\n" +
        "- genre: a few words.\n" +
        "- tone: a few words on the mood and voice.\n" +
        "- cast: the main characters, one per line, as \"Name: who they are and what they want\"." +
        (kind == StoryKind.Interactive ? " The first line is the main character, whom the player plays.\n" : "\n") +
        "- world: the setting and the facts and rules that matter, in a short paragraph or a few lines.\n" +
        "- style_guide: three or four short rules for the prose, such as sentence length, how much dialogue, what to avoid.\n" +
        (kind == StoryKind.Authored
            ? "- chapters: an outline of four to six chapters. Each has a title and a plan of two to four sentences saying what happens in it. Together they tell the whole story, with an ending.\n"
            : "Leave the plot open: it is an interactive story and the player decides what happens.\n") +
        "Reply with the JSON object only.";

    /// <summary>Drafts from <paramref name="idea"/>. <paramref name="progress"/> gets the text written so far, as it grows.</summary>
    /// <exception cref="InvalidOperationException">No model is loaded, or the model's answer was not usable.</exception>
    public async Task<StoryDraft> DraftAsync(string idea, StoryKind kind, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!engine.IsReady) throw new InvalidOperationException("No model is loaded. Load one first.");
        idea = idea.Trim();
        if (idea.Length == 0) throw new InvalidOperationException("Describe the story first.");

        var preset = SamplerPresets.Get(SamplerPresets.DefaultId) with { Temperature = 0.8, MaxTokens = MaxTokens, JsonSchema = Schema(kind) };
        var prompt = new List<PromptMessage> { new("system", Instruction(kind)), new("user", "Idea: " + idea) };
        var text = new StringBuilder();
        await foreach (var delta in engine.StreamChatAsync(prompt, preset, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (delta.Content is null) continue;
            text.Append(delta.Content);
            progress?.Invoke(text.ToString());
        }
        return Parse(text.ToString());
    }

    /// <summary>Reads the model's JSON. Tolerates text around the object and missing fields; refuses an answer with no premise.</summary>
    public static StoryDraft Parse(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model did not write a story plan. Try again, or try another model.");
        JsonObject o;
        try
        {
            o = JsonNode.Parse(answer[start..(end + 1)]) as JsonObject ?? throw new InvalidOperationException("The model did not write a story plan.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The model's story plan was cut off or garbled. Try again, or try another model.");
        }

        var bible = new StoryBible
        {
            Premise = Str(o, "premise"),
            Genre = Str(o, "genre"),
            Tone = Str(o, "tone"),
            Cast = Str(o, "cast"),
            World = Str(o, "world"),
            StyleGuide = Str(o, "style_guide"),
        };
        if (bible.Premise.Length == 0) throw new InvalidOperationException("The model's story plan has no premise. Try again.");
        var outline = (o["chapters"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(c => new StoryChapter { Title = Str(c, "title"), Plan = Str(c, "plan") })
            .Where(c => c.Title.Length > 0 || c.Plan.Length > 0)
            .Take(20)
            .ToList();
        var title = Str(o, "title");
        return new StoryDraft(title.Length > 100 ? title[..100] : title, bible, outline);
    }

    /// <summary>Text fields; a model that gives a list where text was asked for (cast, style rules) gets its items joined as lines.</summary>
    private static string Str(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
        JsonArray a => string.Join("\n", a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0)),
        _ => "",
    };
}
