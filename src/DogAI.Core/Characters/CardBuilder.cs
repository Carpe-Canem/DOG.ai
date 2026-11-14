using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core.Chat;

namespace DogAI.Core.Characters;

/// <summary>
/// The AI card builder: the loaded model turns a short idea into a full character card. The engine is asked for JSON
/// that matches <see cref="Schema"/>, so the answer can be read reliably. Nothing is saved; the draft opens in the editor.
/// </summary>
public sealed class CardBuilder(IChatEngine engine)
{
    /// <summary>The JSON shape the model must produce: the standard card fields, by their card names.</summary>
    public const string Schema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "description": { "type": "string" },
            "personality": { "type": "string" },
            "scenario": { "type": "string" },
            "first_mes": { "type": "string" },
            "alternate_greetings": { "type": "array", "items": { "type": "string" }, "maxItems": 2 },
            "mes_example": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 6 }
          },
          "required": ["name", "description", "personality", "scenario", "first_mes", "alternate_greetings", "mes_example", "tags"]
        }
        """;

    public const string Instruction =
        "You write character cards for role-play chats. Given an idea, invent one vivid, consistent character and fill in every field:\n" +
        "- name: the character's name.\n" +
        "- description: 150-300 words on looks, background, habits, what they want and how they behave. Refer to the character as {{char}} and the person chatting as {{user}}.\n" +
        "- personality: a short comma-separated list of traits.\n" +
        "- scenario: one or two sentences on where and when the chat starts and what is going on.\n" +
        "- first_mes: the character's opening message to {{user}}, 60-150 words, in character, with actions in *asterisks*.\n" +
        "- alternate_greetings: one or two different opening messages in the same style.\n" +
        "- mes_example: two short sample exchanges, each starting with <START> on its own line, then lines like \"{{user}}: ...\" and \"{{char}}: ...\".\n" +
        "- tags: three to six short lowercase tags.\n" +
        "Never write {{user}}'s actions in the greetings. Reply with the JSON object only.";

    /// <summary>Tokens the model may write; a full card is usually 800-1500.</summary>
    public const int MaxTokens = 3000;

    /// <summary>Drafts a card from <paramref name="idea"/>. <paramref name="progress"/> gets the text written so far, as it grows.</summary>
    /// <exception cref="InvalidOperationException">No model is loaded, or the model's answer was not a usable card.</exception>
    public async Task<Character> DraftAsync(string idea, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!engine.IsReady) throw new InvalidOperationException("No model is loaded. Load one first.");
        idea = idea.Trim();
        if (idea.Length == 0) throw new InvalidOperationException("Describe the character first.");

        var preset = SamplerPresets.Get(SamplerPresets.DefaultId) with { Temperature = 0.8, MaxTokens = MaxTokens, JsonSchema = Schema };
        var prompt = new List<PromptMessage>
        {
            new("system", Instruction),
            new("user", "Idea: " + idea),
        };
        var text = new StringBuilder();
        await foreach (var delta in engine.StreamChatAsync(prompt, preset, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (delta.Content is null) continue;
            text.Append(delta.Content);
            progress?.Invoke(text.ToString());
        }
        return Parse(text.ToString());
    }

    /// <summary>
    /// Reads the model's JSON into a character. Tolerates text around the object (such as a code fence) and
    /// missing fields; refuses an answer with no name.
    /// </summary>
    public static Character Parse(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model did not write a card. Try again, or try another model.");
        JsonObject o;
        try
        {
            o = JsonNode.Parse(answer[start..(end + 1)]) as JsonObject ?? throw new InvalidOperationException("The model did not write a card.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The model's card was cut off or garbled. Try again, or try another model.");
        }

        var name = Str(o, "name").Trim();
        if (name.Length == 0) throw new InvalidOperationException("The model's card has no name. Try again.");
        return new Character
        {
            Name = name.Length > 100 ? name[..100] : name,
            Description = Str(o, "description").Trim(),
            Personality = Str(o, "personality").Trim(),
            Scenario = Str(o, "scenario").Trim(),
            FirstMessage = Str(o, "first_mes").Trim(),
            AlternateGreetings = Strings(o, "alternate_greetings"),
            ExampleMessages = Str(o, "mes_example").Trim(),
            Tags = Strings(o, "tags").Select(t => t.ToLowerInvariant()).Distinct().Take(10).ToList(),
            Creator = "DOG.ai card builder",
        };
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static List<string> Strings(JsonObject o, string key) =>
        o[key] is JsonArray a
            ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).ToList()
            : [];
}
