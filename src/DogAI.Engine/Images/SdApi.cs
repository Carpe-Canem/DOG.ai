using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core.Images;
using DogAI.Core.Video;

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

/// <summary>A finished clip as sd-server sends it: the file, and what the engine says about it.</summary>
public sealed record SdVideo(byte[] File, int? Frames, int? Fps, string? Format);

/// <summary>A clip's job as sd-server reports it.</summary>
/// <param name="Status">queued, generating, completed, failed or cancelled.</param>
/// <param name="Preview">The newest preview, or null when there is none or it is the one already shown (it is then not decoded again).</param>
/// <param name="PreviewChars">The length of the preview's base64 text: with its step, what tells one preview from the next.</param>
public sealed record SdVideoJob(string Status, VideoProgress? Progress, int? PreviewStep, byte[]? Preview, SdVideo? Video, string? Error, int PreviewChars = 0)
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
        // A large picture is turned from the drawing into the picture piece by piece, or that step alone fills the card.
        if (request.DecodesInTiles) body["vae_tiling_params"] = new JsonObject { ["enabled"] = true };
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
        var passes = new HiresPasses(request.Hires is not null);
        try
        {
            while (true)
            {
                var job = await JobAsync(id, ct).ConfigureAwait(false);
                if (progress is not null && job.Progress is { } told)
                {
                    var now = passes.Told(told);
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
            await CancelAndWaitAsync(id, CancelWait).ConfigureAwait(false);
            throw;
        }
    }

    // ---- Video (M20). The same program started with a video model; a clip is a job like a picture. ----

    /// <summary>How often a clip being made is asked for its progress. A step takes five to ten seconds, and every answer repeats the newest preview.</summary>
    public TimeSpan VideoPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a cancelled clip is given to stop by itself. While it is drawn it stops after the step in progress;
    /// while the model's own decoder runs it cannot be interrupted at all (three minutes at Better's size), so the
    /// caller ends the program when this has passed.
    /// </summary>
    public TimeSpan VideoCancelWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The request body for one clip. <paramref name="seed"/> is the one actually used (never below zero). The video
    /// route has no <c>batch_count</c> and no <c>embed_image_metadata</c>: that nothing is written into the file is
    /// decided by the program's start switch alone, and checked on every result.
    /// </summary>
    public static JsonObject VideoBody(VideoRequest request, long seed)
    {
        var body = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["negative_prompt"] = request.NegativePrompt,
            ["width"] = request.Width,
            ["height"] = request.Height,
            ["seed"] = seed,
            ["video_frames"] = request.Frames,
            ["fps"] = request.Fps,
            ["sample_params"] = new JsonObject
            {
                ["sample_method"] = request.Sampler,
                ["sample_steps"] = request.Steps,
                ["guidance"] = new JsonObject { ["txt_cfg"] = request.Guidance },
                ["flow_shift"] = request.FlowShift,
            },
            ["output_format"] = "webm",
            ["output_compression"] = request.Compression,
            // "tae" is the newest frame at full size from the fast decoder, which the program is always started with.
            ["preview"] = request.Preview ? "tae" : "none",
            ["preview_interval"] = request.PreviewEvery,
        };
        // The first frame travels as bytes inside the request: never a path, never an address.
        if (request.StartFrame is { } frame) body["init_image"] = Convert.ToBase64String(frame);
        // "Faster" (M21): the engine's block cache, with its settings written out. Added last and only then, so a
        // plain clip's body is byte for byte what it was in M20. Never "end_image": the installed model ends such a
        // clip on washed-out frames (the M21 trial), and DOG.ai has no end frame.
        if (request.Faster)
        {
            body["cache_mode"] = VideoRequest.FasterCacheMode;
            body["cache_option"] = VideoRequest.FasterCacheOption;
        }
        return body;
    }

    /// <summary>Starts a clip and returns its job id.</summary>
    public async Task<string> StartVideoAsync(JsonObject body, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(_base + "vid_gen", JsonContent.Create(body), ct).ConfigureAwait(false);
        var node = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new VideoEngineException(Refusal(response.StatusCode, node, "video"));
        return Text(node?["id"]) ?? throw new VideoEngineException("The video engine did not say which job it started.");
    }

    /// <param name="shown">The preview already passed on (its step and base64 length): the engine repeats it on every poll, and it is not decoded again.</param>
    public Task<SdVideoJob> VideoJobAsync(string id, CancellationToken ct = default, (int? Step, int Chars)? shown = null) =>
        VideoJobAsync(id, new AnswerBuffer(), shown, ct);

    /// <param name="buffer">Where the answer is read; a clip gives the same one to every poll.</param>
    private async Task<SdVideoJob> VideoJobAsync(string id, AnswerBuffer buffer, (int? Step, int Chars)? shown, CancellationToken ct)
    {
        using var response = await http.GetAsync(_base + "jobs/" + Uri.EscapeDataString(id), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var node = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            throw new VideoEngineException(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? "The video engine no longer knows this video (it may have restarted)."
                : Refusal(response.StatusCode, node, "video"));
        }
        // Every answer repeats the newest preview (up to 1.9 MB of text), once a second for minutes. Read as text and
        // taken apart into objects, that was about 10 MB of short-lived copies a poll, and DOG.ai's own memory rose
        // from 0.2 to 1.9 GB over ten clips (M20 step 6). So the answer is read into a buffer and walked in place:
        // nothing of that size is made but a preview that is new, and the clip itself. The buffer is the clip's own,
        // one for all its polls, and goes when the clip is done. Not the shared pool, which M20 used: it keeps what
        // each thread gave back, and after ten clips DOG.ai held 0.3 GB that way (M21 step 6).
        try
        {
            var length = await buffer.ReadAsync(response, ct).ConfigureAwait(false);
            return ParseVideoJob(buffer.Bytes.AsSpan(0, length), shown);
        }
        finally
        {
            // Wiped after every answer: it holds the clip and, in generation_info, the sentence.
            buffer.Wipe();
        }
    }

    /// <summary>
    /// The bytes one clip's answers are read into: one array for all its polls, grown when an answer is larger (a
    /// first preview, then the clip itself), wiped after every answer.
    /// </summary>
    internal sealed class AnswerBuffer
    {
        private int _used;

        public byte[] Bytes { get; private set; } = new byte[1 << 16];

        /// <summary>Reads the whole answer and returns its length.</summary>
        public async Task<int> ReadAsync(HttpResponseMessage response, CancellationToken ct)
        {
            // Sized by what the engine says is coming, with a quarter to spare (the next preview is seldom the same size).
            if (response.Content.Headers.ContentLength is { } known && known > Bytes.Length)
                Grow((int)Math.Min(known + known / 4, 1 << 28), 0);
            var length = 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            while (true)
            {
                if (length == Bytes.Length) Grow(checked(Bytes.Length * 2), length);
                var read = await stream.ReadAsync(Bytes.AsMemory(length), ct).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                _used = Math.Max(_used, length);
            }
            return length;
        }

        private void Grow(int size, int keep)
        {
            var larger = new byte[size];
            Bytes.AsSpan(0, keep).CopyTo(larger);
            Wipe();
            Bytes = larger;
            _used = keep;
        }

        /// <summary>Clears what an answer left in it.</summary>
        public void Wipe()
        {
            Array.Clear(Bytes, 0, Math.Min(_used, Bytes.Length));
            _used = 0;
        }
    }

    /// <summary>
    /// Makes one clip from start to finish: starts the job, reports progress (and new previews) as it changes, and
    /// returns the file. Cancelling <paramref name="ct"/> asks the engine to stop and waits <see cref="VideoCancelWait"/>
    /// at most; the caller ends the program either way.
    /// </summary>
    public async Task<SdVideo> MakeVideoAsync(VideoRequest request, long seed, IProgress<VideoProgress>? progress, CancellationToken ct)
    {
        var id = await StartVideoAsync(VideoBody(request, seed), ct).ConfigureAwait(false);
        VideoProgress? last = null;
        (int? Step, int Chars)? lastPreview = null;
        // One buffer for all this clip's answers (see VideoJobAsync).
        var buffer = new AnswerBuffer();
        try
        {
            while (true)
            {
                // The engine repeats its newest preview (0.5 to 1.4 MB) on every poll: only a new one is decoded.
                var job = await VideoJobAsync(id, buffer, lastPreview, ct).ConfigureAwait(false);
                if (progress is not null && job.Progress is not null)
                {
                    var now = job.Progress;
                    var preview = job.Preview;
                    if (preview is not null) lastPreview = (job.PreviewStep, job.PreviewChars);
                    if (preview is not null || last is null || last.Stage != now.Stage || last.Step != now.Step || last.TotalSteps != now.TotalSteps)
                    {
                        last = now;
                        progress.Report(now with { Preview = preview });
                    }
                }
                switch (job.Status)
                {
                    case "completed":
                        return job.Video ?? throw new VideoEngineException("The video engine finished but sent no video.");
                    case "failed":
                        throw new VideoEngineException("The video could not be made." + (job.Error is { Length: > 0 } said ? " The engine said: " + said : ""));
                    case "cancelled":
                        ct.ThrowIfCancellationRequested();
                        throw new VideoEngineException("The video engine cancelled the video.");
                }
                await Task.Delay(VideoPollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelAndWaitAsync(id, VideoCancelWait).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads a clip's job. Only the named fields are taken: the engine's <c>generation_info</c>, which repeats the
    /// prompt, the negative prompt and the model's file names, is never read, kept or passed on.
    /// </summary>
    /// <param name="utf8">The engine's answer as it arrived. It is walked in place; the file and a new preview are the only large things made of it.</param>
    /// <param name="shown">The preview already passed on (its step and the length of its base64 text): one like it is left as text.</param>
    internal static SdVideoJob ParseVideoJob(ReadOnlySpan<byte> utf8, (int? Step, int Chars)? shown = null)
    {
        var status = "unknown";
        VideoProgress? progress = null;
        byte[]? preview = null;
        int? previewStep = null;
        var previewChars = 0;
        SdVideo? video = null;
        string? error = null;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return new SdVideoJob(status, null, null, null, null, null);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("status"u8))
                {
                    reader.Read();
                    status = Str(ref reader) ?? "unknown";
                }
                else if (reader.ValueTextEquals("progress"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    string? stage = null, unit = null;
                    int? completed = null, total = null;
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("stage"u8)) { reader.Read(); stage = Str(ref reader); }
                        else if (reader.ValueTextEquals("unit"u8)) { reader.Read(); unit = Str(ref reader); }
                        else if (reader.ValueTextEquals("completed"u8)) { reader.Read(); completed = Num(ref reader); }
                        else if (reader.ValueTextEquals("total"u8)) { reader.Read(); total = Num(ref reader); }
                        else { reader.Read(); reader.Skip(); }
                    }
                    var steps = unit == "steps";
                    progress = new VideoProgress(stage ?? "", steps ? completed : null, steps ? total : null);
                }
                else if (reader.ValueTextEquals("preview"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    // The text is looked at where it lies; whether it is decoded is decided once its step is known.
                    ReadOnlySpan<byte> plain = default;
                    byte[]? decoded = null;
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("b64_json"u8))
                        {
                            reader.Read();
                            if (reader.TokenType != JsonTokenType.String) { reader.Skip(); continue; }
                            previewChars = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
                            // Written with escapes (not what the engine does): decoded through the reader, which undoes them.
                            if (reader.ValueIsEscaped || reader.HasValueSequence) decoded = reader.TryGetBytesFromBase64(out var bytes) ? bytes : null;
                            else plain = reader.ValueSpan;
                        }
                        else if (reader.ValueTextEquals("step"u8)) { reader.Read(); previewStep = Num(ref reader); }
                        else { reader.Read(); reader.Skip(); }
                    }
                    if (previewChars == 0) previewStep = null;
                    else if (shown != (previewStep, previewChars)) preview = decoded ?? Decode(plain);
                }
                else if (reader.ValueTextEquals("result"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    byte[]? file = null;
                    int? frames = null, fps = null;
                    string? format = null;
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("b64_json"u8))
                        {
                            reader.Read();
                            if (reader.TokenType != JsonTokenType.String) { reader.Skip(); continue; }
                            file = reader.ValueIsEscaped || reader.HasValueSequence
                                ? (reader.TryGetBytesFromBase64(out var bytes) ? bytes : null)
                                : Decode(reader.ValueSpan);
                        }
                        else if (reader.ValueTextEquals("frame_count"u8)) { reader.Read(); frames = Num(ref reader); }
                        else if (reader.ValueTextEquals("fps"u8)) { reader.Read(); fps = Num(ref reader); }
                        else if (reader.ValueTextEquals("output_format"u8)) { reader.Read(); format = Str(ref reader); }
                        // generation_info (the prompt again, the negative prompt, the model's file names) and anything else: passed over unread.
                        else { reader.Read(); reader.Skip(); }
                    }
                    if (file is { Length: > 0 }) video = new SdVideo(file, frames, fps, format);
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String) error = reader.GetString();
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        string? message = null, code = null;
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            if (reader.ValueTextEquals("message"u8)) { reader.Read(); message = Str(ref reader); }
                            else if (reader.ValueTextEquals("code"u8)) { reader.Read(); code = Str(ref reader); }
                            else { reader.Read(); reader.Skip(); }
                        }
                        error = message ?? code;
                    }
                    else reader.Skip();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // An answer cut short or not JSON: what was read before stands (a status of "unknown" is asked for again).
        }
        return new SdVideoJob(status, progress, previewStep, preview, video, error, previewChars);

        static string? Str(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            reader.Skip();
            return null;
        }

        static int? Num(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.Number)
            {
                reader.Skip();
                return null;
            }
            if (reader.TryGetInt32(out var i)) return i;
            if (reader.TryGetInt64(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
            return reader.TryGetDouble(out var d) && !double.IsNaN(d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : null;
        }
    }

    /// <summary>The same for an answer already taken apart (tests).</summary>
    internal static SdVideoJob ParseVideoJob(JsonObject node, (int? Step, int Chars)? shown = null) =>
        ParseVideoJob(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), shown);

    /// <summary>Base64 text as it lies in an answer (plain ASCII, maybe a data: address) to bytes; null when it is not base64.</summary>
    private static byte[]? Decode(ReadOnlySpan<byte> b64)
    {
        if (b64.StartsWith("data:"u8) && b64.IndexOf((byte)',') is >= 0 and var comma) b64 = b64[(comma + 1)..];
        if (b64.Length == 0) return null;
        var buffer = new byte[System.Buffers.Text.Base64.GetMaxDecodedFromUtf8Length(b64.Length)];
        if (System.Buffers.Text.Base64.DecodeFromUtf8(b64, buffer, out _, out var written) != System.Buffers.OperationStatus.Done) return null;
        return written == buffer.Length ? buffer : buffer[..written];
    }

    /// <summary>The upscaler files the engine found that can enlarge a picture (their names, without the extension).</summary>
    public async Task<IReadOnlyList<string>> UpscalersAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("capabilities", ct).ConfigureAwait(false);
        return (node?["upscalers"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(u => u["image_upscale"] is JsonValue v && v.TryGetValue<bool>(out var can) && can)
            .Select(u => Text(u["name"]))
            .OfType<string>()
            .ToList();
    }

    /// <summary>The request body for enlarging a picture with an upscaler file.</summary>
    public static JsonObject EnlargeBody(ImageEnlarge request) => new()
    {
        ["image"] = Convert.ToBase64String(request.Image),
        // Always named: left out, the engine would take the first file it finds.
        ["upscaler"] = request.Upscaler,
        ["repeats"] = 1,
        ["output_format"] = FormatName(ImageFormat.Webp),
        ["output_compression"] = request.Quality,
    };

    /// <summary>
    /// Enlarges a picture with an upscaler file. The engine answers when it is done (it is not a job: there is no
    /// progress and nothing to cancel; giving up only stops the waiting). The engine writes an XMP part saying how many
    /// times it enlarged; the picture comes back without it.
    /// </summary>
    public async Task<ImageEnlarged> EnlargeAsync(ImageEnlarge request, CancellationToken ct)
    {
        using var response = await http.PostAsync(_base + "upscale", JsonContent.Create(EnlargeBody(request)), ct).ConfigureAwait(false);
        var node = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageEngineException(EnlargeRefusal(response.StatusCode, node, request.Upscaler));
        return ParseEnlarged(node as JsonObject ?? []);
    }

    internal static ImageEnlarged ParseEnlarged(JsonObject node)
    {
        var image = (node["images"] as JsonArray ?? []).OfType<JsonObject>().Select(i => Text(i["b64_json"])).OfType<string>().Select(Decode).OfType<byte[]>().FirstOrDefault()
            ?? throw new ImageEngineException("The picture engine finished but sent no picture.");
        var plain = WebpInfo.PictureOnly(image)
            ?? throw new ImageEngineException("The picture engine sent the enlarged picture in a form DOG.ai does not keep.");
        var info = WebpInfo.Read(plain)!.Value;
        return new ImageEnlarged(plain, info.Width, info.Height, Int(node["scale"]) ?? 0);
    }

    private static string EnlargeRefusal(HttpStatusCode status, JsonNode? node, string upscaler)
    {
        var message = Text((node as JsonObject)?["error"]) ?? "";
        if (message.StartsWith("no compatible image upscaler", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("no RGB ESRGAN", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("could not be loaded", StringComparison.OrdinalIgnoreCase))
            return $"The picture engine cannot use “{upscaler}” as an upscaler. It works with ESRGAN upscalers (for example Real-ESRGAN x4plus).";
        if (message.Contains("must not exceed", StringComparison.OrdinalIgnoreCase))
            return $"The enlarged picture would be larger than {ImageEnlarge.MaxSide} pixels a side, which is the most the picture engine makes.";
        return Refusal(status, node);
    }

    /// <summary>
    /// For a picture with a hires fix: between the end of the first drawing and the start of the second the engine
    /// reports that it is decoding, which reads as "Finishing the picture…" when the picture is only half way. Whatever
    /// it reports in that gap is passed on as "upscaling" (the enlarging it is on the way to). A picture without a
    /// hires fix, and everything after the second drawing began, is passed on as it is.
    /// </summary>
    internal sealed class HiresPasses(bool hires)
    {
        private bool _drew, _refining;

        public ImageProgress Told(ImageProgress now)
        {
            if (!hires) return now;
            if (now.Stage == "sampling")
            {
                _drew = true;
                if (now.Pass > 1) _refining = true;
                return now;
            }
            return _drew && !_refining && now.Stage is not ("upscaling" or "vae_encode") ? now with { Stage = "upscaling" } : now;
        }
    }

    private static bool SameSpot(ImageProgress a, ImageProgress b) =>
        a.Stage == b.Stage && a.Step == b.Step && a.TotalSteps == b.TotalSteps && a.Pass == b.Pass;

    /// <summary>Asks the engine to stop a job and waits, at most <paramref name="wait"/>, until it has. True when it stopped in that time.</summary>
    private async Task<bool> CancelAndWaitAsync(string id, TimeSpan wait)
    {
        using var limit = new CancellationTokenSource(wait);
        try
        {
            await CancelAsync(id, limit.Token).ConfigureAwait(false);
            while (!(await JobAsync(id, limit.Token).ConfigureAwait(false)).IsFinished)
                await Task.Delay(PollInterval, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or ImageEngineException)
        {
            // The caller is cancelling anyway; a server that stopped answering is dealt with on the next picture.
            return false;
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

    private static string Refusal(HttpStatusCode status, JsonNode? node, string engine = "picture")
    {
        // Two shapes seen: {"code": "...", "error": "text"} and {"error": {"code": "...", "message": "text"}}.
        // A third, from the upscale route: {"error": "text"} with no code (indexing a text value would throw).
        var code = Text((node as JsonObject)?["code"]) ?? Text((node?["error"] as JsonObject)?["code"]);
        var message = node?["error"] switch
        {
            JsonObject o => Text(o["message"]),
            JsonValue s => Text(s),
            _ => null,
        };
        return code switch
        {
            "model_not_loaded" => $"The {engine} engine has no model loaded.",
            "model_busy" => $"The {engine} engine is busy with another {engine}.",
            _ => $"The {engine} engine refused the request (HTTP {(int)status})" + (message is { Length: > 0 } ? ": " + message : "."),
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
