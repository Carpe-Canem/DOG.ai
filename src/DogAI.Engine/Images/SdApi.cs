using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core.Images;

namespace DogAI.Engine.Images;

/// <summary>A job as sd-server reports it.</summary>
/// <param name="Status">queued, generating, completed, failed or cancelled.</param>
/// <param name="Images">The finished pictures (raw bytes), in order, once completed.</param>
public sealed record SdJob(
    string Status,
    ImageProgress? Progress,
    int? PreviewStep,
    int PreviewPass,
    byte[]? Preview,
    IReadOnlyList<byte[]> Images,
    string? Error)
{
    public bool IsFinished => Status is "completed" or "failed" or "cancelled";
}

/// <summary>What the loaded engine offers: sampler and scheduler names, and the LoRAs it found.</summary>
/// <param name="Loras">Each LoRA's display name and the path to send back in a request.</param>
public sealed record SdCapabilities(IReadOnlyList<string> Samplers, IReadOnlyList<string> Schedulers, IReadOnlyList<(string Name, string Path)> Loras);

/// <summary>
/// sd-server's own API ("/sdcpp/v1"), over the loopback-only client. A picture is a job: start it, ask for its
/// progress until it is finished, cancel it. Pictures come back in the reply as base64; the server writes no files.
/// </summary>
public sealed class SdApi(HttpClient http, int port)
{
    private readonly string _base = $"http://127.0.0.1:{port}/sdcpp/v1/";

    /// <summary>How often a running job is asked for its progress.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a cancelled picture may take to stop before DOG.ai gives up waiting (the trial saw 0.3 s).</summary>
    public TimeSpan CancelWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>True once the server answers; false while it is still starting (nothing listening yet).</summary>
    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(_base + "models/status", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>"empty", "loading", "ready" or "error".</summary>
    public async Task<string?> ModelStateAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("models/status", ct).ConfigureAwait(false);
        return node?["state"]?.GetValue<string>();
    }

    public async Task<SdCapabilities> CapabilitiesAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("capabilities", ct).ConfigureAwait(false);
        return new SdCapabilities(Names(node?["samplers"]), Names(node?["schedulers"]),
            (node?["loras"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(l => (Name: Text(l["name"]) ?? "", Path: Text(l["path"]) ?? ""))
            .Where(l => l.Path.Length > 0)
            .ToList());

        static IReadOnlyList<string> Names(JsonNode? list) =>
            (list as JsonArray ?? []).Select(n => n is JsonObject o ? Text(o["name"]) ?? Text(o["id"]) : Text(n))
            .OfType<string>().ToList();
    }

    /// <summary>Lets go of the model's graphics memory without ending the program. False when the server says it is busy.</summary>
    public async Task<bool> UnloadAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync(_base + "models/unload", JsonContent.Create(new { }), ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>The request body for one picture. <paramref name="seed"/> is the one actually used (never below zero).</summary>
    public static JsonObject Body(ImageRequest request, long seed)
    {
        var guidance = new JsonObject { ["txt_cfg"] = request.Guidance };
        var sample = new JsonObject
        {
            ["sample_method"] = request.Sampler,
            ["sample_steps"] = request.Steps,
            ["guidance"] = guidance,
        };
        if (request.Scheduler is { Length: > 0 } scheduler) sample["scheduler"] = scheduler;

        var body = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["negative_prompt"] = request.NegativePrompt,
            ["width"] = request.Width,
            ["height"] = request.Height,
            ["seed"] = seed,
            ["batch_count"] = request.Count,
            ["sample_params"] = sample,
            ["output_format"] = FormatName(request.Format),
            // The prompt must not travel inside a picture someone exports later.
            ["embed_image_metadata"] = false,
            ["lora"] = new JsonArray((request.Loras ?? []).Select(l => (JsonNode)new JsonObject
            {
                ["path"] = l.Path.Replace('\\', '/'),
                ["multiplier"] = l.Strength,
            }).ToArray()),
            // "proj" makes previews from the drawing itself and needs no extra model file.
            ["preview"] = request.Preview ? "proj" : "none",
            ["preview_interval"] = request.PreviewEvery,
        };
        if (request.ClipSkip is { } clipSkip) body["clip_skip"] = clipSkip;
        if (request.Quality is { } quality) body["output_compression"] = quality;
        if (request.Hires is { } hires)
        {
            body["hires"] = new JsonObject
            {
                ["enabled"] = true,
                ["scale"] = hires.Scale,
                ["steps"] = hires.Steps,
                ["denoising_strength"] = hires.Denoise,
                ["upscaler"] = hires.Upscaler,
            };
        }
        return body;
    }

    public static string FormatName(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "jpeg",
        ImageFormat.Webp => "webp",
        _ => "png",
    };

    /// <summary>Starts a picture and returns its job id.</summary>
    public async Task<string> StartAsync(JsonObject body, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(_base + "img_gen", JsonContent.Create(body), ct).ConfigureAwait(false);
        var node = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageEngineException(Refusal(response.StatusCode, node));
        return Text(node?["id"]) ?? throw new ImageEngineException("The picture engine did not say which job it started.");
    }

    public async Task<SdJob> JobAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(_base + "jobs/" + Uri.EscapeDataString(id), ct).ConfigureAwait(false);
        var node = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageEngineException(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? "The picture engine no longer knows this picture (it may have restarted)."
                : Refusal(response.StatusCode, node));
        return ParseJob(node as JsonObject ?? []);
    }

    /// <summary>Asks the engine to stop a job. Safe to repeat; a finished job is left as it is.</summary>
    public async Task CancelAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(_base + "jobs/" + Uri.EscapeDataString(id) + "/cancel", JsonContent.Create(new { }), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Draws one request from start to finish: starts the job, reports progress (and new previews) as it changes, and
    /// returns the pictures. Cancelling <paramref name="ct"/> cancels the job in the engine and waits until it has stopped,
    /// so the next picture does not queue behind it.
    /// </summary>
    public async Task<ImageResult> DrawAsync(ImageRequest request, long seed, IProgress<ImageProgress>? progress, CancellationToken ct)
    {
        var id = await StartAsync(Body(request, seed), ct).ConfigureAwait(false);
        ImageProgress? lastReported = null;
        (int? Step, int Pass)? lastPreview = null;
        try
        {
            while (true)
            {
                var job = await JobAsync(id, ct).ConfigureAwait(false);
                if (progress is not null && job.Progress is { } now)
                {
                    var preview = job.Preview is not null && lastPreview != (job.PreviewStep, job.PreviewPass) ? job.Preview : null;
                    if (preview is not null) lastPreview = (job.PreviewStep, job.PreviewPass);
                    if (preview is not null || lastReported is null || !SameSpot(lastReported, now))
                    {
                        lastReported = now;
                        progress.Report(now with { Preview = preview });
                    }
                }
                switch (job.Status)
                {
                    case "completed":
                        if (job.Images.Count == 0) throw new ImageEngineException("The picture engine finished but sent no picture.");
                        return new ImageResult(job.Images, request.Format, seed);
                    case "failed":
                        throw new ImageEngineException("The picture could not be drawn." + (job.Error is { Length: > 0 } e ? " The engine said: " + e : ""));
                    case "cancelled":
                        ct.ThrowIfCancellationRequested();
                        throw new ImageEngineException("The picture engine cancelled the picture.");
                }
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelAndWaitAsync(id).ConfigureAwait(false);
            throw;
        }
    }

    private static bool SameSpot(ImageProgress a, ImageProgress b) =>
        a.Stage == b.Stage && a.Step == b.Step && a.TotalSteps == b.TotalSteps && a.Pass == b.Pass;

    private async Task CancelAndWaitAsync(string id)
    {
        using var limit = new CancellationTokenSource(CancelWait);
        try
        {
            await CancelAsync(id, limit.Token).ConfigureAwait(false);
            while (!(await JobAsync(id, limit.Token).ConfigureAwait(false)).IsFinished)
                await Task.Delay(PollInterval, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or ImageEngineException)
        {
            // The caller is cancelling anyway; a server that stopped answering is dealt with on the next picture.
        }
    }

    internal static SdJob ParseJob(JsonObject node)
    {
        var status = Text(node["status"]) ?? "unknown";

        ImageProgress? progress = null;
        if (node["progress"] is JsonObject p)
        {
            var steps = Text(p["unit"]) == "steps";
            progress = new ImageProgress(
                Text(p["stage"]) ?? "",
                steps ? Int(p["completed"]) : null,
                steps ? Int(p["total"]) : null,
                Int(p["pass"]) ?? 1);
        }

        byte[]? preview = null;
        int? previewStep = null;
        var previewPass = 1;
        if (node["preview"] is JsonObject v && Text(v["b64_json"]) is { Length: > 0 } b64)
        {
            preview = Decode(b64);
            previewStep = Int(v["step"]);
            previewPass = Int(v["pass"]) ?? 1;
        }

        var images = (node["result"]?["images"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .OrderBy(i => Int(i["index"]) ?? 0)
            .Select(i => Text(i["b64_json"]))
            .OfType<string>()
            .Select(Decode)
            .OfType<byte[]>()
            .ToList();

        var error = node["error"] switch
        {
            JsonObject o => Text(o["message"]) ?? Text(o["code"]),
            JsonValue s => Text(s),
            _ => null,
        };
        return new SdJob(status, progress, previewStep, previewPass, preview, images, error);
    }

    private static byte[]? Decode(string b64)
    {
        var comma = b64.StartsWith("data:", StringComparison.Ordinal) ? b64.IndexOf(',') : -1;
        try { return Convert.FromBase64String(comma >= 0 ? b64[(comma + 1)..] : b64); }
        catch (FormatException) { return null; }
    }

    private static string Refusal(HttpStatusCode status, JsonNode? node)
    {
        // Two shapes seen: {"code": "...", "error": "text"} and {"error": {"code": "...", "message": "text"}}.
        var code = Text(node?["code"]) ?? Text(node?["error"]?["code"]);
        var message = node?["error"] switch
        {
            JsonObject o => Text(o["message"]),
            JsonValue s => Text(s),
            _ => null,
        };
        return code switch
        {
            "model_not_loaded" => "The picture engine has no model loaded.",
            "model_busy" => "The picture engine is busy with another picture.",
            _ => $"The picture engine refused the request (HTTP {(int)status})" + (message is { Length: > 0 } ? ": " + message : "."),
        };
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(_base + path, ct).ConfigureAwait(false);
        var node = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ImageEngineException(Refusal(response.StatusCode, node));
        return node;
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }
}
