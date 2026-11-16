using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core.Chat;

namespace DogAI.Engine;

/// <summary>Speeds the engine measured for one benchmark run.</summary>
public sealed record BenchmarkTimings(double GenerateTokensPerSecond, double ReadTokensPerSecond, int TokensGenerated);

/// <summary>Thrown when the model engine reports a problem or cannot be used.</summary>
public sealed class EngineException(string message) : Exception(message);

/// <summary>Calls one running llama-server over its local HTTP API.</summary>
internal sealed class LlamaApi(HttpClient http, int port, string apiKey)
{
    private readonly Uri _base = new($"http://127.0.0.1:{port}/");

    /// <summary>True when ready, false while the model is still loading, null when nothing answers yet.</summary>
    public async Task<bool?> HealthAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "health"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>What the server says about the model it loaded.</summary>
    /// <param name="ContextLength">The context size it actually allocated.</param>
    /// <param name="Vision">True when the model can be given pictures (it was loaded with a projector).</param>
    internal sealed record Props(int? ContextLength, bool Vision);

    /// <summary>The text the engine itself puts where a picture is in a prompt; a new one at every start.</summary>
    private string _mediaMarker = "<__media__>";

    public async Task<Props> PropsAsync(CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, "props"), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var props = ReadProps(json);
        if (json["media_marker"] is JsonValue marker && marker.TryGetValue<string>(out var text) && text.Length > 0) _mediaMarker = text;
        return props;
    }

    internal static Props ReadProps(JsonNode json) => new(
        json["default_generation_settings"]?["n_ctx"] is JsonValue size && size.TryGetValue<int>(out var context) ? context : null,
        json["modalities"]?["vision"] is JsonValue vision && vision.TryGetValue<bool>(out var sees) && sees);

    private int? _countBase;
    private bool _countMissing;

    /// <summary>
    /// What one picture costs in tokens, by the engine's own count: a message with the picture, less the same message
    /// without it. Null from an engine that has no such route.
    /// </summary>
    public async Task<int?> CountPictureTokensAsync(byte[] jpeg, CancellationToken ct)
    {
        if (_countMissing) return null;
        _countBase ??= await CountMessageAsync([CountedText()], ct).ConfigureAwait(false);
        if (_countBase is not { } without)
        {
            _countMissing = true;
            return null;
        }
        return await CountMessageAsync([CountedText(), CountedPicture(jpeg)], ct).ConfigureAwait(false) is { } with && with > without ? with - without : null;
    }

    private static JsonObject CountedText() => new() { ["type"] = "text", ["text"] = "a" };

    /// <summary>A picture as the counting route takes it: the stored JPEG's bytes, never an address.</summary>
    internal static JsonObject CountedPicture(byte[] jpeg) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(Checked(jpeg)) },
    };

    private async Task<int?> CountMessageAsync(JsonArray content, CancellationToken ct)
    {
        var body = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }) };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "v1/messages/count_tokens", body), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return json?["input_tokens"] is JsonValue tokens && tokens.TryGetValue<int>(out var count) ? count : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The bytes of a picture that may be sent: a JPEG that passes Core's check, and nothing else. On bytes it cannot
    /// read the engine tries to start another program, and given an address it fetches it; so neither is ever sent.
    /// </summary>
    internal static byte[] Checked(byte[] jpeg) =>
        AttachedPictures.Problem(jpeg) is { } problem ? throw new EngineException("A picture could not be sent to the model. " + problem) : jpeg;

    /// <summary>A picture as a chat message carries it: "data:image/jpeg;base64," and the stored bytes.</summary>
    internal static JsonObject PicturePart(PromptPicture picture) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(Checked(picture.Jpeg)) },
    };

    public async Task<int> TokenizeAsync(string text, CancellationToken ct)
    {
        var body = new JsonObject { ["content"] = text, ["add_special"] = false, ["parse_special"] = true };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "tokenize", body), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return json["tokens"]?.AsArray().Count ?? 0;
    }

    /// <summary>The prompt's text as the model's template writes it. Text only: a picture is never given to this route.</summary>
    /// <param name="thinking">
    /// False closes a thinking model's thinking block at the end of the prompt (the same switches a chat request gets
    /// with thinking off), for a reply that is carried on: text put after an open block would be read as thinking.
    /// </param>
    public async Task<string> ApplyTemplateAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct, bool thinking = true)
    {
        var body = TemplateBody(messages, thinking);
        using var response = await http.SendAsync(Request(HttpMethod.Post, "apply-template", body), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return json["prompt"]?.GetValue<string>() ?? "";
    }

    internal static JsonObject TemplateBody(IReadOnlyList<PromptMessage> messages, bool thinking)
    {
        var body = new JsonObject { ["messages"] = ToJson(messages, pictures: false) };
        if (!thinking) body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false, ["thinking"] = false };
        return body;
    }

    private static JsonObject SamplerBody(SamplerPreset sampler)
    {
        var body = new JsonObject
        {
            ["stream"] = true,
            ["cache_prompt"] = true,
            ["temperature"] = sampler.Temperature,
            ["top_p"] = sampler.TopP,
            ["top_k"] = sampler.TopK,
            ["min_p"] = sampler.MinP,
            ["repeat_penalty"] = sampler.RepeatPenalty,
            ["dry_multiplier"] = sampler.DryMultiplier,
            ["xtc_probability"] = sampler.XtcProbability,
            ["xtc_threshold"] = sampler.XtcThreshold,
        };
        if (sampler.Stop is { Count: > 0 } stop)
            body["stop"] = new JsonArray(stop.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        // "max_tokens" for chat completions, "n_predict" for /completion; each endpoint ignores the other.
        if (sampler.MaxTokens is { } max)
        {
            body["max_tokens"] = max;
            body["n_predict"] = max;
        }
        return body;
    }

    /// <summary>
    /// Streams a continuation of raw prompt text through the native /completion endpoint, which (unlike chat
    /// completions) does not wrap the text in the chat template, so the model carries on mid-reply.
    /// </summary>
    /// <param name="pictures">Pictures in the text, each placed by how many characters of <paramref name="prompt"/> come before it.</param>
    public async IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, IReadOnlyList<PromptPicture> pictures, SamplerPreset sampler,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = SamplerBody(sampler);
        body["prompt"] = CompletionPrompt(prompt, pictures, _mediaMarker);
        if (sampler.JsonSchema is { } completionSchema) body["json_schema"] = JsonNode.Parse(completionSchema);
        body["n_predict"] ??= -1;

        using var response = await http.SendAsync(
            Request(HttpMethod.Post, "completion", body), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new EngineException(ErrorText(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), (int)response.StatusCode));

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.AsSpan(5).Trim().ToString();
            if (data == "[DONE]") break;
            using var doc = JsonDocument.Parse(data);
            if (ParseCompletionChunk(doc.RootElement) is { } delta) yield return delta;
        }
    }

    /// <summary>
    /// A raw prompt: the text alone, or with pictures the text with the engine's marker where each one is and the
    /// pictures' bytes beside it, in the same order.
    /// </summary>
    internal static JsonNode CompletionPrompt(string prompt, IReadOnlyList<PromptPicture> pictures, string marker)
    {
        if (pictures.Count == 0) return JsonValue.Create(prompt)!;
        // A text that already holds the marker would have more markers than pictures, which the engine refuses.
        if (prompt.Contains(marker, StringComparison.Ordinal)) throw new EngineException("The chat holds text the model engine keeps for pictures, so this reply cannot be carried on.");
        var text = new StringBuilder(prompt.Length + pictures.Count * marker.Length);
        var data = new JsonArray();
        var at = 0;
        foreach (var picture in pictures.OrderBy(p => p.After))
        {
            var place = Math.Clamp(picture.After, at, prompt.Length);
            text.Append(prompt, at, place - at).Append(marker);
            data.Add(Convert.ToBase64String(Checked(picture.Jpeg)));
            at = place;
        }
        text.Append(prompt, at, prompt.Length - at);
        return new JsonObject { ["prompt_string"] = text.ToString(), ["multimodal_data"] = data };
    }

    /// <summary>One /completion stream event: text in "content"; the last one has "stop": true and "timings".</summary>
    internal static ChatDelta? ParseCompletionChunk(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
            throw new EngineException(ErrorText(error.GetRawText(), 0));
        var content = Text(root, "content");
        GenerationStats? stats = null;
        if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
        {
            var speed = timings.TryGetProperty("predicted_per_second", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetDouble() : 0;
            // timings.prompt_n counts only the part not already cached; tokens_evaluated is the whole prompt.
            stats = new GenerationStats(Int(root, "tokens_evaluated") ?? 0, Int(timings, "predicted_n") ?? 0, speed);
        }
        return content is null && stats is null ? null : new ChatDelta(content, null, stats);
    }

    /// <summary>The body of a chat completions request: the messages, the sampler settings and the per-request switches.</summary>
    internal static JsonObject ChatRequestBody(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler)
    {
        var body = SamplerBody(sampler);
        body["messages"] = ToJson(messages);
        // llama-server turns the schema into a grammar, so the reply is always JSON of that shape.
        if (sampler.JsonSchema is { } schema)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "answer", ["schema"] = JsonNode.Parse(schema) },
            };
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        // Chat templates of thinking models read a switch: Qwen and GLM call it enable_thinking, DeepSeek thinking.
        // Templates that have neither ignore both, so a model that cannot be told simply thinks as before.
        if (sampler.Thinking is false)
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false, ["thinking"] = false };
        else
        {
            // The thinking level: the engine ends the thinking at the budget and the model goes on to its answer. No
            // words are added at the cut (owner's decision). An engine too old for the fields ignores them.
            if (sampler.ThinkingBudget is > 0 and var budget) body["thinking_budget_tokens"] = budget;
            // Lets the thinking of this reply be ended on request (Answer now, EndThinkingAsync).
            if (sampler.ThinkingControl) body["reasoning_control"] = true;
        }
        return body;
    }

    public async IAsyncEnumerable<ChatDelta> StreamChatAsync(
        IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = ChatRequestBody(messages, sampler);

        using var response = await http.SendAsync(
            Request(HttpMethod.Post, "v1/chat/completions", body), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new EngineException(ErrorText(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), (int)response.StatusCode));

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        int promptTokens = 0, completionTokens = 0;
        double speed = 0;
        var named = !sampler.ThinkingControl || sampler.Thinking is false;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.AsSpan(5).Trim().ToString();
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new EngineException(ErrorText(error.GetRawText(), 0));

            // The reply's name, once, before its first piece: what Answer now gives back to the engine.
            if (!named && Text(root, "id") is { } id)
            {
                named = true;
                yield return new ChatDelta(null, null, null, id);
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = Int(usage, "prompt_tokens") ?? promptTokens;
                completionTokens = Int(usage, "completion_tokens") ?? completionTokens;
            }
            if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
            {
                completionTokens = Int(timings, "predicted_n") ?? completionTokens;
                if (timings.TryGetProperty("predicted_per_second", out var perSecond) && perSecond.ValueKind == JsonValueKind.Number)
                    speed = perSecond.GetDouble();
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;
            var content = Text(delta, "content");
            var reasoning = Text(delta, "reasoning_content");
            if (content is not null || reasoning is not null)
                yield return new ChatDelta(content, reasoning, null);
        }

        if (completionTokens > 0 || promptTokens > 0)
            yield return new ChatDelta(null, null, new GenerationStats(promptTokens, completionTokens, speed));
    }

    /// <summary>
    /// Ends the thinking of a reply being written (asked for with "reasoning_control"), so the model answers at once.
    /// An engine without the route, or one that does not know the reply's switch, is too old: NotSupported.
    /// </summary>
    public async Task<ThinkingEnd> EndThinkingAsync(string replyId, CancellationToken ct)
    {
        var body = new JsonObject { ["id"] = replyId, ["action"] = "reasoning_end" };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "v1/chat/completions/control", body), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ReadThinkingEnd(response.IsSuccessStatusCode, text);
    }

    /// <summary>What the control route's answer means. "No active completion" is a reply that ended meanwhile, not an old engine.</summary>
    internal static ThinkingEnd ReadThinkingEnd(bool ok, string body)
    {
        if (!ok) return ThinkingEnd.NotSupported;
        try
        {
            var json = JsonNode.Parse(body);
            if (json?["success"] is JsonValue success && success.TryGetValue<bool>(out var done) && done) return ThinkingEnd.Ended;
            var message = json?["message"]?.ToString() ?? "";
            return message.Contains("no active completion", StringComparison.OrdinalIgnoreCase) ? ThinkingEnd.TooLate : ThinkingEnd.NotSupported;
        }
        catch (JsonException)
        {
            return ThinkingEnd.NotSupported;
        }
    }

    /// <summary>One vector per input, in order, from an engine started with --embedding.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["input"] = new JsonArray(inputs.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["encoding_format"] = "float",
        };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "v1/embeddings", body), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var result = new float[inputs.Count][];
        foreach (var item in json["data"]?.AsArray() ?? [])
        {
            var index = item?["index"]?.GetValue<int>() ?? -1;
            if (index < 0 || index >= result.Length) continue;
            result[index] = item!["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        }
        if (result.Any(r => r is null)) throw new EngineException("The document search model did not return a result for every passage.");
        return result;
    }

    /// <summary>A fixed, model-neutral prompt; the reply text itself is thrown away.</summary>
    private const string BenchmarkPrompt =
        "Write a long, detailed description of a walk through an old harbour town at dawn: the boats, the market, " +
        "the smells, the sounds and the people. Keep going without stopping.";

    public async Task<BenchmarkTimings> BenchmarkAsync(int tokens, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = BenchmarkPrompt,
            ["n_predict"] = tokens,
            ["ignore_eos"] = true,
            ["cache_prompt"] = false,
            ["temperature"] = 0.7,
            ["stream"] = false,
        };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "completion", body), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var t = json["timings"];
        return new BenchmarkTimings(
            t?["predicted_per_second"]?.GetValue<double>() ?? 0,
            t?["prompt_per_second"]?.GetValue<double>() ?? 0,
            t?["predicted_n"]?.GetValue<int>() ?? 0);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private static JsonArray ToJson(IReadOnlyList<PromptMessage> messages, bool pictures = true) =>
        new(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = pictures ? Content(m) : m.Content }).ToArray());

    /// <summary>
    /// A turn's content: its text, or with pictures a list of text pieces and pictures in their order. A picture part is
    /// always "data:image/jpeg;base64," and the stored bytes (<see cref="PicturePart"/>); there is no other kind.
    /// </summary>
    internal static JsonNode Content(PromptMessage message)
    {
        if (message.Pictures is not { Count: > 0 } pictures) return JsonValue.Create(message.Content)!;
        var parts = new JsonArray();
        var text = message.Content;
        var at = 0;
        void Text(int to)
        {
            if (to > at) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text[at..to] });
            at = Math.Max(at, to);
        }
        foreach (var picture in pictures.OrderBy(p => p.After))
        {
            Text(Math.Clamp(picture.After, at, text.Length));
            parts.Add(PicturePart(picture));
        }
        Text(text.Length);
        return parts;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new EngineException(ErrorText(text, (int)response.StatusCode));
        try
        {
            return JsonNode.Parse(text) ?? throw new EngineException("The model engine sent an empty answer.");
        }
        catch (JsonException)
        {
            throw new EngineException("The model engine sent an answer DOG.ai could not read.");
        }
    }

    /// <summary>Pulls the human-readable message out of an engine error body.</summary>
    internal static string ErrorText(string body, int status)
    {
        try
        {
            var node = JsonNode.Parse(body);
            // Too much for the context: the engine cuts nothing and says how much there was and how much fits.
            var error = node?["error"] as JsonObject ?? node as JsonObject;
            if (error?["type"]?.ToString() == "exceed_context_size_error"
                && error["n_prompt_tokens"] is JsonValue sent && sent.TryGetValue<int>(out var promptTokens)
                && error["n_ctx"] is JsonValue room && room.TryGetValue<int>(out var context))
                return ContextBudgeter.TooLong(promptTokens, context);
            var message = (node?["error"]?["message"] ?? node?["message"] ?? node?["error"])?.ToString();
            if (!string.IsNullOrWhiteSpace(message)) return "The model engine reported: " + message;
        }
        catch (JsonException)
        {
        }
        return status > 0 ? $"The model engine refused the request (HTTP {status})." : "The model engine reported an error.";
    }

    private static int? Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() is { Length: > 0 } s ? s : null : null;
}
