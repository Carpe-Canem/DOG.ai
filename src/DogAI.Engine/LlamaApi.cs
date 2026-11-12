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

    /// <summary>The context size the server actually allocated.</summary>
    public async Task<int?> ContextLengthAsync(CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, "props"), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return json["default_generation_settings"]?["n_ctx"]?.GetValue<int>();
    }

    public async Task<int> TokenizeAsync(string text, CancellationToken ct)
    {
        var body = new JsonObject { ["content"] = text, ["add_special"] = false, ["parse_special"] = true };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "tokenize", body), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return json["tokens"]?.AsArray().Count ?? 0;
    }

    public async Task<string> ApplyTemplateAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct)
    {
        var body = new JsonObject { ["messages"] = ToJson(messages) };
        using var response = await http.SendAsync(Request(HttpMethod.Post, "apply-template", body), ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return json["prompt"]?.GetValue<string>() ?? "";
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
    public async IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = SamplerBody(sampler);
        body["prompt"] = prompt;
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

    public async IAsyncEnumerable<ChatDelta> StreamChatAsync(
        IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, [EnumeratorCancellation] CancellationToken ct)
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

        using var response = await http.SendAsync(
            Request(HttpMethod.Post, "v1/chat/completions", body), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new EngineException(ErrorText(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), (int)response.StatusCode));

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        int promptTokens = 0, completionTokens = 0;
        double speed = 0;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.AsSpan(5).Trim().ToString();
            if (data == "[DONE]") break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new EngineException(ErrorText(error.GetRawText(), 0));

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

    private static JsonArray ToJson(IReadOnlyList<PromptMessage> messages) =>
        new(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray());

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
            throw new EngineException("The model engine sent an answer DOG AI could not read.");
        }
    }

    /// <summary>Pulls the human-readable message out of an engine error body.</summary>
    private static string ErrorText(string body, int status)
    {
        try
        {
            var node = JsonNode.Parse(body);
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
